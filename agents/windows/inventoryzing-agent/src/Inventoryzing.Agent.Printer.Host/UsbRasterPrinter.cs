using Inventoryzing.Agent.Brother;

namespace Inventoryzing.Agent.Printer.Host;

/// <summary>Serializes idle probes and whole print sessions on the same physical USB device.</summary>
public sealed class UsbRasterPrinter(Func<IBrotherRasterTransport> createTransport)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool outcomeUnknown;

    public async Task<BrotherRasterStatus?> ProbeIfIdleAsync(CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) return null;
        try { return await ReadPreflightAsync(token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<BrotherRasterStatus> ProbeAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await ReadPreflightAsync(token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<BrotherRasterStatus> ReadPreflightAsync(CancellationToken token)
    {
        EnsureResolved();
        await using var session = new BrotherRasterSession(createTransport());
        return await session.PreflightAsync(token).ConfigureAwait(false);
    }

    public async Task PrintAsync(PrintRequestClaim claim, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        var dispatched = false;
        try
        {
            EnsureResolved();
            await using var session = new BrotherRasterSession(createTransport());
            var status = await session.PreflightAsync(token).ConfigureAwait(false);
            var continuous = claim.MediaKind == "continuous";
            var length = continuous ? (byte)0 : checked((byte)claim.HeightMillimeters);
            if (status.ModelCode != (byte)'A')
                throw new InvalidOperationException("The USB device did not report a QL-820NWB model.");
            if (status.State != BrotherRasterState.Ready)
                throw new InvalidOperationException("The USB printer needs attention. Check its cover, roll, and cutter.");
            if (!status.MatchesMedia(continuous, checked((byte)claim.WidthMillimeters), length))
                throw new InvalidOperationException("The installed USB printer roll does not match the requested label media.");
            var page = BrotherRasterProtocol.BuildMonochromePage(continuous,
                (int)claim.WidthMillimeters, length, claim.FeedMarginDots, claim.RasterLines, claim.GetRaster());
            token.ThrowIfCancellationRequested();
            dispatched = true;
            await session.SendPageAsync(page, token).ConfigureAwait(false);
            while (true)
            {
                status = await session.ReadAutomaticStatusAsync(token).ConfigureAwait(false);
                if (status.State == BrotherRasterState.Blocked)
                    throw new IOException("The USB printer reported an error during printing. Check its cover, roll, and cutter.");
                if (status.State == BrotherRasterState.Completed) return;
            }
        }
        catch (Exception exception) when (dispatched)
        {
            // Never probe, reset, or send another page after losing completion evidence.
            outcomeUnknown = true;
            throw new UsbPrintOutcomeUnknownException(exception);
        }
        finally { gate.Release(); }
    }

    private void EnsureResolved()
    {
        if (outcomeUnknown)
            throw new InvalidOperationException(
                "USB print outcome is unknown. Inspect the printer and clear any pending output, then restart the printer agent.");
    }
}

public sealed class UsbPrintOutcomeUnknownException(Exception innerException)
    : IOException("USB printing was interrupted after dispatch began. Inspect the printer before trying again; " +
        "the label may have printed. Restart the printer agent after clearing pending output.", innerException);
