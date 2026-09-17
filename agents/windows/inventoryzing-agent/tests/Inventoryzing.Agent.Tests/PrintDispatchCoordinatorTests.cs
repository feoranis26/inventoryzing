using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class PrintDispatchCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-dispatch-{Guid.NewGuid():N}");

    public PrintDispatchCoordinatorTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Dispatch_uses_verified_cache_and_persists_barrier_before_provider_call()
    {
        var (journal, coordinator) = await CreateCoordinatorAsync();
        var request = Request();
        var probe = new FakePrinterProbe
        {
            Submit = async supplied =>
            {
                var current = await journal.FindAsync(supplied.RequestId, CancellationToken.None);
                Assert.Equal(PrintAttemptState.Dispatching, current!.Status.State);
                Assert.NotSame(request.Artifact, supplied.Artifact);
                Assert.Equal(request.Artifact.Sha256, supplied.Artifact.Sha256);
                return new PrintSubmission(PrintSubmissionStatus.Submitted, "42", "accepted");
            },
        };

        var result = await coordinator.DispatchAsync(
            "brother:ql-820nwb",
            probe,
            request,
            CancellationToken.None);
        var current = await journal.FindAsync(request.RequestId, CancellationToken.None);
        var observations = await journal.ReadObservationsAsync(
            request.RequestId,
            CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Submitted, result.Status);
        Assert.Equal(PrintAttemptState.DriverAccepted, current!.Status.State);
        Assert.Equal(
            [
                "dispatch.claimed",
                "artifact.staged",
                "dispatch.prepared",
                "dispatch.authorized",
                "dispatch.started",
                "dispatch.submitted",
            ],
            observations.Select(item => item.Observation.Code));
    }

    [Fact]
    public async Task Exact_replay_returns_stored_result_without_second_provider_call()
    {
        var (journal, coordinator) = await CreateCoordinatorAsync();
        var request = Request();
        var probe = new FakePrinterProbe();

        var first = await coordinator.DispatchAsync(
            "brother:ql-820nwb",
            probe,
            request,
            CancellationToken.None);
        var replayed = await coordinator.DispatchAsync(
            "brother:ql-820nwb",
            probe,
            request,
            CancellationToken.None);

        Assert.Equal(first, replayed);
        Assert.Equal(1, probe.SubmitCount);
        Assert.NotNull(await journal.FindDispatchResultAsync(request.RequestId, CancellationToken.None));
    }

    [Fact]
    public async Task Changed_replay_conflicts_before_second_provider_call()
    {
        var (_, coordinator) = await CreateCoordinatorAsync();
        var request = Request();
        var probe = new FakePrinterProbe();
        _ = await coordinator.DispatchAsync(
            "brother:ql-820nwb",
            probe,
            request,
            CancellationToken.None);
        var changed = new PrintProbeRequest(
            request.RequestId,
            request.Artifact,
            request.Profile,
            copies: 2,
            request.Fields);

        await Assert.ThrowsAsync<PrintAttemptJournalConflictException>(() =>
            coordinator.DispatchAsync(
                "brother:ql-820nwb",
                probe,
                changed,
                CancellationToken.None).AsTask());
        Assert.Equal(1, probe.SubmitCount);
    }

    [Fact]
    public async Task Concurrent_replay_cannot_release_live_dispatch_slot_or_persist_unknown()
    {
        var (journal, coordinator) = await CreateCoordinatorAsync();
        var (_, replayCoordinator) = await CreateCoordinatorAsync();
        var request = Request();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FakePrinterProbe
        {
            Submit = async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return new PrintSubmission(PrintSubmissionStatus.Submitted, "42", "accepted");
            },
        };
        var active = coordinator.DispatchAsync("brother:ql-820nwb", probe, request,
            CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var replay = await replayCoordinator.DispatchAsync("brother:ql-820nwb", probe,
                request, CancellationToken.None);
            Assert.Equal(PrintSubmissionStatus.Unknown, replay.Status);
            Assert.Equal(PrintAttemptState.Dispatching,
                (await journal.FindAsync(request.RequestId, CancellationToken.None))!.Status.State);
            Assert.Null(await journal.FindDispatchResultAsync(request.RequestId, CancellationToken.None));
            Assert.Equal(request.RequestId,
                (await journal.FindDispatchSlotAsync("brother:ql-820nwb", CancellationToken.None))!.AttemptId);
            Assert.Equal(1, probe.SubmitCount);
        }
        finally
        {
            release.TrySetResult();
            await active.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(PrintAttemptState.DriverAccepted,
            (await journal.FindAsync(request.RequestId, CancellationToken.None))!.Status.State);
    }

    [Fact]
    public async Task Authority_failure_leaves_attempt_prepared_and_never_calls_provider()
    {
        var authority = new FakeDispatchAuthority
        {
            Authorize = _ => throw new IOException("Synthetic coordinator outage."),
        };
        var (journal, coordinator) = await CreateCoordinatorAsync(authority);
        var request = Request();
        var probe = new FakePrinterProbe();

        await Assert.ThrowsAsync<IOException>(() => coordinator.DispatchAsync(
            "brother:ql-820nwb", probe, request, CancellationToken.None).AsTask());

        Assert.Equal(PrintAttemptState.Prepared,
            (await journal.FindAsync(request.RequestId, CancellationToken.None))!.Status.State);
        Assert.Equal(0, probe.SubmitCount);
        Assert.Null(await journal.FindDispatchSlotAsync(
            "brother:ql-820nwb", CancellationToken.None));
    }

    [Fact]
    public async Task Authority_rejection_is_terminal_before_provider_call()
    {
        var authority = new FakeDispatchAuthority
        {
            Authorize = intent => new PrintDispatchAuthorization(
                Guid.NewGuid(),
                PrintDispatchAuthorizationStatus.Rejected,
                "Claim expired."),
        };
        var (journal, coordinator) = await CreateCoordinatorAsync(authority);
        var request = Request();
        var probe = new FakePrinterProbe();

        var result = await coordinator.DispatchAsync(
            "brother:ql-820nwb", probe, request, CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Rejected, result.Status);
        Assert.Equal(PrintAttemptState.Rejected,
            (await journal.FindAsync(request.RequestId, CancellationToken.None))!.Status.State);
        Assert.Equal(0, probe.SubmitCount);
    }

    [Fact]
    public async Task Provider_exception_after_barrier_is_unknown_and_never_redispatched()
    {
        var (journal, coordinator) = await CreateCoordinatorAsync();
        var request = Request();
        var probe = new FakePrinterProbe
        {
            Submit = _ => throw new InvalidOperationException("synthetic provider failure"),
        };

        var result = await coordinator.DispatchAsync(
            "brother:ql-820nwb",
            probe,
            request,
            CancellationToken.None);
        var replayed = await coordinator.DispatchAsync(
            "brother:ql-820nwb",
            probe,
            request,
            CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Unknown, result.Status);
        Assert.Equal(result, replayed);
        Assert.Equal(1, probe.SubmitCount);
        Assert.Equal(
            PrintAttemptState.Unknown,
            (await journal.FindAsync(request.RequestId, CancellationToken.None))!.Status.State);
    }

    [Fact]
    public async Task Same_printer_dispatches_are_serialized()
    {
        var (_, coordinator) = await CreateCoordinatorAsync();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var probe = new FakePrinterProbe
        {
            ResultStatus = PrintSubmissionStatus.Rejected,
            Submit = async _ =>
            {
                var count = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, count);
                firstEntered.TrySetResult();
                if (count == 1)
                {
                    await releaseFirst.Task;
                }
                Interlocked.Decrement(ref active);
                return new PrintSubmission(PrintSubmissionStatus.Rejected, null, null);
            },
        };
        var first = coordinator.DispatchAsync(
            "brother:ql-820nwb",
            probe,
            Request(),
            CancellationToken.None).AsTask();
        await firstEntered.Task;
        var second = coordinator.DispatchAsync(
            "brother:ql-820nwb",
            probe,
            Request(),
            CancellationToken.None).AsTask();

        await Task.Yield();
        Assert.Equal(1, probe.SubmitCount);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maximumActive);
        Assert.Equal(2, probe.SubmitCount);
    }

    [Fact]
    public async Task Accepted_attempt_holds_durable_printer_slot_until_terminal_state()
    {
        var (journal, coordinator) = await CreateCoordinatorAsync();
        var firstRequest = Request();
        var probe = new FakePrinterProbe();
        _ = await coordinator.DispatchAsync(
            "brother:ql-820nwb",
            probe,
            firstRequest,
            CancellationToken.None);

        var blocked = await coordinator.DispatchAsync(
            "brother:ql-820nwb",
            probe,
            Request(),
            CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Rejected, blocked.Status);
        Assert.Contains("active print attempt", blocked.Detail, StringComparison.Ordinal);
        Assert.Equal(1, probe.SubmitCount);
        Assert.Equal(
            firstRequest.RequestId,
            (await journal.FindDispatchSlotAsync(
                "brother:ql-820nwb",
                CancellationToken.None))!.AttemptId);

        var current = (await journal.FindAsync(firstRequest.RequestId, CancellationToken.None))!;
        await journal.TransitionAsync(
            current.AttemptId,
            Guid.NewGuid(),
            current.Status.State,
            current.Version,
            PrintAttemptState.Completed,
            PrintCompletionEvidence.BrotherMonitorConfirmed,
            Now,
            CancellationToken.None);

        Assert.Null(await journal.FindDispatchSlotAsync(
            "brother:ql-820nwb",
            CancellationToken.None));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directoryPath, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<(SqlitePrintAttemptJournal Journal, PrintDispatchCoordinator Coordinator)>
        CreateCoordinatorAsync(IPrintDispatchAuthority? authority = null)
    {
        var journal = new SqlitePrintAttemptJournal(Path.Combine(directoryPath, "printer.db"));
        await journal.InitializeAsync(CancellationToken.None);
        var cache = new FilePrintArtifactCache(Path.Combine(directoryPath, "artifacts"));
        return (
            journal,
            new PrintDispatchCoordinator(
                journal,
                cache,
                new FixedTimeProvider(Now),
                Guid.NewGuid,
                authority));
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
        var artifact = new PrintArtifact(
            "image/png",
            62,
            29,
            ArtifactColorSpace.Srgb,
            [1, 2, 3, 4]);
        return new PrintProbeRequest(
            Guid.NewGuid(),
            artifact,
            profile,
            1,
            [new PrintTemplateField("asset_name", "Bench meter")]);
    }

    private sealed class FakePrinterProbe : IPrinterProbe
    {
        private int submitCount;

        public Func<PrintProbeRequest, Task<PrintSubmission>> Submit { get; init; } =
            _ => Task.FromResult(
                new PrintSubmission(PrintSubmissionStatus.Submitted, null, "accepted"));

        public PrintSubmissionStatus ResultStatus { get; init; } = PrintSubmissionStatus.Submitted;

        public int SubmitCount => Volatile.Read(ref submitCount);

        public ValueTask<PrinterCapabilities> GetCapabilitiesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PrinterCapabilities(
                "brother:ql-820nwb",
                [OutputPalette.Monochrome]));

        public async ValueTask<PrintSubmission> SubmitAsync(
            PrintProbeRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref submitCount);
            return await Submit(request);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeDispatchAuthority : IPrintDispatchAuthority
    {
        public Func<PrintDispatchIntentRecord, PrintDispatchAuthorization> Authorize { get; init; } =
            intent => new PrintDispatchAuthorization(
                Guid.NewGuid(),
                PrintDispatchAuthorizationStatus.Granted,
                null);

        public ValueTask<PrintDispatchAuthorization> AuthorizeAsync(
            PrintDispatchIntentRecord intent,
            CancellationToken cancellationToken) => ValueTask.FromResult(Authorize(intent));
    }
}
