"""Read Brother's documented status OID with SNMPv1 GET; never writes or prints."""
import argparse
import json
import socket
import time


def tlv(tag, value):
    if len(value) >= 128:
        raise ValueError('Probe only encodes short BER values')
    return bytes([tag, len(value)]) + value


def read_tlv(data, offset=0):
    tag, length = data[offset:offset + 2]
    offset += 2
    if length & 128:
        count = length & 127
        length = int.from_bytes(data[offset:offset + count], 'big')
        offset += count
    end = offset + length
    if end > len(data):
        raise ValueError('Truncated BER response')
    return tag, data[offset:end], end


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--host', default='10.0.0.13')
parser.add_argument('--repeat', type=int, default=3)
parser.add_argument('--timeout', type=float, default=2)
args = parser.parse_args()
# 1.3.6.1.4.1.2435.3.3.9.1.6.1.0
oid = bytes.fromhex('2b06010401930303030901060100')
for attempt in range(1, args.repeat + 1):
    request_id = attempt.to_bytes(4, 'big')
    varbind = tlv(0x30, tlv(6, oid) + tlv(5, b''))
    pdu = tlv(0xa0, tlv(2, request_id) + tlv(2, b'\0') + tlv(2, b'\0') + tlv(0x30, varbind))
    request = tlv(0x30, tlv(2, b'\0') + tlv(4, b'public') + pdu)
    started = time.monotonic()
    result = {'attempt': attempt}
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.settimeout(args.timeout)
            sock.connect((args.host, 161))
            sock.send(request)
            packet = sock.recv(4096)
        _, message, _ = read_tlv(packet)
        _, _, offset = read_tlv(message)
        _, _, offset = read_tlv(message, offset)
        tag, response, _ = read_tlv(message, offset)
        if tag != 0xa2:
            raise ValueError('Expected GET response')
        _, actual_id, offset = read_tlv(response)
        if int.from_bytes(actual_id, 'big') != attempt:
            raise ValueError('Response request ID mismatch')
        _, error, offset = read_tlv(response, offset)
        result['snmp_error'] = int.from_bytes(error, 'big')
        _, _, offset = read_tlv(response, offset)
        _, bindings, _ = read_tlv(response, offset)
        _, binding, _ = read_tlv(bindings)
        _, returned_oid, offset = read_tlv(binding)
        if returned_oid != oid:
            raise ValueError('Response OID mismatch')
        tag, status, _ = read_tlv(binding, offset)
        result.update(value_tag=tag, status_hex=status.hex(' '), status_length=len(status))
        if tag == 4 and len(status) == 32:
            result['status'] = dict(model=chr(status[4]), error1=status[8], error2=status[9],
                                    width_mm=status[10], media_type=status[11], length_mm=status[17],
                                    status_type=status[18], phase=status[19])
    except (OSError, ValueError) as error:
        result['error'] = str(error)
    result['elapsed_ms'] = round((time.monotonic() - started) * 1000, 1)
    print(json.dumps(result), flush=True)
