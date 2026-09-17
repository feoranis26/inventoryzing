using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class DurablePrintQueueObserverTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-queue-observer-{Guid.NewGuid():N}");

    public DurablePrintQueueObserverTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Correlation_refreshes_immediately_and_persists_before_queue_state()
    {
        var (journal, attempt) = await CreateDriverAcceptedAttemptAsync();
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var job = Job(attempt.AttemptId, 41, PrintQueueJobStatus.Spooling);
        await using var session = Session(queue, [], [Snapshot(queue, [job])]);
        var observer = Observer((SqlitePrintAttemptJournal)journal);

        var correlation = await observer.CorrelateAsync(
            attempt.AttemptId,
            session,
            job.DocumentName,
            Now.AddSeconds(10),
            null,
            null,
            CancellationToken.None);

        Assert.NotNull(correlation);
        Assert.Equal(job.Key, new PrintQueueJobKey(correlation.Job.JobId, correlation.Job.SubmittedAt));
        Assert.Equal(0, session.WaitCount);
        Assert.Equal(
            PrintAttemptState.SpoolerQueued,
            (await journal.FindAsync(attempt.AttemptId, CancellationToken.None))!.Status.State);
        Assert.Equal(
            ["queue.job.correlated", "queue.job.queued"],
            (await journal.ReadObservationsAsync(attempt.AttemptId, CancellationToken.None))
                .Select(item => item.Observation.Code));
    }

    [Fact]
    public async Task Correlation_waits_past_unrelated_job_and_accepts_exact_delayed_job()
    {
        var (journal, attempt) = await CreateDriverAcceptedAttemptAsync();
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var unrelated = Job(Guid.NewGuid(), 40, PrintQueueJobStatus.Spooling);
        var expected = Job(attempt.AttemptId, 41, PrintQueueJobStatus.Printing);
        await using var session = Session(
            queue,
            [],
            [Snapshot(queue, [unrelated]), Snapshot(queue, [unrelated, expected])],
            [new PrintQueueChangeNotification(PrintQueueChange.AddJob, false, Now)]);

        var correlation = await Observer((SqlitePrintAttemptJournal)journal).CorrelateAsync(
            attempt.AttemptId,
            session,
            expected.DocumentName,
            Now.AddSeconds(10),
            null,
            null,
            CancellationToken.None);

        Assert.NotNull(correlation);
        Assert.Equal(1, session.WaitCount);
        Assert.Equal(
            PrintAttemptState.Printing,
            (await journal.FindAsync(attempt.AttemptId, CancellationToken.None))!.Status.State);
    }

    [Fact]
    public async Task Fast_add_delete_without_snapshot_evidence_becomes_unknown_not_completed()
    {
        var (journal, attempt) = await CreateDriverAcceptedAttemptAsync();
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        await using var session = Session(
            queue,
            [],
            [Snapshot(queue, []), Snapshot(queue, [])],
            [
                new PrintQueueChangeNotification(PrintQueueChange.AddJob | PrintQueueChange.DeleteJob, false, Now),
                new PrintQueueChangeNotification(PrintQueueChange.None, true, Now.AddSeconds(10)),
            ]);

        var correlation = await Observer((SqlitePrintAttemptJournal)journal).CorrelateAsync(
            attempt.AttemptId,
            session,
            $"inventoryzing-{attempt.AttemptId:N}",
            Now.AddSeconds(10),
            null,
            null,
            CancellationToken.None);

        Assert.Null(correlation);
        var current = await journal.FindAsync(attempt.AttemptId, CancellationToken.None);
        Assert.Equal(PrintAttemptState.Unknown, current!.Status.State);
        Assert.Null(current.Status.CompletionEvidence);
        Assert.Equal(
            "queue.correlation.not_found",
            Assert.Single(await journal.ReadObservationsAsync(
                attempt.AttemptId,
                CancellationToken.None)).Observation.Code);
    }

    [Fact]
    public async Task Already_printed_correlated_job_uses_spooler_evidence_after_queue_state()
    {
        var (journal, attempt) = await CreateDriverAcceptedAttemptAsync();
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var job = Job(attempt.AttemptId, 41, PrintQueueJobStatus.Printed);
        await using var session = Session(queue, [], [Snapshot(queue, [job])]);

        _ = await Observer((SqlitePrintAttemptJournal)journal).CorrelateAsync(
            attempt.AttemptId,
            session,
            job.DocumentName,
            Now.AddSeconds(10),
            null,
            null,
            CancellationToken.None);

        var current = await journal.FindAsync(attempt.AttemptId, CancellationToken.None);
        Assert.Equal(PrintAttemptState.Completed, current!.Status.State);
        Assert.Equal(PrintCompletionEvidence.SpoolerConfirmed, current.Status.CompletionEvidence);
        Assert.Contains(
            await journal.ReadTransitionsAsync(attempt.AttemptId, CancellationToken.None),
            transition => transition.Status.State == PrintAttemptState.SpoolerQueued);
    }

    [Fact]
    public async Task Observation_blocks_and_resume_is_recorded_without_resubmission()
    {
        var (journal, attempt) = await CreateDriverAcceptedAttemptAsync();
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var paused = Job(attempt.AttemptId, 41, PrintQueueJobStatus.Paused);
        await using var session = Session(queue, [], [Snapshot(queue, [paused])]);
        var observer = Observer((SqlitePrintAttemptJournal)journal);
        var correlation = await observer.CorrelateAsync(
            attempt.AttemptId,
            session,
            paused.DocumentName,
            Now.AddSeconds(10),
            null,
            null,
            CancellationToken.None);
        session.LiveJob = paused;
        session.ResumeResult = new PrintQueueResumeResult(PrintQueueResumeOutcome.Resumed);
        var commandId = Guid.NewGuid();

        var result = await observer.ResumeAsync(
            commandId,
            attempt.AttemptId,
            session,
            correlation!.Job,
            CancellationToken.None);
        var replayed = await observer.ResumeAsync(
            commandId,
            attempt.AttemptId,
            session,
            correlation.Job,
            CancellationToken.None);

        Assert.Equal(PrintQueueResumeOutcome.Resumed, result.Outcome);
        Assert.Equal(result, replayed);
        Assert.Equal(1, session.ResumeCount);
        Assert.Equal(
            PrintAttemptState.Blocked,
            (await journal.FindAsync(attempt.AttemptId, CancellationToken.None))!.Status.State);
        Assert.Equal(
            "queue.resume.resumed",
(await journal.ReadObservationsAsync(attempt.AttemptId, CancellationToken.None))[(await journal.ReadObservationsAsync(attempt.AttemptId, CancellationToken.None)).Count - 1].Observation.Code);
    }

    [Fact]
    public async Task Restart_reattaches_exact_job_with_new_generation()
    {
        var (journal, attempt) = await CreateDriverAcceptedAttemptAsync();
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var job = Job(attempt.AttemptId, 41, PrintQueueJobStatus.Paused);
        var original = new PrintQueueCorrelationRecord(
            attempt.AttemptId,
            new PrintQueueJobReference(Guid.NewGuid(), queue, job),
            Now);
        await journal.StoreQueueCorrelationAsync(original, CancellationToken.None);
        var recovery = Assert.Single(await journal.ReadRecoverableQueueAttemptsAsync(CancellationToken.None));
        await using var session = Session(queue, [], [Snapshot(queue, [job])]);
        session.LiveJob = job;

        var reattached = await Observer((SqlitePrintAttemptJournal)journal).ReattachAsync(
            recovery,
            session,
            CancellationToken.None);

        Assert.NotNull(reattached);
        Assert.Equal(session.Arm.Generation, reattached.MonitorGeneration);
        Assert.NotEqual(original.Job.MonitorGeneration, reattached.MonitorGeneration);
        Assert.Equal(
            PrintAttemptState.Blocked,
            (await journal.FindAsync(attempt.AttemptId, CancellationToken.None))!.Status.State);
    }

    [Fact]
    public async Task Missing_job_on_restart_becomes_unknown()
    {
        var (journal, attempt) = await CreateDriverAcceptedAttemptAsync();
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var job = Job(attempt.AttemptId, 41, PrintQueueJobStatus.Spooling);
        var original = new PrintQueueCorrelationRecord(
            attempt.AttemptId,
            new PrintQueueJobReference(Guid.NewGuid(), queue, job),
            Now);
        await journal.StoreQueueCorrelationAsync(original, CancellationToken.None);
        var recovery = Assert.Single(await journal.ReadRecoverableQueueAttemptsAsync(CancellationToken.None));
        await using var session = Session(queue, [], [Snapshot(queue, [])]);

        var reattached = await Observer((SqlitePrintAttemptJournal)journal).ReattachAsync(
            recovery,
            session,
            CancellationToken.None);

        Assert.Null(reattached);
        var current = await journal.FindAsync(attempt.AttemptId, CancellationToken.None);
        Assert.Equal(PrintAttemptState.Unknown, current!.Status.State);
        Assert.Null(current.Status.CompletionEvidence);
    }

    [Fact]
    public async Task Reused_printed_job_id_on_restart_is_unknown_not_completion()
    {
        var (journal, attempt) = await CreateDriverAcceptedAttemptAsync();
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var persistedJob = Job(attempt.AttemptId, 41, PrintQueueJobStatus.Spooling);
        await journal.StoreQueueCorrelationAsync(
            new PrintQueueCorrelationRecord(
                attempt.AttemptId,
                new PrintQueueJobReference(Guid.NewGuid(), queue, persistedJob),
                Now),
            CancellationToken.None);
        var recovery = Assert.Single(await journal.ReadRecoverableQueueAttemptsAsync(CancellationToken.None));
        var reusedJob = new PrintQueueJobSnapshot(
            persistedJob.JobId,
            "unrelated-document",
            persistedJob.SubmittedAt.AddMinutes(1),
            (uint)PrintQueueJobStatus.Printed);
        await using var session = Session(queue, [], [Snapshot(queue, [reusedJob])]);
        session.LiveJob = reusedJob;

        var reattached = await Observer((SqlitePrintAttemptJournal)journal).ReattachAsync(
            recovery,
            session,
            CancellationToken.None);

        Assert.Null(reattached);
        var current = await journal.FindAsync(attempt.AttemptId, CancellationToken.None);
        Assert.Equal(PrintAttemptState.Unknown, current!.Status.State);
        Assert.Null(current.Status.CompletionEvidence);
        Assert.Equal(
            "queue.job.identity_mismatch",
            Assert.Single(await journal.ReadObservationsAsync(
                attempt.AttemptId,
                CancellationToken.None)).Observation.Code);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directoryPath, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static DurablePrintQueueObserver Observer(SqlitePrintAttemptJournal journal) =>
        new(journal, new FixedTimeProvider(Now));

    private async Task<(IPrintAttemptJournal Journal, PrintAttemptRecord Attempt)>
        CreateDriverAcceptedAttemptAsync()
    {
        SqlitePrintAttemptJournal journal = new(
            Path.Combine(directoryPath, $"{Guid.NewGuid():N}.db"));
        await journal.InitializeAsync(CancellationToken.None);
        var attempt = await journal.CreateAsync(Guid.NewGuid(), Now, CancellationToken.None);
        PrintAttemptState[] states =
        [
            PrintAttemptState.Claimed,
            PrintAttemptState.Staged,
            PrintAttemptState.Prepared,
            PrintAttemptState.Dispatching,
            PrintAttemptState.DriverAccepted,
        ];
        foreach (var state in states)
        {
            var transition = await journal.TransitionAsync(
                attempt.AttemptId,
                Guid.NewGuid(),
                attempt.Status.State,
                attempt.Version,
                state,
                null,
                Now.AddMilliseconds(attempt.Version + 1),
                CancellationToken.None);
            attempt = attempt with
            {
                Status = transition.Status,
                Version = transition.ResultingVersion,
                UpdatedAt = transition.OccurredAt,
            };
        }

        return (journal, attempt);
    }

    private static FakeSession Session(
        PrintQueueIdentity queue,
        IEnumerable<PrintQueueJobSnapshot> baseline,
        IEnumerable<PrintQueueSnapshot> snapshots,
        IEnumerable<PrintQueueChangeNotification>? notifications = null) =>
        new(queue, baseline, snapshots, notifications ?? []);

    private static PrintQueueSnapshot Snapshot(
        PrintQueueIdentity queue,
        IEnumerable<PrintQueueJobSnapshot> jobs) =>
        new(queue, jobs, new PrintQueuePrinterSnapshot(0), Now);

    private static PrintQueueJobSnapshot Job(
        Guid attemptId,
        uint jobId,
        PrintQueueJobStatus status) =>
        new(
            jobId,
            $"inventoryzing-{attemptId:N}",
            Now.AddSeconds(1),
            (uint)status);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSession : IPrintQueueMonitorSession
    {
        private readonly Queue<PrintQueueSnapshot> snapshots;
        private readonly Queue<PrintQueueChangeNotification> notifications;
        private PrintQueueSnapshot? latestSnapshot;

        public FakeSession(
            PrintQueueIdentity queue,
            IEnumerable<PrintQueueJobSnapshot> baseline,
            IEnumerable<PrintQueueSnapshot> snapshots,
            IEnumerable<PrintQueueChangeNotification> notifications)
        {
            this.snapshots = new Queue<PrintQueueSnapshot>(snapshots);
            this.notifications = new Queue<PrintQueueChangeNotification>(notifications);
            var baselineSnapshot = Snapshot(queue, baseline);
            Arm = new PrintQueueMonitorArm(Guid.NewGuid(), baselineSnapshot, Now);
        }

        public PrintQueueMonitorArm Arm { get; }

        public PrintQueueJobSnapshot? LiveJob { get; set; }

        public PrintQueueResumeResult ResumeResult { get; set; } =
            new(PrintQueueResumeOutcome.NotPaused);

        public int WaitCount { get; private set; }

        public int ResumeCount { get; private set; }

        public ValueTask<PrintQueueSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshots.TryDequeue(out var snapshot))
            {
                latestSnapshot = snapshot;
            }

            return ValueTask.FromResult(latestSnapshot ?? Arm.Baseline);
        }

        public ValueTask<PrintQueueJobSnapshot?> ReadJobAsync(
            uint jobId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                LiveJob?.JobId == jobId
                    ? LiveJob
                    : latestSnapshot?.Jobs.SingleOrDefault(job => job.JobId == jobId));
        }

        public ValueTask<PrintQueueChangeNotification> WaitForChangeAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitCount++;
            return ValueTask.FromResult(notifications.TryDequeue(out var notification)
                ? notification
                : new PrintQueueChangeNotification(PrintQueueChange.None, true, Now));
        }

        public ValueTask<PrintQueueResumeResult> ResumeAsync(
            PrintQueueJobReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResumeCount++;
            return ValueTask.FromResult(ResumeResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}