using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Tests;

public sealed class ScannerConnectionTrackerTests
{
    [Fact]
    public void Reconnect_replaces_transient_runtime_id_but_preserves_stable_identity()
    {
        var tracker = new ScannerConnectionTracker();
        var first = new ScanSource("zebra:serial:ABC123", "7", ScanSourceType.ZebraSnapi, "DS2208", "ABC123");
        var reconnected = first with { RuntimeId = "12" };

        tracker.Connect(first);
        Assert.True(tracker.Disconnect("7"));
        tracker.Connect(reconnected);

        var current = Assert.Single(tracker.Connections);
        Assert.Equal(ScannerConnectionState.Connected, current.State);
        Assert.Equal("zebra:serial:ABC123", current.Source.StableId);
        Assert.Equal("12", current.Source.RuntimeId);
        Assert.False(tracker.IsCurrent(first));
        Assert.True(tracker.IsCurrent(reconnected));
    }

    [Fact]
    public void Unknown_runtime_disconnect_does_not_change_current_connection()
    {
        var tracker = new ScannerConnectionTracker();
        var source = new ScanSource("zebra:serial:ABC123", "7", ScanSourceType.ZebraSnapi, "DS2208", "ABC123");
        tracker.Connect(source);

        Assert.False(tracker.Disconnect("99"));
        Assert.True(tracker.IsCurrent(source));
    }
}