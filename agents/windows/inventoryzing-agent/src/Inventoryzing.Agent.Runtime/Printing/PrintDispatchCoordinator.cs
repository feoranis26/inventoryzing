using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed class PrintDispatchCoordinator(
    IPrintAttemptJournal journal,
    IPrintArtifactCache artifactCache,
    TimeProvider? timeProvider = null,
    Func<Guid>? createId = null,
    IPrintDispatchAuthority? dispatchAuthority = null,
    PrinterRuntimeMetrics? metrics = null)
{
    private readonly IPrintAttemptJournal journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly IPrintArtifactCache artifactCache = artifactCache ?? throw new ArgumentNullException(nameof(artifactCache));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<Guid> createId = createId ?? Guid.NewGuid;
    private readonly IPrintDispatchAuthority dispatchAuthority =
        dispatchAuthority ?? new LocalPrintDispatchAuthority();
    private readonly PrinterRuntimeMetrics metrics = metrics ?? new PrinterRuntimeMetrics();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> printerGates =
        new(StringComparer.Ordinal);

    public async ValueTask<PrintSubmission> DispatchAsync(
        string printerId,
        IPrinterProbe printer,
        PrintProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerId);
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(request);
        var gate = printerGates.GetOrAdd(printerId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DispatchExclusiveAsync(
                printerId,
                printer,
                request,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<PrintSubmission> DispatchExclusiveAsync(
        string printerId,
        IPrinterProbe printer,
        PrintProbeRequest request,
        CancellationToken cancellationToken)
    {
        var current = await BindIntentAsync(printerId, request, cancellationToken)
            .ConfigureAwait(false);
        var storedResult = await journal.FindDispatchResultAsync(
            request.RequestId,
            cancellationToken).ConfigureAwait(false);
        if (storedResult is not null)
        {
            _ = await ApplyStoredResultAsync(current, storedResult, cancellationToken)
                .ConfigureAwait(false);
            return storedResult.Submission;
        }

        if (current.Status.State == PrintAttemptState.Dispatching)
        {
            // A concurrent replay is not proof of a crash. Only startup recovery may
            // reconcile an interrupted dispatch; keep a live owner's slot intact.
            return new PrintSubmission(PrintSubmissionStatus.Unknown, null,
                "Dispatch has started; its outcome is not yet known. Observe or recover the existing attempt.");
        }
        if (current.Status.State == PrintAttemptState.DriverAccepted)
        {
            return await RecordResultAsync(
                current,
                new PrintSubmission(
                    PrintSubmissionStatus.Unknown,
                    null,
                    "Driver acceptance was persisted without its dispatch result."),
                CancellationToken.None).ConfigureAwait(false);
        }
        if (PrintAttemptStateMachine.IsTerminal(current.Status.State))
        {
            return TerminalReplay(current);
        }

        var dispatchControl = await journal.FindDispatchControlAsync(printerId, cancellationToken)
            .ConfigureAwait(false);
        if (dispatchControl?.IsHeld == true)
        {
            throw new PrinterDispatchHeldException(printerId, dispatchControl.Reason);
        }

        current = await AdvanceIfCurrentAsync(
            current,
            PrintAttemptState.Created,
            PrintAttemptState.Claimed,
            "dispatch.claimed",
            printerId,
            cancellationToken).ConfigureAwait(false);

        if (current.Status.State == PrintAttemptState.Claimed)
        {
            try
            {
                _ = await artifactCache.StoreAsync(request.Artifact, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return await RejectBeforeDispatchAsync(current, exception.Message, cancellationToken)
                    .ConfigureAwait(false);
            }

            current = await AdvanceAsync(
                current,
                PrintAttemptState.Staged,
                "artifact.staged",
                request.Artifact.Sha256,
                cancellationToken).ConfigureAwait(false);
        }

        PrintProbeRequest cachedRequest;
        try
        {
            cachedRequest = await BuildVerifiedRequestAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var capabilities = await printer.GetCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            var issues = PrintPreflight.Validate(
                cachedRequest.Artifact,
                cachedRequest.Profile,
                capabilities);
            if (issues.Count > 0)
            {
                return await RejectBeforeDispatchAsync(
                    current,
                    string.Join(" ", issues.Select(issue => $"{issue.Code}: {issue.Message}")),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await RejectBeforeDispatchAsync(current, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        current = await AdvanceIfCurrentAsync(
            current,
            PrintAttemptState.Staged,
            PrintAttemptState.Prepared,
            "dispatch.prepared",
            null,
            cancellationToken).ConfigureAwait(false);
        if (current.Status.State == PrintAttemptState.Prepared)
        {
            var intent = await journal.FindDispatchIntentAsync(
                current.AttemptId, cancellationToken).ConfigureAwait(false) ??
                throw new PrintAttemptJournalConflictException(
                    $"Print attempt '{current.AttemptId}' has no dispatch intent.");
            var authorization = await dispatchAuthority.AuthorizeAsync(intent, cancellationToken)
                .ConfigureAwait(false);
            if (authorization.AcknowledgementId == Guid.Empty)
            {
                throw new InvalidOperationException("Dispatch authority returned an empty acknowledgement ID.");
            }
            if (!Enum.IsDefined(authorization.Status))
            {
                throw new InvalidOperationException("Dispatch authority returned an invalid status.");
            }

            _ = await journal.AppendObservationAsync(
                new PrintObservation(
                    authorization.AcknowledgementId,
                    current.AttemptId,
                    PrintObservationSource.Coordinator,
                    authorization.Status == PrintDispatchAuthorizationStatus.Granted
                        ? "dispatch.authorized"
                        : "dispatch.authorization_rejected",
                    authorization.Detail,
                    null,
                    null,
                    null,
                    null,
                    timeProvider.GetUtcNow(),
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            if (authorization.Status == PrintDispatchAuthorizationStatus.Rejected)
            {
                return await RejectBeforeDispatchAsync(
                    current,
                    authorization.Detail ?? "Coordinator rejected dispatch authorization.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        current = await AdvanceIfCurrentAsync(
            current,
            PrintAttemptState.Prepared,
            PrintAttemptState.Dispatching,
            "dispatch.started",
            null,
            CancellationToken.None).ConfigureAwait(false);
        if (current.Status.State != PrintAttemptState.Dispatching)
        {
            return TerminalReplay(current);
        }

        try
        {
            _ = await journal.AcquireDispatchSlotAsync(
                new PrintDispatchSlotRecord(printerId, current.AttemptId, timeProvider.GetUtcNow()),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PrinterDispatchBusyException exception)
        {
            return await RecordResultAsync(
                current,
                new PrintSubmission(PrintSubmissionStatus.Rejected, null, exception.Message),
                CancellationToken.None).ConfigureAwait(false);
        }

        PrintSubmission submission;
        try
        {
            submission = await printer.SubmitAsync(cachedRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            submission = new PrintSubmission(
                PrintSubmissionStatus.Unknown,
                null,
                $"The printer provider threw after the dispatch barrier: {exception.Message}");
        }

        return await RecordResultAsync(current, submission, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async ValueTask<PrintAttemptRecord> BindIntentAsync(
        string printerId,
        PrintProbeRequest request,
        CancellationToken cancellationToken)
    {
        var existingIntent = await journal.FindDispatchIntentAsync(
            request.RequestId,
            cancellationToken).ConfigureAwait(false);
        var current = await journal.FindAsync(request.RequestId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            var createdAt = existingIntent?.CreatedAt ?? timeProvider.GetUtcNow();
            current = await journal.CreateAsync(
                request.RequestId,
                createdAt,
                cancellationToken).ConfigureAwait(false);
        }

        var intent = new PrintDispatchIntentRecord(
            request.RequestId,
            printerId,
            request.Artifact.Sha256,
            Fingerprint(printerId, request),
            existingIntent?.CreatedAt ?? current.CreatedAt);
        var origin = await journal.FindAttemptOriginAsync(request.RequestId, cancellationToken)
            .ConfigureAwait(false);
        if (origin is not null)
        {
            var sourceIntent = await journal.FindDispatchIntentAsync(
                origin.SourceAttemptId,
                cancellationToken).ConfigureAwait(false) ??
                throw new PrintAttemptJournalConflictException(
                    $"Source print attempt '{origin.SourceAttemptId}' has no dispatch intent.");
            if (!DerivedIntentMatches(sourceIntent, intent))
            {
                throw new PrintAttemptJournalConflictException(
                    "Retry and Reprint must reuse the source printer and exact print content.");
            }
        }
        _ = await journal.StoreDispatchIntentAsync(intent, cancellationToken).ConfigureAwait(false);
        return current;
    }

    private async ValueTask<PrintProbeRequest> BuildVerifiedRequestAsync(
        PrintProbeRequest request,
        CancellationToken cancellationToken)
    {
        var content = await artifactCache.ReadAsync(request.Artifact.Sha256, cancellationToken)
            .ConfigureAwait(false);
        var artifact = new PrintArtifact(
            request.Artifact.MediaType,
            request.Artifact.WidthMillimeters,
            request.Artifact.HeightMillimeters,
            request.Artifact.ColorSpace,
            content);
        if (!string.Equals(artifact.Sha256, request.Artifact.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The cached artifact identity changed during verification.");
        }

        return new PrintProbeRequest(
            request.RequestId,
            artifact,
            request.Profile,
            request.Copies,
            request.Fields);
    }

    private async ValueTask<PrintSubmission> RejectBeforeDispatchAsync(
        PrintAttemptRecord current,
        string detail,
        CancellationToken cancellationToken)
    {
        var submission = new PrintSubmission(PrintSubmissionStatus.Rejected, null, detail);
        return await RecordResultAsync(current, submission, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PrintSubmission> RecordResultAsync(
        PrintAttemptRecord current,
        PrintSubmission submission,
        CancellationToken cancellationToken)
    {
        var recordedAt = timeProvider.GetUtcNow();
        _ = await journal.AppendObservationAsync(
            new PrintObservation(
                current.AttemptId,
                current.AttemptId,
                PrintObservationSource.PrinterAdapter,
                ResultCode(submission.Status),
                ResultDetail(submission),
                null,
                null,
                null,
                null,
                recordedAt,
                recordedAt),
            cancellationToken).ConfigureAwait(false);
        var result = await journal.StoreDispatchResultAsync(
            new PrintDispatchResultRecord(current.AttemptId, submission, recordedAt),
            cancellationToken).ConfigureAwait(false);
        metrics.RecordDispatchResult(result.Submission.Status);
        _ = await ApplyStoredResultAsync(current, result, cancellationToken).ConfigureAwait(false);
        return result.Submission;
    }

    private async ValueTask<PrintAttemptRecord> ApplyStoredResultAsync(
        PrintAttemptRecord current,
        PrintDispatchResultRecord result,
        CancellationToken cancellationToken)
    {
        var next = result.Submission.Status switch
        {
            PrintSubmissionStatus.Submitted => PrintAttemptState.DriverAccepted,
            PrintSubmissionStatus.Rejected => PrintAttemptState.Rejected,
            PrintSubmissionStatus.Unknown => PrintAttemptState.Unknown,
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
        if (current.Status.State == next ||
            !PrintAttemptStateMachine.CanTransition(current.Status.State, next))
        {
            return current;
        }

        var transition = await journal.TransitionAsync(
            current.AttemptId,
            NextId(),
            current.Status.State,
            current.Version,
            next,
            null,
            result.RecordedAt,
            cancellationToken).ConfigureAwait(false);
        return current with
        {
            Status = transition.Status,
            Version = transition.ResultingVersion,
            UpdatedAt = transition.OccurredAt,
        };
    }

    private async ValueTask<PrintAttemptRecord> AdvanceIfCurrentAsync(
        PrintAttemptRecord current,
        PrintAttemptState expected,
        PrintAttemptState next,
        string code,
        string? detail,
        CancellationToken cancellationToken) =>
        current.Status.State == expected
            ? await AdvanceAsync(current, next, code, detail, cancellationToken).ConfigureAwait(false)
            : current;

    private async ValueTask<PrintAttemptRecord> AdvanceAsync(
        PrintAttemptRecord current,
        PrintAttemptState next,
        string code,
        string? detail,
        CancellationToken cancellationToken)
    {
        var occurredAt = timeProvider.GetUtcNow();
        var eventId = NextId();
        _ = await journal.AppendObservationAsync(
            new PrintObservation(
                eventId,
                current.AttemptId,
                PrintObservationSource.PrinterAdapter,
                code,
                detail,
                null,
                null,
                null,
                null,
                occurredAt,
                occurredAt),
            cancellationToken).ConfigureAwait(false);
        var transition = await journal.TransitionAsync(
            current.AttemptId,
            eventId,
            current.Status.State,
            current.Version,
            next,
            null,
            occurredAt,
            cancellationToken).ConfigureAwait(false);
        return current with
        {
            Status = transition.Status,
            Version = transition.ResultingVersion,
            UpdatedAt = transition.OccurredAt,
        };
    }

    private Guid NextId()
    {
        var id = createId();
        return id == Guid.Empty
            ? throw new InvalidOperationException("The print ID source returned an empty ID.")
            : id;
    }

    private static PrintSubmission TerminalReplay(PrintAttemptRecord current) =>
        current.Status.State switch
        {
            PrintAttemptState.Rejected => new PrintSubmission(
                PrintSubmissionStatus.Rejected,
                null,
                "The durable print attempt was already rejected."),
            PrintAttemptState.Completed => new PrintSubmission(
                PrintSubmissionStatus.Submitted,
                null,
                "The durable print attempt was already completed."),
            _ => new PrintSubmission(
                PrintSubmissionStatus.Unknown,
                null,
                $"The durable print attempt is terminal in state '{current.Status.State}'."),
        };

    private static string ResultCode(PrintSubmissionStatus status) => status switch
    {
        PrintSubmissionStatus.Submitted => "dispatch.submitted",
        PrintSubmissionStatus.Rejected => "dispatch.rejected",
        PrintSubmissionStatus.Unknown => "dispatch.unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string? ResultDetail(PrintSubmission submission) =>
        submission.SpoolJobId is null
            ? submission.Detail
            : $"Spool job '{submission.SpoolJobId}'. {submission.Detail}".TrimEnd();

    private static string Fingerprint(string printerId, PrintProbeRequest request)
    {
        var payload = new DispatchFingerprint(
            printerId,
            request.Artifact.Sha256,
            request.Artifact.MediaType,
            request.Artifact.WidthMillimeters,
            request.Artifact.HeightMillimeters,
            request.Artifact.ColorSpace,
            request.Profile.ProfileId,
            request.Profile.Palette,
            request.Profile.DpiX,
            request.Profile.DpiY,
            request.Profile.WidthMillimeters,
            request.Profile.HeightMillimeters,
            request.Copies,
            [.. request.Fields
                .OrderBy(field => field.Name, StringComparer.Ordinal)
                .Select(field => new DispatchField(field.Name, field.Value))]);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Convert.ToHexString(SHA256.HashData(serialized)).ToLowerInvariant();
    }

    private sealed record DispatchFingerprint(
        string PrinterId,
        string ArtifactSha256,
        string ArtifactMediaType,
        decimal ArtifactWidthMillimeters,
        decimal ArtifactHeightMillimeters,
        ArtifactColorSpace ArtifactColorSpace,
        string ProfileId,
        OutputPalette Palette,
        int DpiX,
        int DpiY,
        decimal ProfileWidthMillimeters,
        decimal ProfileHeightMillimeters,
        int Copies,
        IReadOnlyList<DispatchField> Fields);

    private sealed record DispatchField(string Name, string Value);

    private static bool DerivedIntentMatches(
        PrintDispatchIntentRecord source,
        PrintDispatchIntentRecord derived) =>
        string.Equals(source.PrinterId, derived.PrinterId, StringComparison.Ordinal) &&
        string.Equals(source.ArtifactSha256, derived.ArtifactSha256, StringComparison.Ordinal) &&
        string.Equals(source.RequestFingerprint, derived.RequestFingerprint, StringComparison.Ordinal);
}
