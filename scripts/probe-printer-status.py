"""Bounded TCP status probe. Never sends raster, feed, cut, or print commands."""
import argparse
import json
import socket
import time
from datetime import UTC, datetime

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--host', default='10.0.0.13')
parser.add_argument('--port', type=int, default=9100)
parser.add_argument('--raster-mode', action='store_true')
parser.add_argument('--half-close', action='store_true')
parser.add_argument('--initialize', action='store_true')
parser.add_argument('--repeat', type=int, default=1)
parser.add_argument('--timeout', type=float, default=5)
args = parser.parse_args()
for attempt in range(args.repeat):
    started = time.monotonic()
    record = {'at': datetime.now(UTC).isoformat(), 'attempt': attempt + 1}
    received = bytearray()
    commands = (b'\x1b\x69\x61\x01' if args.raster_mode else b'') + b'\x1b\x69\x53'
    if args.initialize:
        commands = bytes(400) + b'\x1b\x40' + commands
    record['sent_hex'] = commands.hex(' ')
    try:
        with socket.create_connection((args.host, args.port), timeout=args.timeout) as sock:
            record['connect_ms'] = round((time.monotonic() - started) * 1000, 1)
            sock.sendall(commands)
            if args.half_close:
                sock.shutdown(socket.SHUT_WR)
            deadline = time.monotonic() + args.timeout
            while len(received) < 32:
                sock.settimeout(max(0.001, deadline - time.monotonic()))
                block = sock.recv(32 - len(received))
                if not block:
                    record['error'] = 'connection closed before full status frame'
                    break
                received.extend(block)
            if len(received) == 32:
                record['status'] = {
                    'model': chr(received[4]), 'error1': received[8], 'error2': received[9],
                    'width_mm': received[10], 'media_type': received[11],
                    'length_mm': received[17], 'status_type': received[18], 'phase': received[19],
                }
    except OSError as error:
        record['error'] = str(error)
    record['received_hex'] = received.hex(' ')
    record['elapsed_ms'] = round((time.monotonic() - started) * 1000, 1)
    print(json.dumps(record), flush=True)
