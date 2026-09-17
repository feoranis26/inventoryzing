using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class PrintDispatchControlTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"inventoryzing-dispatch-control-{Guid.NewGuid():N}");

    [Fact]
    public async Task Hold_and_release_are_durable_idempotent_and_gate_dispatch()
    {
        Directory.CreateDirectory(directory);
        var journal = new SqlitePrintAttemptJournal(Path.Combine(directory, "journal.db"));
        await journal.InitializeAsync(CancellationToken.None);
        var hold = new PrintDispatchControlRecord(
            Guid.NewGuid(), "printer", true, "Media inspection.", "operator", Now);
        Assert.Equal(hold, await journal.SetDispatchControlAsync(hold, CancellationToken.None));
        Assert.Equal(hold, await journal.SetDispatchControlAsync(hold, CancellationToken.None));

        var request = Request();
        var probe = new Probe();
        var coordinator = new PrintDispatchCoordinator(
            journal, new FilePrintArtifactCache(Path.Combine(directory, "cache")));
        await Assert.ThrowsAsync<PrinterDispatchHeldException>(() => coordinator.DispatchAsync(
            "printer", probe, request, CancellationToken.None).AsTask());
        Assert.Equal(0, probe.SubmitCount);
        Assert.Equal(PrintAttemptState.Created,
            (await journal.FindAsync(request.RequestId, CancellationToken.None))!.Status.State);

        var release = new PrintDispatchControlRecord(
            Guid.NewGuid(), "printer", false, null, "operator", Now.AddMinutes(1));
        await journal.SetDispatchControlAsync(release, CancellationToken.None);
        Assert.Equal(release, await journal.FindDispatchControlAsync("printer", CancellationToken.None));
        Assert.Equal(PrintSubmissionStatus.Submitted,
            (await coordinator.DispatchAsync("printer", probe, request, CancellationToken.None)).Status);
        Assert.Equal(1, probe.SubmitCount);
    }

    [Fact]
    public async Task Changed_command_replay_conflicts()
    {
        Directory.CreateDirectory(directory);
        var journal = new SqlitePrintAttemptJournal(Path.Combine(directory, "journal.db"));
        await journal.InitializeAsync(CancellationToken.None);
        var command = new PrintDispatchControlRecord(
            Guid.NewGuid(), "printer", true, "Inspection.", "operator", Now);
        await journal.SetDispatchControlAsync(command, CancellationToken.None);

        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            journal.SetDispatchControlAsync(command with { Reason = "Changed." },
                CancellationToken.None).AsTask());
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

    private static PrintProbeRequest Request() => new(
        Guid.NewGuid(),
        new PrintArtifact("image/png", 29, 90, ArtifactColorSpace.Srgb, [1, 2, 3]),
        new PrintProfile("mono", OutputPalette.Monochrome, 300, 300, 29, 90),
        1);

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
                new PrintSubmission(PrintSubmissionStatus.Submitted, null, null));
        }
    }
}
