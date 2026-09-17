[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$DestinationDirectory,

    [string]$ContainerName,
    [string]$ComposeService = 'db',
    [string]$Database = 'inventoryzing',
    [string]$DatabaseUser = 'inventoryzing',
    [string]$ApplicationImage = 'inventoryzing-coordinator:local',

    [ValidateRange(0, 36500)]
    [int]$RetentionDays = 0,

    [switch]$ArchiveApplicationImage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Docker {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $nativeErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & docker @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $nativeErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "docker $($Arguments -join ' ') failed:`n$($output -join [Environment]::NewLine)"
    }
    return @($output)
}

function Test-IsWithinDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Directory
    )

    $normalizedPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $normalizedDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    return $normalizedPath.Equals($normalizedDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        $normalizedPath.StartsWith("$normalizedDirectory$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)
}

$root = Split-Path -Parent $PSScriptRoot
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if (Test-IsWithinDirectory -Path $destination -Directory $root) {
    throw 'Backup destination must be outside the inventoryzing workspace.'
}

[void](New-Item -ItemType Directory -Path $destination -Force)
$safeDatabase = $Database -replace '[^A-Za-z0-9_.-]', '_'
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$baseName = "$safeDatabase-$timestamp"
$dumpPath = Join-Path $destination "$baseName.dump"
$partialDumpPath = "$dumpPath.partial"
$manifestPath = Join-Path $destination "$baseName.manifest.json"
$partialManifestPath = "$manifestPath.partial"
$remoteDumpPath = "/tmp/inventoryzing-backup-$([Guid]::NewGuid().ToString('N')).dump"
$container = $ContainerName
$applicationArchivePartialPath = $null
$backupCompleted = $false

Push-Location $root
try {
    if ([string]::IsNullOrWhiteSpace($container)) {
        $containers = Invoke-Docker -Arguments @('compose', 'ps', '-q', $ComposeService) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        if (@($containers).Count -ne 1) {
            throw "Expected exactly one running Compose service '$ComposeService'; found $(@($containers).Count)."
        }
        $container = $containers[0]
    }

    $running = (Invoke-Docker -Arguments @('inspect', '--type', 'container', '--format', '{{.State.Running}}', $container) | Select-Object -First 1).Trim()
    if ($running -ne 'true') {
        throw "Database container '$container' is not running."
    }

    $databaseImage = (Invoke-Docker -Arguments @('inspect', '--type', 'container', '--format', '{{.Config.Image}}', $container) | Select-Object -First 1).Trim()
    $databaseImageId = (Invoke-Docker -Arguments @('inspect', '--type', 'container', '--format', '{{.Image}}', $container) | Select-Object -First 1).Trim()
    $applicationImageId = (Invoke-Docker -Arguments @('image', 'inspect', '--format', '{{.Id}}', $ApplicationImage) | Select-Object -First 1).Trim()
    $postgresVersion = (Invoke-Docker -Arguments @('exec', $container, 'psql', '-X', '-U', $DatabaseUser, '-d', $Database, '-At', '-c', 'SHOW server_version;') | Select-Object -First 1).Trim()
    $migrationVersions = (Invoke-Docker -Arguments @('exec', $container, 'psql', '-X', '-U', $DatabaseUser, '-d', $Database, '-At', '-c', "SELECT COALESCE(string_agg(version_num, ',' ORDER BY version_num), '') FROM public.alembic_version;") | Select-Object -First 1).Trim()

    [void](Invoke-Docker -Arguments @('exec', $container, 'pg_dump', '-U', $DatabaseUser, '-d', $Database, '-Fc', '--file', $remoteDumpPath))
    [void](Invoke-Docker -Arguments @('exec', $container, 'pg_restore', '--list', $remoteDumpPath))
    [void](Invoke-Docker -Arguments @('cp', "${container}:$remoteDumpPath", $partialDumpPath))
    if (-not (Test-Path $partialDumpPath) -or (Get-Item $partialDumpPath).Length -eq 0) {
        throw 'Copied backup is empty.'
    }
    Move-Item -LiteralPath $partialDumpPath -Destination $dumpPath

    $applicationArchive = $null
    $applicationArchiveHash = $null
    if ($ArchiveApplicationImage) {
        $safeImageId = $applicationImageId -replace '[^A-Za-z0-9_.-]', '-'
        $applicationArchivePath = Join-Path $destination "application-$safeImageId.tar"
        if (-not (Test-Path $applicationArchivePath)) {
            $applicationArchivePartialPath = "$applicationArchivePath.partial"
            [void](Invoke-Docker -Arguments @('image', 'save', '--output', $applicationArchivePartialPath, $ApplicationImage))
            if (-not (Test-Path $applicationArchivePartialPath) -or (Get-Item $applicationArchivePartialPath).Length -eq 0) {
                throw 'Application image archive is empty.'
            }
            Move-Item -LiteralPath $applicationArchivePartialPath -Destination $applicationArchivePath
        }
        $applicationArchive = [IO.Path]::GetFileName($applicationArchivePath)
        $applicationArchiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $applicationArchivePath).Hash.ToLowerInvariant()
    }

    $countQuery = @"
SELECT json_build_object(
    'sites', (SELECT count(*) FROM iz.sites),
    'entities', (SELECT count(*) FROM iz.entities),
    'principals', (SELECT count(*) FROM iz.principals),
    'accounts', (SELECT count(*) FROM iz.accounts),
    'roles', (SELECT count(*) FROM iz.roles),
    'permissions', (SELECT count(*) FROM iz.permissions),
    'role_permissions', (SELECT count(*) FROM iz.role_permissions),
    'account_roles', (SELECT count(*) FROM iz.account_roles),
    'object_types', (SELECT count(*) FROM iz.object_types),
    'objects', (SELECT count(*) FROM iz.objects),
    'placements', (SELECT count(*) FROM iz.placements),
    'tags', (SELECT count(*) FROM iz.tags),
    'tag_edges', (SELECT count(*) FROM iz.tag_edges),
    'entity_tags', (SELECT count(*) FROM iz.entity_tags),
    'identifiers', (SELECT count(*) FROM iz.identifiers),
    'command_epochs', (SELECT count(*) FROM iz.command_epochs),
    'domain_events', (SELECT count(*) FROM iz.domain_events),
    'event_subjects', (SELECT count(*) FROM iz.event_subjects),
    'replication_outbox', (SELECT count(*) FROM iz.replication_outbox),
    'command_receipts', (SELECT count(*) FROM iz.command_receipts),
    'sessions', (SELECT count(*) FROM iz.sessions)
)::text;
"@
    $recordCountsJson = (Invoke-Docker -Arguments @('exec', $container, 'psql', '-X', '-U', $DatabaseUser, '-d', $Database, '-At', '-c', $countQuery)) -join ''
    $recordCounts = $recordCountsJson | ConvertFrom-Json
    $recordCountsObservedAt = [DateTime]::UtcNow.ToString('o')

    $manifest = [ordered]@{
        format_version = 1
        created_at_utc = [DateTime]::UtcNow.ToString('o')
        database = $Database
        database_user = $DatabaseUser
        dump_file = [IO.Path]::GetFileName($dumpPath)
        dump_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $dumpPath).Hash.ToLowerInvariant()
        dump_size_bytes = (Get-Item $dumpPath).Length
        postgres_version = $postgresVersion
        database_image = $databaseImage
        database_image_id = $databaseImageId
        migration_versions = $migrationVersions
        application_image = $ApplicationImage
        application_image_id = $applicationImageId
        application_image_archive = $applicationArchive
        application_image_archive_sha256 = $applicationArchiveHash
        record_counts = $recordCounts
        record_counts_observed_at_utc = $recordCountsObservedAt
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($partialManifestPath, "$manifestJson`n", (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $partialManifestPath -Destination $manifestPath
    $backupCompleted = $true

    if ($RetentionDays -gt 0) {
        $cutoff = [DateTime]::UtcNow.AddDays(-$RetentionDays)
        Get-ChildItem -LiteralPath $destination -Filter "$safeDatabase-*.dump" -File |
            Where-Object { $_.LastWriteTimeUtc -lt $cutoff -and $_.FullName -ne $dumpPath } |
            ForEach-Object {
                $expiredManifest = Join-Path $destination "$($_.BaseName).manifest.json"
                Remove-Item -LiteralPath $_.FullName -Force
                if (Test-Path $expiredManifest) {
                    Remove-Item -LiteralPath $expiredManifest -Force
                }
            }
    }

    Write-Output "Backup: $dumpPath"
    Write-Output "Manifest: $manifestPath"
} finally {
    if (-not [string]::IsNullOrWhiteSpace($container)) {
        try { [void](Invoke-Docker -Arguments @('exec', $container, 'rm', '-f', $remoteDumpPath)) } catch { Write-Warning $_.Exception.Message }
    }
    $partialPaths = @($partialDumpPath, $partialManifestPath) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if (-not [string]::IsNullOrWhiteSpace($applicationArchivePartialPath)) {
        $partialPaths += $applicationArchivePartialPath
    }
    Remove-Item -LiteralPath $partialPaths -Force -ErrorAction SilentlyContinue
    if (-not $backupCompleted) {
        Remove-Item -LiteralPath $dumpPath, $manifestPath -Force -ErrorAction SilentlyContinue
    }
    Pop-Location
}