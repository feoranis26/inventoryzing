using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed class DurablePrinterMonitorObserver(
    IPrintAttemptJournal journal,
    TimeProvider? timeProvider = null)
{
    private const string PagePrintedCode = "brother.page_printed";
    private readonly IPrintAttemptJournal journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask<PrintAttemptRecord> ObserveAsync(
        Guid attemptId,
        PrinterMonitorObservation observation,
        PrinterMonitorCompletionCapability completionCapability,
        int expectedPageCompletions,
        CancellationToken cancellationToken)
    {
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("Attempt ID cannot be empty.", nameof(attemptId));
        }
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.ObservationId == Guid.Empty)
        {
            throw new ArgumentException("Observation ID cannot be empty.", nameof(observation));
        }
        if (!Enum.IsDefined(observation.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(observation));
        }
        if (!Enum.IsDefined(completionCapability))
        {
            throw new ArgumentOutOfRangeException(nameof(completionCapability));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedPageCompletions);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await journal.AppendObservationAsync(
                new PrintObservation(
                    observation.ObservationId,
                    attemptId,
                    PrintObservationSource.BrotherMonitor,
                    Code(observation.Kind),
                    observation.Value,
                    null,
                    null,
                    observation.RawStatus,
                    null,
                    observation.ObservedAt,
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);

            var current = await FindAttemptAsync(attemptId, cancellationToken).ConfigureAwait(false);
            var next = await ReduceAsync(
                attemptId,
                current,
                observation.Kind,
                completionCapability,
                expectedPageCompletions,
                cancellationToken).ConfigureAwait(false);
            if (next is null || current.Status == next)
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
                observation.ObservationId,
                current.Status.State,
                current.Version,
                next.State,
                next.CompletionEvidence,
                observation.ObservedAt,
                cancellationToken).ConfigureAwait(false);
            return current with
            {
                Status = transition.Status,
                Version = transition.ResultingVersion,
                UpdatedAt = transition.OccurredAt,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<PrintAttemptStatus?> ReduceAsync(
        Guid attemptId,
        PrintAttemptRecord current,
        PrinterMonitorEventKind kind,
        PrinterMonitorCompletionCapability completionCapability,
        int expectedPageCompletions,
        CancellationToken cancellationToken)
    {
        if (PrintAttemptStateMachine.IsTerminal(current.Status.State) &&
            current.Status.State != PrintAttemptState.Completed)
        {
            return null;
        }

        return kind switch
        {
            PrinterMonitorEventKind.PagePrinted => await ReducePagePrintedAsync(
                attemptId,
                completionCapability,
                expectedPageCompletions,
                cancellationToken).ConfigureAwait(false),
            PrinterMonitorEventKind.Offline or
            PrinterMonitorEventKind.Paused or
            PrinterMonitorEventKind.Error or
            PrinterMonitorEventKind.PrinterNotFound =>
                new PrintAttemptStatus(PrintAttemptState.Blocked),
            PrinterMonitorEventKind.Deleted =>
                new PrintAttemptStatus(PrintAttemptState.Failed),
            PrinterMonitorEventKind.Unknown => null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private async ValueTask<PrintAttemptStatus> ReducePagePrintedAsync(
        Guid attemptId,
        PrinterMonitorCompletionCapability completionCapability,
        int expectedPageCompletions,
        CancellationToken cancellationToken)
    {
        if (completionCapability == PrinterMonitorCompletionCapability.PageCompletion)
        {
            var observations = await journal.ReadObservationsAsync(attemptId, cancellationToken)
                .ConfigureAwait(false);
            var completedPages = observations.Count(item =>
                item.Observation.Source == PrintObservationSource.BrotherMonitor &&
                string.Equals(item.Observation.Code, PagePrintedCode, StringComparison.Ordinal));
            if (completedPages >= expectedPageCompletions)
            {
                return new PrintAttemptStatus(
                    PrintAttemptState.Completed,
                    PrintCompletionEvidence.BrotherMonitorConfirmed);
            }
        }

        return new PrintAttemptStatus(PrintAttemptState.Printing);
    }

    private async ValueTask<PrintAttemptRecord> FindAttemptAsync(
        Guid attemptId,
        CancellationToken cancellationToken) =>
        await journal.FindAsync(attemptId, cancellationToken).ConfigureAwait(false) ??
        throw new PrintAttemptNotFoundException($"Print attempt '{attemptId}' was not found.");

    private static string Code(PrinterMonitorEventKind kind) => kind switch
    {
        PrinterMonitorEventKind.PagePrinted => PagePrintedCode,
        PrinterMonitorEventKind.Offline => "brother.offline",
        PrinterMonitorEventKind.Paused => "brother.paused",
        PrinterMonitorEventKind.Deleted => "brother.deleted",
        PrinterMonitorEventKind.Error => "brother.error",
        PrinterMonitorEventKind.PrinterNotFound => "brother.printer_not_found",
        PrinterMonitorEventKind.Unknown => "brother.unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
