# USB raster printing on Ubuntu and Windows

The existing .NET 10 printer host runs on both operating systems, despite its
historical `agents/windows` source location. Set `Inventoryzing:Printer:Transport`
to `usb` to use direct USB raster and status feedback, or `tcp` (the default) to
retain TCP plus SNMP. Rendering and coordinator APIs are unchanged.

This initial USB backend supports the Brother QL-820NWB only: USB VID `04f9`, PID
`209d`, interface 0, bulk input `81` and output `02`. Selection requires the exact
USB serial descriptor, which Brother documents as the last twelve digits of the
printer serial number. It never selects an arbitrary first printer.

## Ubuntu, native service

Prepare USB on the Ubuntu host from the repository root:

```sh
sudo bash scripts/install-printer-usb.sh --user "$USER"
```

This installs `libusb-1.0-0` and USB diagnostic tools, creates the `inventoryzing`
service account/group if absent, grants the specified existing user access,
installs the printer-specific udev rule, and refreshes permissions for this
printer. It prints the group ID and serial numbers of connected QL-820NWBs.
No library downloads or copying are needed. Run with `--dry-run` to preview;
omit `--user` if only the service account/container needs access. Log out and
back in before testing as a newly added user. Re-running the installer is safe;
any different existing rule is backed up before replacement.

The script prepares USB for both native and Docker deployments; it does not
start printing, modify CUPS queues, install .NET, or create coordinator tokens.
For a native deployment, install .NET 10 and run the agent as the dedicated
`inventoryzing` account with an accessible token file. Docker supplies .NET in
the printer image and needs no host .NET installation.
Disable any CUPS queue for this printer while using direct USB. libusb detaches
the Linux kernel printer driver while an interface is claimed and reattaches it
when released; another print service must not compete for the same device.

Publish on a machine with the .NET 10 SDK (run from the repository root):

```sh
dotnet publish agents/windows/inventoryzing-agent/src/Inventoryzing.Agent.Printer.Host/Inventoryzing.Agent.Printer.Host.csproj -c Release -o .local/printer-publish
```

Configure the service environment with the existing coordinator token file and
URL, plus the USB settings (substitute real paths and serial):

```sh
Inventoryzing__Agent__DataDirectory=/var/lib/inventoryzing/printer
Inventoryzing__Printer__Enabled=true
Inventoryzing__Printer__CoordinatorUri=http://127.0.0.1:8088/
Inventoryzing__Printer__CredentialFile=/etc/inventoryzing/coordinator.token
Inventoryzing__Printer__Transport=usb
Inventoryzing__Printer__UsbSerialNumber=000000000001
```

Start `dotnet /path/to/publish/Inventoryzing.Agent.Printer.Host.dll`. No raster
host, raster port, or SNMP status port is needed in USB mode.

## Ubuntu, Docker Compose

Run `sudo bash scripts/install-printer-usb.sh` on the **host**, then copy its
reported group ID and the intended printer serial into `.env`:

```dotenv
IZ_PRINTER_USB_SERIAL=YOUR_USB_SERIAL
IZ_PRINTER_USB_GID=GROUP_ID_FROM_INSTALLER
```

The printer image already includes libusb; no host .NET runtime is needed.

```sh
docker compose -f compose.yaml -f deploy/compose.printer-usb.yaml --profile printer up -d --build printer-agent
```

This explicit override mounts the USB bus and allows USB character-device
access, including replugged devices, without a privileged container. Host udev
permissions limit access to the printer. The token mount and coordinator URL
come from the base Compose file. When specifying `-f`, also name any local
override file whose settings you need; Compose does not auto-load it in this case.
Native Windows USB testing uses the native .NET host, not Docker Desktop USB
passthrough.

## Windows

Publish the same project with .NET 10 using the command above, then run from the
repository root:

```powershell
.\scripts\install-printer-usb.ps1 -LaunchDriverInstaller
```

The script downloads pinned libusb 1.0.30 from its official release, checks its
SHA-256 hash, extracts it with Windows' built-in `tar.exe`, validates the DLL's
architecture, and installs it into `.local/printer-publish`. No manual download,
7-Zip installation, or DLL copying is required. It defaults to **x64**; use
`-Architecture arm64` or `-Architecture x86` to match a different agent process.
For a custom publish directory, pass `-DestinationDirectory C:\Inventoryzing\Printer`.
Do not select architecture solely from the OS: an x64 .NET process on ARM64
Windows still needs the x64 DLL.

