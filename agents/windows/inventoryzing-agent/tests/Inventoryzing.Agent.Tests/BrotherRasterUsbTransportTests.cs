namespace Inventoryzing.Agent.Tests;

public sealed class BrotherRasterUsbTransportTests
{
    [Fact]
    public void Partial_timeout_continues_without_resending_bytes()
    {
        var device = new FakeDevice((_, data, offset, count) => (offset == 0 ? -7 : 0, Math.Min(3, count)));
        BrotherRasterUsbTransport.Transfer(device, 2, [1, 2, 3, 4, 5], CancellationToken.None);
        Assert.Equal(new[] { 0, 3 }, device.Offsets);
    }

    [Fact]
    public void Repeated_timeouts_observe_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var device = new FakeDevice((_, _, _, _) => { cancellation.Cancel(); return (-7, 0); });
        Assert.ThrowsAny<OperationCanceledException>(() =>
            BrotherRasterUsbTransport.Transfer(device, 2, new byte[3], cancellation.Token));
        Assert.Single(device.Offsets);
    }

    [Fact]
    public void Disconnect_is_not_retried()
    {
        var device = new FakeDevice((_, _, _, _) => (-4, 1));
        Assert.Throws<IOException>(() => BrotherRasterUsbTransport.Transfer(device, 2, new byte[3], CancellationToken.None));
        Assert.Single(device.Offsets);
    }

    [Fact]
    public void Read_assembles_fragments_and_preserves_coalesced_frames()
    {
        var call = 0;
        var device = new FakeDevice((endpoint, data, offset, count) =>
        {
            Assert.Equal(0x81, endpoint);
            Assert.Equal(64, count);
            var size = call++ == 0 ? 10 : 54;
            Array.Fill(data, (byte)call, 0, size);
            return (0, size);
        });
        var pending = new Queue<byte>();
        var first = BrotherRasterUsbTransport.Read(device, pending, 32, CancellationToken.None);
        var second = BrotherRasterUsbTransport.Read(device, pending, 32, CancellationToken.None);
        Assert.Equal(Enumerable.Repeat((byte)1, 10).Concat(Enumerable.Repeat((byte)2, 22)), first);
        Assert.All(second, b => Assert.Equal(2, b));
        Assert.Equal(2, call);
    }

    private sealed class FakeDevice(Func<byte, byte[], int, int, (int Error, int Count)> transfer) : IUsbBulkDevice
    {
        public List<int> Offsets { get; } = [];
        public int Transfer(byte endpoint, byte[] buffer, int offset, int count, out int transferred)
        {
            Offsets.Add(offset);
            var result = transfer(endpoint, buffer, offset, count);
            transferred = result.Count;
            return result.Error;
        }
    }
}
