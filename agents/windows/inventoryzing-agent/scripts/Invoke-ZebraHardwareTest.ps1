[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Enumerate', 'Scan', 'Reconnect', 'Rapid')]
    [string]$Scenario,

    [switch]$AuthorizeHardwareAccess,

    [string]$ExpectedPayload,

    [string]$SerialNumber,

    [ValidateRange(2, 256)]
    [int]$ScanCount = 5,

    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds = 90,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not $AuthorizeHardwareAccess.IsPresent) {
    throw 'Pass -AuthorizeHardwareAccess to acknowledge that this test opens Zebra CoreScanner and accesses the connected scanner.'
}

$scenarios = @{
    Enumerate = @{
        Gate = 'zebra-enumerate'
        Method = 'Enumerates_connected_snapi_scanner_with_stable_serial_identity'
        Instruction = 'Keep the target SNAPI scanner connected; no scan or device change is required.'
        RequiresPayload = $false
    }
    Scan = @{
        Gate = 'zebra-scan'
        Method = 'Barcode_event_preserves_payload_symbology_source_and_unique_event_id'
        Instruction = 'After the test starts, scan the barcode containing the expected payload once.'
        RequiresPayload = $true
    }
    Reconnect = @{
        Gate = 'zebra-reconnect'
        Method = 'Unplug_and_replug_preserves_stable_identity_and_resumes_scanning'
        Instruction = 'After the test starts, unplug the scanner, reconnect it, then scan the expected barcode once.'
        RequiresPayload = $true
    }
    Rapid = @{
        Gate = 'zebra-rapid'
        Method = 'Rapid_repeated_scans_are_not_dropped_or_deduplicated_by_payload'
        Instruction = "After the test starts, scan the same expected barcode $ScanCount times rapidly."
        RequiresPayload = $true
    }
}

$selected = $scenarios[$Scenario]
if ($selected.RequiresPayload -and [string]::IsNullOrEmpty($ExpectedPayload)) {
    throw "Scenario '$Scenario' requires -ExpectedPayload with the exact decoded barcode text."
}

$testName = "Inventoryzing.Agent.Tests.ZebraHardwareAcceptanceTests.$($selected.Method)"
$solutionRoot = Split-Path -Parent $PSScriptRoot
$arguments = @(
    'test',
    '.\Inventoryzing.Agent.sln',
    '--no-restore',
    '--filter',
    "FullyQualifiedName=$testName",
    '--logger',
    'console;verbosity=detailed'
)

Write-Host "Scenario: $Scenario"
Write-Host $selected.Instruction
if (-not [string]::IsNullOrWhiteSpace($SerialNumber)) {
    Write-Host "Target serial: $SerialNumber"
}
Write-Host "Timeout per operator step: $TimeoutSeconds seconds"
Write-Host "Command: dotnet $($arguments -join ' ')"

if ($DryRun.IsPresent) {
    Write-Host 'Dry run only; CoreScanner was not opened.'
    return
}

$environment = @{
    INVENTORYZING_HARDWARE_TEST = $selected.Gate
    INVENTORYZING_ZEBRA_EXPECTED_PAYLOAD = $ExpectedPayload
    INVENTORYZING_ZEBRA_SERIAL = $SerialNumber
    INVENTORYZING_ZEBRA_SCAN_COUNT = $ScanCount.ToString([Globalization.CultureInfo]::InvariantCulture)
    INVENTORYZING_ZEBRA_TIMEOUT_SECONDS = $TimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
}
$previousEnvironment = @{}

foreach ($name in $environment.Keys) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

Push-Location $solutionRoot
try {
    foreach ($name in $environment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Zebra hardware scenario '$Scenario' failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
}