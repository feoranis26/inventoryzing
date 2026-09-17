[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Discover', 'Monochrome')]
    [string]$Scenario,

    [switch]$AuthorizeHardwareAccess,

    [string]$PrintAcknowledgement,

    [string]$PrinterName,

    [string]$TemplatePath,

    [string]$ArtifactPath,

    [string]$ArtifactObjectName,

    [string]$MediaName,

    [Nullable[int]]$MediaId,

    [Nullable[decimal]]$WidthMillimeters,

    [Nullable[decimal]]$HeightMillimeters,

    [Nullable[int]]$DpiX,

    [Nullable[int]]$DpiY,

    [ValidateSet('DriverDefault', 'AutoCut', 'NoCut')]
    [string]$CutMode,

    [hashtable]$Fields = @{},

    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds = 90,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not $AuthorizeHardwareAccess.IsPresent) {
    throw 'Pass -AuthorizeHardwareAccess to acknowledge that this test activates Brother b-PAC and accesses configured printers.'
}

$scenarios = @{
    Discover = @{
        Gate = 'brother-discover'
        Method = 'Discovers_bpac_printers_supported_media_and_loaded_status'
        Instruction = 'This scenario queries b-PAC printer, online, loaded-media, and supported-media state without submitting a print job.'
    }
    Monochrome = @{
        Gate = 'brother-monochrome'
        Method = 'Submits_exactly_one_authorized_monochrome_sample'
        Instruction = 'Confirm that the selected printer contains the exact configured monochrome media; this scenario submits exactly one label.'
    }
}

$selected = $scenarios[$Scenario]
$resolvedTemplatePath = $null
$resolvedArtifactPath = $null
$normalizedCutMode = $null
$fieldsJson = '{}'

if ($Scenario -eq 'Monochrome') {
    if (-not [string]::Equals(
        $PrintAcknowledgement,
        'PRINT-ONE-LABEL',
        [StringComparison]::Ordinal)) {
        throw "Scenario 'Monochrome' requires -PrintAcknowledgement PRINT-ONE-LABEL."
    }

    $requiredStrings = @{
        PrinterName = $PrinterName
        TemplatePath = $TemplatePath
        ArtifactPath = $ArtifactPath
        ArtifactObjectName = $ArtifactObjectName
        CutMode = $CutMode
    }
    foreach ($name in $requiredStrings.Keys) {
        if ([string]::IsNullOrWhiteSpace($requiredStrings[$name])) {
            throw "Scenario 'Monochrome' requires -$name."
        }
    }

    if (-not (Test-Path -LiteralPath $TemplatePath -PathType Leaf)) {
        throw "Brother template '$TemplatePath' was not found."
    }
    $resolvedTemplatePath = (Resolve-Path -LiteralPath $TemplatePath).ProviderPath
    if (-not [string]::Equals(
        [IO.Path]::GetExtension($resolvedTemplatePath),
        '.lbx',
        [StringComparison]::OrdinalIgnoreCase)) {
        throw '-TemplatePath must reference a Brother .lbx template.'
    }

    if (-not (Test-Path -LiteralPath $ArtifactPath -PathType Leaf)) {
        throw "Print artifact '$ArtifactPath' was not found."
    }
    $resolvedArtifactPath = (Resolve-Path -LiteralPath $ArtifactPath).ProviderPath
    $artifactExtensions = @('.png', '.jpg', '.jpeg', '.bmp', '.gif', '.tif', '.tiff')
    if ([IO.Path]::GetExtension($resolvedArtifactPath) -notin $artifactExtensions) {
        throw "-ArtifactPath must reference one of: $($artifactExtensions -join ', ')."
    }

    $hasMediaName = -not [string]::IsNullOrWhiteSpace($MediaName)
    $hasMediaId = $null -ne $MediaId
    if ($hasMediaName -eq $hasMediaId) {
        throw 'Set exactly one of -MediaName or -MediaId.'
    }
    if ($hasMediaId -and $MediaId -lt 0) {
        throw '-MediaId must be a non-negative integer.'
    }

    foreach ($dimension in @{
        WidthMillimeters = $WidthMillimeters
        HeightMillimeters = $HeightMillimeters
    }.GetEnumerator()) {
        if ($null -eq $dimension.Value -or $dimension.Value -le 0) {
            throw "-$($dimension.Key) must be a positive decimal number."
        }
    }
    foreach ($resolution in @{
        DpiX = $DpiX
        DpiY = $DpiY
    }.GetEnumerator()) {
        if ($null -eq $resolution.Value -or
            $resolution.Value -lt 1 -or
            $resolution.Value -gt 2400) {
            throw "-$($resolution.Key) must be an integer from 1 through 2400."
        }
    }

    $normalizedCutMode = @{
        DriverDefault = 'DriverDefault'
        AutoCut = 'AutoCut'
        NoCut = 'NoCut'
    }[$CutMode]

    foreach ($field in $Fields.GetEnumerator()) {
        if ($field.Key -isnot [string] -or [string]::IsNullOrWhiteSpace($field.Key)) {
            throw '-Fields keys must be non-empty strings.'
        }
        if ($field.Value -isnot [string]) {
            throw "-Fields value for '$($field.Key)' must be a string."
        }
        if ([string]::Equals($field.Key, $ArtifactObjectName, [StringComparison]::Ordinal)) {
            throw "-Fields cannot populate reserved artifact object '$ArtifactObjectName'."
        }
    }
    $fieldsJson = ConvertTo-Json -InputObject $Fields -Compress
}

