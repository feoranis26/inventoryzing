using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class PrintAttemptReporterTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"inventoryzing-reporter-{Guid.NewGuid():N}");

    [Fact]
    public async Task Lost_response_resends_identical_durable_snapshot_without_dispatch()
    {
        Directory.CreateDirectory(directory);
        var journal = new SqlitePrintAttemptJournal(Path.Combine(directory, "journal.db"));
        await journal.InitializeAsync(CancellationToken.None);
        var request = new PrintProbeRequest(
            Guid.NewGuid(),
            new PrintArtifact("image/png", 29, 90, ArtifactColorSpace.Srgb, [1, 2, 3]),
            new PrintProfile("mono", OutputPalette.Monochrome, 300, 300, 29, 90),
            1);
        var probe = new Probe();
        var coordinator = new PrintDispatchCoordinator(
            journal,
            new FilePrintArtifactCache(Path.Combine(directory, "cache")),
            new FixedTimeProvider());
        _ = await coordinator.DispatchAsync("printer", probe, request, CancellationToken.None);
        var sink = new Sink { FailFirstResponse = true };
        var reporter = new PrintAttemptReporter(journal, sink);

        await Assert.ThrowsAsync<IOException>(() =>
            reporter.ReportAsync(request.RequestId, CancellationToken.None).AsTask());
        Assert.Contains(request.RequestId,
            await journal.ReadPendingReportAttemptIdsAsync(CancellationToken.None));
        var replay = await reporter.ReportAsync(request.RequestId, CancellationToken.None);

        Assert.Equal(2, sink.Reports.Count);
        Assert.Equal(sink.Reports[0].Attempt, sink.Reports[1].Attempt);
        Assert.Equal(sink.Reports[0].Intent, sink.Reports[1].Intent);
        Assert.Equal(sink.Reports[0].DispatchResult, sink.Reports[1].DispatchResult);
        Assert.Equal(sink.Reports[0].QueueCorrelation, sink.Reports[1].QueueCorrelation);
        Assert.Equal(sink.Reports[0].Observations, sink.Reports[1].Observations);
        Assert.Same(replay, sink.Reports[1]);
        Assert.Equal(1, probe.SubmitCount);
        Assert.All(replay.Observations, observation =>
            Assert.NotEqual(Guid.Empty, observation.Observation.ObservationId));
        Assert.DoesNotContain(request.RequestId,
            await journal.ReadPendingReportAttemptIdsAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private sealed class Sink : IPrintAttemptReportSink
    {
        public bool FailFirstResponse { get; init; }
        public List<PrintAttemptReport> Reports { get; } = [];

        public ValueTask ReportAsync(PrintAttemptReport report, CancellationToken cancellationToken)
        {
            Reports.Add(report);
            if (FailFirstResponse && Reports.Count == 1)
            {
                throw new IOException("Synthetic lost response.");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Probe : IPrinterProbe
    {
        public int SubmitCount { get; private set; }

        public ValueTask<PrinterCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PrinterCapabilities("printer", [OutputPalette.Monochrome]));

        public ValueTask<PrintSubmission> SubmitAsync(
            PrintProbeRequest request,
            CancellationToken cancellationToken)
        {
            SubmitCount++;
            return ValueTask.FromResult(
                new PrintSubmission(PrintSubmissionStatus.Submitted, null, "accepted"));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
