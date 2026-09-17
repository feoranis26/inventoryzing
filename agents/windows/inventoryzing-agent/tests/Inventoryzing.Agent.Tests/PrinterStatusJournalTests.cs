using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class PrinterStatusJournalTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-printer-status-{Guid.NewGuid():N}");

    public PrinterStatusJournalTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Status_and_profile_media_are_idempotent_and_durable()
    {
        var journal = await JournalAsync();
        var observation = Observation();

        var stored = await journal.AppendPrinterStatusAsync(
            observation,
            Now.AddSeconds(1),
            CancellationToken.None);
        var replayed = await journal.AppendPrinterStatusAsync(
            observation,
            Now.AddSeconds(1),
            CancellationToken.None);
        var reopened = await JournalAsync();
        var persisted = Assert.Single(await reopened.ReadPrinterStatusesAsync(
            observation.DeviceId,
            CancellationToken.None));

        Assert.Equal(stored.Sequence, replayed.Sequence);
        Assert.Equal(stored.Sequence, persisted.Sequence);
        Assert.Equal(observation.ObservationId, persisted.Observation.ObservationId);
        Assert.Equal("62mm x 29mm", persisted.Observation.LoadedMediaName);
        Assert.Equal(-73, persisted.Observation.RawProviderStatus);
        Assert.Equal(PrinterMonitorCompletionCapability.Unknown, persisted.Observation.CompletionCapability);
        Assert.Equal(observation.Profiles, persisted.Observation.Profiles);
    }

    [Fact]
    public async Task Changed_status_replay_conflicts()
    {
        var journal = await JournalAsync();
        var observation = Observation();
        await journal.AppendPrinterStatusAsync(observation, Now, CancellationToken.None);
        var changed = new PrinterStatusObservation(
            observation.ObservationId,
            observation.DeviceId,
            observation.PrinterName,
            observation.IsSupported,
            isOnline: false,
            observation.LoadedMediaName,
            observation.LoadedMediaId,
            observation.RawProviderStatus,
            observation.ErrorDetail,
            observation.CompletionCapability,
            observation.Profiles,
            observation.ObservedAt);

        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            journal.AppendPrinterStatusAsync(changed, Now, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Durable_observer_records_probe_result_and_receive_time()
    {
        var journal = await JournalAsync();
        var observation = Observation();
        var observer = new DurablePrinterStatusObserver(
            journal,
            new FixedTimeProvider(Now.AddSeconds(5)));

        var stored = await observer.ObserveAsync(
            new FakeStatusProbe(observation),
            CancellationToken.None);

        Assert.Equal(observation.ObservationId, stored.Observation.ObservationId);
        Assert.Equal(Now.AddSeconds(5), stored.ReceivedAt);
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

    private static PrinterStatusObservation Observation() =>
        new(
            Guid.NewGuid(),
            "brother:ql-820nwb",
            "Brother QL-820NWB",
            true,
            true,
            "62mm x 29mm",
            42,
            -73,
            "synthetic",
            PrinterMonitorCompletionCapability.Unknown,
            [new PrinterProfileStatus(
                "brother-62x29",
                PrinterProfileReadiness.Ready,
                "name '62mm x 29mm'",
                "62mm x 29mm",
                42,
                -73,
                null)],
            Now);

    private sealed class FakeStatusProbe(PrinterStatusObservation observation) : IPrinterStatusProbe
    {
        public ValueTask<PrinterStatusObservation> ObserveStatusAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(observation);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}