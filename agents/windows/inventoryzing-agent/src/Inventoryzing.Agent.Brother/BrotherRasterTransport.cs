using System.Net.Sockets;

namespace Inventoryzing.Agent.Brother;

public interface IBrotherRasterTransport : IAsyncDisposable
{
    ValueTask ConnectAsync(CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken);
}

/// <summary>
/// A small TCP-only transport. It has no discovery, printing policy, or retry behavior;
/// callers must configure a verified endpoint and own the connection lifetime.
/// </summary>
public sealed class BrotherRasterTcpTransport : IBrotherRasterTransport
{
    private readonly string host;
    private readonly int port;
    private readonly TcpClient client = new();
    private NetworkStream? stream;

    public BrotherRasterTcpTransport(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        if (port > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        this.host = host;
        this.port = port;
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        if (stream is not null)
        {
            throw new InvalidOperationException("The Brother raster transport is already connected.");
        }

        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        stream = client.GetStream();
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        GetStream().WriteAsync(bytes, cancellationToken);

    public ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken) =>
        GetStream().ReadExactlyAsync(destination, cancellationToken);

    public ValueTask DisposeAsync()
    {
        stream?.Dispose();
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private NetworkStream GetStream() => stream ?? throw new InvalidOperationException(
        "Connect the Brother raster transport before reading or writing.");
}

/// <summary>
/// Owns one protocol connection. Once page bytes start, the Brother protocol allows automatic
/// status frames only; a second status-request command is rejected locally.
/// </summary>
public sealed class BrotherRasterSession(IBrotherRasterTransport transport) : IAsyncDisposable
{
    private bool connected;
    private bool pageWritten;

    public async ValueTask<BrotherRasterStatus> PreflightAsync(CancellationToken cancellationToken)
    {
        if (pageWritten)
        {
            throw new InvalidOperationException("A status request is forbidden after raster bytes are sent.");
        }

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await transport.WriteAsync(BrotherRasterStatusParser.StatusRequest.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        return await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendPageAsync(ReadOnlyMemory<byte> page, CancellationToken cancellationToken)
    {
        if (pageWritten)
        {
            throw new InvalidOperationException("Only one physical page may be sent on this session.");
        }

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await transport.WriteAsync(page, cancellationToken).ConfigureAwait(false);
        pageWritten = true;
    }

    public ValueTask<BrotherRasterStatus> ReadAutomaticStatusAsync(CancellationToken cancellationToken)
    {
        if (!pageWritten)
        {
            throw new InvalidOperationException("Automatic print status is available only after page dispatch.");
        }

        return ReadStatusAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => transport.DisposeAsync();

    private async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        if (connected)
        {
            return;
        }

        await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        connected = true;
    }

    private async ValueTask<BrotherRasterStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var frame = new byte[BrotherRasterStatusParser.StatusLength];
        await transport.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        return BrotherRasterStatusParser.Parse(frame);
    }
}
