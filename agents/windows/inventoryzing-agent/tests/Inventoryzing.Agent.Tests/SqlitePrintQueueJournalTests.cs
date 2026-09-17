using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class SqlitePrintQueueJournalTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-print-queue-journal-{Guid.NewGuid():N}");

    public SqlitePrintQueueJournalTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Queue_correlation_is_idempotent_durable_and_immutable()
    {
        var journal = await CreateInitializedJournalAsync();
        var attempt = await journal.CreateAsync(Guid.NewGuid(), Now, CancellationToken.None);
        var correlation = Correlation(attempt.AttemptId);

        var stored = await journal.StoreQueueCorrelationAsync(correlation, CancellationToken.None);
        var replayed = await journal.StoreQueueCorrelationAsync(correlation, CancellationToken.None);
        var reopened = await CreateInitializedJournalAsync();
        var persisted = await reopened.FindQueueCorrelationAsync(
            attempt.AttemptId,
            CancellationToken.None);
        var changed = correlation with
        {
            Job = new PrintQueueJobReference(
                correlation.Job.MonitorGeneration,
                correlation.Job.Queue,
                correlation.Job.JobId,
                "inventoryzing-other",
                correlation.Job.SubmittedAt),
        };

        Assert.Equal(stored, replayed);
        Assert.Equal(stored, persisted);
        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            reopened.StoreQueueCorrelationAsync(changed, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Recovery_query_returns_only_nonterminal_correlated_attempts()
    {
        var journal = await CreateInitializedJournalAsync();
        var active = await journal.CreateAsync(Guid.NewGuid(), Now, CancellationToken.None);
        var rejected = await journal.CreateAsync(Guid.NewGuid(), Now.AddSeconds(1), CancellationToken.None);
        await journal.StoreQueueCorrelationAsync(Correlation(active.AttemptId), CancellationToken.None);
        await journal.StoreQueueCorrelationAsync(Correlation(rejected.AttemptId), CancellationToken.None);
        await journal.TransitionAsync(
            rejected.AttemptId,
            Guid.NewGuid(),
            PrintAttemptState.Created,
            0,
            PrintAttemptState.Rejected,
            null,
            Now.AddSeconds(2),
            CancellationToken.None);

        var recoverable = await journal.ReadRecoverableQueueAttemptsAsync(CancellationToken.None);

        var item = Assert.Single(recoverable);
        Assert.Equal(active.AttemptId, item.Attempt.AttemptId);
        Assert.Equal(active.AttemptId, item.Correlation.AttemptId);
    }

    [Fact]
    public async Task Observation_preserves_raw_flags_and_replay_is_idempotent()
    {
        var journal = await CreateInitializedJournalAsync();
        var attempt = await journal.CreateAsync(Guid.NewGuid(), Now, CancellationToken.None);
        var observation = new PrintObservation(
            Guid.NewGuid(),
            attempt.AttemptId,
            PrintObservationSource.WindowsSpooler,
            "queue.job.paused",
            "Paused by operator",
            uint.MaxValue,
            0x80000000,
            -42,
            Guid.NewGuid(),
            Now.AddMilliseconds(10),
            Now.AddMilliseconds(11));

        var stored = await journal.AppendObservationAsync(observation, CancellationToken.None);
        var replayed = await journal.AppendObservationAsync(observation, CancellationToken.None);
        var reopened = await CreateInitializedJournalAsync();
        var persisted = Assert.Single(await reopened.ReadObservationsAsync(
            attempt.AttemptId,
            CancellationToken.None));

        Assert.Equal(stored, replayed);
        Assert.Equal(stored, persisted);
        Assert.Equal(uint.MaxValue, persisted.Observation.RawJobStatus);
        Assert.Equal(-42, persisted.Observation.RawProviderStatus);
        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            reopened.AppendObservationAsync(
                observation with { Code = "queue.job.changed" },
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Resume_request_and_result_are_durable_and_idempotent()
    {
        var journal = await CreateInitializedJournalAsync();
        var attempt = await AdvanceToBlockedAsync(journal, Guid.NewGuid());
        var correlation = Correlation(attempt.AttemptId);
        await journal.StoreQueueCorrelationAsync(correlation, CancellationToken.None);
        var commandId = Guid.NewGuid();

        var pending = await journal.BeginResumeAsync(
            commandId,
            attempt.AttemptId,
            correlation.Job,
            Now.AddSeconds(10),
            CancellationToken.None);
        var replayedPending = await journal.BeginResumeAsync(
            commandId,
            attempt.AttemptId,
            correlation.Job,
            Now.AddSeconds(10),
            CancellationToken.None);
        var result = new PrintQueueResumeResult(PrintQueueResumeOutcome.Resumed);
        var completed = await journal.CompleteResumeAsync(
            commandId,
            result,
            Now.AddSeconds(11),
            CancellationToken.None);
        var replayedCompleted = await journal.CompleteResumeAsync(
            commandId,
            result,
            Now.AddSeconds(11),
            CancellationToken.None);
        var reopened = await CreateInitializedJournalAsync();

        Assert.Null(pending.Result);
        Assert.Equal(pending, replayedPending);
        Assert.Equal(result, completed.Result);
        Assert.Equal(completed, replayedCompleted);
        Assert.Equal(completed, await reopened.FindResumeAsync(commandId, CancellationToken.None));
        Assert.Empty(await reopened.ReadPendingResumesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Resume_requires_blocked_attempt_and_persisted_job_identity()
    {
        var journal = await CreateInitializedJournalAsync();
        var created = await journal.CreateAsync(Guid.NewGuid(), Now, CancellationToken.None);
        var correlation = Correlation(created.AttemptId);
        await journal.StoreQueueCorrelationAsync(correlation, CancellationToken.None);

        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            journal.BeginResumeAsync(
                Guid.NewGuid(),
                created.AttemptId,
                correlation.Job,
                Now.AddSeconds(1),
                CancellationToken.None).AsTask());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directoryPath, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<SqlitePrintAttemptJournal> CreateInitializedJournalAsync()
    {
        var journal = new SqlitePrintAttemptJournal(Path.Combine(directoryPath, "printer.db"));
        await journal.InitializeAsync(CancellationToken.None);
        return journal;
    }

    private static PrintQueueCorrelationRecord Correlation(Guid attemptId)
    {
        var job = new PrintQueueJobReference(
            Guid.NewGuid(),
            new PrintQueueIdentity("Brother QL-820NWB"),
            42,
            $"inventoryzing-{attemptId:N}",
            Now.AddSeconds(5));
        return new PrintQueueCorrelationRecord(attemptId, job, Now.AddSeconds(6));
    }

    private static async Task<PrintAttemptRecord> AdvanceToBlockedAsync(
        SqlitePrintAttemptJournal journal,
        Guid attemptId)
    {
        var current = await journal.CreateAsync(attemptId, Now, CancellationToken.None);
        PrintAttemptState[] states =
        [
            PrintAttemptState.Claimed,
            PrintAttemptState.Staged,
            PrintAttemptState.Prepared,
            PrintAttemptState.Dispatching,
            PrintAttemptState.DriverAccepted,
            PrintAttemptState.SpoolerQueued,
            PrintAttemptState.Blocked,
        ];
        foreach (var state in states)
        {
            var transition = await journal.TransitionAsync(
                attemptId,
                Guid.NewGuid(),
                current.Status.State,
                current.Version,
                state,
                null,
                Now.AddSeconds(current.Version + 1),
                CancellationToken.None);
            current = current with
            {
                Status = transition.Status,
                Version = transition.ResultingVersion,
                UpdatedAt = transition.OccurredAt,
            };
        }

        return current;
    }
}