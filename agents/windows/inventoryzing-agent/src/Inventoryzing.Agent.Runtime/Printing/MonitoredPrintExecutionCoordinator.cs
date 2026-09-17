using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed record MonitoredPrintExecutionResult(
    PrintSubmission Submission,
    PrintQueueCorrelationRecord? Correlation,
    PrintAttemptRecord Attempt);

public sealed class MonitoredPrintExecutionCoordinator(
    IPrintAttemptJournal journal,
    IPrintQueueMonitor queueMonitor,
    PrintDispatchCoordinator dispatchCoordinator,
    DurablePrintQueueObserver queueObserver,
    DurablePrinterMonitorObserver printerObserver,
    TimeProvider? timeProvider = null)
{
    private readonly IPrintAttemptJournal journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly IPrintQueueMonitor queueMonitor = queueMonitor ?? throw new ArgumentNullException(nameof(queueMonitor));
    private readonly PrintDispatchCoordinator dispatchCoordinator = dispatchCoordinator ??
            throw new ArgumentNullException(nameof(dispatchCoordinator));
    private readonly DurablePrintQueueObserver queueObserver = queueObserver ?? throw new ArgumentNullException(nameof(queueObserver));
    private readonly DurablePrinterMonitorObserver printerObserver = printerObserver ?? throw new ArgumentNullException(nameof(printerObserver));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<MonitoredPrintExecutionResult> ExecuteAsync(
        string printerId,
        PrintQueueIdentity queue,
        IMonitoredPrinterSession printer,
        PrintProbeRequest request,
        string documentName,
        TimeSpan correlationTimeout,
        string? expectedOwnerName,
        string? expectedMachineName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerId);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        if (printer.AttemptId != request.RequestId)
        {
            throw new ArgumentException("The printer session belongs to another attempt.", nameof(printer));
        }
        if (!string.Equals(documentName, $"inventoryzing-{request.RequestId:N}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The spool document name must identify this attempt exactly.", nameof(documentName));
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(correlationTimeout, TimeSpan.Zero);

        var existing = await journal.FindAsync(request.RequestId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Status.State is not (
            PrintAttemptState.Created or PrintAttemptState.Claimed or
            PrintAttemptState.Staged or PrintAttemptState.Prepared))
        {
            // Replays return durable state. Recovery owns already-dispatched queue jobs;
            // a new session cannot reconstruct the original Brother callback stream.
            var replay = await dispatchCoordinator.DispatchAsync(printerId, printer, request, cancellationToken)
                .ConfigureAwait(false);
            return new MonitoredPrintExecutionResult(replay,
                await journal.FindQueueCorrelationAsync(request.RequestId, cancellationToken).ConfigureAwait(false),
                await FindAttemptAsync(request.RequestId, cancellationToken).ConfigureAwait(false));
        }

        await using var queueSession = await queueMonitor.ArmAsync(queue, cancellationToken)
            .ConfigureAwait(false);
        var submission = await dispatchCoordinator.DispatchAsync(
            printerId,
            printer,
            request,
            cancellationToken).ConfigureAwait(false);
        if (submission.Status != PrintSubmissionStatus.Submitted)
        {
            return new MonitoredPrintExecutionResult(
                submission,
                null,
                await FindAttemptAsync(request.RequestId, cancellationToken).ConfigureAwait(false));
        }

        var correlation = await queueObserver.CorrelateAsync(
            request.RequestId,
            queueSession,
            documentName,
            timeProvider.GetUtcNow() + correlationTimeout,
            expectedOwnerName,
            expectedMachineName,
            cancellationToken).ConfigureAwait(false);
        var current = await FindAttemptAsync(request.RequestId, cancellationToken).ConfigureAwait(false);
        if (correlation is null)
        {
            return new MonitoredPrintExecutionResult(submission, correlation, current);
        }

        var monitorAvailable =
            printer.CompletionCapability != PrinterMonitorCompletionCapability.Unsupported;
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<PrintQueueChangeNotification>? queueWait = null;
        Task<PrinterMonitorObservation>? monitorWait = null;
        try
        {
            monitorWait = monitorAvailable
                ? printer.WaitForMonitorEventAsync(monitoring.Token).AsTask()
                : null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Drain already-available page evidence before reducing queue disappearance
                // or returning a weaker spooler completion. Keep the losing read alive:
                // cancelling it each iteration can consume a callback without recording it.
                while (monitorWait?.IsCompletedSuccessfully == true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current = await printerObserver.ObserveAsync(
                        request.RequestId, monitorWait.Result, printer.CompletionCapability,
                        request.Copies, cancellationToken).ConfigureAwait(false);
                    monitorWait = printer.WaitForMonitorEventAsync(monitoring.Token).AsTask();
                }

                if (PrintAttemptStateMachine.IsTerminal(current.Status.State))
                {
                    break;
                }

                queueWait ??= queueSession.WaitForChangeAsync(Timeout.InfiniteTimeSpan, monitoring.Token).AsTask();
                if (queueWait.IsCompletedSuccessfully)
                {
                    current = await queueObserver.ObserveAsync(
                        request.RequestId, queueSession, correlation.Job, cancellationToken).ConfigureAwait(false);
                    queueWait = null;
                    continue;
                }

                var completed = monitorWait is null
                    ? queueWait
                    : await Task.WhenAny(queueWait, monitorWait).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
        }
        finally
        {
            monitoring.Cancel();
            // Join both reads before disposing their queue/session resources, including
            // on caller cancellation and observer failures.
            await Task.WhenAll(
                IgnoreMonitoringCancellationAsync(queueWait, monitoring.Token),
                IgnoreMonitoringCancellationAsync(monitorWait, monitoring.Token)).ConfigureAwait(false);
        }

        return new MonitoredPrintExecutionResult(submission, correlation, current);
    }

    private async ValueTask<PrintAttemptRecord> FindAttemptAsync(
        Guid attemptId,
        CancellationToken cancellationToken) =>
        await journal.FindAsync(attemptId, cancellationToken).ConfigureAwait(false) ??
        throw new PrintAttemptNotFoundException($"Print attempt '{attemptId}' was not found.");

    private static async Task IgnoreMonitoringCancellationAsync(
        Task? task,
        CancellationToken iterationToken)
    {
        if (task is null)
        {
            return;
        }
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (iterationToken.IsCancellationRequested)
        {
        }
    }
}
