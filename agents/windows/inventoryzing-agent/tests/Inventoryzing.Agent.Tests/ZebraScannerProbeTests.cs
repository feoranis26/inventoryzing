using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Tests;

public sealed class ZebraScannerProbeTests
{
    private const string ScannerXml = """
        <scanners>
          <scanner type="SNAPI">
            <scannerID>7</scannerID>
            <modelnumber>DS2208</modelnumber>
            <serialnumber>ABC123</serialnumber>
            <GUID>AABBCCDD</GUID>
          </scanner>
        </scanners>
        """;

    [Fact]
    public async Task Enumerate_opens_only_snapi_and_returns_normalized_sources()
    {
        using var client = new FakeCoreScannerClient { ScannerXml = ScannerXml };
        await using var probe = new ZebraScannerProbe(client);

        var source = Assert.Single(await probe.EnumerateAsync(CancellationToken.None));

        Assert.Equal([ZebraScannerProbe.SnapiScannerType], client.OpenedScannerTypes);
        Assert.Equal("zebra:serial:ABC123", source.StableId);
        Assert.False(client.EventsRegistered);
    }

    [Fact]
    public async Task Watch_registers_barcode_and_pnp_and_preserves_repeated_scans()
    {
        using var client = new FakeCoreScannerClient { ScannerXml = ScannerXml };
        await using var probe = new ZebraScannerProbe(client, eventBufferCapacity: 2);
        await using var enumerator = probe.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        var firstMove = enumerator.MoveNextAsync().AsTask();
        await client.WaitUntilEventsRegisteredAsync();

        var producer = Task.Run(() =>
        {
            client.EmitBarcode(BarcodeXml("7", "0x41"));
            client.EmitBarcode(BarcodeXml("7", "0x41"));
            client.EmitBarcode(BarcodeXml("7", "0x41"));
        });

        var scans = new List<ScanEvent>();
        Assert.True(await firstMove.WaitAsync(TimeSpan.FromSeconds(1)));
        scans.Add(enumerator.Current);
        while (scans.Count < 3)
        {
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            scans.Add(enumerator.Current);
        }

        await producer.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal([ZebraScannerProbe.BarcodeEventId, ZebraScannerProbe.PnpEventId], client.RegisteredEventIds);
        Assert.All(scans, scan => Assert.Equal("A", scan.Payload));
        Assert.Equal(3, scans.Select(scan => scan.EventId).Distinct().Count());
    }

