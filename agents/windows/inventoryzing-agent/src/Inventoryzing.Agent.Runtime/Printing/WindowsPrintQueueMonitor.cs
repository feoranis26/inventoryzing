using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

public sealed class WindowsPrintQueueMonitor : IPrintQueueMonitor
{
    private readonly IPrintSpoolerApi api;
    private readonly TimeProvider timeProvider;

    public WindowsPrintQueueMonitor(TimeProvider? timeProvider = null)
        : this(new WindowsPrintSpoolerApi(), timeProvider ?? TimeProvider.System)
    {
    }

    internal WindowsPrintQueueMonitor(IPrintSpoolerApi api, TimeProvider timeProvider)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<IPrintQueueMonitorSession> ArmAsync(
        PrintQueueIdentity queue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queue);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(
            () => Arm(queue, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private IPrintQueueMonitorSession Arm(
        PrintQueueIdentity queue,
        CancellationToken cancellationToken)
    {
        IPrintSpoolerReadSession? readSession = null;
        IPrintSpoolerChangeSubscription? subscription = null;
        try
        {
            readSession = api.OpenRead(queue);
            subscription = readSession.Subscribe();
            var armedAt = timeProvider.GetUtcNow();
            var baseline = readSession.Snapshot(armedAt);
            cancellationToken.ThrowIfCancellationRequested();
            IPrintQueueMonitorSession monitorSession = new WindowsPrintQueueMonitorSession(
                api,
                readSession,
                subscription,
                new PrintQueueMonitorArm(Guid.NewGuid(), baseline, armedAt),
                timeProvider);
            readSession = null;
            subscription = null;
            return monitorSession;
        }
        finally
        {
            subscription?.Dispose();
            readSession?.Dispose();
        }
    }

    private sealed class WindowsPrintQueueMonitorSession(
        IPrintSpoolerApi api,
        IPrintSpoolerReadSession readSession,
        IPrintSpoolerChangeSubscription subscription,
        PrintQueueMonitorArm arm,
        TimeProvider timeProvider) : IPrintQueueMonitorSession
    {
        private readonly SemaphoreSlim snapshotGate = new(1, 1);
        private readonly SemaphoreSlim waitGate = new(1, 1);
        private int disposed;

        public PrintQueueMonitorArm Arm { get; } = arm;

        public async ValueTask<PrintQueueSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                return await Task.Run(
                    () => readSession.Snapshot(timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                snapshotGate.Release();
            }
        }

        public async ValueTask<PrintQueueJobSnapshot?> ReadJobAsync(
            uint jobId,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfZero(jobId);
            await snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                return await Task.Run(
                    () => readSession.ReadJob(jobId),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                snapshotGate.Release();
            }
        }

        public async ValueTask<PrintQueueChangeNotification> WaitForChangeAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            await waitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                return await subscription.WaitAsync(timeout, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                waitGate.Release();
            }
        }

        public async ValueTask<PrintQueueResumeResult> ResumeAsync(
            PrintQueueJobReference reference,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(reference);
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.MonitorGeneration != Arm.Generation ||
                !QueueMatches(reference.Queue, Arm.Queue))
            {
                return new PrintQueueResumeResult(
                    PrintQueueResumeOutcome.IdentityMismatch,
                    "The job reference does not belong to this monitor generation and queue.");
            }

            return await Task.Run(
                () => api.Resume(reference),
                cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            subscription.Dispose();
            readSession.Dispose();
            snapshotGate.Dispose();
            waitGate.Dispose();
            return ValueTask.CompletedTask;
        }

        private static bool QueueMatches(PrintQueueIdentity first, PrintQueueIdentity second) =>
            string.Equals(first.PrinterName, second.PrinterName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(first.ServerName, second.ServerName, StringComparison.OrdinalIgnoreCase);

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed != 0, this);
    }
}
