using Inventoryzing.Agent.Brother;

namespace Inventoryzing.Agent.Tests;

public sealed class BrotherRasterProtocolTests
{
    [Fact]
    public void Parses_ready_status_and_validates_the_loaded_29_by_90_media()
    {
        var response = new byte[BrotherRasterStatusParser.StatusLength];
        response[0] = 0x80;
        response[1] = 0x20;
        response[2] = (byte)'B';
        response[3] = (byte)'4';
        response[4] = (byte)'A';
        response[5] = (byte)'0';
        response[6] = (byte)'0';
        response[10] = 29;
        response[11] = BrotherRasterProtocol.DieCutMediaType;
        response[17] = 90;

        var status = BrotherRasterStatusParser.Parse(response);

        Assert.Equal(BrotherRasterState.Ready, status.State);
        Assert.True(status.MatchesDieCutMedia(29, 90));
        Assert.Equal(new byte[] { 0x1B, 0x69, 0x53 }, BrotherRasterStatusParser.StatusRequest.ToArray());
    }

    [Fact]
    public void Classifies_error_status_as_blocked_even_if_the_printer_is_not_printing()
    {
        var response = new byte[BrotherRasterStatusParser.StatusLength];
        response[0] = 0x80;
        response[1] = 0x20;
        response[2] = (byte)'B';
        response[3] = (byte)'4';
        response[4] = (byte)'A';
        response[5] = (byte)'0';
        response[6] = (byte)'0';
        response[9] = 0x10; // cover open

        var status = BrotherRasterStatusParser.Parse(response);

        Assert.Equal(BrotherRasterState.Blocked, status.State);
    }

