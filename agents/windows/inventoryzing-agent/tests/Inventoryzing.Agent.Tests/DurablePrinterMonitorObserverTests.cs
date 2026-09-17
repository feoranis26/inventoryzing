using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class DurablePrinterMonitorObserverTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-printer-monitor-{Guid.NewGuid():N}");

    public DurablePrinterMonitorObserverTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Verified_page_completion_requires_all_unique_expected_pages()
    {
        var (journal, attempt) = await CreateDriverAcceptedAttemptAsync();
        var observer = new DurablePrinterMonitorObserver(
            journal,
            new FixedTimeProvider(Now.AddSeconds(10)));
        var first = Observation(PrinterMonitorEventKind.PagePrinted, 0, "page 1");

        var afterFirst = await observer.ObserveAsync(
            attempt.AttemptId,
            first,
            PrinterMonitorCompletionCapability.PageCompletion,
            2,
            CancellationToken.None);
        var afterReplay = await observer.ObserveAsync(
            attempt.AttemptId,
            first,
            PrinterMonitorCompletionCapability.PageCompletion,
            2,
            CancellationToken.None);
        var afterSecond = await observer.ObserveAsync(
            attempt.AttemptId,
            Observation(PrinterMonitorEventKind.PagePrinted, 0, "page 2"),
            PrinterMonitorCompletionCapability.PageCompletion,
            2,
            CancellationToken.None);

        Assert.Equal(PrintAttemptState.Printing, afterFirst.Status.State);
        Assert.Equal(afterFirst, afterReplay);
        Assert.Equal(PrintAttemptState.Completed, afterSecond.Status.State);
        Assert.Equal(
            PrintCompletionEvidence.BrotherMonitorConfirmed,
            afterSecond.Status.CompletionEvidence);
        Assert.Equal(2, (await journal.ReadObservationsAsync(
            attempt.AttemptId,
            CancellationToken.None)).Count);
    }

    [Theory]
    [InlineData(PrinterMonitorCompletionCapability.Unknown)]
    [InlineData(PrinterMonitorCompletionCapability.Unsupported)]
    public async Task Unverified_page_event_never_completes(
        PrinterMonitorCompletionCapability capability)
    {
        var (journal, attempt) = await CreateDriverAcceptedAttemptAsync();
        var result = await new DurablePrinterMonitorObserver(journal).ObserveAsync(
            attempt.AttemptId,
            Observation(PrinterMonitorEventKind.PagePrinted, 0, "page 1"),
            capability,
            1,
            CancellationToken.None);

        Assert.Equal(PrintAttemptState.Printing, result.Status.State);
        Assert.Null(result.Status.CompletionEvidence);
    }

    [Theory]
    [InlineData(PrinterMonitorEventKind.Offline, PrintAttemptState.Blocked)]
    [InlineData(PrinterMonitorEventKind.Paused, PrintAttemptState.Blocked)]
    [InlineData(PrinterMonitorEventKind.Error, PrintAttemptState.Blocked)]
    [InlineData(PrinterMonitorEventKind.PrinterNotFound, PrintAttemptState.Blocked)]
    [InlineData(PrinterMonitorEventKind.Deleted, PrintAttemptState.Failed)]
    [InlineData(PrinterMonitorEventKind.Unknown, PrintAttemptState.DriverAccepted)]
    public async Task Monitor_status_is_persisted_before_conservative_reduction(
        PrinterMonitorEventKind kind,
        PrintAttemptState expectedState)
    {
        var (journal, attempt) = await CreateDriverAcceptedAttemptAsync();
        var observation = Observation(kind, -73, "synthetic status");

        var result = await new DurablePrinterMonitorObserver(journal).ObserveAsync(
            attempt.AttemptId,
            observation,
            PrinterMonitorCompletionCapability.Unknown,
            1,
            CancellationToken.None);
        var persisted = Assert.Single(await journal.ReadObservationsAsync(
            attempt.AttemptId,
            CancellationToken.None));

        Assert.Equal(expectedState, result.Status.State);
        Assert.Equal(observation.ObservationId, persisted.Observation.ObservationId);
        Assert.Equal(-73, persisted.Observation.RawProviderStatus);
        Assert.Equal("synthetic status", persisted.Observation.Detail);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directoryPath, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<(SqlitePrintAttemptJournal Journal, PrintAttemptRecord Attempt)>
        CreateDriverAcceptedAttemptAsync()
    {
        var journal = new SqlitePrintAttemptJournal(Path.Combine(directoryPath, "printer.db"));
        await journal.InitializeAsync(CancellationToken.None);
        var current = await journal.CreateAsync(Guid.NewGuid(), Now, CancellationToken.None);
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

        return (journal, current);
    }

    private static PrinterMonitorObservation Observation(
        PrinterMonitorEventKind kind,
        int rawStatus,
        string? value) =>
        new(Guid.NewGuid(), kind, rawStatus, value, Now.AddSeconds(1));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}