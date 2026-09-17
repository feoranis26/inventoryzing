using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;

namespace Inventoryzing.Agent.Tests;

public sealed class WindowsPrintQueueMonitorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Arm_subscribes_before_taking_baseline_snapshot()
    {
        List<string> calls = [];
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var api = new FakeSpoolerApi(queue, calls);
        var monitor = new WindowsPrintQueueMonitor(api, new FixedTimeProvider(Now));

        await using var session = await monitor.ArmAsync(queue, CancellationToken.None);

        Assert.Equal(["OpenRead", "Subscribe", "Snapshot"], calls);
        Assert.Equal(queue, session.Arm.Queue);
        Assert.Equal(Now, session.Arm.ArmedAt);
        Assert.Equal(api.ReadSession.SnapshotValue.Jobs.Select(job => job.Key), session.Arm.BaselineJobs);
    }

    [Fact]
    public async Task Change_wait_and_refresh_use_the_armed_session()
    {
        List<string> calls = [];
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var api = new FakeSpoolerApi(queue, calls);
        var monitor = new WindowsPrintQueueMonitor(api, new FixedTimeProvider(Now));
        await using var session = await monitor.ArmAsync(queue, CancellationToken.None);
        api.Subscription.Notification = new PrintQueueChangeNotification(
            PrintQueueChange.AddJob,
            TimedOut: false,
            Now.AddMilliseconds(10));

        var change = await session.WaitForChangeAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var snapshot = await session.RefreshAsync(CancellationToken.None);
        var job = await session.ReadJobAsync(7, CancellationToken.None);

        Assert.Equal(PrintQueueChange.AddJob, change.Changes);
        Assert.Equal(api.ReadSession.SnapshotValue, snapshot);
        Assert.Equal(api.ReadSession.SnapshotValue.Jobs[0], job);
        Assert.Equal(
            ["OpenRead", "Subscribe", "Snapshot", "Wait", "Snapshot", "ReadJob"],
            calls);
    }

    [Fact]
    public async Task Resume_rejects_wrong_generation_before_requesting_admin_operation()
    {
        List<string> calls = [];
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var api = new FakeSpoolerApi(queue, calls);
        var monitor = new WindowsPrintQueueMonitor(api, new FixedTimeProvider(Now));
        await using var session = await monitor.ArmAsync(queue, CancellationToken.None);
        var reference = new PrintQueueJobReference(
            Guid.NewGuid(),
            queue,
            api.ReadSession.SnapshotValue.Jobs[0]);

        var result = await session.ResumeAsync(reference, CancellationToken.None);

        Assert.Equal(PrintQueueResumeOutcome.IdentityMismatch, result.Outcome);
        Assert.DoesNotContain("Resume", calls);
    }

    [Fact]
    public async Task Resume_for_armed_job_uses_separate_spooler_operation()
    {
        List<string> calls = [];
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var api = new FakeSpoolerApi(queue, calls)
        {
            ResumeResult = new PrintQueueResumeResult(PrintQueueResumeOutcome.Resumed),
        };
        var monitor = new WindowsPrintQueueMonitor(api, new FixedTimeProvider(Now));
        await using var session = await monitor.ArmAsync(queue, CancellationToken.None);
        var reference = new PrintQueueJobReference(
            session.Arm.Generation,
            queue,
            api.ReadSession.SnapshotValue.Jobs[0]);

        var result = await session.ResumeAsync(reference, CancellationToken.None);

        Assert.Equal(PrintQueueResumeOutcome.Resumed, result.Outcome);
        Assert.Equal("Resume", calls[^1]);
        Assert.Equal(reference, api.ResumedReference);
    }

    [Fact]
    public async Task Arm_failure_disposes_subscription_and_read_handle()
    {
        List<string> calls = [];
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var api = new FakeSpoolerApi(queue, calls);
        api.ReadSession.SnapshotException = new InvalidOperationException("Synthetic snapshot failure");
        var monitor = new WindowsPrintQueueMonitor(api, new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            monitor.ArmAsync(queue, CancellationToken.None).AsTask());

        Assert.Equal(
            ["OpenRead", "Subscribe", "Snapshot", "DisposeSubscription", "DisposeRead"],
            calls);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSpoolerApi : IPrintSpoolerApi
    {
        private readonly List<string> calls;

        public FakeSpoolerApi(PrintQueueIdentity queue, List<string> calls)
        {
            this.calls = calls;
            Subscription = new FakeSubscription(calls);
            ReadSession = new FakeReadSession(queue, calls, Subscription);
        }

        public FakeReadSession ReadSession { get; }

        public FakeSubscription Subscription { get; }

        public PrintQueueResumeResult ResumeResult { get; init; } =
            new(PrintQueueResumeOutcome.NotPaused);

        public PrintQueueJobReference? ResumedReference { get; private set; }

        public IPrintSpoolerReadSession OpenRead(PrintQueueIdentity queue)
        {
            calls.Add("OpenRead");
            Assert.Equal(ReadSession.SnapshotValue.Queue, queue);
            return ReadSession;
        }

        public PrintQueueResumeResult Resume(PrintQueueJobReference reference)
        {
            calls.Add("Resume");
            ResumedReference = reference;
            return ResumeResult;
        }
    }

    private sealed class FakeReadSession(
        PrintQueueIdentity queue,
        List<string> calls,
WindowsPrintQueueMonitorTests.FakeSubscription subscription) : IPrintSpoolerReadSession
    {
        private readonly List<string> calls = calls;
        private readonly FakeSubscription subscription = subscription;

        public PrintQueueSnapshot SnapshotValue { get; } = new PrintQueueSnapshot(
                queue,
                [new PrintQueueJobSnapshot(7, "existing", Now.AddSeconds(-1), 0)],
                new PrintQueuePrinterSnapshot(0),
                Now);

        public Exception? SnapshotException { get; set; }

        public IPrintSpoolerChangeSubscription Subscribe()
        {
            calls.Add("Subscribe");
            return subscription;
        }

        public PrintQueueSnapshot Snapshot(DateTimeOffset observedAt)
        {
            calls.Add("Snapshot");
            if (SnapshotException is not null)
            {
                throw SnapshotException;
            }

            return SnapshotValue;
        }

        public PrintQueueJobSnapshot? ReadJob(uint jobId)
        {
            calls.Add("ReadJob");
            return SnapshotValue.Jobs.SingleOrDefault(job => job.JobId == jobId);
        }

        public void Dispose() => calls.Add("DisposeRead");
    }

    private sealed class FakeSubscription(List<string> calls) : IPrintSpoolerChangeSubscription
    {
        public PrintQueueChangeNotification Notification { get; set; } =
            new(PrintQueueChange.None, TimedOut: true, Now);

        public ValueTask<PrintQueueChangeNotification> WaitAsync(
            TimeSpan timeout,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            calls.Add("Wait");
            return ValueTask.FromResult(Notification);
        }

        public void Dispose() => calls.Add("DisposeSubscription");
    }
}