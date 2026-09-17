using System.ComponentModel;
using Inventoryzing.Agent.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed record PrintStartupReconciliationResult(
    int DispatchAttemptsReconciled,
    int QueueAttemptsReconciled);

public sealed class PrintStartupReconciler(
    IPrintAttemptJournal journal,
    PrintDispatchRecovery dispatchRecovery,
    IPrintQueueMonitor queueMonitor,
    DurablePrintQueueObserver queueObserver,
    TimeProvider? timeProvider = null,
    ILogger<PrintStartupReconciler>? logger = null,
    TimeSpan? reconnectDelay = null,
    PrinterRuntimeMetrics? metrics = null)
{
    private readonly IPrintAttemptJournal journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly PrintDispatchRecovery dispatchRecovery = dispatchRecovery ??
            throw new ArgumentNullException(nameof(dispatchRecovery));
    private readonly IPrintQueueMonitor queueMonitor = queueMonitor ?? throw new ArgumentNullException(nameof(queueMonitor));
    private readonly DurablePrintQueueObserver queueObserver = queueObserver ?? throw new ArgumentNullException(nameof(queueObserver));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<PrintStartupReconciler> logger = logger ?? NullLogger<PrintStartupReconciler>.Instance;
    private readonly TimeSpan reconnectDelay = ValidateReconnectDelay(reconnectDelay ?? TimeSpan.FromSeconds(5));
    private readonly PrinterRuntimeMetrics metrics = metrics ?? new PrinterRuntimeMetrics();

    public async ValueTask<PrintStartupReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken)
    {
        var dispatchCount = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var queueAttempts = await journal.ReadRecoverableQueueAttemptsAsync(cancellationToken)
            .ConfigureAwait(false);
        var queueCount = 0;
        foreach (var group in queueAttempts.GroupBy(
            attempt => attempt.Correlation.Job.Queue,
            PrintQueueIdentityComparer.Instance))
        {
            await using var session = await queueMonitor.ArmAsync(group.Key, cancellationToken)
                .ConfigureAwait(false);
            foreach (var attempt in group)
            {
                _ = await queueObserver.ReattachAsync(attempt, session, cancellationToken)
                    .ConfigureAwait(false);
                queueCount++;
            }
        }

        return new PrintStartupReconciliationResult(dispatchCount, queueCount);
    }

    public async ValueTask<int> InitializeAsync(CancellationToken cancellationToken)
    {
        await journal.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var dispatches = await dispatchRecovery.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        return dispatches.Count;
    }

    // Run once per host startup, after the dispatch recovery barrier and before
    // accepting new work. Each queue keeps its own notification session alive.
    public async Task MonitorRecoveredAsync(CancellationToken cancellationToken)
    {
        var attempts = await journal.ReadRecoverableQueueAttemptsAsync(cancellationToken)
            .ConfigureAwait(false);
        var tasks = attempts.GroupBy(attempt => attempt.Correlation.Job.Queue,
                PrintQueueIdentityComparer.Instance)
            .Select(group => MonitorQueueWithReconnectAsync(group.Key, [.. group], cancellationToken))
            .ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task MonitorQueueWithReconnectAsync(
        PrintQueueIdentity queue,
        IReadOnlyList<RecoverablePrintQueueAttempt> attempts,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await MonitorQueueSessionAsync(queue, attempts, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableMonitorFailure(exception))
            {
                metrics.RecordRecoveryReconnect();
                logger.LogWarning(exception,
                    "Print queue recovery monitor for {PrinterName} failed; reconnecting.",
                    queue.PrinterName);
            }

            await Task.Delay(reconnectDelay, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MonitorQueueSessionAsync(
        PrintQueueIdentity queue,
        IReadOnlyList<RecoverablePrintQueueAttempt> attempts,
        CancellationToken cancellationToken)
    {
        await using var session = await queueMonitor.ArmAsync(queue, cancellationToken)
            .ConfigureAwait(false);
        var live = new Dictionary<Guid, PrintQueueJobReference>();
        foreach (var attempt in attempts)
        {
            var current = await journal.FindAsync(attempt.Attempt.AttemptId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null || PrintAttemptStateMachine.IsTerminal(current.Status.State))
            {
                continue;
            }

            var reference = await queueObserver.ReattachAsync(attempt, session, cancellationToken)
                .ConfigureAwait(false);
            current = await journal.FindAsync(attempt.Attempt.AttemptId, cancellationToken)
                .ConfigureAwait(false);
            if (reference is not null && current is not null &&
                !PrintAttemptStateMachine.IsTerminal(current.Status.State))
            {
                live.Add(current.AttemptId, reference);
            }
        }

        while (live.Count > 0)
        {
            _ = await session.WaitForChangeAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            foreach (var (attemptId, reference) in live.ToArray())
            {
                var current = await queueObserver.ObserveAsync(
                    attemptId, session, reference, cancellationToken).ConfigureAwait(false);
                if (PrintAttemptStateMachine.IsTerminal(current.Status.State))
                {
                    live.Remove(attemptId);
                }
            }
        }
    }

    private static TimeSpan ValidateReconnectDelay(TimeSpan value) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));

    private static bool IsRecoverableMonitorFailure(Exception exception) =>
        exception is IOException or Win32Exception;

    private sealed class PrintQueueIdentityComparer : IEqualityComparer<PrintQueueIdentity>
    {
        public static PrintQueueIdentityComparer Instance { get; } = new();

        public bool Equals(PrintQueueIdentity? first, PrintQueueIdentity? second) =>
            ReferenceEquals(first, second) ||
            (first is not null &&
             second is not null &&
             string.Equals(
                 first.PrinterName,
                 second.PrinterName,
                 StringComparison.OrdinalIgnoreCase) &&
             string.Equals(
                 first.ServerName,
                 second.ServerName,
                 StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(PrintQueueIdentity value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.PrinterName),
                value.ServerName is null
                    ? 0
                    : StringComparer.OrdinalIgnoreCase.GetHashCode(value.ServerName));
        }
    }
}

internal sealed class PrinterRecoveryHostedService(
    PrintStartupReconciler reconciler,
    IOptions<AgentRuntimeOptions> options) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        _ = await reconciler.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        reconciler.MonitorRecoveredAsync(stoppingToken);
}