    [Fact]
    public async Task Reconnect_uses_new_runtime_id_with_same_stable_identity()
    {
        using var client = new FakeCoreScannerClient { ScannerXml = ScannerXml };
        await using var probe = new ZebraScannerProbe(client);
        var original = Assert.Single(await probe.EnumerateAsync(CancellationToken.None));
        await using var enumerator = probe.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        var move = enumerator.MoveNextAsync().AsTask();
        await client.WaitUntilEventsRegisteredAsync();

        client.EmitPnp(1, ScannerXml);
        var disconnected = Assert.Single(probe.Connections);
        Assert.Equal(ScannerConnectionState.Disconnected, disconnected.State);
        client.EmitPnp(0, ScannerXml.Replace("<scannerID>7</scannerID>", "<scannerID>12</scannerID>"));
        var reconnected = Assert.Single(probe.Connections);
        Assert.Equal(ScannerConnectionState.Connected, reconnected.State);
        Assert.Equal("12", reconnected.Source.RuntimeId);
        client.EmitBarcode(BarcodeXml("7", "0x41"));
        client.EmitBarcode(BarcodeXml("12", "0x42"));

        Assert.True(await move.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("B", enumerator.Current.Payload);
        Assert.Equal(original.StableId, enumerator.Current.Source.StableId);
        Assert.Equal("12", enumerator.Current.Source.RuntimeId);
    }

    [Fact]
    public async Task Malformed_callback_is_ignored_without_stopping_delivery()
    {
        using var client = new FakeCoreScannerClient { ScannerXml = ScannerXml };
        await using var probe = new ZebraScannerProbe(client);
        await using var enumerator = probe.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        var move = enumerator.MoveNextAsync().AsTask();
        await client.WaitUntilEventsRegisteredAsync();

        client.EmitBarcode("<outArgs><broken>");
        client.EmitBarcode(BarcodeXml("7", "0x43"));

        Assert.True(await move.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("C", enumerator.Current.Payload);
    }

    [Fact]
    public async Task Dispose_closes_client_and_ignores_late_callbacks()
    {
        using var client = new FakeCoreScannerClient { ScannerXml = ScannerXml };
        var probe = new ZebraScannerProbe(client);
        await using var enumerator = probe.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        var move = enumerator.MoveNextAsync().AsTask();
        await client.WaitUntilEventsRegisteredAsync();

        await probe.DisposeAsync();
        client.EmitBarcode(BarcodeXml("7", "0x44"));

        Assert.False(await move.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(client.CloseCalled);
        Assert.True(client.DisposeCalled);
    }

    [Fact]
    public async Task Construct_and_dispose_do_not_activate_default_com_client()
    {
        await using var probe = new ZebraScannerProbe();

        await probe.DisposeAsync();
    }

    [Fact]
    public async Task Feedback_maps_named_patterns_to_allowlisted_actions()
    {
        using var client = new FakeCoreScannerClient();
        await using var probe = new ZebraScannerProbe(client);
        var source = new ScanSource("zebra:serial", "7", ScanSourceType.ZebraSnapi, "DS22", "serial");

        await probe.ExecuteFeedbackAsync(source, new ScannerFeedback(
            ScannerIndicatorColor.Green, ScannerTonePattern.Rising, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal([(7, 23), (7, 43), (7, 42)], client.Actions);
    }

    [Fact]
    public async Task Command_success_adds_double_high_after_rising_tone()
    {
        using var client = new FakeCoreScannerClient();
        await using var probe = new ZebraScannerProbe(client);
        var source = new ScanSource("zebra:serial", "7", ScanSourceType.ZebraSnapi, "DS22", "serial");
        var options = new Inventoryzing.Agent.Scanner.Host.ScannerAgentOptions { FeedbackFlashDuration = TimeSpan.Zero };
        await probe.ExecuteFeedbackAsync(source,
            options.FeedbackFor(Inventoryzing.Agent.Scanner.Host.ScannerOperationOutcome.Success, commandCompleted: true),
            CancellationToken.None);
        Assert.Equal([(7, 23), (7, 1), (7, 43), (7, 42)], client.Actions);
        Assert.Equal(ScannerTonePattern.Rising,
            options.FeedbackFor(Inventoryzing.Agent.Scanner.Host.ScannerOperationOutcome.Success).Tone);
    }

    private static string BarcodeXml(string runtimeId, string dataLabel) => $$"""
        <outArgs>
          <scannerID>{{runtimeId}}</scannerID>
          <arg-xml>
            <scandata>
              <modelnumber>DS2208</modelnumber>
              <serialnumber>ABC123</serialnumber>
              <GUID>AABBCCDD</GUID>
              <datatype>28</datatype>
              <datalabel>{{dataLabel}}</datalabel>
              <rawdata>{{dataLabel}}</rawdata>
            </scandata>
          </arg-xml>
        </outArgs>
        """;

    [Theory]
    [InlineData("2")]
    [InlineData("19")]
    public async Task Cradle_feedback_uses_its_handheld_LED_and_low_unknown_tone(string handheldId)
    {
        using var client = new FakeCoreScannerClient { ScannerXml = $$"""
            <scanners><scanner type="SNAPI">
              <scannerID>1</scannerID><modelnumber>CR2278</modelnumber><serialnumber>CRADLE</serialnumber>
              <scanner type="SNAPI"><scannerID>{{handheldId}}</scannerID>
                <modelnumber>DS2278</modelnumber><serialnumber>HANDHELD</serialnumber></scanner>
            </scanner></scanners>
            """ };
        await using var probe = new ZebraScannerProbe(client);
        var source = new ScanSource("zebra:serial:CRADLE", "1", ScanSourceType.ZebraSnapi, "CR2278", "CRADLE");
        var options = new Inventoryzing.Agent.Scanner.Host.ScannerAgentOptions { FeedbackFlashDuration = TimeSpan.Zero };
        await probe.ExecuteFeedbackAsync(source,
            options.FeedbackFor(Inventoryzing.Agent.Scanner.Host.ScannerOperationOutcome.NoAction), CancellationToken.None);
        var target = int.Parse(handheldId);
        Assert.Equal([(1, 6), (target, 45), (target, 46)], client.Actions);
    }

    [Fact]
    public async Task Missing_handheld_does_not_flash_the_cradle()
    {
        using var client = new FakeCoreScannerClient();
        await using var probe = new ZebraScannerProbe(client);
        var source = new ScanSource("zebra:serial:CRADLE", "1", ScanSourceType.ZebraSnapi, "CR2278", "CRADLE");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await probe.ExecuteFeedbackAsync(source, new ScannerFeedback(
                ScannerIndicatorColor.Green, ScannerTonePattern.Rising, TimeSpan.Zero), CancellationToken.None));
        Assert.Equal([(1, 23)], client.Actions);
    }

    private sealed class FakeCoreScannerClient : IZebraCoreScannerClient
    {
        private readonly TaskCompletionSource eventsRegistered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<ZebraCoreScannerEventArgs>? BarcodeReceived;

        public event EventHandler<ZebraCoreScannerEventArgs>? PnpChanged;

        public string ScannerXml { get; init; } = "<scanners />";

        public IReadOnlyCollection<short> OpenedScannerTypes { get; private set; } = [];

        public IReadOnlyCollection<short> RegisteredEventIds { get; private set; } = [];

        public bool EventsRegistered { get; private set; }

        public bool CloseCalled { get; private set; }

        public bool DisposeCalled { get; private set; }

        public List<(int ScannerId, int Action)> Actions { get; } = [];

        public void Open(IReadOnlyCollection<short> scannerTypes) =>
            OpenedScannerTypes = [.. scannerTypes];

        public string GetScanners() => ScannerXml;

        public string GetDeviceTopology() => ScannerXml;

        public void RegisterForEvents(IReadOnlyCollection<short> eventIds)
        {
            RegisteredEventIds = [.. eventIds];
            EventsRegistered = true;
            eventsRegistered.TrySetResult();
        }

        public void ExecuteAction(int scannerId, int action) => Actions.Add((scannerId, action));

        public void Close() => CloseCalled = true;

        public void Dispose() => DisposeCalled = true;

        public void EmitBarcode(string xml) =>
            BarcodeReceived?.Invoke(this, new ZebraCoreScannerEventArgs(1, xml));

        public void EmitPnp(short eventType, string xml) =>
            PnpChanged?.Invoke(this, new ZebraCoreScannerEventArgs(eventType, xml));

        public Task WaitUntilEventsRegisteredAsync() =>
            eventsRegistered.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
