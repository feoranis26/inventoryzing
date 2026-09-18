# Linux Zebra scanner agent

This is the Linux counterpart to the Windows Zebra scanner host. It forwards a
decoded barcode directly to the coordinator and keeps no scan queue, label data,
or database on the terminal. Its only local hardware dependency is Zebra
CoreScanner for Linux.

The managed host owns inventoryzing behavior: terminal identity, direct delivery,
and the configurable success, command-success, failure, and no-action feedback
rules. The small native bridge owns only the Zebra C++ SDK event and command API.
That separation keeps vendor headers, libraries, and USB handling outside the
coordinator and the web application.

## Prerequisites

Install a Zebra Scanner SDK for Linux package that matches the thin client's
architecture and distribution. The installation must provide a running
CoreScanner daemon plus the development headers and `libcs-client.so`. Zebra's
standard paths are `/usr/include/zebra-scanner` and
`/usr/lib/zebra-scanner/corescanner`.

Configure the DS22/CR2278 in USB SNAPI mode. Verify CoreScanner can see the
scanner with Zebra's supplied utility before starting inventoryzing.

The vendor package is intentionally not copied into this repository or a public
container image. It is platform-specific software supplied by Zebra.

## Build the bridge

On the Linux terminal, from this directory:

```sh
cmake -S native/corescanner-bridge -B .build/corescanner-bridge
cmake --build .build/corescanner-bridge --parallel
sudo cmake --install .build/corescanner-bridge
sudo ldconfig
```

This installs `inventoryzing-zebra-bridge` at
`/usr/local/bin/inventoryzing-zebra-bridge`. The bridge communicates with the
local CoreScanner daemon and does not expose a network port.

## Run the agent

Provision a scanner credential on the coordinator host with the existing
`scripts/provision-scanner-agent.ps1` script. Copy it to the terminal with file
permissions that allow only the agent account to read it. Open **Scanner** in
the dedicated terminal browser and use the displayed terminal ID.

```sh
export Inventoryzing__Agent__DataDirectory=/var/lib/inventoryzing/scanner
export Inventoryzing__Scanner__Enabled=true
export Inventoryzing__Scanner__CoordinatorUri=http://COORDINATOR_TAILNET_IP:8088/
export Inventoryzing__Scanner__CredentialFile=/etc/inventoryzing/scanner.token
export Inventoryzing__Scanner__TerminalId=replace-with-terminal-id
export Inventoryzing__Scanner__LinuxCoreScanner__BridgePath=/usr/local/bin/inventoryzing-zebra-bridge
dotnet run --project src/Inventoryzing.Agent.Scanner.Linux
```

Use the coordinator's Tailnet address when the terminal reaches it through
Tailscale. The agent must be able to reach that address, but the browser page is
still served by the coordinator.

## Feedback behavior

The normal decode beep remains a scanner setting. After the coordinator replies,
the bridge applies the configured inventoryzing feedback to the scanner. A
wireless cradle is resolved to its paired handheld from CoreScanner's device
topology before its LED is flashed; beeper commands stay associated with the
source that delivered the decode.

The defaults are:

| Result | LED | Tone |
| --- | --- | --- |
| Item navigation | Green | Rising |
| Command completed | Green | Rising then two high beeps |
| Failed delivery or operation | Red | Falling |
| No recognized action | Amber | Two low beeps |

Override them with the same `Inventoryzing__Scanner__*` variables documented
for the Windows host. `TopologyTimeout` defaults to one second and can be
changed with `Inventoryzing__Scanner__LinuxCoreScanner__TopologyTimeout`.

## Container on the kiosk

The separate `deploy/scanner.compose.yaml` builds and runs CoreScanner, the
native bridge, and the managed agent together. The coordinator and database
remain on the server. No Zebra installation on the kiosk host is needed for
this route; do not run another CoreScanner daemon against the same USB scanner.

Obtain Zebra's **Ubuntu 24.04** CoreScanner and development `.deb` packages for
the kiosk architecture and place just those packages in `.local/zebra-sdk` at
the repository root. The container uses Ubuntu 24.04 (Noble). ARM64 compatibility
depends on Zebra providing matching ARM64 packages; Linux support alone does
not guarantee that an AMD64 or ARM32 package works on a 64-bit Raspberry Pi.
Vendor binaries are installed into your local image and are not committed here.

From the repository root on the Linux kiosk:

```sh
export IZ_ZEBRA_SDK_DIR="$PWD/.local/zebra-sdk"
export IZ_SCANNER_COORDINATOR_URI=http://COORDINATOR_TAILNET_IP:8088/
export IZ_SCANNER_TERMINAL_ID=replace-with-terminal-id
export IZ_SCANNER_TOKEN_FILE=/absolute/path/to/scanner.token
docker compose -f deploy/scanner.compose.yaml up -d --build
docker compose -f deploy/scanner.compose.yaml logs -f scanner-agent
```

Keep the token file readable only by its owner (`chmod 600`). The container
runs as root for the vendor daemon's USB access, with a USB bus bind mount and
device permission for USB major 189. It does not use privileged mode or publish
network ports. The udev database is mounted read-only for device discovery.
USB permissions cover reconnects without pinning a changing bus/device number.
Docker Desktop's Windows USB environment is not the deployment target.

The entrypoint starts Zebra's supplied `cscored` init script and stops both
processes on shutdown. A daemon or agent exit causes the container to restart;
scans are not persisted or replayed. Use a token provisioned specifically for
the kiosk's browser terminal ID.

The managed project builds on Windows. Native compilation, the complete image,
and USB hotplug/feedback still require verification with the actual Zebra
packages and scanner on Linux.
