using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Runtime.Printing;

internal sealed class WindowsPrintSpoolerApi : IPrintSpoolerApi
{
    private readonly IWindowsPrintSpoolerNative native;

    public WindowsPrintSpoolerApi()
        : this(new WindowsPrintSpoolerNative())
    {
    }

    internal WindowsPrintSpoolerApi(IWindowsPrintSpoolerNative native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public IPrintSpoolerReadSession OpenRead(PrintQueueIdentity queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var handle = native.OpenPrinter(BuildPrinterName(queue), WindowsPrinterAccess.Use);
        return new ReadSession(queue, native, handle);
    }

    public PrintQueueResumeResult Resume(PrintQueueJobReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        using var handle = native.OpenPrinter(
            BuildPrinterName(reference.Queue),
            WindowsPrinterAccess.Use | WindowsPrinterAccess.Administer);
        var liveJob = native.GetJob(handle, reference.JobId);
        if (liveJob is null)
        {
            return new PrintQueueResumeResult(
                PrintQueueResumeOutcome.JobMissing,
                "The correlated spooler job no longer exists.");
        }

        if (!IdentityMatches(reference, liveJob))
        {
            return new PrintQueueResumeResult(
                PrintQueueResumeOutcome.IdentityMismatch,
                "The live spooler job does not match the persisted job identity.");
        }

        if (((PrintQueueJobStatus)liveJob.RawStatus & PrintQueueJobStatus.Paused) == 0)
        {
            return new PrintQueueResumeResult(
                PrintQueueResumeOutcome.AlreadyResumed,
                "The correlated spooler job is no longer paused.");
        }

        return native.Resume(handle, reference.JobId) switch
        {
            WindowsSpoolerResumeOutcome.Succeeded =>
                new PrintQueueResumeResult(PrintQueueResumeOutcome.Resumed),
            WindowsSpoolerResumeOutcome.JobMissing =>
                new PrintQueueResumeResult(
                    PrintQueueResumeOutcome.JobMissing,
                    "The correlated spooler job disappeared before Resume was applied."),
            _ => throw new InvalidOperationException("Unknown native Resume outcome."),
        };
    }

    private static bool IdentityMatches(
        PrintQueueJobReference reference,
        WindowsSpoolerJob job) =>
        reference.JobId == job.JobId &&
        reference.SubmittedAt == job.SubmittedAt.ToUniversalTime() &&
        string.Equals(reference.DocumentName, job.DocumentName, StringComparison.Ordinal);

    private static string BuildPrinterName(PrintQueueIdentity queue)
    {
        if (queue.ServerName is null || queue.PrinterName.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return queue.PrinterName;
        }

        var server = queue.ServerName.TrimEnd('\\');
        if (!server.StartsWith("\\\\", StringComparison.Ordinal))
        {
            server = $"\\\\{server.TrimStart('\\')}";
        }

        return $"{server}\\{queue.PrinterName.TrimStart('\\')}";
    }

    private static PrintQueueJobSnapshot Map(WindowsSpoolerJob job) =>
        new(
            job.JobId,
            job.DocumentName,
            job.SubmittedAt,
            job.RawStatus,
            job.StatusText,
            job.OwnerName,
            job.MachineName,
            job.TotalPages,
            job.PagesPrinted);

    private sealed class ReadSession(
        PrintQueueIdentity queue,
        IWindowsPrintSpoolerNative native,
        IWindowsPrinterHandle handle) : IPrintSpoolerReadSession
    {
        private int disposed;

        public IPrintSpoolerChangeSubscription Subscribe()
        {
            ThrowIfDisposed();
            return new ChangeSubscription(native, native.Subscribe(handle));
        }

        public PrintQueueSnapshot Snapshot(DateTimeOffset observedAt)
        {
            ThrowIfDisposed();
            var snapshot = native.Snapshot(handle);
            return new PrintQueueSnapshot(
                queue,
                snapshot.Jobs.Select(Map),
                new PrintQueuePrinterSnapshot(snapshot.RawPrinterStatus),
                observedAt);
        }

        public PrintQueueJobSnapshot? ReadJob(uint jobId)
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfZero(jobId);
            var job = native.GetJob(handle, jobId);
            return job is null ? null : Map(job);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                handle.Dispose();
            }
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed != 0, this);
    }

    private sealed class ChangeSubscription(
        IWindowsPrintSpoolerNative native,
        IWindowsPrinterChangeHandle handle) : IPrintSpoolerChangeSubscription
    {
        private const int MaximumRefreshAttempts = 3;
        private int disposed;

        public ValueTask<PrintQueueChangeNotification> WaitAsync(
            TimeSpan timeout,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return new ValueTask<PrintQueueChangeNotification>(Task.Run(
                () => Wait(timeout, timeProvider, cancellationToken),
                cancellationToken));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                handle.Dispose();
            }
        }

        private PrintQueueChangeNotification Wait(
            TimeSpan timeout,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var outcome = native.WaitForChange(handle, timeout, cancellationToken);
            var observedAt = timeProvider.GetUtcNow();
            if (outcome == WindowsSpoolerWaitOutcome.TimedOut)
            {
                return new PrintQueueChangeNotification(
                    PrintQueueChange.None,
                    TimedOut: true,
                    observedAt);
            }

            var notification = native.NextNotification(handle, refresh: false);
            var rawChanges = notification.RawChanges;
            for (var attempt = 0; notification.Discarded && attempt < MaximumRefreshAttempts; attempt++)
            {
                notification = native.NextNotification(handle, refresh: true);
                rawChanges |= notification.RawChanges;
            }

            if (notification.Discarded)
            {
                throw new InvalidOperationException(
                    "Winspool continued discarding notification data after refresh.");
            }

            return new PrintQueueChangeNotification(
                (PrintQueueChange)rawChanges,
                TimedOut: false,
                observedAt);
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed != 0, this);
    }
}