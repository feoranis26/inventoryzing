using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Xml;
using Inventoryzing.Agent.Core;

namespace Inventoryzing.Agent.Zebra;

public sealed class ZebraScannerProbe : IScannerProbe
{
    internal const short SnapiScannerType = 2;
    internal const short BarcodeEventId = 1;
    internal const short PnpEventId = 16;

    private const short DecodeGoodEventType = 1;
    private const short ScannerAttachedEventType = 0;
    private const short ScannerDetachedEventType = 1;

    private readonly IZebraCoreScannerClient client;
    private readonly Channel<ScanEvent> scans;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly ScannerConnectionTracker connectionTracker = new();
    private readonly TimeProvider timeProvider;
    private readonly Lock connectionLock = new();
    private bool isOpen;
    private bool eventsRegistered;
    private bool disposed;

    public IReadOnlyCollection<ScannerConnection> Connections
    {
        get
        {
            lock (connectionLock)
            {
                return connectionTracker.Connections;
            }
        }
    }

    public ZebraScannerProbe(int eventBufferCapacity = 256, TimeProvider? timeProvider = null)
        : this(new ZebraCoreScannerComClient(), eventBufferCapacity, timeProvider)
    {
    }

    public ZebraScannerProbe(
        IZebraCoreScannerClient client,
        int eventBufferCapacity = 256,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventBufferCapacity);

        this.client = client;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        scans = Channel.CreateBounded<ScanEvent>(new BoundedChannelOptions(eventBufferCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        client.BarcodeReceived += HandleBarcodeReceived;
        client.PnpChanged += HandlePnpChanged;
    }

    public async ValueTask<IReadOnlyList<ScanSource>> EnumerateAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            OpenIfNeeded();
            cancellationToken.ThrowIfCancellationRequested();

            var sources = ZebraCoreScannerXml.ParseScanners(client.GetScanners());
            lock (connectionLock)
            {
                foreach (var source in sources)
                {
                    connectionTracker.Connect(source);
                }
            }

            return sources;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async IAsyncEnumerable<ScanEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            OpenIfNeeded();
            if (!eventsRegistered)
            {
                client.RegisterForEvents([BarcodeEventId, PnpEventId]);
                eventsRegistered = true;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        await foreach (var scan in scans.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return scan;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lifetimeCancellation.Cancel();
            scans.Writer.TryComplete();
            client.BarcodeReceived -= HandleBarcodeReceived;
            client.PnpChanged -= HandlePnpChanged;

            try
            {
                if (isOpen)
                {
                    client.Close();
                    isOpen = false;
                }
            }
            finally
            {
                client.Dispose();
                lifetimeCancellation.Dispose();
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask ExecuteFeedbackAsync(
        ScanSource source,
        ScannerFeedback feedback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(feedback);
        if (feedback.FlashDuration < TimeSpan.Zero || feedback.FlashDuration > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(feedback));
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            OpenIfNeeded();
            if (!int.TryParse(source.RuntimeId, out var scannerId) || scannerId <= 0)
            {
                throw new InvalidOperationException("The scanner runtime ID is not valid for CoreScanner.");
            }

            var (ledOn, ledOff) = feedback.Color switch
            {
                ScannerIndicatorColor.Green => (43, 42),
                ScannerIndicatorColor.Amber => (45, 46),
                ScannerIndicatorColor.Red => (47, 48),
                _ => throw new ArgumentOutOfRangeException(nameof(feedback)),
            };
            var tone = feedback.Tone switch
            {
                ScannerTonePattern.Rising => 23,
                ScannerTonePattern.RisingDoubleHigh => 23,
                ScannerTonePattern.Falling => 22,
                ScannerTonePattern.DoubleShort => 1,
                ScannerTonePattern.DoubleLowShort => 6,
                _ => throw new ArgumentOutOfRangeException(nameof(feedback)),
            };
            client.ExecuteAction(scannerId, tone);
            if (feedback.Tone == ScannerTonePattern.RisingDoubleHigh)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
                client.ExecuteAction(scannerId, 1);
            }
            var ledSource = source.Model?.StartsWith("CR", StringComparison.OrdinalIgnoreCase) == true
                ? ZebraCoreScannerXml.FindHandheld(client.GetDeviceTopology(), source)
                : source;
            if (ledSource is null || !int.TryParse(ledSource.RuntimeId, out var ledScannerId) || ledScannerId <= 0)
            {
                throw new InvalidOperationException("Cannot identify a unique handheld LED target for this cradle.");
            }
            client.ExecuteAction(ledScannerId, ledOn);
            try
            {
                if (feedback.FlashDuration > TimeSpan.Zero)
                {
                    await Task.Delay(feedback.FlashDuration, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                client.ExecuteAction(ledScannerId, ledOff);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private void OpenIfNeeded()
    {
        if (isOpen)
        {
            return;
        }

        client.Open([SnapiScannerType]);
        isOpen = true;
    }

    private void HandleBarcodeReceived(object? sender, ZebraCoreScannerEventArgs eventArgs)
    {
        if (eventArgs.EventType != DecodeGoodEventType || Volatile.Read(ref disposed))
        {
            return;
        }

        try
        {
            var frame = ZebraCoreScannerXml.ParseBarcode(eventArgs.Xml);
            lock (connectionLock)
            {
                var existingConnection = connectionTracker.Connections.FirstOrDefault(connection =>
                    string.Equals(connection.Source.StableId, frame.Source.StableId, StringComparison.Ordinal));
                if (existingConnection is null || existingConnection.State == ScannerConnectionState.Disconnected)
                {
                    connectionTracker.Connect(frame.Source);
                }
                else if (!connectionTracker.IsCurrent(frame.Source))
                {
                    return;
                }
            }

            var scan = new ScanEvent(
                Guid.NewGuid(),
                ZebraCoreScannerXml.DecodePayload(frame.PayloadBytes),
                frame.Source,
                timeProvider.GetUtcNow(),
                frame.Symbology);
            WriteScan(scan);
        }
        catch (Exception exception) when (exception is XmlException or FormatException or ArgumentException)
        {
        }
    }

    private void HandlePnpChanged(object? sender, ZebraCoreScannerEventArgs eventArgs)
    {
        if (Volatile.Read(ref disposed))
        {
            return;
        }

        try
        {
            var sources = ZebraCoreScannerXml.ParseScanners(eventArgs.Xml);
            lock (connectionLock)
            {
                foreach (var source in sources)
                {
                    if (eventArgs.EventType == ScannerAttachedEventType)
                    {
                        connectionTracker.Connect(source);
                    }
                    else if (eventArgs.EventType == ScannerDetachedEventType)
                    {
                        connectionTracker.Disconnect(source.RuntimeId);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is XmlException or FormatException or ArgumentException)
        {
        }
    }

    private void WriteScan(ScanEvent scan)
    {
        try
        {
            if (!scans.Writer.TryWrite(scan))
            {
                scans.Writer.WriteAsync(scan, lifetimeCancellation.Token).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disposed))
        {
        }
        catch (ChannelClosedException) when (Volatile.Read(ref disposed))
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
