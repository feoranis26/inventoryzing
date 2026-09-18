#!/bin/bash
set -euo pipefail

agent_pid=''
cleanup() {
    trap - EXIT TERM INT
    if [[ -n "$agent_pid" ]]; then
        kill -TERM "$agent_pid" 2>/dev/null || true
        wait "$agent_pid" 2>/dev/null || true
    fi
    /etc/init.d/cscored stop || true
}
trap cleanup EXIT
trap 'exit 143' TERM
trap 'exit 130' INT

/etc/init.d/cscored start
for attempt in {1..20}; do
    if pgrep -x cscore >/dev/null; then break; fi
    sleep 0.25
done
pgrep -x cscore >/dev/null || { echo 'CoreScanner failed to start.' >&2; exit 1; }
dotnet /app/Inventoryzing.Agent.Scanner.Linux.dll &
agent_pid=$!
while kill -0 "$agent_pid" 2>/dev/null; do
    pgrep -x cscore >/dev/null || { echo 'CoreScanner stopped; restarting container is required.' >&2; exit 1; }
    sleep 1 &
    wait $! || true
done
wait "$agent_pid"
