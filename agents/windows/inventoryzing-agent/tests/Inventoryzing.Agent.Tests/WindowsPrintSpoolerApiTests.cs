using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;

namespace Inventoryzing.Agent.Tests;

public sealed class WindowsPrintSpoolerApiTests
{
    private static readonly DateTimeOffset SubmittedAt =
        new(2026, 9, 15, 12, 0, 0, 123, TimeSpan.Zero);

    [Fact]
    public void Snapshot_preserves_native_job_and_printer_facts()
    {
        var native = new FakeNative
        {
            QueueSnapshot = new WindowsSpoolerQueueSnapshot(
                0x80000080,
                [Job(17, 0x80000001, "Paused by operator")]),
        };
        var api = new WindowsPrintSpoolerApi(native);
        var queue = new PrintQueueIdentity("Brother QL-820NWB", "print-server");

        using var session = api.OpenRead(queue);
        var snapshot = session.Snapshot(SubmittedAt.AddSeconds(1));

        Assert.Equal("\\\\print-server\\Brother QL-820NWB", native.OpenRequests[0].PrinterName);
        Assert.Equal(WindowsPrinterAccess.Use, native.OpenRequests[0].Access);
        Assert.Equal(0x80000080U, snapshot.Printer.RawStatus);
        var job = Assert.Single(snapshot.Jobs);
        Assert.Equal(17U, job.JobId);
        Assert.Equal("inventoryzing-attempt", job.DocumentName);
        Assert.Equal(SubmittedAt, job.SubmittedAt);
        Assert.Equal(0x80000001U, job.RawStatus);
        Assert.Equal("Paused by operator", job.StatusText);
        Assert.Equal("inventory-agent", job.OwnerName);
        Assert.Equal("WORKSTATION", job.MachineName);
        Assert.Equal(2U, job.TotalPages);
        Assert.Equal(1U, job.PagesPrinted);
        Assert.Equal(job, session.ReadJob(17));
    }

    [Fact]
    public async Task Discarded_notification_is_refreshed_before_returning_change()
    {
        var native = new FakeNative();
        native.Notifications.Enqueue(new WindowsSpoolerNotification(0x100, Discarded: true));
        native.Notifications.Enqueue(new WindowsSpoolerNotification(0x200, Discarded: false));
        var api = new WindowsPrintSpoolerApi(native);
        using var session = api.OpenRead(new PrintQueueIdentity("Brother QL-820NWB"));
        using var subscription = session.Subscribe();

        var notification = await subscription.WaitAsync(
            TimeSpan.FromSeconds(1),
            new FixedTimeProvider(SubmittedAt),
            CancellationToken.None);

        Assert.False(notification.TimedOut);
        Assert.Equal((PrintQueueChange)0x300, notification.Changes);
        Assert.Equal([false, true], native.RefreshRequests);
    }

    [Fact]
    public async Task Timed_out_wait_does_not_acknowledge_notification()
    {
        var native = new FakeNative { WaitOutcome = WindowsSpoolerWaitOutcome.TimedOut };
        var api = new WindowsPrintSpoolerApi(native);
        using var session = api.OpenRead(new PrintQueueIdentity("Brother QL-820NWB"));
        using var subscription = session.Subscribe();

        var notification = await subscription.WaitAsync(
            TimeSpan.Zero,
            new FixedTimeProvider(SubmittedAt),
            CancellationToken.None);

        Assert.True(notification.TimedOut);
        Assert.Empty(native.RefreshRequests);
    }

    [Fact]
    public void Resume_requires_exact_live_identity_and_paused_status()
    {
        var native = new FakeNative { LiveJob = Job(17, (uint)PrintQueueJobStatus.Paused) };
        var api = new WindowsPrintSpoolerApi(native);
        var reference = Reference(native.LiveJob);

        var result = api.Resume(reference);

        Assert.Equal(PrintQueueResumeOutcome.Resumed, result.Outcome);
        Assert.Equal(17U, native.ResumedJobId);
        Assert.Equal(
            WindowsPrinterAccess.Use | WindowsPrinterAccess.Administer,
            native.OpenRequests[0].Access);
    }

