using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed class DurablePrintQueueObserver(
    IPrintAttemptJournal journal,
    TimeProvider? timeProvider = null,
    Func<Guid>? createId = null)
{
    private readonly IPrintAttemptJournal journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<Guid> createId = createId ?? Guid.NewGuid;

    public async ValueTask<PrintQueueCorrelationRecord?> CorrelateAsync(
        Guid attemptId,
        IPrintQueueMonitorSession session,
        string documentName,
        DateTimeOffset deadline,
        string? expectedOwnerName,
        string? expectedMachineName,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        var normalizedDeadline = deadline.ToUniversalTime();
        var request = session.Arm.CreateCorrelationRequest(
            documentName,
            normalizedDeadline,
            expectedOwnerName,
            expectedMachineName);

        while (true)
        {
            var snapshot = await session.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var result = PrintQueueCorrelator.Correlate(request, snapshot.Jobs);
            if (result.Outcome == PrintQueueCorrelationOutcome.Matched)
            {
                var job = result.Match!;
                var reference = new PrintQueueJobReference(
                    session.Arm.Generation,
                    session.Arm.Queue,
                    job);
                var correlation = await journal.StoreQueueCorrelationAsync(
                    new PrintQueueCorrelationRecord(
                        attemptId,
                        reference,
                        timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
                await AppendQueueObservationAsync(
                    attemptId,
                    session.Arm.Generation,
                    job,
                    snapshot.Printer,
                    "queue.job.correlated",
                    job.StatusText,
                    snapshot.ObservedAt,
                    cancellationToken).ConfigureAwait(false);
                await TransitionIfAllowedAsync(
                    attemptId,
                    new PrintAttemptStatus(PrintAttemptState.SpoolerQueued),
                    snapshot.ObservedAt,
                    cancellationToken).ConfigureAwait(false);
                await ApplyJobSnapshotAsync(
                    attemptId,
                    session.Arm.Generation,
                    job,
                    snapshot.Printer,
                    snapshot.ObservedAt,
                    cancellationToken).ConfigureAwait(false);
                return correlation;
            }

            if (result.Outcome == PrintQueueCorrelationOutcome.Ambiguous)
            {
                await RecordUncertainAsync(
                    attemptId,
                    session.Arm.Generation,
                    snapshot,
                    "queue.correlation.ambiguous",
                    $"{result.Candidates.Count} queue jobs matched the submission identity.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            var remaining = normalizedDeadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                await RecordUncertainAsync(
                    attemptId,
                    session.Arm.Generation,
                    snapshot,
                    "queue.correlation.not_found",
                    "No queue job matched before the correlation deadline.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            var notification = await session.WaitForChangeAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
            if (notification.TimedOut)
            {
                await RecordUncertainAsync(
                    attemptId,
                    session.Arm.Generation,
                    snapshot,
                    "queue.correlation.not_found",
                    "No queue job matched before the correlation deadline.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
    }

    public async ValueTask<PrintAttemptRecord> ObserveAsync(
        Guid attemptId,
        IPrintQueueMonitorSession session,
        PrintQueueJobReference job,
        CancellationToken cancellationToken)
    {
        ValidateAttemptId(attemptId);
        ValidateSessionJob(session, job);
        var snapshot = await session.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var liveJob = await session.ReadJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
        return await ApplyLiveJobAsync(
            attemptId,
            job,
            liveJob,
            snapshot,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PrintQueueJobReference?> ReattachAsync(
        RecoverablePrintQueueAttempt recovery,
        IPrintQueueMonitorSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(session);
        if (!QueueMatches(recovery.Correlation.Job.Queue, session.Arm.Queue))
        {
            throw new ArgumentException(
                "The monitor session does not target the persisted queue.",
                nameof(session));
        }

        var persisted = recovery.Correlation.Job;
        var snapshot = await session.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var liveJob = await session.ReadJobAsync(persisted.JobId, cancellationToken).ConfigureAwait(false);
        var expected = new PrintQueueJobReference(
            session.Arm.Generation,
            session.Arm.Queue,
            persisted.JobId,
            persisted.DocumentName,
            persisted.SubmittedAt);
        await ApplyLiveJobAsync(
            recovery.Attempt.AttemptId,
            expected,
            liveJob,
            snapshot,
            cancellationToken).ConfigureAwait(false);
        return liveJob is not null && QueueJobIdentityMatches(expected, liveJob)
            ? new PrintQueueJobReference(session.Arm.Generation, session.Arm.Queue, liveJob)
            : null;
    }

    public async ValueTask<PrintQueueResumeResult> ResumeAsync(
        Guid commandId,
        Guid attemptId,
        IPrintQueueMonitorSession session,
        PrintQueueJobReference job,
        CancellationToken cancellationToken)
    {
        ValidateCommandId(commandId);
        ValidateAttemptId(attemptId);
        ValidateSessionJob(session, job);
        var command = await journal.FindResumeAsync(commandId, cancellationToken).ConfigureAwait(false);
        if (command is null)
        {
            command = await journal.BeginResumeAsync(
                commandId,
                attemptId,
                job,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        else if (command.AttemptId != attemptId || !QueueJobIdentityMatches(command.Job, job))
        {
            throw new PrintAttemptJournalConflictException(
                $"Print Resume command '{commandId}' does not match this attempt and queue job.");
        }

        if (command.Result is null)
        {
            var result = await session.ResumeAsync(job, cancellationToken).ConfigureAwait(false);
            command = await journal.CompleteResumeAsync(
                commandId,
                result,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }

        await AppendResumeObservationAsync(command, cancellationToken).ConfigureAwait(false);
        return command.Result!;
    }

    private async ValueTask<PrintAttemptRecord> ApplyLiveJobAsync(
        Guid attemptId,
        PrintQueueJobReference expected,
        PrintQueueJobSnapshot? liveJob,
        PrintQueueSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (liveJob is null)
        {
            await AppendQueueObservationAsync(
                attemptId,
                snapshot.Queue == expected.Queue ? expected.MonitorGeneration : null,
                null,
                snapshot.Printer,
                "queue.job.missing",
                "The persisted queue job is no longer present.",
                snapshot.ObservedAt,
                cancellationToken).ConfigureAwait(false);
            return await TransitionIfAllowedAsync(
                attemptId,
                new PrintAttemptStatus(PrintAttemptState.Unknown),
                snapshot.ObservedAt,
                cancellationToken).ConfigureAwait(false);
        }

        if (!QueueJobIdentityMatches(expected, liveJob))
        {
            await AppendQueueObservationAsync(
                attemptId,
                expected.MonitorGeneration,
                liveJob,
                snapshot.Printer,
                "queue.job.identity_mismatch",
                "The queue reused the persisted job ID for a different job.",
                snapshot.ObservedAt,
                cancellationToken).ConfigureAwait(false);
            return await TransitionIfAllowedAsync(
                attemptId,
                new PrintAttemptStatus(PrintAttemptState.Unknown),
                snapshot.ObservedAt,
                cancellationToken).ConfigureAwait(false);
        }

        return await ApplyJobSnapshotAsync(
            attemptId,
            expected.MonitorGeneration,
            liveJob,
            snapshot.Printer,
            snapshot.ObservedAt,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PrintAttemptRecord> ApplyJobSnapshotAsync(
        Guid attemptId,
        Guid monitorGeneration,
        PrintQueueJobSnapshot job,
        PrintQueuePrinterSnapshot printer,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var assessment = PrintQueueStatusReducer.Assess(job, printer);
        await AppendQueueObservationAsync(
            attemptId,
            monitorGeneration,
            job,
            printer,
            assessment.Code,
            assessment.Detail,
            observedAt,
            cancellationToken).ConfigureAwait(false);
        var current = await journal.FindAsync(attemptId, cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException($"Print attempt '{attemptId}' was not found.");
        if (current.Status.State == PrintAttemptState.DriverAccepted)
        {
            current = await TransitionIfAllowedAsync(
                attemptId,
                new PrintAttemptStatus(PrintAttemptState.SpoolerQueued),
                observedAt,
                cancellationToken).ConfigureAwait(false);
        }

        return current.Status == assessment.Status
            ? current
            : await TransitionIfAllowedAsync(
                attemptId,
                assessment.Status,
                observedAt,
                cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RecordUncertainAsync(
        Guid attemptId,
        Guid monitorGeneration,
        PrintQueueSnapshot snapshot,
        string code,
        string detail,
        CancellationToken cancellationToken)
    {
        await AppendQueueObservationAsync(
            attemptId,
            monitorGeneration,
            null,
            snapshot.Printer,
            code,
            detail,
            snapshot.ObservedAt,
            cancellationToken).ConfigureAwait(false);
        _ = await TransitionIfAllowedAsync(
            attemptId,
            new PrintAttemptStatus(PrintAttemptState.Unknown),
            snapshot.ObservedAt,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendQueueObservationAsync(
        Guid attemptId,
        Guid? monitorGeneration,
        PrintQueueJobSnapshot? job,
        PrintQueuePrinterSnapshot printer,
        string code,
        string? detail,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        _ = await journal.AppendObservationAsync(
            new PrintObservation(
                NextId(),
                attemptId,
                PrintObservationSource.WindowsSpooler,
                code,
                detail,
                job?.RawStatus,
                printer.RawStatus,
                null,
                monitorGeneration,
                observedAt,
                timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendResumeObservationAsync(
        PrintQueueResumeCommandRecord command,
        CancellationToken cancellationToken)
    {
        var result = command.Result ??
            throw new InvalidOperationException("A pending Resume command has no result to observe.");
        var completedAt = command.CompletedAt ??
            throw new InvalidOperationException("A completed Resume command has no completion time.");
        _ = await journal.AppendObservationAsync(
            new PrintObservation(
                command.CommandId,
                command.AttemptId,
                PrintObservationSource.WindowsSpooler,
                ResumeCode(result.Outcome),
                result.Detail,
                null,
                null,
                null,
                command.Job.MonitorGeneration,
                completedAt,
                completedAt),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PrintAttemptRecord> TransitionIfAllowedAsync(
        Guid attemptId,
        PrintAttemptStatus next,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var current = await journal.FindAsync(attemptId, cancellationToken).ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException($"Print attempt '{attemptId}' was not found.");
        if (current.Status == next)
        {
            return current;
        }
        if (current.Status.State == PrintAttemptState.Completed &&
            next.State == PrintAttemptState.Completed &&
            current.Status.CompletionEvidence >= next.CompletionEvidence)
        {
            return current;
        }
        if (!PrintAttemptStateMachine.CanTransition(current.Status.State, next.State))
        {
            return current;
        }

        var transition = await journal.TransitionAsync(
            attemptId,
            NextId(),
            current.Status.State,
            current.Version,
            next.State,
            next.CompletionEvidence,
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

    private static string ResumeCode(PrintQueueResumeOutcome outcome) => outcome switch
    {
        PrintQueueResumeOutcome.Resumed => "queue.resume.resumed",
        PrintQueueResumeOutcome.AlreadyResumed => "queue.resume.already_resumed",
        PrintQueueResumeOutcome.JobMissing => "queue.resume.job_missing",
        PrintQueueResumeOutcome.IdentityMismatch => "queue.resume.identity_mismatch",
        PrintQueueResumeOutcome.NotPaused => "queue.resume.not_paused",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static bool QueueJobIdentityMatches(
        PrintQueueJobReference expected,
        PrintQueueJobSnapshot actual) =>
        expected.JobId == actual.JobId &&
        expected.SubmittedAt == actual.SubmittedAt &&
        string.Equals(expected.DocumentName, actual.DocumentName, StringComparison.Ordinal);

    private static bool QueueJobIdentityMatches(
        PrintQueueJobReference first,
        PrintQueueJobReference second) =>
        QueueMatches(first.Queue, second.Queue) &&
        first.JobId == second.JobId &&
        first.SubmittedAt == second.SubmittedAt &&
        string.Equals(first.DocumentName, second.DocumentName, StringComparison.Ordinal);

    private static void ValidateSessionJob(
        IPrintQueueMonitorSession session,
        PrintQueueJobReference job)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(job);
        if (session.Arm.Generation != job.MonitorGeneration ||
            !QueueMatches(session.Arm.Queue, job.Queue))
        {
            throw new ArgumentException(
                "The queue job does not belong to this monitor session.",
                nameof(job));
        }
    }

    private static bool QueueMatches(PrintQueueIdentity first, PrintQueueIdentity second) =>
        string.Equals(first.PrinterName, second.PrinterName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.ServerName, second.ServerName, StringComparison.OrdinalIgnoreCase);

    private static void ValidateAttemptId(Guid attemptId)
    {
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("Print attempt ID cannot be empty.", nameof(attemptId));
        }
    }

    private static void ValidateCommandId(Guid commandId)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("Resume command ID cannot be empty.", nameof(commandId));
        }
    }
}
