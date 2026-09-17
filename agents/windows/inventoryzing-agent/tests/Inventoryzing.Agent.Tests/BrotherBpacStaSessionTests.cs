using System.Threading.Channels;
using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Tests;

public sealed class BrotherBpacStaSessionTests
{
    [Fact]
    public async Task Submission_callback_and_disposal_share_one_sta_apartment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var client = new TrackingClient();
        var profile = Profile();
        var request = Request(profile);
        await using var session = new BrotherBpacStaSession(
            request.RequestId,
            "brother:ql-820nwb",
            "Brother QL-820NWB",
            [new BrotherBpacProfile(
                profile,
                "asset.lbx",
                "qr_image",
                BrotherMediaSelection.ByName("62mm x 29mm"),
                BrotherCutMode.AutoCut)],
            PrinterMonitorCompletionCapability.Unknown,
            () => client,
            new BrotherBpacStaDispatcher(),
            TimeProvider.System,
            Guid.NewGuid);

        var submission = await session.SubmitAsync(request, CancellationToken.None);
        var expected = new PrinterMonitorObservation(
            Guid.NewGuid(),
            PrinterMonitorEventKind.PagePrinted,
            0,
            "page 1",
            DateTimeOffset.UtcNow);
        client.Subscription.Publish(expected);
        var observed = await session.WaitForMonitorEventAsync(CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Submitted, submission.Status);
        Assert.Equal(expected, observed);
        Assert.Equal("ArmEvents", client.Calls[0].Operation);
        var thread = Assert.Single(client.Calls.Select(call => call.ThreadId).Distinct());
        Assert.All(client.Calls, call => Assert.Equal(ApartmentState.STA, call.ApartmentState));

        await session.DisposeAsync();

