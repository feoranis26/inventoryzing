using System.Threading.Channels;
using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class MonitoredPrintExecutionCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"inventoryzing-monitored-execution-{Guid.NewGuid():N}");

    public MonitoredPrintExecutionCoordinatorTests()
    {
        Directory.CreateDirectory(directoryPath);
    }

    [Fact]
    public async Task Queue_is_prearmed_and_all_verified_page_events_complete_attempt()
    {
        var journal = new SqlitePrintAttemptJournal(Path.Combine(directoryPath, "printer.db"));
        await journal.InitializeAsync(CancellationToken.None);
        List<string> calls = [];
        var request = Request(copies: 2);
        var documentName = $"inventoryzing-{request.RequestId:N}";
        var queue = new PrintQueueIdentity("Brother QL-820NWB");
        var job = new PrintQueueJobSnapshot(
            42,
            documentName,
            Now.AddSeconds(1),
            (uint)PrintQueueJobStatus.Spooling,
            totalPages: 2);
        var queueMonitor = new FakeQueueMonitor(queue, job, calls);
        await using var printer = new FakeMonitoredPrinter(request.RequestId, calls);
        printer.Publish(Page("page 1"));
        printer.Publish(Page("page 2"));
        var coordinator = new MonitoredPrintExecutionCoordinator(
            journal,
            queueMonitor,
            new PrintDispatchCoordinator(
                journal,
                new FilePrintArtifactCache(Path.Combine(directoryPath, "artifacts")),
                new FixedTimeProvider(Now),
                Guid.NewGuid),
            new DurablePrintQueueObserver(journal, new FixedTimeProvider(Now), Guid.NewGuid),
            new DurablePrinterMonitorObserver(journal, new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now));

        var result = await coordinator.ExecuteAsync(
            "brother:ql-820nwb",
            queue,
            printer,
            request,
            documentName,
            TimeSpan.FromSeconds(10),
            null,
            null,
            CancellationToken.None);

        Assert.Equal("ArmQueue", calls[0]);
        Assert.True(calls.IndexOf("ArmQueue") < calls.IndexOf("Submit"));
        Assert.Equal(PrintAttemptState.Completed, result.Attempt.Status.State);
        Assert.Equal(
            PrintCompletionEvidence.BrotherMonitorConfirmed,
            result.Attempt.Status.CompletionEvidence);
        Assert.Equal(2, (await journal.ReadObservationsAsync(
            request.RequestId,
            CancellationToken.None)).Count(item =>
                item.Observation.Code == "brother.page_printed"));
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

    private static PrintProbeRequest Request(int copies)
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
            copies);
    }

    private static PrinterMonitorObservation Page(string value) =>
        new(Guid.NewGuid(), PrinterMonitorEventKind.PagePrinted, 0, value, Now.AddSeconds(2));

    private sealed class FakeMonitoredPrinter(Guid attemptId, List<string> calls) : IMonitoredPrinterSession
    {
        public Guid AttemptId { get; } = attemptId;
        private readonly Channel<PrinterMonitorObservation> events =
            Channel.CreateUnbounded<PrinterMonitorObservation>();

        public PrinterMonitorCompletionCapability CompletionCapability =>
            PrinterMonitorCompletionCapability.PageCompletion;

        public ValueTask<PrinterCapabilities> GetCapabilitiesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PrinterCapabilities(
                "brother:ql-820nwb",
                [OutputPalette.Monochrome]));

        public ValueTask<PrintSubmission> SubmitAsync(
            PrintProbeRequest request,
            CancellationToken cancellationToken)
        {
            calls.Add("Submit");
            return ValueTask.FromResult(
                new PrintSubmission(PrintSubmissionStatus.Submitted, null, "accepted"));
        }

        public ValueTask<PrinterStatusObservation> ObserveStatusAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<PrinterMonitorObservation> WaitForMonitorEventAsync(
            CancellationToken cancellationToken) => events.Reader.ReadAsync(cancellationToken);

        public void Publish(PrinterMonitorObservation observation) =>
            Assert.True(events.Writer.TryWrite(observation));

        public ValueTask DisposeAsync()
        {
            events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeQueueMonitor(
        PrintQueueIdentity queue,
        PrintQueueJobSnapshot job,
        List<string> calls) : IPrintQueueMonitor
    {
        public ValueTask<IPrintQueueMonitorSession> ArmAsync(
            PrintQueueIdentity requestedQueue,
            CancellationToken cancellationToken)
        {
            calls.Add("ArmQueue");
            Assert.Equal(queue, requestedQueue);
            var baseline = new PrintQueueSnapshot(
                queue,
                [],
                new PrintQueuePrinterSnapshot(0),
                Now);
            var current = new PrintQueueSnapshot(
                queue,
                [job],
                new PrintQueuePrinterSnapshot(0),
                Now.AddSeconds(1));
            return ValueTask.FromResult<IPrintQueueMonitorSession>(
                new FakeQueueSession(new PrintQueueMonitorArm(Guid.NewGuid(), baseline, Now), current));
        }
    }

    private sealed class FakeQueueSession(
        PrintQueueMonitorArm arm,
        PrintQueueSnapshot snapshot) : IPrintQueueMonitorSession
    {
        public PrintQueueMonitorArm Arm { get; } = arm;

        public ValueTask<PrintQueueSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot);

        public ValueTask<PrintQueueJobSnapshot?> ReadJobAsync(
            uint jobId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot.Jobs.SingleOrDefault(job => job.JobId == jobId));

        public async ValueTask<PrintQueueChangeNotification> WaitForChangeAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        public ValueTask<PrintQueueResumeResult> ResumeAsync(
            PrintQueueJobReference reference,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
