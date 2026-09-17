using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Tests;

public sealed class ScanEventContractTests
{
    [Fact]
    public void Preserves_exact_payload_and_source_context()
    {
        var receivedAt = new DateTimeOffset(2026, 9, 15, 6, 30, 0, TimeSpan.Zero);
        var source = new ScanSource(
            StableId: "zebra:serial:ABC123",
            RuntimeId: "7",
            Type: ScanSourceType.ZebraSnapi,
            Model: "DS2208",
            SerialNumber: "ABC123");

        var scan = new ScanEvent(
            EventId: Guid.Parse("6df45d69-aa59-4f0f-ac36-a119e655f6e2"),
            Payload: "I000042",
            Source: source,
            ReceivedAt: receivedAt,
            Symbology: "QR_CODE");

        Assert.Equal("I000042", scan.Payload);
        Assert.Equal("zebra:serial:ABC123", scan.Source.StableId);
        Assert.Equal("7", scan.Source.RuntimeId);
        Assert.Equal(ScanSourceType.ZebraSnapi, scan.Source.Type);
        Assert.Equal(receivedAt, scan.ReceivedAt);
        Assert.Equal("QR_CODE", scan.Symbology);
    }

    [Fact]
    public void Repeated_payloads_are_distinct_scan_events()
    {
        var source = new ScanSource("zebra:serial:ABC123", "7", ScanSourceType.ZebraSnapi, null, "ABC123");
        var first = new ScanEvent(Guid.NewGuid(), "I000042", source, DateTimeOffset.UtcNow, "QR_CODE");
        var second = first with { EventId = Guid.NewGuid() };

        Assert.Equal(first.Payload, second.Payload);
        Assert.NotEqual(first.EventId, second.EventId);
    }
}