using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;
using System.Threading.Channels;

namespace Inventoryzing.Agent.Tests;

public sealed class PrintStartupReconcilerTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-startup-recovery-{Guid.NewGuid():N}");

    public PrintStartupReconcilerTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Empty_journal_does_not_open_a_print_queue()
    {
        var journal = Journal();
        var monitor = new FakeQueueMonitor();

        var result = await Reconciler(journal, monitor).ReconcileAsync(CancellationToken.None);

        Assert.Equal(new PrintStartupReconciliationResult(0, 0), result);
        Assert.Equal(0, monitor.ArmCount);
    }

    [Fact]
    public async Task Startup_resolves_dispatch_barrier_before_reattaching_exact_queue_job()
    {
        var journal = Journal();
        await journal.InitializeAsync(CancellationToken.None);
        var interrupted = await DispatchingAsync(journal);
        var queued = await QueuedAsync(journal);
        var liveJob = new PrintQueueJobSnapshot(
            queued.Correlation.Job.JobId,
            queued.Correlation.Job.DocumentName,
            queued.Correlation.Job.SubmittedAt,
            (uint)PrintQueueJobStatus.Printed,
            totalPages: 1,
            pagesPrinted: 1);
        var monitor = new FakeQueueMonitor { LiveJob = liveJob };

        var result = await Reconciler(journal, monitor).ReconcileAsync(CancellationToken.None);

        Assert.Equal(new PrintStartupReconciliationResult(1, 1), result);
        Assert.Equal(
            PrintAttemptState.Unknown,
            (await journal.FindAsync(interrupted.AttemptId, CancellationToken.None))!.Status.State);
        var completed = await journal.FindAsync(queued.Attempt.AttemptId, CancellationToken.None);
        Assert.Equal(PrintAttemptState.Completed, completed!.Status.State);
        Assert.Equal(PrintCompletionEvidence.SpoolerConfirmed, completed.Status.CompletionEvidence);
        Assert.Equal(1, monitor.ArmCount);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directoryPath, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Recovered_blocked_job_remains_monitored_until_completion()
    {
        var journal = Journal();
        await journal.InitializeAsync(CancellationToken.None);
        var queued = await QueuedAsync(journal);
        var monitor = new FakeQueueMonitor
        {
            LiveJob = Job(queued, PrintQueueJobStatus.Paused),
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var recovery = Reconciler(journal, monitor).MonitorRecoveredAsync(stop.Token);
        await monitor.Waiting.Task.WaitAsync(stop.Token);
        Assert.False(recovery.IsCompleted);
        Assert.False(monitor.Disposed);
        Assert.Equal(PrintAttemptState.Blocked,
            (await journal.FindAsync(queued.Attempt.AttemptId, stop.Token))!.Status.State);

        monitor.LiveJob = Job(queued, PrintQueueJobStatus.Printed);
        monitor.Changes.Writer.TryWrite(new PrintQueueChangeNotification(0, false, Now));
        await recovery.WaitAsync(stop.Token);

        Assert.True(monitor.Disposed);
        var result = await journal.FindAsync(queued.Attempt.AttemptId, stop.Token);
        Assert.Equal(PrintAttemptState.Completed, result!.Status.State);
        Assert.Equal(PrintCompletionEvidence.SpoolerConfirmed, result.Status.CompletionEvidence);
        Assert.Equal(queued.Correlation,
            await journal.FindQueueCorrelationAsync(queued.Attempt.AttemptId, stop.Token));
    }

    [Fact]
    public async Task Shutdown_preserves_blocked_job_for_next_startup()
    {
        var journal = Journal();
        await journal.InitializeAsync(CancellationToken.None);
        var queued = await QueuedAsync(journal);
        var monitor = new FakeQueueMonitor { LiveJob = Job(queued, PrintQueueJobStatus.Paused) };
        using var stop = new CancellationTokenSource();
        var recovery = Reconciler(journal, monitor).MonitorRecoveredAsync(stop.Token);
        await monitor.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        Assert.True(monitor.Disposed);
        Assert.Equal(PrintAttemptState.Blocked,
            (await journal.FindAsync(queued.Attempt.AttemptId, CancellationToken.None))!.Status.State);

        var restarted = Journal();
        var nextMonitor = new FakeQueueMonitor { LiveJob = Job(queued, PrintQueueJobStatus.Printed) };
        await Reconciler(restarted, nextMonitor).ReconcileAsync(CancellationToken.None);
        Assert.Equal(PrintAttemptState.Completed,
            (await restarted.FindAsync(queued.Attempt.AttemptId, CancellationToken.None))!.Status.State);
    }

    [Fact]
    public async Task Recovery_reconnects_after_transient_queue_open_failure()
    {
        var journal = Journal();
        await journal.InitializeAsync(CancellationToken.None);
        var queued = await QueuedAsync(journal);
        var monitor = new FakeQueueMonitor
        {
            ArmFailuresRemaining = 1,
            LiveJob = Job(queued, PrintQueueJobStatus.Printed),
        };

        await Reconciler(journal, monitor, TimeSpan.FromMilliseconds(1))
            .MonitorRecoveredAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, monitor.ArmCount);
        Assert.Equal(PrintAttemptState.Completed,
            (await journal.FindAsync(queued.Attempt.AttemptId, CancellationToken.None))!.Status.State);
    }

    private static PrintQueueJobSnapshot Job(RecoverablePrintQueueAttempt queued, PrintQueueJobStatus status) =>
        new(queued.Correlation.Job.JobId, queued.Correlation.Job.DocumentName,
            queued.Correlation.Job.SubmittedAt, (uint)status);

    private SqlitePrintAttemptJournal Journal() =>
        new(Path.Combine(directoryPath, "printer.db"));

    private static PrintStartupReconciler Reconciler(
        SqlitePrintAttemptJournal journal,
        IPrintQueueMonitor monitor,
        TimeSpan? reconnectDelay = null) =>
        new(
            journal,
            new PrintDispatchRecovery(journal, new FixedTimeProvider(Now), Guid.NewGuid),
            monitor,
            new DurablePrintQueueObserver(
                journal,
                new FixedTimeProvider(Now),
                Guid.NewGuid),
            reconnectDelay: reconnectDelay);

    private static async Task<PrintAttemptRecord> DispatchingAsync(SqlitePrintAttemptJournal journal)
    {
        var current = await journal.CreateAsync(Guid.NewGuid(), Now, CancellationToken.None);
        await journal.StoreDispatchIntentAsync(
            new PrintDispatchIntentRecord(
                current.AttemptId,
                "brother:ql-820nwb",
                new string('a', 64),
                new string('b', 64),
                Now),
            CancellationToken.None);
        return await AdvanceAsync(journal, current, PrintAttemptState.Dispatching);
    }

    private static async Task<RecoverablePrintQueueAttempt> QueuedAsync(
        SqlitePrintAttemptJournal journal)
    {
        var current = await journal.CreateAsync(Guid.NewGuid(), Now, CancellationToken.None);
        current = await AdvanceAsync(journal, current, PrintAttemptState.SpoolerQueued);
        var correlation = new PrintQueueCorrelationRecord(
            current.AttemptId,
            new PrintQueueJobReference(
                Guid.NewGuid(),
                new PrintQueueIdentity("Brother QL-820NWB"),
                42,
                $"inventoryzing-{current.AttemptId:N}",
                Now),
            Now);
        await journal.StoreQueueCorrelationAsync(correlation, CancellationToken.None);
        return new RecoverablePrintQueueAttempt(current, correlation);
    }

    private static async Task<PrintAttemptRecord> AdvanceAsync(
        SqlitePrintAttemptJournal journal,
        PrintAttemptRecord current,
        PrintAttemptState target)
    {
        PrintAttemptState[] states =
        [
            PrintAttemptState.Claimed,
            PrintAttemptState.Staged,
            PrintAttemptState.Prepared,
            PrintAttemptState.Dispatching,
            PrintAttemptState.DriverAccepted,
            PrintAttemptState.SpoolerQueued,
        ];
        foreach (var state in states)
        {
            if (current.Status.State == target)
            {
                break;
            }
            var transition = await journal.TransitionAsync(
                current.AttemptId,
                Guid.NewGuid(),
                current.Status.State,
                current.Version,
                state,
                null,
                Now,
                CancellationToken.None);
            current = current with
            {
                Status = transition.Status,
                Version = transition.ResultingVersion,
            };
        }
        return current;
    }

    private sealed class FakeQueueMonitor : IPrintQueueMonitor
    {
        public int ArmCount { get; private set; }
        public int ArmFailuresRemaining { get; set; }
        public PrintQueueJobSnapshot? LiveJob { get; set; }
        public bool Disposed { get; set; }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<PrintQueueChangeNotification> Changes { get; } =
            Channel.CreateUnbounded<PrintQueueChangeNotification>();

        public ValueTask<IPrintQueueMonitorSession> ArmAsync(
            PrintQueueIdentity queue,
            CancellationToken cancellationToken)
        {
            ArmCount++;
            if (ArmFailuresRemaining > 0)
            {
                ArmFailuresRemaining--;
                throw new IOException("Synthetic queue open failure.");
            }
            var snapshot = new PrintQueueSnapshot(
                queue,
                LiveJob is null ? [] : [LiveJob],
                new PrintQueuePrinterSnapshot(0),
                Now);
            return ValueTask.FromResult<IPrintQueueMonitorSession>(
                new FakeSession(new PrintQueueMonitorArm(Guid.NewGuid(), snapshot, Now), snapshot, this));
        }
    }

    private sealed class FakeSession(
        PrintQueueMonitorArm arm,
        PrintQueueSnapshot snapshot,
        FakeQueueMonitor monitor) : IPrintQueueMonitorSession
    {
        public PrintQueueMonitorArm Arm { get; } = arm;

        public ValueTask<PrintQueueSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot);

        public ValueTask<PrintQueueJobSnapshot?> ReadJobAsync(
            uint jobId,
            CancellationToken cancellationToken) => ValueTask.FromResult(monitor.LiveJob);

        public ValueTask<PrintQueueChangeNotification> WaitForChangeAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            monitor.Waiting.TrySetResult();
            return monitor.Changes.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask<PrintQueueResumeResult> ResumeAsync(
            PrintQueueJobReference reference,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            monitor.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
