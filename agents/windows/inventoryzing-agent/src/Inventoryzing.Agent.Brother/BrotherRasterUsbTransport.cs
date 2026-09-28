using System.Runtime.InteropServices;
using System.Text;

namespace Inventoryzing.Agent.Brother;

/// <summary>QL-820NWB USB interface 0, bulk IN 1 / OUT 2 (Brother raster reference, appendix A).
/// Requires libusb-1.0 and a WinUSB driver on Windows, or USB device permissions on Linux.
/// One owner must serialize calls and await pending I/O before disposal.</summary>
public sealed class BrotherRasterUsbTransport(string serialNumber) : IBrotherRasterTransport
{
    private readonly LibUsbDevice device = new(serialNumber);
    private readonly Queue<byte> received = new();

    public ValueTask ConnectAsync(CancellationToken cancellationToken) => new(Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        device.Open();
        cancellationToken.ThrowIfCancellationRequested();
    }, cancellationToken));

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        new(Task.Run(() => Transfer(device, 0x02, bytes.ToArray(), cancellationToken), cancellationToken));

    public async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var buffer = await Task.Run(() => Read(device, received, destination.Length, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        buffer.CopyTo(destination);
    }

    internal static byte[] Read(IUsbBulkDevice device, Queue<byte> received, int length, CancellationToken token)
    {
        var result = new byte[length];
        var offset = 0;
        // Read a whole USB packet even when the caller needs just a 32-byte frame.
        // Preserve coalesced frames and assemble fragmented responses.
        var packet = new byte[64];
        while (offset < length)
        {
            token.ThrowIfCancellationRequested();
            if (received.Count != 0)
            {
                result[offset++] = received.Dequeue();
                continue;
            }
            var error = device.Transfer(0x81, packet, 0, packet.Length, out var transferred);
            if (transferred < 0 || transferred > packet.Length)
                throw new IOException("USB returned an invalid transfer length.");
            if (error != 0 && error != -7)
                throw new IOException($"USB read failed (libusb {error}).");
            for (var i = 0; i < transferred; i++) received.Enqueue(packet[i]);
        }
        return result;
    }

    internal static void Transfer(IUsbBulkDevice device, byte endpoint, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(16384, buffer.Length - offset);
            var result = device.Transfer(endpoint, buffer, offset, count, out var transferred);
            if (transferred < 0 || transferred > count)
                throw new IOException("USB returned an invalid transfer length.");
            offset += transferred;
            // libusb may transfer some bytes even on timeout. Continue from that offset;
            // never resend the prefix or restart a partially transmitted page.
            if (result != 0 && result != -7)
                throw new IOException($"USB transfer failed (libusb {result}).");
        }
    }

    public ValueTask DisposeAsync()
    {
        device.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal interface IUsbBulkDevice
{
    int Transfer(byte endpoint, byte[] buffer, int offset, int count, out int transferred);
}

internal sealed class LibUsbDevice(string serialNumber) : IUsbBulkDevice, IDisposable
{
    private nint context;
    private nint handle;
    private bool claimed;

    public void Open()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        if (context != 0) throw new InvalidOperationException("USB transport already opened.");
        try
        {
            Check(Native.libusb_init(out context), "initialize");
            var count = Native.libusb_get_device_list(context, out var list);
            if (count < 0) throw new IOException($"USB enumeration failed ({count}).");
            try
            {
                for (nint i = 0; i < count; i++)
                {
                    var device = Marshal.ReadIntPtr(list, checked((int)i * IntPtr.Size));
                    var descriptor = new byte[18];
                    Check(Native.libusb_get_device_descriptor(device, descriptor), "read descriptor");
                    if (descriptor[8] != 0xF9 || descriptor[9] != 0x04 ||
                        descriptor[10] != 0x9D || descriptor[11] != 0x20) continue;
                    Check(Native.libusb_open(device, out var candidate), "open QL-820NWB (check driver/permissions)");
                    try
                    {
                        var serial = new byte[256];
                        var length = Native.libusb_get_string_descriptor_ascii(candidate, descriptor[16], serial, serial.Length);
                        Check(length, "read serial number");
                        if (Encoding.ASCII.GetString(serial, 0, length) != serialNumber) continue;
                        if (handle != 0) throw new IOException("Multiple USB printers match the configured serial number.");
                        handle = candidate;
                        candidate = 0;
                    }
                    finally { if (candidate != 0) Native.libusb_close(candidate); }
                }
            }
            finally { Native.libusb_free_device_list(list, 1); }
            if (handle == 0) throw new IOException("Configured QL-820NWB USB serial number was not found.");
            if (OperatingSystem.IsLinux())
                Check(Native.libusb_set_auto_detach_kernel_driver(handle, 1), "enable kernel driver reattachment");
            Check(Native.libusb_claim_interface(handle, 0), "claim printer interface");
            claimed = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public unsafe int Transfer(byte endpoint, byte[] buffer, int offset, int count, out int transferred)
    {
        if (!claimed) throw new InvalidOperationException("USB printer is not connected.");
        fixed (byte* pointer = &buffer[offset])
            return Native.libusb_bulk_transfer(handle, endpoint, (nint)pointer, count, out transferred, 250);
    }

    public void Dispose()
    {
        if (claimed) Native.libusb_release_interface(handle, 0);
        claimed = false;
        if (handle != 0) Native.libusb_close(handle);
        handle = 0;
        if (context != 0) Native.libusb_exit(context);
        context = 0;
    }

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new IOException($"Unable to {operation} (libusb {result}).");
    }

    private static class Native
    {
        private const string Library = "inventoryzing-libusb";
        static Native()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, assembly, path) =>
                name == Library ? NativeLibrary.Load(OperatingSystem.IsWindows()
                    ? "libusb-1.0.dll" : "libusb-1.0.so.0", assembly, path) : 0);
        }

        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int libusb_init(out nint context);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern void libusb_exit(nint context);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern nint libusb_get_device_list(nint context, out nint list);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern void libusb_free_device_list(nint list, int unrefDevices);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int libusb_get_device_descriptor(nint device, [Out] byte[] descriptor);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int libusb_open(nint device, out nint handle);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern void libusb_close(nint handle);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int libusb_get_string_descriptor_ascii(nint handle, byte index, [Out] byte[] data, int length);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int libusb_set_auto_detach_kernel_driver(nint handle, int enable);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int libusb_claim_interface(nint handle, int interfaceNumber);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int libusb_release_interface(nint handle, int interfaceNumber);
        [DllImport(Library, CallingConvention = CallingConvention.Winapi)]
        internal static extern int libusb_bulk_transfer(nint handle, byte endpoint, nint data, int length, out int transferred, uint timeout);
    }
}
