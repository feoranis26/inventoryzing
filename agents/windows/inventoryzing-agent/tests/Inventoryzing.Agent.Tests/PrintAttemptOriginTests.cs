using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class PrintAttemptOriginTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-attempt-origin-{Guid.NewGuid():N}");

    public PrintAttemptOriginTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Retry_requires_durable_provider_rejection_and_is_idempotent()
    {
        var journal = await JournalAsync();
        var rejected = await SourceAsync(journal, PrintSubmissionStatus.Rejected);
        var origin = Origin(rejected.AttemptId, PrintAttemptOriginKind.Retry, acknowledged: false);

        var created = await journal.CreateDerivedAttemptAsync(origin, CancellationToken.None);
        var replayed = await journal.CreateDerivedAttemptAsync(origin, CancellationToken.None);

        Assert.Equal(created, replayed);
        Assert.Equal(PrintAttemptState.Created, created.Attempt.Status.State);
        Assert.Equal(origin, await journal.FindAttemptOriginAsync(origin.AttemptId, CancellationToken.None));
    }

    [Theory]
    [InlineData(PrintSubmissionStatus.Submitted)]
    [InlineData(PrintSubmissionStatus.Unknown)]
    public async Task Retry_rejects_any_source_that_may_have_dispatched(
        PrintSubmissionStatus status)
    {
        var journal = await JournalAsync();
        var source = await SourceAsync(journal, status);

        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            journal.CreateDerivedAttemptAsync(
                Origin(source.AttemptId, PrintAttemptOriginKind.Retry, acknowledged: false),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Reprint_requires_terminal_source_and_explicit_risk_acknowledgement()
    {
        var journal = await JournalAsync();
        var source = await SourceAsync(journal, PrintSubmissionStatus.Unknown);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            journal.CreateDerivedAttemptAsync(
                Origin(
                    source.AttemptId,
                    PrintAttemptOriginKind.Reprint,
                    acknowledged: false),
                CancellationToken.None).AsTask());
        var accepted = Origin(
            source.AttemptId,
            PrintAttemptOriginKind.Reprint,
            acknowledged: true);
        var derived = await journal.CreateDerivedAttemptAsync(accepted, CancellationToken.None);

        Assert.True(derived.Origin.DuplicateRiskAcknowledged);
        Assert.Equal("operator@example.test", derived.Origin.RequestedBy);
    }

    [Fact]
    public async Task Derived_dispatch_must_reuse_exact_source_content()
    {
        var journal = await JournalAsync();
        var cache = new FilePrintArtifactCache(Path.Combine(directoryPath, "artifacts"));
        var coordinator = new PrintDispatchCoordinator(journal, cache);
        var sourceRequest = Request();
        var probe = new FakePrinterProbe(PrintSubmissionStatus.Rejected);
        _ = await coordinator.DispatchAsync(
            "brother:ql-820nwb",
            probe,
            sourceRequest,
            CancellationToken.None);
        var origin = Origin(
            sourceRequest.RequestId,
            PrintAttemptOriginKind.Retry,
            acknowledged: false);
        _ = await journal.CreateDerivedAttemptAsync(origin, CancellationToken.None);
        var changedRequest = new PrintProbeRequest(
            origin.AttemptId,
            sourceRequest.Artifact,
            sourceRequest.Profile,
            copies: 2,
            sourceRequest.Fields);

        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            coordinator.DispatchAsync(
                "brother:ql-820nwb",
                probe,
                changedRequest,
                CancellationToken.None).AsTask());
        Assert.Equal(1, probe.SubmitCount);
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

    private static async Task<PrintAttemptRecord> SourceAsync(
        SqlitePrintAttemptJournal journal,
        PrintSubmissionStatus result)
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

        await journal.StoreDispatchResultAsync(
            new PrintDispatchResultRecord(
                current.AttemptId,
                new PrintSubmission(result, null, result.ToString()),
                Now),
            CancellationToken.None);
        var next = result switch
        {
            PrintSubmissionStatus.Submitted => PrintAttemptState.DriverAccepted,
            PrintSubmissionStatus.Rejected => PrintAttemptState.Rejected,
            PrintSubmissionStatus.Unknown => PrintAttemptState.Unknown,
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
        var final = await journal.TransitionAsync(
            current.AttemptId,
            Guid.NewGuid(),
            current.Status.State,
            current.Version,
            next,
            null,
            Now,
            CancellationToken.None);
        return current with
        {
            Status = final.Status,
            Version = final.ResultingVersion,
        };
    }

    private static PrintAttemptOriginRecord Origin(
        Guid sourceAttemptId,
        PrintAttemptOriginKind kind,
        bool acknowledged)
    {
        if (kind == PrintAttemptOriginKind.Reprint && !acknowledged)
        {
            return new PrintAttemptOriginRecord(
                Guid.NewGuid(),
                Guid.NewGuid(),
                sourceAttemptId,
                kind,
                "operator@example.test",
                acknowledged,
                Now);
        }

        return new PrintAttemptOriginRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            sourceAttemptId,
            kind,
            "operator@example.test",
            acknowledged,
            Now);
    }

    private static PrintProbeRequest Request()
    {
        var profile = new PrintProfile(
            "brother-62x29",
            OutputPalette.Monochrome,
            300,
            300,
            62,
            29);
        return new PrintProbeRequest(
            Guid.NewGuid(),
            new PrintArtifact(
                "image/png",
                62,
                29,
                ArtifactColorSpace.Srgb,
                [1, 2, 3]),
            profile,
            1,
            [new PrintTemplateField("asset_name", "Bench meter")]);
    }

    private sealed class FakePrinterProbe(PrintSubmissionStatus status) : IPrinterProbe
    {
        public int SubmitCount { get; private set; }

        public ValueTask<PrinterCapabilities> GetCapabilitiesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PrinterCapabilities(
                "brother:ql-820nwb",
                [OutputPalette.Monochrome]));

        public ValueTask<PrintSubmission> SubmitAsync(
            PrintProbeRequest request,
            CancellationToken cancellationToken)
        {
            SubmitCount++;
            return ValueTask.FromResult(new PrintSubmission(status, null, status.ToString()));
        }
    }
}