$testName = "Inventoryzing.Agent.Tests.BrotherHardwareAcceptanceTests.$($selected.Method)"
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
if (-not [string]::IsNullOrWhiteSpace($PrinterName)) {
    Write-Host "Target printer: $PrinterName"
}
if ($Scenario -eq 'Monochrome') {
    $mediaDescription = if (-not [string]::IsNullOrWhiteSpace($MediaName)) {
        "name '$MediaName'"
    }
    else {
        "ID $MediaId"
    }
    Write-Host "Template: $resolvedTemplatePath"
    Write-Host "Artifact: $resolvedArtifactPath"
    Write-Host "Artifact object: $ArtifactObjectName"
    Write-Host "Media: $mediaDescription"
    Write-Host "Dimensions: $WidthMillimeters x $HeightMillimeters mm"
    Write-Host "Resolution: $DpiX x $DpiY dpi"
    Write-Host "Cut mode: $normalizedCutMode"
    Write-Host 'Copies: 1 (fixed)'
}
Write-Host "Timeout: $TimeoutSeconds seconds"
Write-Host "Command: dotnet $($arguments -join ' ')"

if ($DryRun.IsPresent) {
    Write-Host 'Dry run only; b-PAC was not activated and no print job was submitted.'
    return
}

$environment = @{
    INVENTORYZING_HARDWARE_TEST = $selected.Gate
    INVENTORYZING_BROTHER_PRINTER = $PrinterName
    INVENTORYZING_BROTHER_TEMPLATE = $resolvedTemplatePath
    INVENTORYZING_BROTHER_ARTIFACT = $resolvedArtifactPath
    INVENTORYZING_BROTHER_ARTIFACT_OBJECT = $ArtifactObjectName
    INVENTORYZING_BROTHER_MEDIA_NAME = $MediaName
    INVENTORYZING_BROTHER_MEDIA_ID = if ($null -eq $MediaId) {
        $null
    }
    else {
        $MediaId.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    INVENTORYZING_BROTHER_WIDTH_MM = if ($null -eq $WidthMillimeters) {
        $null
    }
    else {
        $WidthMillimeters.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    INVENTORYZING_BROTHER_HEIGHT_MM = if ($null -eq $HeightMillimeters) {
        $null
    }
    else {
        $HeightMillimeters.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    INVENTORYZING_BROTHER_DPI_X = if ($null -eq $DpiX) {
        $null
    }
    else {
        $DpiX.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    INVENTORYZING_BROTHER_DPI_Y = if ($null -eq $DpiY) {
        $null
    }
    else {
        $DpiY.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    INVENTORYZING_BROTHER_CUT_MODE = $normalizedCutMode
    INVENTORYZING_BROTHER_FIELDS_JSON = $fieldsJson
    INVENTORYZING_BROTHER_TIMEOUT_SECONDS = $TimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
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
        throw "Brother hardware scenario '$Scenario' failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
}