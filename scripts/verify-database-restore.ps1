[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$DumpPath,

    [string]$ManifestPath,
    [string]$ProjectName,
    [string]$ApplicationImage = 'inventoryzing-coordinator:local',
    [string]$PostgresImage = 'postgres:18.3-alpine',
    [switch]$KeepEnvironment
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

function New-RandomHex {
    param([ValidateRange(1, 1024)][int]$Bytes = 32)

    $buffer = New-Object byte[] $Bytes
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($buffer)
        return -join ($buffer | ForEach-Object { $_.ToString('x2') })
    } finally {
        $random.Dispose()
    }
}

function Get-RestoreCounts {
    param(
        [Parameter(Mandatory = $true)][string]$ComposeFile,
        [Parameter(Mandatory = $true)][string]$ComposeProject,
        [Parameter(Mandatory = $true)][string]$Database
    )

    $query = @"
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
    $arguments = @('compose', '--file', $ComposeFile, '--project-name', $ComposeProject, 'exec', '-T', 'db',
        'psql', '-X', '-U', 'inventoryzing', '-d', $Database, '-v', 'ON_ERROR_STOP=1', '-At', '-c', $query)
    return ((Invoke-Docker -Arguments $arguments) -join '') | ConvertFrom-Json
}

$root = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $root 'deploy/restore.compose.yaml'
$resolvedDumpPath = (Resolve-Path -LiteralPath $DumpPath).Path
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $manifestName = "{0}.manifest.json" -f [IO.Path]::GetFileNameWithoutExtension($resolvedDumpPath)
    $ManifestPath = Join-Path ([IO.Path]::GetDirectoryName($resolvedDumpPath)) $manifestName
}
$resolvedManifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
if ($manifest.format_version -ne 1) {
    throw "Unsupported backup manifest format '$($manifest.format_version)'."
}

$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedDumpPath).Hash.ToLowerInvariant()
if ($actualHash -ne $manifest.dump_sha256) {
    throw 'Backup SHA-256 does not match its manifest.'
}

if ([string]::IsNullOrWhiteSpace($ProjectName)) {
    $ProjectName = "inventoryzing-restore-$([Guid]::NewGuid().ToString('N').Substring(0, 10))"
}
if ($ProjectName -notmatch '^inventoryzing-restore-[a-z0-9][a-z0-9-]*$') {
    throw "Restore project name must begin with 'inventoryzing-restore-' and contain only lowercase letters, digits, and hyphens."
}
$existingResources = @()
foreach ($resourceType in @('container', 'network', 'volume')) {
    $listArguments = if ($resourceType -eq 'container') {
        @('ps', '--all', '--quiet', '--filter', "label=com.docker.compose.project=$ProjectName")
    } else {
        @($resourceType, 'ls', '--quiet', '--filter', "label=com.docker.compose.project=$ProjectName")
    }
    $resourceIds = Invoke-Docker -Arguments $listArguments | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if (@($resourceIds).Count -gt 0) {
        $existingResources += $resourceType
    }
}
if ($existingResources.Count -gt 0) {
    throw "Restore project '$ProjectName' already owns Docker resources ($($existingResources -join ', ')); choose a new project name or remove that isolated project explicitly."
}

$restoreDatabase = 'inventoryzing_restore_test'
$countNames = @(
    'sites', 'entities', 'principals', 'accounts', 'roles', 'permissions',
    'role_permissions', 'account_roles', 'object_types', 'objects', 'placements',
    'tags', 'tag_edges', 'entity_tags', 'identifiers', 'command_epochs',
    'domain_events', 'event_subjects', 'replication_outbox', 'command_receipts', 'sessions'
)
$manifestCountNames = @($manifest.record_counts.PSObject.Properties.Name)
if ($manifestCountNames.Count -eq 0) {
    throw 'Backup manifest contains no record-count evidence.'
}
$unsupportedCountNames = @($manifestCountNames | Where-Object { $_ -notin $countNames })
if ($unsupportedCountNames.Count -gt 0) {
    throw "Backup manifest contains unsupported record counts: $($unsupportedCountNames -join ', ')."
}
$sourceRecordCountsObservedAt = $null
$observedAtProperty = $manifest.PSObject.Properties['record_counts_observed_at_utc']
if ($null -ne $observedAtProperty) {
    $sourceRecordCountsObservedAt = $observedAtProperty.Value
}
$environmentNames = @(
    'IZ_RESTORE_APPLICATION_IMAGE',
    'IZ_RESTORE_DATABASE',
    'IZ_RESTORE_OWNER_PASSWORD',
    'IZ_RESTORE_POSTGRES_IMAGE',
    'IZ_RESTORE_RUNTIME_PASSWORD'
)
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$env:IZ_RESTORE_APPLICATION_IMAGE = $ApplicationImage
$env:IZ_RESTORE_DATABASE = $restoreDatabase
$env:IZ_RESTORE_OWNER_PASSWORD = New-RandomHex
$env:IZ_RESTORE_POSTGRES_IMAGE = $PostgresImage
$env:IZ_RESTORE_RUNTIME_PASSWORD = New-RandomHex

$composePrefix = @('compose', '--file', $composeFile, '--project-name', $ProjectName)
$environmentStarted = $false
$environmentRemoved = $false
$remoteDumpPath = '/tmp/inventoryzing-restore.dump'
$report = $null

