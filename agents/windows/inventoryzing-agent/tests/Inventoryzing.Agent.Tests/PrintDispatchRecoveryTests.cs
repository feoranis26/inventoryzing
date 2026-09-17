using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class PrintDispatchRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-dispatch-recovery-{Guid.NewGuid():N}");

    public PrintDispatchRecoveryTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Interrupted_dispatch_without_result_becomes_unknown_without_provider()
    {
        var journal = await JournalAsync();
        var attempt = await AdvanceAsync(journal, PrintAttemptState.Dispatching);

        var recovered = Assert.Single(await Recovery(journal).ReconcileAsync(CancellationToken.None));

        Assert.Equal(PrintAttemptState.Unknown, recovered.Status.State);
        Assert.Equal(
            PrintSubmissionStatus.Unknown,
            (await journal.FindDispatchResultAsync(attempt.AttemptId, CancellationToken.None))!
                .Submission.Status);
        Assert.Equal(
            "dispatch.recovery.unknown",
            Assert.Single(await journal.ReadObservationsAsync(
                attempt.AttemptId,
                CancellationToken.None)).Observation.Code);
    }

    [Fact]
    public async Task Durable_rejection_is_replayed_but_submitted_without_correlation_is_unknown()
    {
        var journal = await JournalAsync();
        var rejected = await AdvanceAsync(journal, PrintAttemptState.Dispatching);
        await journal.StoreDispatchResultAsync(
            Result(rejected.AttemptId, PrintSubmissionStatus.Rejected),
            CancellationToken.None);
        var accepted = await AdvanceAsync(journal, PrintAttemptState.DriverAccepted);
        var acceptedResult = Result(accepted.AttemptId, PrintSubmissionStatus.Submitted);
        await journal.StoreDispatchResultAsync(acceptedResult, CancellationToken.None);

        var recovered = await Recovery(journal).ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, recovered.Count);
        Assert.Equal(
            PrintAttemptState.Rejected,
            (await journal.FindAsync(rejected.AttemptId, CancellationToken.None))!.Status.State);
        Assert.Equal(
            PrintAttemptState.Unknown,
            (await journal.FindAsync(accepted.AttemptId, CancellationToken.None))!.Status.State);
        Assert.Equal(
            acceptedResult,
            await journal.FindDispatchResultAsync(accepted.AttemptId, CancellationToken.None));
    }

    [Fact]
    public async Task Recovery_replay_has_no_remaining_work()
    {
        var journal = await JournalAsync();
        _ = await AdvanceAsync(journal, PrintAttemptState.Dispatching);
        var recovery = Recovery(journal);

        Assert.Single(await recovery.ReconcileAsync(CancellationToken.None));
        Assert.Empty(await recovery.ReconcileAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directoryPath, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<SqlitePrintAttemptJournal> JournalAsync()
    {
        var journal = new SqlitePrintAttemptJournal(Path.Combine(directoryPath, "printer.db"));
        await journal.InitializeAsync(CancellationToken.None);
        return journal;
    }

    private static PrintDispatchRecovery Recovery(SqlitePrintAttemptJournal journal) =>
        new(journal, new FixedTimeProvider(Now.AddMinutes(1)), Guid.NewGuid);

    private static async Task<PrintAttemptRecord> AdvanceAsync(
        SqlitePrintAttemptJournal journal,
        PrintAttemptState target)
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

    private static PrintDispatchResultRecord Result(
        Guid attemptId,
        PrintSubmissionStatus status) =>
        new(attemptId, new PrintSubmission(status, null, status.ToString()), Now);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}