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

## Obtain Zebra packages (Ubuntu / Raspberry Pi OS)

Use Zebra's official [Scanner SDK for Linux downloads](https://www.zebra.com/us/en/support-downloads/software/scanner-software/scanner-sdk-for-linux.html).
Expand **CoreScanner and Devel Installer Packages for Linux**. The download list
currently supplies version **4.4.1-63** bundles (verified 2026-09-17); its section
heading still says 4.4.1-55. Use the version on the actual files.

Check the target machine with `dpkg --print-architecture`:

| Target architecture | Official bundle |
| --- | --- |
| `arm64`: 64-bit Raspberry Pi OS or Ubuntu on Raspberry Pi | [Debian ARM 64-bit C11 ZIP](https://www.zebra.com/content/dam/support-dam/en/developer-tools/unrestricted/0001/CoreScanner_and_Devel_v4.4.1-63_Debian_ARM_64bit_C11.zip) |
| `amd64`: Ubuntu on Intel/AMD thin clients | [Debian x86 64-bit C11 ZIP](https://www.zebra.com/content/dam/support-dam/en/developer-tools/unrestricted/0001/CoreScanner_and_Devel_v4.4.1-63_Debian_x86_64bit_C11.zip) |
| `armhf`: 32-bit Raspberry Pi OS | Select **Debian ARM 32bit C11** on the official download page; the current container has not been configured or tested for this target. |

Each 64-bit ZIP contains `zebra-scanner-corescanner_4.4.1-63_<arch>.deb` and
`zebra-scanner-devel_4.4.1-63_<arch>.deb`. Both are required to build the bridge.
JavaPOS, IoT Connector, AFM, and CLU are not required. Zebra calls these Debian
packages, not Ubuntu-version-specific packages.

For the **container route**, download and extract on the kiosk from the repository
root (requires `curl` and `unzip`):

```sh
arch=$(dpkg --print-architecture)
case "$arch" in
  arm64) bundle=CoreScanner_and_Devel_v4.4.1-63_Debian_ARM_64bit_C11 ;;
  amd64) bundle=CoreScanner_and_Devel_v4.4.1-63_Debian_x86_64bit_C11 ;;
  *) echo "This container setup requires arm64 or amd64." >&2; exit 1 ;;
esac
mkdir -p .local/zebra-sdk-downloads
curl --fail --location --output ".local/zebra-sdk-downloads/$bundle.zip" \
  "https://www.zebra.com/content/dam/support-dam/en/developer-tools/unrestricted/0001/$bundle.zip"
unzip ".local/zebra-sdk-downloads/$bundle.zip" -d ".local/zebra-sdk-$arch"
export IZ_ZEBRA_SDK_DIR="$PWD/.local/zebra-sdk-$arch/$bundle"
```

Point `IZ_ZEBRA_SDK_DIR` at the extracted directory containing the two `.deb`
files directly. Keep architectures in separate directories. Do not install the
packages on the host when using the container route. The Ubuntu-based image can
run on a Raspberry Pi OS host of matching architecture; its userspace dependencies
are installed inside the image. Actual package installation and USB operation in
this image still need validation.

For a **native installation**, install the matching pair using
`sudo apt install ./zebra-scanner-corescanner_4.4.1-63_arm64.deb ./zebra-scanner-devel_4.4.1-63_arm64.deb`
from the extracted directory (substitute `amd64` on an Intel/AMD machine).
See Zebra's [installation guide](https://techdocs.zebra.com/dcs/scanners/sdk-linux/setup/)
and [distribution support matrix](https://techdocs.zebra.com/dcs/scanners/sdk-linux/about/#supported-linux-distributions)
for vendor support of your specific OS release.

Copies of the ARM64 and AMD64 bundles have also been downloaded into this
workstation's `.local/zebra-sdk-downloads`, with extracted packages under
`.local/zebra-sdk-arm64` and `.local/zebra-sdk-amd64`. These local files are not
committed to Git; copy the desired bundle to the kiosk or download it there.

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

Obtain the matching Debian CoreScanner and development `.deb` packages using
the instructions above. The container uses Ubuntu 24.04 (Noble). ARM64 and AMD64
bundles are available; select the same architecture as the kiosk. Vendor binaries
are installed into your local image and are not committed here.

From the repository root on the Linux kiosk:

```sh
# Keep IZ_ZEBRA_SDK_DIR from the download/extraction step above.
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