`-LaunchDriverInstaller` also downloads checksum-verified Zadig 2.9 and opens it.
In Zadig, choose **Options > List All Devices**, select **Brother QL-820NWB**,
verify USB ID **04F9:209D**, choose **WinUSB**, and click **Install/Replace Driver**.
If only a mouse or other peripherals appear, ensure **List All Devices** is
checked; Zadig can hide devices that already have a driver. If the printer is
still absent, power it on, reconnect its USB device port using a data cable, and
check whether it appears in Windows Device Manager. A Bluetooth/virtual COM
entry alone does not mean the printer is connected over USB. Never replace a
mouse driver to make the printer work.
Windows may request administrator approval for this driver step. Device selection
and driver replacement remain interactive. This replaces the normal printer driver binding
for that USB interface: Brother/P-touch/Windows spooler printing over USB will
not work with that binding. Restore the Brother driver to return to those tools.
No driver installation or replacement is performed by the agent itself.

Omit `-LaunchDriverInstaller` if WinUSB is already configured. Repeating the
script leaves an identical DLL alone; replacing a different DLL requires stopping
the agent and passing `-Force`. Downloads are cached in `.local/downloads` and
verified on every run. `-ArchivePath C:\Downloads\libusb-1.0.30.7z` supports an
offline libusb installation with the same checksum verification. `-WhatIf`
previews the destination without downloading or modifying anything.

Use the same environment settings as Ubuntu with Windows paths, for example:

```powershell
$env:Inventoryzing__Agent__DataDirectory = 'C:\ProgramData\Inventoryzing\Printer'
$env:Inventoryzing__Printer__Enabled = 'true'
$env:Inventoryzing__Printer__CoordinatorUri = 'http://YOUR-SERVER:8088/'
$env:Inventoryzing__Printer__CredentialFile = 'C:\ProgramData\Inventoryzing\coordinator.token'
$env:Inventoryzing__Printer__Transport = 'usb'
$env:Inventoryzing__Printer__UsbSerialNumber = 'YOUR_USB_SERIAL'
dotnet .local/printer-publish/Inventoryzing.Agent.Printer.Host.dll
```

## Verification and failure handling

First stop any running printer agent, then run a read-only probe on each OS:

```sh
dotnet .local/printer-publish/Inventoryzing.Agent.Printer.Host.dll --usb-probe YOUR_USB_SERIAL
```

The probe requests status only, uses a five-second cancellation deadline, prints
JSON, and exits without printing a label. Check model, roll dimensions, and
`HasError`. Errors include native libusb error codes; access/busy errors usually
mean permissions, driver binding, or another owner. A missing library means the
native dependency is not installed for the current process architecture.

Then start the configured agent and request one test label through the normal UI.
Verify size, orientation, cut, QR decoding, and successful completion. Repeat on
Ubuntu. Test wrong media, unplug before dispatch, and unplug during a print.
The latter can leave physical output; inspect it before any retry.

USB probes and printing share an exclusive lock. After dispatch, only automatic
status is read; no polling commands are sent. Completion requires the printer's
explicit completion frame. A partial write, device error, disconnect, or timeout
after dispatch reports `Unknown`, never retries the label, and prevents further
USB probes/prints for that agent instance. Inspect and clear pending output, then
restart the printer agent to clear that interlock. The coordinator's force reset
alone does not clear this USB interlock. Use one agent per physical printer.

Hardware-free tests cover protocol and transfer behavior. Actual Windows driver
binding, Linux permissions, and physical printing require acceptance on both
machines; a successful build alone does not validate these.

References:
- [Brother raster command reference, USB flow and appendix A](https://download.brother.com/welcome/docp100278/cv_ql800_eng_raster_101.pdf)
- [libusb Windows driver and installation guidance](https://github.com/libusb/libusb/wiki/Windows)
- [libusb synchronous transfers and partial timeouts](https://libusb.sourceforge.io/api-1.0/group__libusb__syncio.html)
