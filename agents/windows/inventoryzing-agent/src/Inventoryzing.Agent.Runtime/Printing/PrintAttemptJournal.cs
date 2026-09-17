using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed record PrintAttemptRecord(
    Guid AttemptId,
    PrintAttemptStatus Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PrintAttemptTransitionRecord(
    long Sequence,
    Guid TransitionId,
    Guid AttemptId,
    PrintAttemptState? PreviousState,
    PrintAttemptStatus Status,
    long ResultingVersion,
    DateTimeOffset OccurredAt);

public enum PrintObservationSource
{
    WindowsSpooler,
    BrotherMonitor,
    PrinterAdapter,
    Coordinator,
}

public sealed record PrintQueueCorrelationRecord(
    Guid AttemptId,
    PrintQueueJobReference Job,
    DateTimeOffset CorrelatedAt);

public sealed record RecoverablePrintQueueAttempt(
    PrintAttemptRecord Attempt,
    PrintQueueCorrelationRecord Correlation);

public sealed record PrintObservation(
    Guid ObservationId,
    Guid AttemptId,
    PrintObservationSource Source,
    string Code,
    string? Detail,
    uint? RawJobStatus,
    uint? RawPrinterStatus,
    int? RawProviderStatus,
    Guid? MonitorGeneration,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt);

public sealed record PrintObservationRecord(long Sequence, PrintObservation Observation);

public sealed record PrintDispatchIntentRecord(
    Guid AttemptId,
    string PrinterId,
    string ArtifactSha256,
    string RequestFingerprint,
    DateTimeOffset CreatedAt);

public sealed record PrintDispatchResultRecord(
    Guid AttemptId,
    PrintSubmission Submission,
    DateTimeOffset RecordedAt);

public sealed record RecoverablePrintDispatchAttempt(
    PrintAttemptRecord Attempt,
    PrintDispatchIntentRecord Intent,
    PrintDispatchResultRecord? Result);

public enum PrintAttemptOriginKind
{
    Retry,
    Reprint,
}

public sealed record PrintAttemptOriginRecord(
    Guid CommandId,
    Guid AttemptId,
    Guid SourceAttemptId,
    PrintAttemptOriginKind Kind,
    string RequestedBy,
    bool DuplicateRiskAcknowledged,
    DateTimeOffset RequestedAt);

public sealed record DerivedPrintAttempt(
    PrintAttemptRecord Attempt,
    PrintAttemptOriginRecord Origin);

public sealed record PrintDispatchSlotRecord(
    string PrinterId,
    Guid AttemptId,
    DateTimeOffset AcquiredAt);

public sealed record PrintDispatchControlRecord(
    Guid CommandId,
    string PrinterId,
    bool IsHeld,
    string? Reason,
    string RequestedBy,
    DateTimeOffset OccurredAt);

public sealed record PrintQueueResumeCommandRecord(
    Guid CommandId,
    Guid AttemptId,
    PrintQueueJobReference Job,
    DateTimeOffset RequestedAt,
    PrintQueueResumeResult? Result,
    DateTimeOffset? CompletedAt);

public sealed class PrintAttemptJournalConflictException(string message) : InvalidOperationException(message);

public sealed class PrintAttemptNotFoundException(string message) : InvalidOperationException(message);

public interface IPrintAttemptJournal
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask<PrintAttemptRecord> CreateAsync(
        Guid attemptId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken);

    ValueTask<PrintAttemptTransitionRecord> TransitionAsync(
        Guid attemptId,
        Guid transitionId,
        PrintAttemptState expectedState,
        long expectedVersion,
        PrintAttemptState nextState,
        PrintCompletionEvidence? completionEvidence,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);

    ValueTask<PrintAttemptRecord?> FindAsync(
        Guid attemptId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PrintAttemptTransitionRecord>> ReadTransitionsAsync(
        Guid attemptId,
        CancellationToken cancellationToken);

    ValueTask<PrintQueueCorrelationRecord> StoreQueueCorrelationAsync(
        PrintQueueCorrelationRecord correlation,
        CancellationToken cancellationToken);

    ValueTask<PrintQueueCorrelationRecord?> FindQueueCorrelationAsync(
        Guid attemptId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<RecoverablePrintQueueAttempt>> ReadRecoverableQueueAttemptsAsync(
        CancellationToken cancellationToken);

    ValueTask<PrintObservationRecord> AppendObservationAsync(
        PrintObservation observation,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PrintObservationRecord>> ReadObservationsAsync(
        Guid attemptId,
        CancellationToken cancellationToken);

    ValueTask<PrintDispatchIntentRecord> StoreDispatchIntentAsync(
        PrintDispatchIntentRecord intent,
        CancellationToken cancellationToken);

    ValueTask<PrintDispatchIntentRecord?> FindDispatchIntentAsync(
        Guid attemptId,
        CancellationToken cancellationToken);

    ValueTask<PrintDispatchResultRecord> StoreDispatchResultAsync(
        PrintDispatchResultRecord result,
        CancellationToken cancellationToken);

    ValueTask<PrintDispatchResultRecord?> FindDispatchResultAsync(
        Guid attemptId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<RecoverablePrintDispatchAttempt>>
        ReadRecoverableDispatchAttemptsAsync(CancellationToken cancellationToken);

    ValueTask<DerivedPrintAttempt> CreateDerivedAttemptAsync(
        PrintAttemptOriginRecord origin,
        CancellationToken cancellationToken);

    ValueTask<PrintAttemptOriginRecord?> FindAttemptOriginAsync(
        Guid attemptId,
        CancellationToken cancellationToken);

    ValueTask<PrintDispatchSlotRecord> AcquireDispatchSlotAsync(
        PrintDispatchSlotRecord slot,
        CancellationToken cancellationToken);

    ValueTask<PrintDispatchSlotRecord?> FindDispatchSlotAsync(
        string printerId,
        CancellationToken cancellationToken);

    ValueTask<PrintDispatchControlRecord> SetDispatchControlAsync(
        PrintDispatchControlRecord command,
        CancellationToken cancellationToken);

    ValueTask<PrintDispatchControlRecord?> FindDispatchControlAsync(
        string printerId,
        CancellationToken cancellationToken);

    ValueTask<PrintQueueResumeCommandRecord> BeginResumeAsync(
        Guid commandId,
        Guid attemptId,
        PrintQueueJobReference job,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken);

    ValueTask<PrintQueueResumeCommandRecord> CompleteResumeAsync(
        Guid commandId,
        PrintQueueResumeResult result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    ValueTask<PrintQueueResumeCommandRecord?> FindResumeAsync(
        Guid commandId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PrintQueueResumeCommandRecord>> ReadPendingResumesAsync(
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Guid>> ReadPendingReportAttemptIdsAsync(
        CancellationToken cancellationToken);

    ValueTask MarkReportAcknowledgedAsync(
        Guid attemptId,
        long version,
        CancellationToken cancellationToken);
}

public sealed class PrinterDispatchBusyException(
    string printerId,
    Guid activeAttemptId)
    : InvalidOperationException(
        $"Printer '{printerId}' already has active print attempt '{activeAttemptId}'.")
{
    public string PrinterId { get; } = printerId;

    public Guid ActiveAttemptId { get; } = activeAttemptId;
}

public sealed class PrinterDispatchHeldException(string printerId, string? reason)
    : InvalidOperationException(
        reason is null
            ? $"Printer '{printerId}' is under operator hold."
            : $"Printer '{printerId}' is under operator hold: {reason}")
{
    public string PrinterId { get; } = printerId;
}