Push-Location $root
try {
    $environmentStarted = $true
    [void](Invoke-Docker -Arguments ($composePrefix + @('up', '--detach', '--wait', 'db')))
    $databaseContainer = (Invoke-Docker -Arguments ($composePrefix + @('ps', '--quiet', 'db')) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1).Trim()
    $restoredDatabaseImage = (Invoke-Docker -Arguments @('inspect', '--type', 'container', '--format', '{{.Config.Image}}', $databaseContainer) |
        Select-Object -First 1).Trim()
    $restoredDatabaseImageId = (Invoke-Docker -Arguments @('inspect', '--type', 'container', '--format', '{{.Image}}', $databaseContainer) |
        Select-Object -First 1).Trim()
    $restoredPostgresVersion = (Invoke-Docker -Arguments ($composePrefix + @('exec', '-T', 'db', 'psql', '-X', '-U', 'inventoryzing',
        '-d', $restoreDatabase, '-At', '-c', 'SHOW server_version;')) | Select-Object -First 1).Trim()
    [void](Invoke-Docker -Arguments ($composePrefix + @('cp', $resolvedDumpPath, "db:$remoteDumpPath")))
    [void](Invoke-Docker -Arguments ($composePrefix + @('exec', '-T', 'db', 'pg_restore', '-U', 'inventoryzing',
        '-d', $restoreDatabase, '--no-owner', '--no-privileges', '--exit-on-error', $remoteDumpPath)))

    $restoredCounts = Get-RestoreCounts -ComposeFile $composeFile -ComposeProject $ProjectName -Database $restoreDatabase
    $countComparison = [ordered]@{}
    foreach ($name in $manifestCountNames) {
        $countComparison[$name] = [ordered]@{
            source_observation = [long]$manifest.record_counts.$name
            restored = [long]$restoredCounts.$name
            matches = [long]$restoredCounts.$name -eq [long]$manifest.record_counts.$name
        }
    }

    [void](Invoke-Docker -Arguments ($composePrefix + @('run', '--rm', 'migrate')))
    $migratedCounts = Get-RestoreCounts -ComposeFile $composeFile -ComposeProject $ProjectName -Database $restoreDatabase
    foreach ($name in $countNames) {
        if ([long]$migratedCounts.$name -ne [long]$restoredCounts.$name) {
            throw "Migration changed the restored $name count from $($restoredCounts.$name) to $($migratedCounts.$name)."
        }
    }

    [void](Invoke-Docker -Arguments ($composePrefix + @('exec', '-T', 'db', 'psql', '-X', '-U', 'inventoryzing',
        '-d', $restoreDatabase, '-v', 'ON_ERROR_STOP=1', '-c', 'TRUNCATE TABLE iz.sessions;')))
    $postInvalidationCounts = Get-RestoreCounts -ComposeFile $composeFile -ComposeProject $ProjectName -Database $restoreDatabase
    if ([long]$postInvalidationCounts.sessions -ne 0) {
        throw 'Restored sessions were not invalidated.'
    }

    $migrationVersions = (Invoke-Docker -Arguments ($composePrefix + @('exec', '-T', 'db', 'psql', '-X', '-U', 'inventoryzing',
        '-d', $restoreDatabase, '-At', '-c', "SELECT COALESCE(string_agg(version_num, ',' ORDER BY version_num), '') FROM public.alembic_version;")) |
        Select-Object -First 1).Trim()
    $applicationImageId = (Invoke-Docker -Arguments @('image', 'inspect', '--format', '{{.Id}}', $ApplicationImage) |
        Select-Object -First 1).Trim()
    [void](Invoke-Docker -Arguments ($composePrefix + @('exec', '-T', 'db', 'rm', '-f', $remoteDumpPath)))

    $report = [ordered]@{
        format_version = 1
        verified_at_utc = [DateTime]::UtcNow.ToString('o')
        dump_file = [IO.Path]::GetFileName($resolvedDumpPath)
        dump_sha256 = $actualHash
        source_migration_versions = $manifest.migration_versions
        restored_migration_versions = $migrationVersions
        source_postgres_version = $manifest.postgres_version
        restored_postgres_version = $restoredPostgresVersion
        source_database_image = $manifest.database_image
        source_database_image_id = $manifest.database_image_id
        restored_database_image = $restoredDatabaseImage
        restored_database_image_id = $restoredDatabaseImageId
        source_application_image = $manifest.application_image
        source_application_image_id = $manifest.application_image_id
        application_image = $ApplicationImage
        application_image_id = $applicationImageId
        source_record_counts_observed_at_utc = $sourceRecordCountsObservedAt
        source_record_counts = $manifest.record_counts
        record_counts = $restoredCounts
        source_count_comparison = $countComparison
        count_comparison_is_observational = $true
        restored_sessions_invalidated = [long]$restoredCounts.sessions
        post_invalidation_record_counts = $postInvalidationCounts
        remaining_restored_sessions = [long]$postInvalidationCounts.sessions
        environment_retained = [bool]$KeepEnvironment
    }

    if (-not $KeepEnvironment) {
        [void](Invoke-Docker -Arguments ($composePrefix + @('down', '--volumes', '--remove-orphans')))
        $environmentRemoved = $true
    }

    $reportTimestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $reportName = "{0}.restore-verified-{1}.json" -f [IO.Path]::GetFileNameWithoutExtension($resolvedDumpPath), $reportTimestamp
    $reportPath = Join-Path ([IO.Path]::GetDirectoryName($resolvedDumpPath)) $reportName
    $reportJson = $report | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($reportPath, "$reportJson`n", (New-Object Text.UTF8Encoding($false)))
    Write-Output "Restore verification passed: $reportPath"
    if ($KeepEnvironment) {
        Write-Output "Isolated project retained without host ports: $ProjectName"
    }
} finally {
    if ($environmentStarted -and -not $KeepEnvironment -and -not $environmentRemoved) {
        try {
            [void](Invoke-Docker -Arguments ($composePrefix + @('down', '--volumes', '--remove-orphans')))
        } catch {
            Write-Warning "Could not remove isolated restore project '$ProjectName': $($_.Exception.Message)"
        }
    }
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
    Pop-Location
}