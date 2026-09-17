using Inventoryzing.Agent.Core;
using Xunit.Abstractions;

namespace Inventoryzing.Agent.Tests;

public sealed class ZebraHardwareAcceptanceTests(ITestOutputHelper output)
{
    public const string EnumerateScenario = "zebra-enumerate";
    public const string ScanScenario = "zebra-scan";
    public const string ReconnectScenario = "zebra-reconnect";
    public const string RapidScenario = "zebra-rapid";

    private const string ExpectedPayloadEnvironmentVariable = "INVENTORYZING_ZEBRA_EXPECTED_PAYLOAD";
    private const string SerialEnvironmentVariable = "INVENTORYZING_ZEBRA_SERIAL";
    private const string ScanCountEnvironmentVariable = "INVENTORYZING_ZEBRA_SCAN_COUNT";
    private const string TimeoutEnvironmentVariable = "INVENTORYZING_ZEBRA_TIMEOUT_SECONDS";
    private const string SkipReason =
        "Requires explicit physical-test authorization, a connected SNAPI scanner, and operator-directed actions.";

    [HardwareFact(EnumerateScenario, SkipReason)]
    [Trait("Category", "Hardware")]
    public async Task Enumerates_connected_snapi_scanner_with_stable_serial_identity()
    {
        await using var probe = new ZebraScannerProbe();
        using var cancellation = new CancellationTokenSource(ReadTimeout());

        var sources = await probe.EnumerateAsync(cancellation.Token);

        Assert.NotEmpty(sources);
        WriteSources(sources);
        Assert.All(sources, source =>
        {
            Assert.False(string.IsNullOrWhiteSpace(source.SerialNumber));
            Assert.Equal($"zebra:serial:{source.SerialNumber}", source.StableId);
            Assert.False(string.IsNullOrWhiteSpace(source.RuntimeId));
            Assert.Equal(ScanSourceType.ZebraSnapi, source.Type);
        });
        _ = SelectTarget(sources);
    }

    [HardwareFact(ScanScenario, SkipReason)]
    [Trait("Category", "Hardware")]
    public async Task Barcode_event_preserves_payload_symbology_source_and_unique_event_id()
    {
        var expectedPayload = RequiredEnvironmentVariable(ExpectedPayloadEnvironmentVariable);
        var timeout = ReadTimeout();
        await using var probe = new ZebraScannerProbe();
        var target = SelectTarget(await probe.EnumerateAsync(CancellationToken.None));
        await using var scans = probe.WatchAsync(CancellationToken.None).GetAsyncEnumerator();

        output.WriteLine("Scanner ready. Scan the expected barcode once.");
        var scan = await ReadScanAsync(scans, timeout);

        Assert.Equal(expectedPayload, scan.Payload);
        Assert.Equal(target.StableId, scan.Source.StableId);
        Assert.Equal(target.SerialNumber, scan.Source.SerialNumber);
        Assert.NotEqual(Guid.Empty, scan.EventId);
        Assert.False(string.IsNullOrWhiteSpace(scan.Symbology));
        Assert.True(scan.ReceivedAt <= DateTimeOffset.UtcNow);
        output.WriteLine(
            "Received {0} from {1} as {2} with event {3}.",
            scan.Payload,
            scan.Source.StableId,
            scan.Symbology,
            scan.EventId);
    }

    [HardwareFact(ReconnectScenario, SkipReason)]
    [Trait("Category", "Hardware")]
    public async Task Unplug_and_replug_preserves_stable_identity_and_resumes_scanning()
    {
        var expectedPayload = RequiredEnvironmentVariable(ExpectedPayloadEnvironmentVariable);
        var timeout = ReadTimeout();
        await using var probe = new ZebraScannerProbe();
        var original = SelectTarget(await probe.EnumerateAsync(CancellationToken.None));
        await using var scans = probe.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        var scanTask = ReadScanAsync(scans, timeout);

        output.WriteLine("Scanner ready. Unplug it, reconnect it, then scan the expected barcode once.");
        var disconnected = await WaitForConnectionStateAsync(
            probe,
            original.StableId,
            ScannerConnectionState.Disconnected,
            timeout);
        var reconnected = await WaitForConnectionStateAsync(
            probe,
            original.StableId,
            ScannerConnectionState.Connected,
            timeout);
        var scan = await scanTask;

        Assert.Equal(expectedPayload, scan.Payload);
        Assert.Equal(original.StableId, disconnected.Source.StableId);
        Assert.Equal(original.StableId, reconnected.Source.StableId);
        Assert.Equal(reconnected.Source.RuntimeId, scan.Source.RuntimeId);
        output.WriteLine(
            "Reconnect survived: stable ID {0}, runtime ID {1} -> {2}.",
            original.StableId,
            original.RuntimeId,
            reconnected.Source.RuntimeId);
    }

