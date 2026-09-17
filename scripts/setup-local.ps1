$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root '.env'
if (Test-Path $path) {
    Write-Host '.env already exists; it was not modified.'
    exit 0
}
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $passwords = 1..2 | ForEach-Object {
        $bytes = New-Object byte[] 32
        $random.GetBytes($bytes)
        -join ($bytes | ForEach-Object { $_.ToString('x2') })
    }
    $lines = @(
        "POSTGRES_PASSWORD=$($passwords[0])"
        "IZ_RUNTIME_PASSWORD=$($passwords[1])"
        'IZ_DB_PORT=55438'
        'IZ_HTTP_PORT=8088'
    )
    $content = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $stream.Write($content, 0, $content.Length) } finally { $stream.Dispose() }
    Write-Host 'Created local configuration with independent random database passwords.'
} finally {
    $random.Dispose()
}