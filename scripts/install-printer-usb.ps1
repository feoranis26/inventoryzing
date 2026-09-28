#Requires -Version 5.1
<#
.SYNOPSIS
Installs the official libusb runtime beside the Windows printer agent.
.DESCRIPTION
Downloads pinned, SHA-256-verified releases. Uses Windows tar.exe to extract
only the selected DLL. Optionally opens Zadig for interactive WinUSB binding.
The script itself never selects a USB device or replaces its driver.
.EXAMPLE
.\scripts\install-printer-usb.ps1 -LaunchDriverInstaller
.EXAMPLE
.\scripts\install-printer-usb.ps1 -DestinationDirectory C:\Inventoryzing\Printer -Architecture arm64
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$DestinationDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '.local\printer-publish'),
    # Match the agent process, not necessarily the OS (e.g. x64 .NET on ARM64).
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Architecture = 'x64',
    [string]$CacheDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '.local\downloads'),
    # Optional offline archive; the same pinned checksum is always enforced.
    [string]$ArchivePath,
    [switch]$LaunchDriverInstaller,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'This installer is for Windows. On Ubuntu, install libusb-1.0-0.' }

$version = '1.0.30'
$archiveHash = '7fb1dfec805b97983763d7d0ae244320da12add1003d4249c96cc4d586398c79'
$archiveUrl = "https://github.com/libusb/libusb/releases/download/v$version/libusb-$version.7z"
$zadigUrl = 'https://github.com/pbatard/libwdi/releases/download/v1.5.1/zadig-2.9.exe'
$zadigHash = '4ecaa95df3da3621486a043aef8b3050b8bafe7c901402871e816229ef82039b'
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$cache = [IO.Path]::GetFullPath($CacheDirectory)
$dllPath = Join-Path $destination 'libusb-1.0.dll'
$archiveMember = switch ($Architecture) {
    'x64' { 'VS2022/MS64/dll/libusb-1.0.dll' }
    'x86' { 'VS2022/MS32/dll/libusb-1.0.dll' }
    'arm64' { 'VS2025/ARM64/dll/libusb-1.0.dll' }
}
$machine = switch ($Architecture) { 'x64' { 0x8664 } 'x86' { 0x014c } 'arm64' { 0xaa64 } }

function Assert-Hash([string]$Path, [string]$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw "SHA-256 verification failed: $Path. Remove the cached file and retry; nothing from it will be installed."
    }
}

function Get-VerifiedDownload([string]$Url, [string]$Path, [string]$Hash) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force
        $partial = "$Path.$([Guid]::NewGuid().ToString('N')).partial"
        $previousTls = [Net.ServicePointManager]::SecurityProtocol
        try {
            [Net.ServicePointManager]::SecurityProtocol = $previousTls -bor [Net.SecurityProtocolType]::Tls12
            Write-Host "Downloading $Url"
            Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing -TimeoutSec 180
            Assert-Hash $partial $Hash
            Move-Item -LiteralPath $partial -Destination $Path -Force
        } finally {
            [Net.ServicePointManager]::SecurityProtocol = $previousTls
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
        }
    }
    Assert-Hash $Path $Hash
}

if (-not $PSCmdlet.ShouldProcess($destination, "Install libusb $version ($Architecture)")) { return }
$tar = Get-Command tar.exe -CommandType Application -ErrorAction Stop
if ($ArchivePath) {
    $archive = (Resolve-Path -LiteralPath $ArchivePath).Path
    Assert-Hash $archive $archiveHash
} else {
    $archive = Join-Path $cache "libusb-$version.7z"
    Get-VerifiedDownload $archiveUrl $archive $archiveHash
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$staging = Join-Path $tempRoot ("inventoryzing-libusb-" + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $staging
try {
    & $tar.Source -xf $archive -C $staging $archiveMember
    if ($LASTEXITCODE -ne 0) { throw 'Windows tar could not extract libusb. Update Windows to obtain a current tar.exe.' }
    $source = Join-Path $staging $archiveMember
    $bytes = [IO.File]::ReadAllBytes($source)
    if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5a4d) { throw 'Invalid libusb DLL header.' }
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($pe -lt 0 -or $pe -gt $bytes.Length - 6 -or
        [BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550 -or
        [BitConverter]::ToUInt16($bytes, $pe + 4) -ne $machine) {
        throw "The downloaded DLL does not match the requested $Architecture architecture."
    }
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $alreadyInstalled = $false
    if (Test-Path -LiteralPath $dllPath -PathType Leaf) {
        $alreadyInstalled = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash -eq $hash
        if (-not $alreadyInstalled -and -not $Force) {
            throw "A different DLL exists at $dllPath. Stop the agent and rerun with -Force to replace it."
        }
    }
    if (-not $alreadyInstalled) {
        $null = New-Item -ItemType Directory -Path $destination -Force
        Copy-Item -LiteralPath $source -Destination $dllPath -Force
        Assert-Hash $dllPath $hash
    }
    Write-Host "libusb $version ($Architecture) is ready: $dllPath"
} finally {
    # Only remove this invocation's unique extraction directory within the temp root.
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    if ((Split-Path -Parent $resolvedStaging) -ne $tempRoot -or
        (Split-Path -Leaf $resolvedStaging) -notmatch '^inventoryzing-libusb-[0-9a-f]{32}$') {
        throw 'Refusing to remove an unexpected staging directory.'
    }
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
}

if ($LaunchDriverInstaller -and $PSCmdlet.ShouldProcess('Zadig', 'Download and open the interactive WinUSB driver installer')) {
    $zadig = Join-Path $cache 'zadig-2.9.exe'
    Get-VerifiedDownload $zadigUrl $zadig $zadigHash
    Write-Host 'In Zadig: Options > List All Devices; select Brother QL-820NWB.'
    Write-Host 'Verify USB ID 04F9:209D, choose WinUSB, then Install/Replace Driver.'
    Write-Host 'This changes the USB driver binding: normal Brother/P-touch USB printing requires restoring its driver.'
    Start-Process -FilePath $zadig -WorkingDirectory $cache -WindowStyle Normal
} else {
    Write-Host 'If WinUSB is not installed for the printer, rerun with -LaunchDriverInstaller for guided driver setup.'
}
Write-Host 'After driver setup, stop the agent and run the read-only check:'
Write-Host ('dotnet "{0}" --usb-probe YOUR_USB_SERIAL' -f (Join-Path $destination 'Inventoryzing.Agent.Printer.Host.dll'))
