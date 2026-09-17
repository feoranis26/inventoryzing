"""Print ONE timestamp label on a QL-820NWB with DK-1209 (62 x 29 mm).

Standard library only. Keep the inventoryzing agent stopped while testing.
  python scripts/print-test-label.py --dry-run
  python scripts/print-test-label.py --host 10.0.0.13
No coordinator, database, spooler, retries, or saved label files.
Use --dk1221-no-margin for one experimental 23 x 23 mm edge/grid pattern.
This sends the full physical height; firmware may still add die-cut feed offsets.
Protocol: Brother QL-800/810W/820NWB Raster Command Reference 1.01;
network status OID from Brother's developer command FAQ.
"""
import argparse
from datetime import datetime
import select
import socket
import struct
import time


# Small bitmap digits avoid dependencies on fonts or imaging libraries.
FONT = dict(zip('0123456789-:+ ', (
    '01110 10001 10011 10101 11001 10001 01110',
    '00100 01100 00100 00100 00100 00100 01110',
    '01110 10001 00001 00010 00100 01000 11111',
    '11110 00001 00001 01110 00001 00001 11110',
    '00010 00110 01010 10010 11111 00010 00010',
    '11111 10000 10000 11110 00001 00001 11110',
    '01110 10000 10000 11110 10001 10001 01110',
    '11111 00001 00010 00100 01000 01000 01000',
    '01110 10001 10001 01110 10001 10001 01110',
    '01110 10001 10001 01111 00001 00001 01110',
    '00000 00000 00000 11111 00000 00000 00000',
    '00000 00100 00100 00000 00100 00100 00000',
    '00000 00100 00100 11111 00100 00100 00000',
    '00000 00000 00000 00000 00000 00000 00000',
)))


def make_page(stamp):
    # 720 head pins; 696 printable columns at x=12, 271 printable rows.
    # The printer supplies die-cut feed offsets; do NOT send the physical
    # label's 341 rows (that double-counts the 35-dot top/bottom offsets).
    raster = bytearray(271 * 90)
    for text, y, scale in ((stamp.strftime('%Y-%m-%d'), 35, 7),
                           (stamp.strftime('%H:%M:%S'), 110, 7),
                           (stamp.strftime('%z'), 185, 5)):
        left = (720 - (len(text) * 6 - 1) * scale) // 2
        for index, char in enumerate(text):
            for row, bits in enumerate(FONT[char].split()):
                for col, bit in enumerate(bits):
                    if bit == '1':
                        for dy in range(scale):
                            for dx in range(scale):
                                x = left + (index * 6 + col) * scale + dx
                                # Head pin order is opposite to visual left-to-right.
                                x = 719 - x
                                offset = (y + row * scale + dy) * 90 + x // 8
                                raster[offset] |= 0x80 >> (x % 8)
    page = bytearray(400)
    page += b'\x1b@\x1bia\x01\x1bi!\x00'  # initialize, raster, notify
    # 0x0e validates media dimensions/type; 0x40 prioritizes print quality.
    page += b'\x1biz\x4e\x0b\x3e\x1d' + struct.pack('<I', 271) + b'\0\0'
    page += b'\x1biM\x40\x1biA\x01\x1biK\x08'  # cut one, 300 dpi
    page += b'\x1bid\0\0M\0'  # die-cut margin zero, no compression
    for row in range(271):
        page += b'g\0\x5a' + raster[row * 90:(row + 1) * 90]
    return bytes(page + b'\x1a')  # exactly one final-page print command