    [HardwareFact(RapidScenario, SkipReason)]
    [Trait("Category", "Hardware")]
    public async Task Rapid_repeated_scans_are_not_dropped_or_deduplicated_by_payload()
    {
        var expectedPayload = RequiredEnvironmentVariable(ExpectedPayloadEnvironmentVariable);
        var expectedCount = ReadScanCount();
        var timeout = ReadTimeout();
        await using var probe = new ZebraScannerProbe(eventBufferCapacity: expectedCount);
        var target = SelectTarget(await probe.EnumerateAsync(CancellationToken.None));
        await using var scans = probe.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        List<ScanEvent> received = [];

        output.WriteLine("Scanner ready. Scan the same expected barcode {0} times rapidly.", expectedCount);
        while (received.Count < expectedCount)
        {
            received.Add(await ReadScanAsync(scans, timeout));
        }

        Assert.All(received, scan =>
        {
            Assert.Equal(expectedPayload, scan.Payload);
            Assert.Equal(target.StableId, scan.Source.StableId);
        });
        Assert.Equal(expectedCount, received.Select(scan => scan.EventId).Distinct().Count());
        output.WriteLine("Received all {0} distinct deliveries.", expectedCount);
    }

    private static async Task<ScanEvent> ReadScanAsync(
        IAsyncEnumerator<ScanEvent> scans,
        TimeSpan timeout)
    {
        var hasScan = await scans.MoveNextAsync().AsTask().WaitAsync(timeout);
        Assert.True(hasScan, "The scan stream ended before a barcode was received.");
        return scans.Current;
    }

    private static async Task<ScannerConnection> WaitForConnectionStateAsync(
        ZebraScannerProbe probe,
        string stableId,
        ScannerConnectionState expectedState,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            var connection = probe.Connections.FirstOrDefault(candidate =>
                string.Equals(candidate.Source.StableId, stableId, StringComparison.Ordinal) &&
                candidate.State == expectedState);
            if (connection is not null)
            {
                return connection;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Scanner '{stableId}' did not reach state '{expectedState}' within {timeout}.");
            }
        }
    }

    private ScanSource SelectTarget(IReadOnlyList<ScanSource> sources)
    {
        WriteSources(sources);
        var expectedSerial = Environment.GetEnvironmentVariable(SerialEnvironmentVariable)?.Trim();
        if (string.IsNullOrEmpty(expectedSerial))
        {
            return Assert.Single(sources);
        }

        return Assert.Single(sources, source =>
            string.Equals(source.SerialNumber, expectedSerial, StringComparison.Ordinal));
    }

    private void WriteSources(IEnumerable<ScanSource> sources)
    {
        foreach (var source in sources)
        {
            output.WriteLine(
                "Scanner: stable={0}, runtime={1}, model={2}, serial={3}",
                source.StableId,
                source.RuntimeId,
                source.Model ?? "<unknown>",
                source.SerialNumber ?? "<unknown>");
        }
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException($"Set {name} to the exact barcode payload before running this scenario.")
            : value;
    }

    private static int ReadScanCount()
    {
        const int defaultCount = 5;
        var value = Environment.GetEnvironmentVariable(ScanCountEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultCount;
        }

        return int.TryParse(value, out var count) && count is >= 2 and <= 256
            ? count
            : throw new InvalidOperationException(
                $"{ScanCountEnvironmentVariable} must be an integer from 2 through 256.");
    }

    private static TimeSpan ReadTimeout()
    {
        const int defaultSeconds = 60;
        var value = Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.FromSeconds(defaultSeconds);
        }

        return int.TryParse(value, out var seconds) && seconds is >= 5 and <= 600
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidOperationException(
                $"{TimeoutEnvironmentVariable} must be an integer from 5 through 600.");
    }
}