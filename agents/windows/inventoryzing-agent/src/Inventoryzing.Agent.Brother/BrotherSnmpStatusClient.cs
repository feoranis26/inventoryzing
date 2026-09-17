using System.Net.Sockets;

namespace Inventoryzing.Agent.Brother;

/// <summary>
/// Reads Brother's documented network status object. This deliberately implements only
/// SNMPv1 GET for the one read-only Brother OID; it cannot send an SNMP SET request.
/// </summary>
public sealed class BrotherSnmpStatusClient(string host, int port = 161)
{
    private static readonly byte[] StatusOid =
        // 1.3.6.1.4.1.2435.3.3.9.1.6.1.0 (2435 occupies two BER bytes).
        [0x2B, 0x06, 0x01, 0x04, 0x01, 0x93, 0x03, 0x03, 0x03, 0x09, 0x01, 0x06, 0x01, 0x00];
    private int nextRequestId;

    public async ValueTask<BrotherRasterStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var requestId = Interlocked.Increment(ref nextRequestId);
        using var client = new UdpClient();
        await client.Client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var request = BuildGetRequest(requestId);
        await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var response = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return ParseGetResponse(response.Buffer, requestId);
    }

    internal static byte[] BuildGetRequest(int requestId)
    {
        var varBind = Tlv(0x30, [.. Tlv(0x06, StatusOid), .. Tlv(0x05, [])]);
        var requestIdBytes = IntegerBytes(requestId);
        var getRequest = Tlv(0xA0,
        [
            .. Tlv(0x02, requestIdBytes), .. Tlv(0x02, [0]), .. Tlv(0x02, [0]),
            .. Tlv(0x30, varBind),
        ]);
        return Tlv(0x30, [.. Tlv(0x02, [0]), .. Tlv(0x04, "public"u8.ToArray()), .. getRequest]);
    }

    private static BrotherRasterStatus ParseGetResponse(ReadOnlySpan<byte> packet, int requestId)
    {
        var message = ReadTlv(ref packet, out var messageTag);
        RequireTag(messageTag, 0x30);
        _ = ReadTlv(ref message, out _); // version
        _ = ReadTlv(ref message, out _); // community
        var response = ReadTlv(ref message, out var responseTag);
        RequireTag(responseTag, 0xA2);
        var returnedId = ReadInteger(ReadTlv(ref response, out var idTag));
        RequireTag(idTag, 0x02);
        if (returnedId != requestId)
        {
            throw new InvalidDataException("Brother SNMP response did not match the status request.");
        }
        var error = ReadInteger(ReadTlv(ref response, out var errorTag));
        RequireTag(errorTag, 0x02);
        if (error != 0)
        {
            throw new InvalidDataException($"Brother SNMP status request failed with error {error}.");
        }
        _ = ReadTlv(ref response, out _); // error index
        var bindings = ReadTlv(ref response, out var bindingsTag);
        RequireTag(bindingsTag, 0x30);
        var binding = ReadTlv(ref bindings, out var bindingTag);
        RequireTag(bindingTag, 0x30);
        var oid = ReadTlv(ref binding, out var oidTag);
        RequireTag(oidTag, 0x06);
        if (!oid.SequenceEqual(StatusOid))
        {
            throw new InvalidDataException("Brother SNMP response returned an unexpected object.");
        }
        var status = ReadTlv(ref binding, out var statusTag);
        RequireTag(statusTag, 0x04);
        return BrotherRasterStatusParser.Parse(status);
    }

    private static ReadOnlySpan<byte> ReadTlv(ref ReadOnlySpan<byte> data, out byte tag)
    {
        if (data.Length < 2)
        {
            throw new InvalidDataException("Truncated Brother SNMP response.");
        }
        tag = data[0];
        var offset = 2;
        var length = (int)data[1];
        if ((length & 0x80) != 0)
        {
            var count = length & 0x7F;
            if (count is 0 or > 4 || data.Length < offset + count)
            {
                throw new InvalidDataException("Invalid Brother SNMP response length.");
            }
            length = 0;
            for (var index = 0; index < count; index++)
            {
                length = (length << 8) | data[offset++];
            }
        }
        if (data.Length < offset + length)
        {
            throw new InvalidDataException("Truncated Brother SNMP response value.");
        }
        var value = data.Slice(offset, length);
        data = data[(offset + length)..];
        return value;
    }

    private static int ReadInteger(ReadOnlySpan<byte> bytes)
    {
        if (bytes is [] or { Length: > 4 })
        {
            throw new InvalidDataException("Unsupported Brother SNMP integer.");
        }
        var value = 0;
        foreach (var valueByte in bytes)
        {
            value = (value << 8) | valueByte;
        }
        return value;
    }

    private static void RequireTag(byte actual, byte expected)
    {
        if (actual != expected)
        {
            throw new InvalidDataException("Malformed Brother SNMP response.");
        }
    }

    private static byte[] IntegerBytes(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Tlv(byte tag, ReadOnlySpan<byte> value)
    {
        if (value.Length >= 128)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        return [tag, (byte)value.Length, .. value];
    }
}
