# Printer and scanner setup

These optional agents connect a Brother QL-820NWB network printer or Zebra DS22 scanner to inventoryzing. First complete the [app setup](../../../README.md#get-running).

Run the commands below from the repository root. For the foreground hosts, install the .NET 10 SDK and keep the console open while using the device. Stop a host with Ctrl+C. Automatic Windows Service installation is not provided.

## Run the First Managed Tag

Connect the QL-820NWB to a network reachable from the agent. Allow TCP 9100 for printing and UDP 161 for status checks. The direct printer connection does not require a Windows print queue or P-touch Editor.

Supported presets include 17 Ã— 54, 23 Ã— 23, 29 Ã— 90, 62 Ã— 29, and 62 Ã— 100 mm die-cut labels, plus 12 mm and 62 mm continuous rolls. Continuous cut lengths range from 12.7 to 1000 mm. Choose a matching template and roll; labels are not resized automatically.

Provision a narrow service credential from the repository root. The script applies
the current migrations, creates the credential directly at the requested path, and
restricts its Windows ACL to the current user, SYSTEM, and Administrators. It refuses
to overwrite an existing credential. Do not put the file in the repository.

```powershell
.\scripts\provision-printer-agent.ps1 -Name 'workshop-printer'
```

Configure the agent with environment variables. Replace the
example paths and names with the verified local values. `CoordinatorUri` must be
reachable from the Windows host; the default local Compose installation uses
`http://localhost:8088/`.

```powershell
$env:Inventoryzing__Agent__DataDirectory = 'C:\ProgramData\Inventoryzing\Printer'
$env:Inventoryzing__Printer__Enabled = 'true'
$env:Inventoryzing__Printer__CoordinatorUri = 'http://127.0.0.1:8088/'
$env:Inventoryzing__Printer__CredentialFile = 'C:\ProgramData\Inventoryzing\Printer\coordinator.token'
$env:Inventoryzing__Printer__PrinterId = 'brother:ql-820nwb'
$env:Inventoryzing__Printer__RasterHost = '10.0.0.13'
$env:Inventoryzing__Printer__RasterPort = '9100'
$env:Inventoryzing__Printer__StatusPort = '161'
dotnet run --project .\agents\windows\inventoryzing-agent\src\Inventoryzing.Agent.Printer.Host
```

Keep the console running, open an existing object in the browser, and choose
**Print tag**. Check the label and the reported result before printing more.

### Agent Logs

The agent logs to its console. To retain and watch the same stream in
another PowerShell window, start it through `Tee-Object` and tail the file:

```powershell
$log = 'C:\ProgramData\Inventoryzing\Printer\printer-agent.log'
dotnet run --project .\agents\windows\inventoryzing-agent\src\Inventoryzing.Agent.Printer.Host 2>&1 |
    Tee-Object -FilePath $log -Append

# In another window:
Get-Content -LiteralPath $log -Wait -Tail 100
```

Bearer tokens and label raster data are never written to the log.

## Linux container

The printer host has no Windows or USB dependency. It can run on a Linux terminal
that can reach both the coordinator and the QL-820NWB over the network.

Create the credential on the machine that runs the coordinator, then place that
existing file at `.local/printer/coordinator.token` beside `compose.yaml`. Set the
printer's LAN address in `.env`:

```text
IZ_PRINTER_RASTER_HOST=10.0.0.13
```

On Linux, make the mounted token readable only by the container's fixed UID:

```sh
sudo install -d -m 0700 .local/printer
sudo install -o 10001 -g 10001 -m 0400 /path/to/coordinator.token .local/printer/coordinator.token
```

Start the optional agent service:

```powershell
docker compose --profile printer up -d --build printer-agent
docker compose logs --follow printer-agent
```

The container receives the token through a read-only mount. It has no persistent
label or job volume; restarting it intentionally forgets any in-flight request.

## Run the Live Scanner Agent

The scanner agent forwards each decode directly to the coordinator. It does not
store scan payloads, retry them after failure, or replay them after restart. If a
delivery fails, scan the label again.

Provision a scanner-only service credential from the repository root:

```powershell
.\scripts\provision-scanner-agent.ps1 -Name 'workshop-scanner'
```

Then start the foreground host on the Windows machine with Zebra CoreScanner
installed and the DS22 configured for SNAPI:

```powershell
$env:Inventoryzing__Agent__DataDirectory = 'C:\ProgramData\Inventoryzing\Scanner'
$env:Inventoryzing__Scanner__Enabled = 'true'
$env:Inventoryzing__Scanner__CoordinatorUri = 'http://127.0.0.1:8088/'
$env:Inventoryzing__Scanner__CredentialFile = 'C:\ProgramData\Inventoryzing\Scanner\coordinator.token'
$env:Inventoryzing__Scanner__TerminalId = 'paste-the-scanner-terminal-id-shown-in-the-web-UI'
dotnet run --project .\agents\windows\inventoryzing-agent\src\Inventoryzing.Agent.Scanner.Host
```

For a Linux terminal or thin client, use the [Linux Zebra scanner agent](../../linux/inventoryzing-scanner-agent/README.md).

Open **Scanner** in the browser that will be the dedicated terminal and copy its
**Scanner terminal ID** into `Inventoryzing__Scanner__TerminalId`. The agent then
binds to that terminal on its first delivery; one service credential cannot later
be redirected to another browser. The terminal identity survives sign-out. Only one tab controls the scanner at a time;
another tab must explicitly take control. Lookup remains active while that browser
navigates to a scanned object. Delayed scans are rejected rather than applied to a
later workflow.

The normal successful-decode beep remains controlled by the scanner. After the
coordinator result, inventoryzing defaults to green plus a rising tone for
navigation success, green plus a rising tone followed by two short high beeps for
command success, red plus a falling tone for failure, and amber plus two short low beeps when
there is no applicable action. These settings can be overridden with the
`Inventoryzing__Scanner__SuccessColor`, `SuccessTone`, `FailureColor`,
`CommandSuccessColor`, `CommandSuccessTone`, `FailureTone`, `NoActionColor`, `NoActionTone`, and `FeedbackFlashDuration`
environment variables.

## Troubleshooting

- **Agent cannot connect:** confirm the coordinator address and port are reachable from the device computer and that the credential file exists and is readable.
- **Printer unavailable or wrong media:** check the printer address, TCP 9100/UDP 161 access, and installed roll. Select the matching label template.
- **Uncertain print result:** inspect the printer before requesting another label. Restarting an agent forgets an outstanding request; force reset cannot cancel bytes already sent to the printer.
- **Scanner not detected:** check that Zebra CoreScanner is installed and the DS22 uses SNAPI. For a nonstandard SDK location, set `ZEBRA_CORE_SCANNER_INTEROP_PATH` to `Interop.CoreScanner.dll`.
- **Scan delivery failed:** restore the connection and scan again. Scans are not stored for replay.
- **Wrong browser terminal:** use the ID from the intended browser before the first scan. A credential binds to that terminal and cannot later be redirected; provision a separate credential file for another terminal.

For device acceptance tests and legacy adapter reference, see [Device agent development reference](../../../docs/device-agent-development.md).
