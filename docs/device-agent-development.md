# Device agent development reference

For setup and use, see the [device agent guide](../agents/windows/inventoryzing-agent/README.md).
Hardware test commands below run from `agents/windows/inventoryzing-agent`.

## Zebra Physical Acceptance

The entry point requires `-AuthorizeHardwareAccess` and enables exactly one test
in its child process. It restores the prior process environment afterward. A dry
run validates arguments and shows the selected command without opening
CoreScanner:

```powershell
.\scripts\Invoke-ZebraHardwareTest.ps1 -Scenario Enumerate -AuthorizeHardwareAccess -DryRun
```

Run read-only enumeration with the target SNAPI scanner connected:

```powershell
.\scripts\Invoke-ZebraHardwareTest.ps1 -Scenario Enumerate -AuthorizeHardwareAccess
```

For scan tests, provide the exact decoded payload. If multiple SNAPI scanners are
connected, also provide `-SerialNumber`.

```powershell
.\scripts\Invoke-ZebraHardwareTest.ps1 -Scenario Scan -ExpectedPayload 'I000042' -AuthorizeHardwareAccess
.\scripts\Invoke-ZebraHardwareTest.ps1 -Scenario Reconnect -ExpectedPayload 'I000042' -TimeoutSeconds 120 -AuthorizeHardwareAccess
.\scripts\Invoke-ZebraHardwareTest.ps1 -Scenario Rapid -ExpectedPayload 'I000042' -ScanCount 5 -AuthorizeHardwareAccess
```

`Reconnect` waits for a real PNP detach, a PNP attach under the same stable
serial identity, and then a successful scan. `Rapid` requires the same payload
for every trigger and verifies that every delivery has a distinct event ID.

## Brother b-PAC Adapter

`BrotherPrinterProbe` implements `IPrinterProbe` from configured b-PAC profiles.
Each profile selects an `.lbx` template, its artifact image object, exact media,
palette, and cut mode. Requests provide immutable named template fields plus the
opaque print artifact; QR or barcode encoding remains outside the coordinator.

Before opening a template, submission verifies that b-PAC supports the printer,
the printer is online, the configured media is supported, and the loaded media
matches by exact name or ID. Template geometry is never fitted implicitly.
PNG, JPEG, BMP, GIF, and TIFF artifacts are supplied to the configured image
object through b-PAC's documented `SetData(0, path, 4)` operation.

Palette and cut flags are passed to `StartPrint`; monochrome profiles rely on
their exact configured media because b-PAC aliases `bpoMono` and `bpoNoCut` to
the same value. `PrintOut` receives only the documented default option and the
requested copy count. A successful return is `Submitted`, not proof of physical
completion. Failures before `PrintOut` are `Rejected`; failures or cancellation
once submission may have begun are `Unknown`, preventing an unsafe automatic
retry. `EndPrint`, `Close`, temporary artifact deletion, and COM release are
attempted on every applicable path.

The default `BrotherBpacComClient` resolves the registered `bpac.Document` and
`bpac.Printer` ProgIDs. Construction and disposal do not activate COM; the first
capability query or submission does. The proprietary SDK interop assembly is
not required or committed.

## Brother Physical Acceptance

The Brother entry point requires `-AuthorizeHardwareAccess`. It validates all
arguments before enabling one exact test and restores the prior process
environment afterward. Discovery does not submit a print job:

```powershell
.\scripts\Invoke-BrotherHardwareTest.ps1 -Scenario Discover -AuthorizeHardwareAccess -DryRun
.\scripts\Invoke-BrotherHardwareTest.ps1 -Scenario Discover -AuthorizeHardwareAccess
```

Add `-PrinterName 'Brother QL-820NWB'` to discovery to require that exact printer
to be online with supported media reported. Discovery reports every b-PAC-usable
printer and its online state. The standalone `bpac.Printer` exposes a read-only
`Name`, so loaded and supported media are reported only for the printer that
object already represents; other installed printers explicitly report their
media details as unavailable rather than as empty capability lists.

Monochrome printing requires explicit printer, `.lbx` template, artifact object,
image artifact, physical dimensions, DPI, cut mode, and exactly one media name or
ID. The wrapper has no copy-count option and fixes the request to one copy. It
also requires the literal acknowledgement `PRINT-ONE-LABEL`:

For a 29 mm x 90 mm monochrome roll, the installed b-PAC SDK's
`CharLabel.lbx` sample provides image object `objImage` and text object
`objName`. Although the sample was originally authored for a PT-series printer,
both objects fit within 29 mm x 90 mm and the adapter explicitly rebinds the
document to the selected QL printer and media before printing. Use this preset
only when discovery reports the exact loaded media name shown below:

```powershell
$sdkTemplates = Join-Path $env:ProgramFiles 'Brother bPAC3 SDK\Templates'
$print = @{
	Scenario = 'Monochrome'
	AuthorizeHardwareAccess = $true
	PrintAcknowledgement = 'PRINT-ONE-LABEL'
	PrinterName = 'Brother QL-820NWB'
	TemplatePath = (Join-Path $sdkTemplates 'CharLabel.lbx')
	ArtifactPath = (Join-Path $sdkTemplates 'Coffee.bmp')
	ArtifactObjectName = 'objImage'
	MediaName = '29mm x 90mm'
	WidthMillimeters = 29
	HeightMillimeters = 90
	DpiX = 300
	DpiY = 300
	CutMode = 'AutoCut'
	Fields = @{ objName = 'Inventoryzing test' }
}
.\scripts\Invoke-BrotherHardwareTest.ps1 @print -DryRun
.\scripts\Invoke-BrotherHardwareTest.ps1 @print
```

Use `MediaId` instead of `MediaName` only when discovery confirms the intended
ID. If a different roll is loaded, create a media-native template in P-touch
Editor instead of changing only the dimensions above. `DriverDefault`,
`AutoCut`, and `NoCut` are the accepted cut modes. A passing test means b-PAC
accepted the submission; the operator must still confirm output, dimensions,
content, and cutting. Black/red output, independent QR decoding, and physical
failure injection remain disabled for later acceptance steps.

