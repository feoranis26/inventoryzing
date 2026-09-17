[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Name,

    [string]$CredentialFile = (Join-Path $env:ProgramData 'Inventoryzing\Scanner\coordinator.token')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$credentialPath = [IO.Path]::GetFullPath($CredentialFile)
$credentialDirectory = Split-Path -Parent $credentialPath
$credentialName = Split-Path -Leaf $credentialPath

if (Test-Path -LiteralPath $credentialPath) {
    throw "Credential file already exists: $credentialPath"
}
$null = New-Item -ItemType Directory -Path $credentialDirectory -Force

function Protect-SecretPath([string]$Path, [bool]$IsDirectory) {
    $rights = [Security.AccessControl.FileSystemRights]::FullControl
    $inheritance = if ($IsDirectory) {
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    } else { [Security.AccessControl.InheritanceFlags]::None }
    $acl = if ($IsDirectory) { New-Object Security.AccessControl.DirectorySecurity }
        else { New-Object Security.AccessControl.FileSecurity }
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]'S-1-5-18',
        [Security.Principal.SecurityIdentifier]'S-1-5-32-544')) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $identity, $rights, $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

Protect-SecretPath -Path $credentialDirectory -IsDirectory $true
$mount = "${credentialDirectory}:/inventoryzing-secret"
Push-Location $root
try {
    & docker compose build migrate
    if ($LASTEXITCODE -ne 0) { throw 'Unable to build the migration image.' }
    & docker compose run --rm migrate
    if ($LASTEXITCODE -ne 0) { throw 'Unable to migrate the coordinator database.' }
    & docker compose run --rm --volume $mount migrate python -m inventoryzing.cli `
        provision-scanner-agent --name $Name `
        --token-file "/inventoryzing-secret/$credentialName"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to provision the scanner agent.' }
} finally { Pop-Location }

if (-not (Test-Path -LiteralPath $credentialPath -PathType Leaf)) {
    throw "Provisioning completed without creating the credential file: $credentialPath"
}
Protect-SecretPath -Path $credentialPath -IsDirectory $false
Write-Host "Scanner credential is ready at $credentialPath"
