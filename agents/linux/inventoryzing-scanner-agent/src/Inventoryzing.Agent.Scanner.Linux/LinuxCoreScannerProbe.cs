using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using System.Xml;
using Inventoryzing.Agent.Core;
using Inventoryzing.Agent.Zebra;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventoryzing.Agent.Scanner.Linux;

/// <summary>
/// Adapts the Zebra Linux SDK bridge to inventoryzing's scanner contract. The bridge
/// owns Zebra's native C++ SDK; this process owns delivery, terminal binding, and policy.
/// </summary>
public sealed class LinuxCoreScannerProbe : IScannerProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LinuxCoreScannerOptions options;
    private readonly ILogger<LinuxCoreScannerProbe> logger;
    private readonly Channel<ScanEvent> scans = Channel.CreateBounded<ScanEvent>(
        new BoundedChannelOptions(256)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly Lock stateLock = new();
    private readonly Dictionary<string, ScanSource> sources = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private Process? bridge;
    private TaskCompletionSource<string>? topologyWaiter;
    private bool disposed;

    public LinuxCoreScannerProbe(
        IOptions<LinuxCoreScannerOptions> options,
        ILogger<LinuxCoreScannerProbe> logger,
        TimeProvider? timeProvider = null)
    {
        this.options = options.Value;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<IReadOnlyList<ScanSource>> EnumerateAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await RequestTopologyAsync(cancellationToken).ConfigureAwait(false);
        lock (stateLock)
        {
            return [.. sources.Values];
        }
    }

    public async IAsyncEnumerable<ScanEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var scan in scans.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return scan;
        }
    }

    public async ValueTask ExecuteFeedbackAsync(
        ScanSource source,
        ScannerFeedback feedback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(feedback);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var ledRuntimeId = source.RuntimeId;
        if (source.Model?.StartsWith("CR", StringComparison.OrdinalIgnoreCase) == true)
        {
            ledRuntimeId = string.Empty;
            var topology = await RequestTopologyAsync(cancellationToken).ConfigureAwait(false);
            if (topology is not null)
            {
                ledRuntimeId = ZebraCoreScannerXml.FindHandheld(topology, source)?.RuntimeId ?? string.Empty;
            }
            if (string.IsNullOrEmpty(ledRuntimeId))
            {
                logger.LogWarning("No unique handheld LED target is available for cradle {SourceId}; feedback will beep without changing the cradle LED.",
                    source.StableId);
            }
        }

        await SendCommandAsync(string.Join('\t',
            "feedback",
            source.RuntimeId,
            ledRuntimeId,
            feedback.Color.ToString(),
            feedback.Tone.ToString(),
            Math.Round(feedback.FlashDuration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
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
            scans.Writer.TryComplete();
            lock (stateLock)
            {
                topologyWaiter?.TrySetCanceled();
                topologyWaiter = null;
            }
            if (bridge is { HasExited: false })
            {
                try
                {
                    bridge.StandardInput.Close();
                    await bridge.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    bridge.Kill(entireProcessTree: true);
                }
            }
            bridge?.Dispose();
            bridge = null;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (bridge is not null)
            {
                if (bridge.HasExited)
                {
                    throw new InvalidOperationException($"Zebra CoreScanner bridge exited with code {bridge.ExitCode}.");
                }
                return;
            }
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("The Zebra Linux CoreScanner adapter can run only on Linux.");
            }

            bridge = Process.Start(new ProcessStartInfo
            {
                FileName = options.BridgePath,
                Arguments = options.BridgeArguments ?? string.Empty,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Unable to start the Zebra CoreScanner bridge.");
            _ = ReadOutputAsync(bridge);
            _ = ReadErrorsAsync(bridge);
            logger.LogInformation("Started Zebra Linux CoreScanner bridge at {BridgePath}.", options.BridgePath);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask<string?> RequestTopologyAsync(CancellationToken cancellationToken)
    {
        Task<string> wait;
        lock (stateLock)
        {
            if (topologyWaiter is not null)
            {
                wait = topologyWaiter.Task;
            }
            else
            {
                topologyWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = topologyWaiter.Task;
            }
        }
        await SendCommandAsync("topology", cancellationToken).ConfigureAwait(false);
        try
        {
            return await wait.WaitAsync(options.TopologyTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Timed out waiting for Zebra CoreScanner topology; skipping handheld LED selection.");
            return null;
        }
        finally
        {
            lock (stateLock)
            {
                if (topologyWaiter?.Task == wait)
                {
                    topologyWaiter = null;
                }
            }
        }
    }

    private async ValueTask SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var input = bridge?.StandardInput ?? throw new InvalidOperationException("Zebra CoreScanner bridge is not running.");
            await input.WriteLineAsync(command).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commandGate.Release();
        }
    }

    private async Task ReadOutputAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                BridgeMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<BridgeMessage>(line, JsonOptions);
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "Ignoring malformed message from Zebra CoreScanner bridge.");
                    continue;
                }
                if (message is not null)
                {
                    HandleMessage(message);
                }
            }
        }
        catch (Exception exception) when (!disposed)
        {
            logger.LogError(exception, "Zebra CoreScanner bridge output ended unexpectedly.");
        }
        finally
        {
            scans.Writer.TryComplete(disposed ? null : new IOException("Zebra CoreScanner bridge stopped. Restart the agent and scan again."));
        }
    }

    private async Task ReadErrorsAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            logger.LogWarning("Zebra CoreScanner bridge: {Message}", line);
        }
    }

    private void HandleMessage(BridgeMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Type) || string.IsNullOrWhiteSpace(message.Xml))
        {
            if (!string.IsNullOrWhiteSpace(message.Message))
            {
                logger.LogWarning("Zebra CoreScanner bridge: {Message}", message.Message);
            }
            return;
        }

        try
        {
            switch (message.Type)
            {
                case "barcode" when message.EventType == 1:
                {
                    var frame = ZebraCoreScannerXml.ParseBarcode(message.Xml);
                    Remember(frame.Source);
                    scans.Writer.TryWrite(new ScanEvent(
                        Guid.NewGuid(),
                        ZebraCoreScannerXml.DecodePayload(frame.PayloadBytes),
                        frame.Source,
                        timeProvider.GetUtcNow(),
                        frame.Symbology));
                    break;
                }
                case "pnp":
                case "scanners":
                    foreach (var source in ZebraCoreScannerXml.ParseScanners(message.Xml))
                    {
                        Remember(source);
                    }
                    break;
                case "topology":
                    lock (stateLock)
                    {
                        topologyWaiter?.TrySetResult(message.Xml);
                    }
                    break;
            }
        }
        catch (Exception exception) when (exception is XmlException or FormatException or ArgumentException)
        {
            logger.LogWarning(exception, "Ignoring invalid CoreScanner {MessageType} XML.", message.Type);
        }
    }

    private void Remember(ScanSource source)
    {
        lock (stateLock)
        {
            sources[source.StableId] = source;
        }
    }

    private sealed record BridgeMessage(string? Type, short EventType, string? Xml, string? Message);
}
