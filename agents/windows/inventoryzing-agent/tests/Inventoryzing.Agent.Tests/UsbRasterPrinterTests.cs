using Inventoryzing.Agent.Printer.Host;

namespace Inventoryzing.Agent.Tests;

public sealed class UsbRasterPrinterTests
{
    private static PrintRequestClaim Claim() => new(Guid.NewGuid(), "test",
        "application/vnd.inventoryzing.brother-raster", 29, 90, "die_cut", 0, 90, 1,
        Convert.ToBase64String(new byte[90]));

    [Fact]
    public async Task Completes_only_on_terminal_status_and_does_not_poll_during_printing()
    {
        var transport = new FakeTransport(Frame(), Frame(6), Frame(1));
        var printer = new UsbRasterPrinter(() => transport);
        await printer.PrintAsync(Claim(), CancellationToken.None);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(BrotherRasterStatusParser.StatusRequest.ToArray(), transport.Writes[0]);
        Assert.Equal(0x1A, transport.Writes[1][^1]);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task Wrong_media_is_rejected_before_page_bytes()
    {
        var frame = Frame();
        frame[10] = 62;
        var transport = new FakeTransport(frame);
        var printer = new UsbRasterPrinter(() => transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => printer.PrintAsync(Claim(), CancellationToken.None));
        Assert.Single(transport.Writes);
        Assert.True(transport.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Write_or_status_failure_is_unknown_and_prevents_redispatch_or_probes(bool duringWrite)
    {
        var transport = new FakeTransport(Frame()) { FailPageWrite = duringWrite };
        var opens = 0;
        var printer = new UsbRasterPrinter(() => { opens++; return transport; });
        await Assert.ThrowsAsync<UsbPrintOutcomeUnknownException>(() => printer.PrintAsync(Claim(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => printer.PrintAsync(Claim(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => printer.ProbeAsync(CancellationToken.None));
        Assert.Equal(1, opens);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task Idle_probe_cannot_interleave_with_a_print()
    {
        var transport = new FakeTransport(Frame()) { WaitForCompletion = true };
        var opens = 0;
        var printer = new UsbRasterPrinter(() => { opens++; return transport; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var print = printer.PrintAsync(Claim(), deadline.Token);
        await transport.Waiting.Task.WaitAsync(deadline.Token);
        Assert.Null(await printer.ProbeIfIdleAsync(deadline.Token));
        using var cancelled = new CancellationTokenSource();
        var probe = printer.ProbeAsync(cancelled.Token);
        Assert.Equal(1, opens);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        transport.Completion.SetResult(Frame(1));
        await print;
    }

    [Fact]
    public async Task Preflight_ignores_old_completion_and_phase_frames()
    {
        var transport = new FakeTransport(Frame(1), Frame(6), Frame());
        await using var session = new BrotherRasterSession(transport);
        var status = await session.PreflightAsync(CancellationToken.None);
        Assert.Equal(0, status.StatusType);
    }

    [Fact]
    public async Task Cancellation_after_dispatch_is_unknown()
    {
        var transport = new FakeTransport(Frame()) { WaitForCompletion = true };
        var printer = new UsbRasterPrinter(() => transport);
        using var cancellation = new CancellationTokenSource();
        var printing = printer.PrintAsync(Claim(), cancellation.Token);
        await transport.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<UsbPrintOutcomeUnknownException>(() => printing);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task Printer_error_after_dispatch_is_unknown()
    {
        var errorFrame = Frame(2);
        errorFrame[9] = 0x10;
        var transport = new FakeTransport(Frame(), errorFrame);
        var printer = new UsbRasterPrinter(() => transport);
        await Assert.ThrowsAsync<UsbPrintOutcomeUnknownException>(() => printer.PrintAsync(Claim(), CancellationToken.None));
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task Preflight_failure_allows_a_later_attempt_without_sending_page_bytes()
    {
        var failed = new FakeTransport();
        var recovered = new FakeTransport(Frame(), Frame(1));
        var transports = new Queue<IBrotherRasterTransport>([failed, recovered]);
        var printer = new UsbRasterPrinter(transports.Dequeue);
        await Assert.ThrowsAsync<IOException>(() => printer.PrintAsync(Claim(), CancellationToken.None));
        await printer.PrintAsync(Claim(), CancellationToken.None);
        Assert.Single(failed.Writes);
        Assert.Equal(2, recovered.Writes.Count);
    }

    [Fact]
    public async Task Partial_page_failure_forbids_later_status_commands_on_session()
    {
        var transport = new FakeTransport(Frame()) { FailPageWrite = true };
        await using var session = new BrotherRasterSession(transport);
        await session.PreflightAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async () => await session.SendPageAsync(new byte[100], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.PreflightAsync(CancellationToken.None));
        Assert.Equal(2, transport.Writes.Count);
    }

    [Theory]
    [InlineData("usb", "123", true)]
    [InlineData("usb", "", false)]
    [InlineData("tcp", "123", false)]
    [InlineData("invalid", "123", false)]
    public void Usb_options_require_serial_but_not_network_address(string transport, string serial, bool valid)
    {
        Assert.Equal(valid, PrinterAgentHost.ValidPrinterOptions(new PrinterAgentOptions
        {
            Enabled = true, CoordinatorUri = new Uri("http://localhost:8088/"),
            CredentialFile = Path.GetFullPath("coordinator.token"), Transport = transport, UsbSerialNumber = serial,
        }));
    }

    private static byte[] Frame(byte type = 0)
    {
        var frame = new byte[32];
        frame[0] = 0x80; frame[1] = 0x20; frame[2] = (byte)'B';
        frame[3] = (byte)'4'; frame[4] = (byte)'A'; frame[5] = (byte)'0';
        frame[10] = 29; frame[11] = 0x0B; frame[17] = 90; frame[18] = type;
        return frame;
    }

    private sealed class FakeTransport(params byte[][] frames) : IBrotherRasterTransport
    {
        private readonly Queue<byte[]> frames = new(frames);
        public List<byte[]> Writes { get; } = [];
        public bool FailPageWrite { get; init; }
        public bool WaitForCompletion { get; init; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask ConnectAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            Writes.Add(bytes.ToArray());
            if (FailPageWrite && Writes.Count == 2) throw new IOException("unplugged during write");
            return ValueTask.CompletedTask;
        }
        public async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken token)
        {
            if (frames.TryDequeue(out var frame)) { frame.CopyTo(destination); return; }
            if (!WaitForCompletion) throw new IOException("unplugged during status read");
            Waiting.SetResult();
            (await Completion.Task.WaitAsync(token)).CopyTo(destination);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
