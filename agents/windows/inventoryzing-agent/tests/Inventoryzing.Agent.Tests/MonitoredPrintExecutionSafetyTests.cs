using System.Threading.Channels;
using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Runtime.Printing;
using Microsoft.Data.Sqlite;

namespace Inventoryzing.Agent.Tests;

public sealed class MonitoredPrintExecutionSafetyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"print-safety-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Buffered_page_evidence_wins_over_simultaneous_queue_disappearance_or_printed_status(
        bool queuePrinted)
    {
        var (journal, coordinator, request, queue, printer) = await SetupAsync();
        await using var ownedPrinter = printer;
        printer.PublishPage();
        printer.PublishPage();
        queue.NotifyOnce = true;
        queue.Disappear = !queuePrinted;
        queue.Printed = queuePrinted;

        var result = await ExecuteAsync(coordinator, request, queue, printer);

        Assert.Equal(PrintAttemptState.Completed, result.Attempt.Status.State);
        Assert.Equal(PrintCompletionEvidence.BrotherMonitorConfirmed, result.Attempt.Status.CompletionEvidence);
        Assert.Equal(2, (await journal.ReadObservationsAsync(request.RequestId, CancellationToken.None))
            .Count(item => item.Observation.Code == "brother.page_printed"));
        Assert.Equal(0, printer.ActiveReads);
        Assert.True(queue.Disposed);
    }

    [Fact]
    public async Task Correlation_completion_keeps_stronger_buffered_Brother_evidence()
    {
        var (_, coordinator, request, queue, printer) = await SetupAsync();
        await using var ownedPrinter = printer;
        queue.PrintedAtCorrelation = true;
        printer.PublishPage();
        printer.PublishPage();

        var result = await ExecuteAsync(coordinator, request, queue, printer);

        Assert.Equal(PrintCompletionEvidence.BrotherMonitorConfirmed, result.Attempt.Status.CompletionEvidence);
        Assert.Equal(0, printer.ActiveReads);
    }

    [Fact]
    public async Task Queue_notification_does_not_cancel_pending_callback_read()
    {
        var (_, coordinator, request, queue, printer) = await SetupAsync();
        await using var ownedPrinter = printer;
        queue.NotifyOnce = true;
        queue.BeforeRead = () =>
        {
            Assert.Equal(0, printer.CancelledReads);
            printer.PublishPage();
            printer.PublishPage();
        };

        var result = await ExecuteAsync(coordinator, request, queue, printer);

        Assert.Equal(PrintAttemptState.Completed, result.Attempt.Status.State);
        Assert.Equal(0, printer.ActiveReads);
    }

    [Fact]
    public async Task Missing_job_without_positive_evidence_is_unknown()
    {
        var (_, coordinator, request, queue, printer) = await SetupAsync();
        await using var ownedPrinter = printer;
        queue.NotifyOnce = true;
        queue.Disappear = true;

        var result = await ExecuteAsync(coordinator, request, queue, printer);

        Assert.Equal(PrintAttemptState.Unknown, result.Attempt.Status.State);
        Assert.Null(result.Attempt.Status.CompletionEvidence);
        Assert.Equal(0, printer.ActiveReads);
    }

    [Fact]
    public async Task Uncorrelated_job_cannot_complete_from_Brother_callbacks_alone()
    {
        var (_, coordinator, request, queue, printer) = await SetupAsync();
        await using var ownedPrinter = printer;
        queue.MissingAtCorrelation = true;
        printer.PublishPage();
        printer.PublishPage();

        var result = await ExecuteAsync(coordinator, request, queue, printer);

        Assert.Equal(PrintAttemptState.Unknown, result.Attempt.Status.State);
        Assert.Null(result.Correlation);
        Assert.Equal(0, printer.ActiveReads);
    }

    [Fact]
    public async Task Unsupported_Brother_monitor_uses_only_spooler_evidence()
    {
        var (_, coordinator, request, queue, printer) = await SetupAsync();
        await using var ownedPrinter = printer;
        printer.CompletionCapability = PrinterMonitorCompletionCapability.Unsupported;
        queue.PrintedAtCorrelation = true;

        var result = await ExecuteAsync(coordinator, request, queue, printer);

        Assert.Equal(PrintCompletionEvidence.SpoolerConfirmed, result.Attempt.Status.CompletionEvidence);
        Assert.Equal(0, printer.ReadCount);
    }

    [Fact]
    public async Task Cancellation_joins_waiters_and_preserves_recoverable_job_and_slot()
    {
        var (journal, coordinator, request, queue, printer) = await SetupAsync();
        await using var ownedPrinter = printer;
        using var stop = new CancellationTokenSource();
        var execution = ExecuteAsync(coordinator, request, queue, printer, stop.Token);
        await queue.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Equal(0, printer.ActiveReads);
        Assert.True(queue.Disposed);
        Assert.Equal(PrintAttemptState.SpoolerQueued,
            (await journal.FindAsync(request.RequestId, CancellationToken.None))!.Status.State);
        Assert.NotNull(await journal.FindDispatchSlotAsync("printer", CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wrong_session_or_document_name_is_rejected_before_queue_or_dispatch(bool wrongSession)
    {
        var (_, coordinator, request, queue, printer) = await SetupAsync();
        await using var ownedPrinter = printer;
        if (wrongSession)
        {
            printer.AttemptId = Guid.NewGuid();
        }

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.ExecuteAsync(
            "printer", queue.Identity, printer, request,
            wrongSession ? $"inventoryzing-{request.RequestId:N}" : "wrong-document",
            TimeSpan.FromSeconds(1), null, null, CancellationToken.None).AsTask());

        Assert.Equal(0, queue.ArmCount);
        Assert.Equal(0, printer.SubmitCount);
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

    private async Task<(SqlitePrintAttemptJournal, MonitoredPrintExecutionCoordinator,
        PrintProbeRequest, QueueMonitor, Printer)> SetupAsync()
    {
        Directory.CreateDirectory(directory);
        var journal = new SqlitePrintAttemptJournal(Path.Combine(directory, "journal.db"));
        await journal.InitializeAsync(CancellationToken.None);
        var request = new PrintProbeRequest(Guid.NewGuid(),
            new PrintArtifact("image/png", 29, 90, ArtifactColorSpace.Srgb, [1, 2, 3]),
            new PrintProfile("mono", OutputPalette.Monochrome, 300, 300, 29, 90), 2);
        var queue = new QueueMonitor(request.RequestId);
        var printer = new Printer(request.RequestId);
        var clock = new FixedTimeProvider();
        var coordinator = new MonitoredPrintExecutionCoordinator(journal, queue,
            new PrintDispatchCoordinator(journal, new FilePrintArtifactCache(Path.Combine(directory, "cache")), clock),
            new DurablePrintQueueObserver(journal, clock), new DurablePrinterMonitorObserver(journal, clock), clock);
        return (journal, coordinator, request, queue, printer);
    }

    private static Task<MonitoredPrintExecutionResult> ExecuteAsync(
        MonitoredPrintExecutionCoordinator coordinator, PrintProbeRequest request,
        QueueMonitor queue, Printer printer, CancellationToken cancellationToken = default) =>
        coordinator.ExecuteAsync("printer", queue.Identity, printer, request,
            $"inventoryzing-{request.RequestId:N}", TimeSpan.FromSeconds(1), null, null,
            cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Printer(Guid attemptId) : IMonitoredPrinterSession
    {
        private readonly Channel<PrinterMonitorObservation> events = Channel.CreateUnbounded<PrinterMonitorObservation>();
        public Guid AttemptId { get; set; } = attemptId;
        public PrinterMonitorCompletionCapability CompletionCapability { get; set; } = PrinterMonitorCompletionCapability.PageCompletion;
        public int ActiveReads { get; private set; }
        public int CancelledReads { get; private set; }
        public int ReadCount { get; private set; }
        public int SubmitCount { get; private set; }
        public void PublishPage() => Assert.True(events.Writer.TryWrite(new PrinterMonitorObservation(
            Guid.NewGuid(), PrinterMonitorEventKind.PagePrinted, 0, "page", Now)));
        public async ValueTask<PrinterMonitorObservation> WaitForMonitorEventAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            ActiveReads++;
            try
            {
                return await events.Reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancelledReads++;
                throw;
            }
            finally
            {
                ActiveReads--;
            }
        }
        public ValueTask<PrinterCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PrinterCapabilities("printer", [OutputPalette.Monochrome]));
        public ValueTask<PrintSubmission> SubmitAsync(PrintProbeRequest request, CancellationToken cancellationToken)
        {
            SubmitCount++;
            return ValueTask.FromResult(new PrintSubmission(PrintSubmissionStatus.Submitted, null, "accepted"));
        }
        public ValueTask<PrinterStatusObservation> ObserveStatusAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class QueueMonitor(Guid attemptId) : IPrintQueueMonitor, IPrintQueueMonitorSession
    {
        public PrintQueueIdentity Identity { get; } = new("Fake printer");
        public PrintQueueMonitorArm Arm { get; private set; } = null!;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool NotifyOnce { get; set; }
        public bool Disappear { get; set; }
        public bool Printed { get; set; }
        public bool PrintedAtCorrelation { get; set; }
        public bool MissingAtCorrelation { get; set; }
        public bool Disposed { get; private set; }
        public int ArmCount { get; private set; }
        public Action? BeforeRead { get; set; }
        public ValueTask<IPrintQueueMonitorSession> ArmAsync(PrintQueueIdentity queue, CancellationToken cancellationToken)
        {
            ArmCount++;
            Arm = new PrintQueueMonitorArm(Guid.NewGuid(), Snapshot([]), Now);
            return ValueTask.FromResult<IPrintQueueMonitorSession>(this);
        }
        public ValueTask<PrintQueueSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Snapshot(MissingAtCorrelation ? [] : [Job(PrintedAtCorrelation)]));
        public ValueTask<PrintQueueJobSnapshot?> ReadJobAsync(uint jobId, CancellationToken cancellationToken)
        {
            BeforeRead?.Invoke();
            BeforeRead = null;
            return ValueTask.FromResult(Disappear ? null : Job(Printed));
        }
        public async ValueTask<PrintQueueChangeNotification> WaitForChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Waiting.TrySetResult();
            if (NotifyOnce || MissingAtCorrelation)
            {
                NotifyOnce = false;
                return new PrintQueueChangeNotification(0, MissingAtCorrelation, Now);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
        public ValueTask<PrintQueueResumeResult> ResumeAsync(PrintQueueJobReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
        private PrintQueueSnapshot Snapshot(IEnumerable<PrintQueueJobSnapshot> jobs) =>
            new(Identity, jobs, new PrintQueuePrinterSnapshot(0), Now);
        private PrintQueueJobSnapshot Job(bool printed) =>
            new(42, $"inventoryzing-{attemptId:N}", Now,
                (uint)(printed ? PrintQueueJobStatus.Printed : PrintQueueJobStatus.Spooling), totalPages: 2);
    }
}