        Assert.Equal(thread, client.Subscription.DisposeThreadId);
        Assert.Equal(thread, client.DisposeThreadId);
    }

    [Fact]
    public async Task Invalid_request_does_not_activate_client_or_arm_events()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var activations = 0;
        var profile = Profile();
        var attemptId = Guid.NewGuid();
        await using var session = new BrotherBpacStaSession(
            attemptId,
            "brother:ql-820nwb",
            "Brother QL-820NWB",
            [new BrotherBpacProfile(
                profile,
                "asset.lbx",
                "qr_image",
                BrotherMediaSelection.ByName("62mm x 29mm"),
                BrotherCutMode.AutoCut)],
            PrinterMonitorCompletionCapability.Unknown,
            () =>
            {
                activations++;
                return new TrackingClient();
            },
            new BrotherBpacStaDispatcher(),
            TimeProvider.System,
            Guid.NewGuid);
        var artifact = new PrintArtifact(
            "application/pdf",
            62,
            29,
            ArtifactColorSpace.Srgb,
            [1, 2, 3]);

        var result = await session.SubmitAsync(
            new PrintProbeRequest(attemptId, artifact, profile, 1),
            CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Rejected, result.Status);
        Assert.Equal(0, activations);
    }

    [Fact]
    public async Task Explicitly_unsupported_monitor_does_not_require_event_client()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var client = new SubmissionOnlyClient();
        var profile = Profile();
        var request = Request(profile);
        await using var session = new BrotherBpacStaSession(
            request.RequestId,
            "brother:without-events",
            "Brother without events",
            [new BrotherBpacProfile(
                profile,
                "asset.lbx",
                "qr_image",
                BrotherMediaSelection.ByName("62mm x 29mm"),
                BrotherCutMode.AutoCut)],
            PrinterMonitorCompletionCapability.Unsupported,
            () => client,
            new BrotherBpacStaDispatcher(),
            TimeProvider.System,
            Guid.NewGuid);

        var result = await session.SubmitAsync(request, CancellationToken.None);

        Assert.Equal(PrintSubmissionStatus.Submitted, result.Status);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            session.WaitForMonitorEventAsync(CancellationToken.None).AsTask());
    }

    private static PrintProfile Profile() =>
        new("brother-62x29", OutputPalette.Monochrome, 300, 300, 62, 29);

    private static PrintProbeRequest Request(PrintProfile profile) =>
        new(
            Guid.NewGuid(),
            new PrintArtifact("image/png", 62, 29, ArtifactColorSpace.Srgb, [1, 2, 3]),
            profile,
            1);

    [Fact]
    public async Task Session_rejects_other_attempts_and_duplicate_submission_before_provider_access()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var client = new TrackingClient();
        var request = Request(Profile());
        await using var session = new BrotherBpacStaSession(
            request.RequestId,
            "brother:ql-820nwb",
            "Brother QL-820NWB",
            [new BrotherBpacProfile(request.Profile, "asset.lbx", "qr_image",
                BrotherMediaSelection.ByName("62mm x 29mm"), BrotherCutMode.AutoCut)],
            PrinterMonitorCompletionCapability.PageCompletion,
            () => client,
            new BrotherBpacStaDispatcher(),
            TimeProvider.System,
            Guid.NewGuid);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.SubmitAsync(Request(Profile()), CancellationToken.None).AsTask());
        Assert.Empty(client.Calls);
        Assert.Equal(PrintSubmissionStatus.Submitted,
            (await session.SubmitAsync(request, CancellationToken.None)).Status);
        client.Subscription.Publish(new PrinterMonitorObservation(
            Guid.NewGuid(), PrinterMonitorEventKind.PagePrinted, 0, "late page", DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SubmitAsync(request, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.SubmitAsync(Request(Profile()), CancellationToken.None).AsTask());

        Assert.Single(client.Calls, call => call.Operation == "StartPrint");
        Assert.Equal(request.RequestId, session.AttemptId);
    }

    private sealed class TrackingClient : IBrotherBpacClient, IBrotherBpacPrintEventClient
    {
        public List<Call> Calls { get; } = [];
        public TrackingSubscription Subscription { get; } = new();
        public int DisposeThreadId { get; private set; }
        public int DocumentErrorCode => 0;
        public int PrinterErrorCode => 0;
        public string? PrinterErrorMessage => null;

        public IPrinterMonitorSubscription ArmPrintedEvents(
            TimeProvider? timeProvider = null,
            Func<Guid>? createId = null)
        {
            Record("ArmEvents");
            return Subscription;
        }

        public IReadOnlyList<BrotherBpacPrinterDiscovery> DiscoverPrinters() => [];

        public BrotherBpacPrinterAvailability GetPrinterAvailability(string printerName)
        {
            Record("Availability");
            return new BrotherBpacPrinterAvailability(true, true, 0, null);
        }

        public BrotherBpacPrinterStatus GetSelectedPrinterStatus(BrotherMediaSelection media)
        {
            Record("Status");
            return new BrotherBpacPrinterStatus(
                true,
                true,
                true,
                "62mm x 29mm",
                null,
                0,
                null);
        }

        public bool Open(string templatePath)
        {
            Record("Open");
            return true;
        }

        public bool SetPrinter(string printerName, bool fitPage)
        {
            Record("SetPrinter");
            return true;
        }

        public bool SetMedia(BrotherMediaSelection media, bool fitPage)
        {
            Record("SetMedia");
            return true;
        }

        public bool SetObjectText(string objectName, string value)
        {
            Record("SetObjectText");
            return true;
        }

        public bool SetObjectImage(string objectName, string imagePath)
        {
            Record("SetObjectImage");
            return true;
        }

        public bool StartPrint(string documentName, int options)
        {
            Record("StartPrint");
            return true;
        }

        public bool PrintOut(int copies, int options)
        {
            Record("PrintOut");
            return true;
        }

        public bool EndPrint()
        {
            Record("EndPrint");
            return true;
        }

        public bool Close()
        {
            Record("Close");
            return true;
        }

        public void Dispose()
        {
            Record("Dispose");
            DisposeThreadId = Environment.CurrentManagedThreadId;
        }

        private void Record(string operation) => Calls.Add(new Call(
            operation,
            Environment.CurrentManagedThreadId,
            Thread.CurrentThread.GetApartmentState()));
    }

    private sealed class TrackingSubscription : IPrinterMonitorSubscription
    {
        private readonly Channel<PrinterMonitorObservation> events =
            Channel.CreateUnbounded<PrinterMonitorObservation>();

        public int DisposeThreadId { get; private set; }

        public ValueTask<PrinterMonitorObservation> WaitAsync(CancellationToken cancellationToken) =>
            events.Reader.ReadAsync(cancellationToken);

        public void Publish(PrinterMonitorObservation observation) =>
            Assert.True(events.Writer.TryWrite(observation));

        public void Dispose()
        {
            DisposeThreadId = Environment.CurrentManagedThreadId;
            events.Writer.TryComplete();
        }
    }

    private sealed record Call(string Operation, int ThreadId, ApartmentState ApartmentState);

    private sealed class SubmissionOnlyClient : IBrotherBpacClient
    {
        public int DocumentErrorCode => 0;
        public int PrinterErrorCode => 0;
        public string? PrinterErrorMessage => null;
        public IReadOnlyList<BrotherBpacPrinterDiscovery> DiscoverPrinters() => [];
        public BrotherBpacPrinterAvailability GetPrinterAvailability(string printerName) =>
            new(true, true, 0, null);
        public BrotherBpacPrinterStatus GetSelectedPrinterStatus(BrotherMediaSelection media) =>
            new(true, true, true, "62mm x 29mm", null, 0, null);
        public bool Open(string templatePath) => true;
        public bool SetPrinter(string printerName, bool fitPage) => true;
        public bool SetMedia(BrotherMediaSelection media, bool fitPage) => true;
        public bool SetObjectText(string objectName, string value) => true;
        public bool SetObjectImage(string objectName, string imagePath) => true;
        public bool StartPrint(string documentName, int options) => true;
        public bool PrintOut(int copies, int options) => true;
        public bool EndPrint() => true;
        public bool Close() => true;
        public void Dispose()
        {
        }
    }
}
