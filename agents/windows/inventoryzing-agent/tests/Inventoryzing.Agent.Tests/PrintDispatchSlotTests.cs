using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class PrintDispatchSlotTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-dispatch-slot-{Guid.NewGuid():N}");

    public PrintDispatchSlotTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Slot_is_idempotent_blocks_other_attempt_and_releases_on_terminal_transition()
    {
        var journal = await JournalAsync();
        var first = await DispatchingAsync(journal);
        var second = await DispatchingAsync(journal);
        var slot = new PrintDispatchSlotRecord("brother:ql-820nwb", first.AttemptId, Now);

        Assert.Equal(slot, await journal.AcquireDispatchSlotAsync(slot, CancellationToken.None));
        Assert.Equal(slot, await journal.AcquireDispatchSlotAsync(slot, CancellationToken.None));
        var busy = await Assert.ThrowsAsync<PrinterDispatchBusyException>(() =>
            journal.AcquireDispatchSlotAsync(
                new PrintDispatchSlotRecord("brother:ql-820nwb", second.AttemptId, Now),
                CancellationToken.None).AsTask());
        Assert.Equal(first.AttemptId, busy.ActiveAttemptId);

        await journal.TransitionAsync(
            first.AttemptId,
            Guid.NewGuid(),
            first.Status.State,
            first.Version,
            PrintAttemptState.Unknown,
            null,
            Now,
            CancellationToken.None);

        Assert.Null(await journal.FindDispatchSlotAsync("brother:ql-820nwb", CancellationToken.None));
        var acquired = await journal.AcquireDispatchSlotAsync(
            new PrintDispatchSlotRecord("brother:ql-820nwb", second.AttemptId, Now),
            CancellationToken.None);
        Assert.Equal(second.AttemptId, acquired.AttemptId);
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

    private static async Task<PrintAttemptRecord> DispatchingAsync(SqlitePrintAttemptJournal journal)
    {
        var current = await journal.CreateAsync(Guid.NewGuid(), Now, CancellationToken.None);
        await journal.StoreDispatchIntentAsync(
            new PrintDispatchIntentRecord(
                current.AttemptId,
                $"printer:{current.AttemptId:N}",
                new string('a', 64),
                new string('b', 64),
                Now),
            CancellationToken.None);
        foreach (var state in new[]
        {
            PrintAttemptState.Claimed,
            PrintAttemptState.Staged,
            PrintAttemptState.Prepared,
            PrintAttemptState.Dispatching,
        })
        {
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
}