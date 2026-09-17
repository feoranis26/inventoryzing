using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class SqlitePrintDispatchJournalTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-dispatch-journal-{Guid.NewGuid():N}");

    public SqlitePrintDispatchJournalTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Intent_and_result_are_immutable_idempotent_and_durable()
    {
        var journal = await JournalAsync();
        var attempt = await journal.CreateAsync(Guid.NewGuid(), Now, CancellationToken.None);
        var intent = Intent(attempt.AttemptId);
        var result = new PrintDispatchResultRecord(
            attempt.AttemptId,
            new PrintSubmission(PrintSubmissionStatus.Submitted, "42", "accepted"),
            Now.AddSeconds(1));

        Assert.Equal(intent, await journal.StoreDispatchIntentAsync(intent, CancellationToken.None));
        Assert.Equal(intent, await journal.StoreDispatchIntentAsync(intent, CancellationToken.None));
        Assert.Equal(result, await journal.StoreDispatchResultAsync(result, CancellationToken.None));
        Assert.Equal(result, await journal.StoreDispatchResultAsync(result, CancellationToken.None));
        var reopened = await JournalAsync();
        Assert.Equal(intent, await reopened.FindDispatchIntentAsync(attempt.AttemptId, CancellationToken.None));
        Assert.Equal(result, await reopened.FindDispatchResultAsync(attempt.AttemptId, CancellationToken.None));
        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            reopened.StoreDispatchIntentAsync(
                intent with { PrinterId = "other-printer" },
                CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            reopened.StoreDispatchResultAsync(
                result with
                {
                    Submission = new PrintSubmission(PrintSubmissionStatus.Unknown, null, "changed"),
                },
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Recovery_returns_dispatching_and_uncorrelated_accepted_attempts()
    {
        var journal = await JournalAsync();
        var dispatching = await AdvanceAsync(journal, PrintAttemptState.Dispatching);
        var accepted = await AdvanceAsync(journal, PrintAttemptState.DriverAccepted);
        var completed = await AdvanceAsync(journal, PrintAttemptState.DriverAccepted);
        await journal.StoreDispatchResultAsync(
            new PrintDispatchResultRecord(
                accepted.AttemptId,
                new PrintSubmission(PrintSubmissionStatus.Submitted, null, "accepted"),
                Now),
            CancellationToken.None);
        await journal.StoreDispatchResultAsync(
            new PrintDispatchResultRecord(
                completed.AttemptId,
                new PrintSubmission(PrintSubmissionStatus.Submitted, null, "accepted"),
                Now),
            CancellationToken.None);
        await journal.TransitionAsync(
            completed.AttemptId,
            Guid.NewGuid(),
            PrintAttemptState.DriverAccepted,
            completed.Version,
            PrintAttemptState.Completed,
            PrintCompletionEvidence.BrotherMonitorConfirmed,
            Now,
            CancellationToken.None);

        var recoverable = await journal.ReadRecoverableDispatchAttemptsAsync(CancellationToken.None);

        // Both attempts have the same timestamp; their random IDs determine the
        // journal's tie-break order, not their insertion order.
        Assert.Equal(2, recoverable.Count);
        Assert.Null(Assert.Single(recoverable,
            item => item.Attempt.AttemptId == dispatching.AttemptId).Result);
        Assert.Equal(PrintSubmissionStatus.Submitted, Assert.Single(recoverable,
            item => item.Attempt.AttemptId == accepted.AttemptId).Result!.Submission.Status);
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

    private static async Task<PrintAttemptRecord> AdvanceAsync(
        SqlitePrintAttemptJournal journal,
        PrintAttemptState target)
    {
        var current = await journal.CreateAsync(Guid.NewGuid(), Now, CancellationToken.None);
        await journal.StoreDispatchIntentAsync(Intent(current.AttemptId), CancellationToken.None);
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

    private static PrintDispatchIntentRecord Intent(Guid attemptId) =>
        new(attemptId, "brother:ql-820nwb", new string('a', 64), new string('b', 64), Now);
}