    [Fact]
    public void Resume_does_not_touch_reused_or_already_resumed_job()
    {
        var reusedNative = new FakeNative
        {
            LiveJob = Job(17, (uint)PrintQueueJobStatus.Paused) with
            {
                SubmittedAt = SubmittedAt.AddSeconds(1),
            },
        };
        var reusedApi = new WindowsPrintSpoolerApi(reusedNative);
        var reference = Reference(Job(17, (uint)PrintQueueJobStatus.Paused));

        var reused = reusedApi.Resume(reference);

        Assert.Equal(PrintQueueResumeOutcome.IdentityMismatch, reused.Outcome);
        Assert.Null(reusedNative.ResumedJobId);

        var runningNative = new FakeNative { LiveJob = Job(17, (uint)PrintQueueJobStatus.Printing) };
        var runningApi = new WindowsPrintSpoolerApi(runningNative);

        var running = runningApi.Resume(Reference(runningNative.LiveJob));

        Assert.Equal(PrintQueueResumeOutcome.AlreadyResumed, running.Outcome);
        Assert.Null(runningNative.ResumedJobId);
    }

    [Fact]
    public void Resume_reports_job_that_disappears_during_command()
    {
        var native = new FakeNative
        {
            LiveJob = Job(17, (uint)PrintQueueJobStatus.Paused),
            ResumeOutcome = WindowsSpoolerResumeOutcome.JobMissing,
        };
        var api = new WindowsPrintSpoolerApi(native);

        var result = api.Resume(Reference(native.LiveJob));

        Assert.Equal(PrintQueueResumeOutcome.JobMissing, result.Outcome);
    }

    private static PrintQueueJobReference Reference(WindowsSpoolerJob job) =>
        new(
            Guid.NewGuid(),
            new PrintQueueIdentity("Brother QL-820NWB"),
            new PrintQueueJobSnapshot(
                job.JobId,
                job.DocumentName,
                job.SubmittedAt,
                job.RawStatus));

    private static WindowsSpoolerJob Job(uint jobId, uint status, string? statusText = null) =>
        new(
            jobId,
            "inventoryzing-attempt",
            SubmittedAt,
            status,
            statusText,
            "inventory-agent",
            "WORKSTATION",
            2,
            1);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeNative : IWindowsPrintSpoolerNative
    {
        public List<(string PrinterName, WindowsPrinterAccess Access)> OpenRequests { get; } = [];

        public Queue<WindowsSpoolerNotification> Notifications { get; } = [];

        public List<bool> RefreshRequests { get; } = [];

        public WindowsSpoolerQueueSnapshot QueueSnapshot { get; init; } =
            new(0, []);

        public WindowsSpoolerJob? LiveJob { get; init; }

        public WindowsSpoolerWaitOutcome WaitOutcome { get; init; } =
            WindowsSpoolerWaitOutcome.Signaled;

        public WindowsSpoolerResumeOutcome ResumeOutcome { get; init; } =
            WindowsSpoolerResumeOutcome.Succeeded;

        public uint? ResumedJobId { get; private set; }

        public IWindowsPrinterHandle OpenPrinter(
            string printerName,
            WindowsPrinterAccess desiredAccess)
        {
            OpenRequests.Add((printerName, desiredAccess));
            return new FakePrinterHandle();
        }

        public IWindowsPrinterChangeHandle Subscribe(IWindowsPrinterHandle printerHandle) =>
            new FakeChangeHandle();

        public WindowsSpoolerQueueSnapshot Snapshot(IWindowsPrinterHandle printerHandle) =>
            QueueSnapshot;

        public WindowsSpoolerJob? GetJob(IWindowsPrinterHandle printerHandle, uint jobId) =>
            LiveJob ?? QueueSnapshot.Jobs.SingleOrDefault(job => job.JobId == jobId);

        public WindowsSpoolerWaitOutcome WaitForChange(
            IWindowsPrinterChangeHandle changeHandle,
            TimeSpan timeout,
            CancellationToken cancellationToken) => WaitOutcome;

        public WindowsSpoolerNotification NextNotification(
            IWindowsPrinterChangeHandle changeHandle,
            bool refresh)
        {
            RefreshRequests.Add(refresh);
            return Notifications.Dequeue();
        }

        public WindowsSpoolerResumeOutcome Resume(
            IWindowsPrinterHandle printerHandle,
            uint jobId)
        {
            ResumedJobId = jobId;
            return ResumeOutcome;
        }
    }

    private sealed class FakePrinterHandle : IWindowsPrinterHandle
    {
        public void Dispose()
        {
        }
    }

    private sealed class FakeChangeHandle : IWindowsPrinterChangeHandle
    {
        public void Dispose()
        {
        }
    }
}