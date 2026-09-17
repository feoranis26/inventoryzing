using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed class PrintDispatchRecovery(
    IPrintAttemptJournal journal,
    TimeProvider? timeProvider = null,
    Func<Guid>? createId = null)
{
    private readonly IPrintAttemptJournal journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<Guid> createId = createId ?? Guid.NewGuid;

    public async ValueTask<IReadOnlyList<PrintAttemptRecord>> ReconcileAsync(
        CancellationToken cancellationToken)
    {
        var recoverable = await journal.ReadRecoverableDispatchAttemptsAsync(cancellationToken)
            .ConfigureAwait(false);
        List<PrintAttemptRecord> reconciled = [];
        foreach (var recovery in recoverable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reconciled.Add(await ReconcileAsync(recovery, cancellationToken).ConfigureAwait(false));
        }

        return reconciled;
    }

    private async ValueTask<PrintAttemptRecord> ReconcileAsync(
        RecoverablePrintDispatchAttempt recovery,
        CancellationToken cancellationToken)
    {
        var current = await journal.FindAsync(recovery.Attempt.AttemptId, cancellationToken)
            .ConfigureAwait(false) ??
            throw new PrintAttemptNotFoundException(
                $"Print attempt '{recovery.Attempt.AttemptId}' was not found during recovery.");
        if (current.Status.State is not (
            PrintAttemptState.Dispatching or PrintAttemptState.DriverAccepted))
        {
            return current;
        }

        PrintAttemptState next;
        string code;
        string detail;
        if (current.Status.State == PrintAttemptState.Dispatching &&
            recovery.Result?.Submission.Status == PrintSubmissionStatus.Rejected)
        {
            next = PrintAttemptState.Rejected;
            code = "dispatch.recovery.rejected";
            detail = "The provider rejection was durable before the process stopped.";
        }
        else
        {
            next = PrintAttemptState.Unknown;
            code = "dispatch.recovery.unknown";
            detail = current.Status.State == PrintAttemptState.DriverAccepted
                ? "Driver acceptance had no durable queue correlation when the process restarted."
                : "The process stopped after the dispatch barrier without proof that no output occurred.";
        }

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
        if (recovery.Result is null)
        {
            _ = await journal.StoreDispatchResultAsync(
                new PrintDispatchResultRecord(
                    current.AttemptId,
                    new PrintSubmission(PrintSubmissionStatus.Unknown, null, detail),
                    occurredAt),
                cancellationToken).ConfigureAwait(false);
        }

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
}