    [Fact]
    public void Builds_a_single_die_cut_monochrome_page_with_fixed_line_width()
    {
        var raster = new byte[2 * BrotherRasterProtocol.BytesPerRasterLine];
        raster[0] = 0x80;
        raster[^1] = 0x01;

        var output = BrotherRasterProtocol.BuildDieCutMonochromePage(29, 90, 2, raster);

        Assert.Equal(400 + 2 + 4 + 4 + 13 + 19 + 2 * 93 + 1, output.Length);
        Assert.All(output[..400], value => Assert.Equal(0, value));
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1B, 0x69, 0x61, 0x01, 0x1B, 0x69, 0x21, 0x00 },
            output[400..410]);
        Assert.Equal(new byte[] { 0x1B, 0x69, 0x7A, 0x4E, 0x0B, 29, 90, 2, 0, 0, 0, 0, 0 },
            output[410..423]);
        Assert.Equal(new byte[]
        {
            0x1B, 0x69, 0x4D, 0x40, 0x1B, 0x69, 0x41, 0x01, 0x1B, 0x69,
            0x4B, 0x08, 0x1B, 0x69, 0x64, 0x00, 0x00, (byte)'M', 0x00,
        }, output[423..442]);
        Assert.Equal((byte)'g', output[442]);
        Assert.Equal(0, output[443]);
        Assert.Equal(90, output[444]);
        Assert.Equal(0x80, output[445]);
        Assert.Equal((byte)'g', output[535]);
        Assert.Equal(0x01, output[627]);
        Assert.Equal(0x1A, output[^1]);
    }

    [Fact]
    public void Rejects_pages_that_do_not_fill_the_fixed_720_dot_line()
    {
        Assert.Throws<ArgumentException>(() =>
            BrotherRasterProtocol.BuildDieCutMonochromePage(29, 90, 1, new byte[89]));
    }

    [Theory]
    [InlineData(29, 90, 991)]
    [InlineData(62, 29, 271)]
    [InlineData(62, 100, 1109)]
    [InlineData(17, 54, 566)]
    public void Builds_normal_height_pages_with_quality_priority_and_die_cut_controls(
        int width, int height, int lines)
    {
        var output = BrotherRasterProtocol.BuildDieCutMonochromePage(
            width, height, lines, new byte[lines * BrotherRasterProtocol.BytesPerRasterLine]);

        Assert.Equal(lines, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(417, 4)));
        Assert.Equal(width, output[415]);
        Assert.Equal(height, output[416]);
        Assert.Equal(0x4E, output[413]);
        Assert.Equal(new byte[] { 0x1B, 0x69, 0x4D, 0x40 }, output[423..427]);
        Assert.Equal(0x1A, output[^1]);
    }

    [Fact]
    public void Builds_continuous_page_with_zero_media_length_and_feed_margin()
    {
        var output = BrotherRasterProtocol.BuildMonochromePage(
            true, 62, 0, 35, 521,
            new byte[521 * BrotherRasterProtocol.BytesPerRasterLine]);

        Assert.Equal(BrotherRasterProtocol.ContinuousMediaType, output[414]);
        Assert.Equal(62, output[415]);
        Assert.Equal(0, output[416]);
        Assert.Equal(521, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            output.AsSpan(417, 4)));
        Assert.Equal(35, output[438]);
        Assert.Equal(0, output[439]);
    }

    [Fact]
    public void Rejects_invalid_continuous_margin()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BrotherRasterProtocol.BuildMonochromePage(
                true, 62, 0, 0, 591,
                new byte[591 * BrotherRasterProtocol.BytesPerRasterLine]));
    }

    [Fact]
    public void Encodes_the_exact_documented_brother_snmp_status_get()
    {
        var request = BrotherSnmpStatusClient.BuildGetRequest(1);

        Assert.Equal(Convert.FromHexString(
            "302F02010004067075626C6963A02202040000000102010002010030143012060E2B060104019303030309010601000500"),
            request);
    }

    [Fact]
    public void Parses_captured_network_status_with_reserved_byte_04()
    {
        var status = BrotherRasterStatusParser.Parse(Convert.FromHexString(
            "802042344130040000001D0B00000100" +
            "005A0000000000000001000000000000"));

        Assert.Equal((byte)'A', status.ModelCode);
        Assert.Equal(BrotherRasterState.Ready, status.State);
        Assert.True(status.MatchesDieCutMedia(29, 90));
    }

    [Fact]
    public async Task Session_requests_status_only_before_sending_page_bytes()
    {
        var transport = new FakeTransport(ReadyFrame(), CompletedFrame());
        await using var session = new BrotherRasterSession(transport);

        var ready = await session.PreflightAsync(CancellationToken.None);
        await session.SendPageAsync(new byte[] { 0x1A }, CancellationToken.None);
        var completed = await session.ReadAutomaticStatusAsync(CancellationToken.None);

        Assert.Equal(BrotherRasterState.Ready, ready.State);
        Assert.Equal(BrotherRasterState.Completed, completed.State);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(new byte[] { 0x1B, 0x69, 0x53 }, transport.Writes[0]);
        Assert.Equal(new byte[] { 0x1A }, transport.Writes[1]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.PreflightAsync(CancellationToken.None));
    }

    private static byte[] ReadyFrame() => Frame(statusType: 0x00, phaseType: 0x00);

    private static byte[] CompletedFrame() => Frame(statusType: 0x01, phaseType: 0x00);

    private static byte[] Frame(byte statusType, byte phaseType)
    {
        var frame = new byte[BrotherRasterStatusParser.StatusLength];
        frame[0] = 0x80;
        frame[1] = 0x20;
        frame[2] = (byte)'B';
        frame[3] = (byte)'4';
        frame[4] = (byte)'A';
        frame[5] = (byte)'0';
        frame[6] = (byte)'0';
        frame[18] = statusType;
        frame[19] = phaseType;
        return frame;
    }

    private sealed class FakeTransport(params byte[][] reads) : IBrotherRasterTransport
    {
        private readonly Queue<byte[]> reads = new(reads);

        public List<byte[]> Writes { get; } = [];

        public ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Writes.Add(bytes.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            reads.Dequeue().CopyTo(destination);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