def make_margin_test(normal_height=False):
    """Full-width DK-1221 raster, optionally retaining normal die-cut feed length."""
    dots = round(23 * 300 / 25.4)
    rows = 202 if normal_height else dots
    # Documented print band starts at 442; physical label starts 18 pins earlier.
    left_pin = 442 - 18
    raster = bytearray(rows * 90)
    ticks = {round(mm * 300 / 25.4) for mm in range(1, 23)}
    major = {round(mm * 300 / 25.4) for mm in (5, 10, 15, 20)}
    for y in range(rows):
        for x in range(dots):
            # Thin perimeter, 5 mm grid, 1 mm edge ticks, and diagonal.
            dark = (x in (0, dots - 1) or y in (0, rows - 1)
                    or x in major or y in major or x == y
                    or (x in ticks and (y < 8 or y >= rows - 8))
                    or (y in ticks and (x < 8 or x >= dots - 8)))
            if dark:
                pin = 719 - (left_pin + x)
                raster[y * 90 + pin // 8] |= 0x80 >> (pin % 8)
    page = bytearray(400)
    page += b'\x1b@\x1bia\x01\x1bi!\x00'
    page += b'\x1biz\x4e\x0b\x17\x17' + struct.pack('<I', rows) + b'\0\0'
    page += b'\x1biM\x40\x1biA\x01\x1biK\x08'
    page += b'\x1bid\0\0M\0'
    for row in range(rows):
        page += b'g\0\x5a' + raster[row * 90:(row + 1) * 90]
    return bytes(page + b'\x1a')


def tlv(tag, value):
    if len(value) >= 128:
        raise ValueError('SNMP request too long')
    return bytes((tag, len(value))) + value


def read_tlv(data, offset=0):
    if offset + 2 > len(data):
        raise ValueError('Truncated SNMP response')
    tag, length = data[offset:offset + 2]
    offset += 2
    if length & 128:
        count = length & 127
        if not count or offset + count > len(data):
            raise ValueError('Invalid SNMP length')
        length = int.from_bytes(data[offset:offset + count], 'big')
        offset += count
    if offset + length > len(data):
        raise ValueError('Truncated SNMP value')
    return tag, data[offset:offset + length], offset + length


def get_status(host, request_id):
    oid = bytes.fromhex('2b06010401930303030901060100')
    binding = tlv(0x30, tlv(6, oid) + tlv(5, b''))
    pdu = tlv(0xa0, tlv(2, request_id.to_bytes(4, 'big'))
              + tlv(2, b'\0') + tlv(2, b'\0') + tlv(0x30, binding))
    request = tlv(0x30, tlv(2, b'\0') + tlv(4, b'public') + pdu)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.settimeout(0.5)
        sock.connect((host, 161))
        sock.send(request)
        packet = sock.recv(4096)
    _, message, _ = read_tlv(packet)
    _, _, offset = read_tlv(message)
    _, _, offset = read_tlv(message, offset)
    tag, response, _ = read_tlv(message, offset)
    if tag != 0xa2:
        raise ValueError('Not an SNMP GET response')
    _, actual_id, offset = read_tlv(response)
    _, error, offset = read_tlv(response, offset)
    if int.from_bytes(actual_id, 'big') != request_id or int.from_bytes(error, 'big'):
        raise ValueError('SNMP request ID mismatch or error')
    _, _, offset = read_tlv(response, offset)
    _, bindings, _ = read_tlv(response, offset)
    _, binding, _ = read_tlv(bindings)
    _, actual_oid, offset = read_tlv(binding)
    tag, status, _ = read_tlv(binding, offset)
    if actual_oid != oid or tag != 4 or len(status) != 32 or status[:4] != b'\x80\x20B4':
        raise ValueError('Unexpected Brother status response')
    return status


def describe(status):
    kind = {0: 'status reply', 1: 'PRINTING COMPLETED', 2: 'error',
            4: 'turned off', 5: 'notification', 6: 'phase change'}.get(status[18], 'other')
    phase = {0: 'receiving/idle', 1: 'printing'}.get(status[19], 'other')
    return (f'{kind}; phase={phase}; media={status[10]}x{status[17]}mm '
            f'type=0x{status[11]:02x}; errors=0x{status[8]:02x}/0x{status[9]:02x}; '
            f'raw={status.hex(" ")}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', default='10.0.0.13')
    parser.add_argument('--seconds', type=float, default=15, help='Live observation duration')
    parser.add_argument('--dry-run', action='store_true', help='Build label only; no network access')
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--dk1221-no-margin', action='store_true',
                        help='Experimental full-size DK-1221 grid, with no software margins')
    modes.add_argument('--dk1221-full-width', action='store_true',
                       help='DK-1221 full-width grid with normal 202-row height and zero feed margin')
    args = parser.parse_args()
    if not 1 <= args.seconds <= 120:
        parser.error('--seconds must be between 1 and 120')
    stamp = datetime.now().astimezone()
    dk1221 = args.dk1221_no_margin or args.dk1221_full_width
    page = make_margin_test(args.dk1221_full_width) if dk1221 else make_page(stamp)
    required_media = (23, 11, 23) if dk1221 else (62, 11, 29)
    label = ('DK-1221 full-width, 202-row test' if args.dk1221_full_width
             else 'DK-1221 full-size margin test' if dk1221 else 'DK-1209')
    started = time.monotonic()

    def log(message):
        print(f'{datetime.now().astimezone().isoformat(timespec="milliseconds")} '
              f'+{time.monotonic() - started:6.3f}s {message}', flush=True)

    log(f'ONE {label} label: {stamp.isoformat(timespec="seconds")}; {len(page)} bytes')
    if args.dry_run:
        log('Dry run: page built; no network access or printing.')
        return 0
    before = get_status(args.host, 1)
    log('Preflight: ' + describe(before))
    if (before[4] != ord('A') or before[8] or before[9] or before[19] != 0
            or (before[10], before[11], before[17]) != required_media):
        raise ValueError(f'Requires idle, error-free QL-820NWB with '
                         f'{required_media[0]} x {required_media[2]} mm die-cut media')
    completed = False
    had_error = False
    previous = None
    last_output = 0.0
    tcp_buffer = bytearray()
    with socket.create_connection((args.host, 9100), timeout=3) as printer:
        log('TCP connected; sending one page (no automatic retry).')
        printer.sendall(page)
        log('TCP send finished. Observing feedback; this is NOT confirmation of printing.')
        deadline = time.monotonic() + args.seconds
        request_id = 1
        tcp_open = True
        while time.monotonic() < deadline:
            if tcp_open and select.select([printer], [], [], 0)[0]:
                chunk = printer.recv(4096)
                if not chunk:
                    tcp_open = False
                    log('TCP peer closed.')
                else:
                    log('TCP received: ' + chunk.hex(' '))
                    tcp_buffer.extend(chunk)
                    while len(tcp_buffer) >= 32:
                        status = bytes(tcp_buffer[:32])
                        del tcp_buffer[:32]
                        if status[:4] == b'\x80\x20B4':
                            log('TCP status: ' + describe(status))
                            completed |= status[18] == 1 and not (status[8] or status[9])
                            had_error |= bool(status[8] or status[9])
            request_id += 1
            try:
                status = get_status(args.host, request_id)
                now = time.monotonic()
                if status != previous or now - last_output >= 1:
                    log('SNMP: ' + describe(status))
                    previous, last_output = status, now
                completed |= status[18] == 1 and not (status[8] or status[9])
                had_error |= bool(status[8] or status[9])
            except (OSError, ValueError) as error:
                log(f'SNMP read failed: {error}')
            time.sleep(0.1)
    if had_error:
        log('Printer reported an error; inspect the trace and physical output. No retry.')
        return 1
    if completed:
        log('Observed explicit PRINTING COMPLETED status after dispatch.')
        return 0
    log('No explicit completion status observed; physical outcome unconfirmed. No retry.')
    return 2


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, ValueError) as error:
        print(f'STOPPED: {error}. No automatic retry; if sending began, output is uncertain.', flush=True)
        raise SystemExit(1)
    except KeyboardInterrupt:
        print('\nMonitoring stopped; an already sent label may still print. No retry.', flush=True)
        raise SystemExit(130)
