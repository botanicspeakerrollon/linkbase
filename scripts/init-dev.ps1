# First-time local setup: creates a random database password in .env (git-ignored)
# and writes the matching connection string to src/Linkbase/appsettings.Development.local.json (also git-ignored).
# Safe to re-run.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'

if (Test-Path $envFile) {
    $line = Get-Content $envFile | Where-Object { $_ -match '^POSTGRES_PASSWORD=' } | Select-Object -First 1
    if (-not $line) { throw ".env exists but has no POSTGRES_PASSWORD line" }
    $password = $line.Substring('POSTGRES_PASSWORD='.Length)
    Write-Host 'Using the existing .env'
} else {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    Set-Content -Path $envFile -Value "POSTGRES_PASSWORD=$password" -Encoding ascii
    Write-Host 'Created .env'
}

$connectionString = "Host=localhost;Port=5432;Database=linkbase;Username=linkbase;Password=$password"

# Only the Development environment reads this file, and only if it is there. The password is never printed.
$settingsFile = Join-Path $root 'src/Linkbase/appsettings.Development.local.json'
@{ ConnectionStrings = @{ Linkbase = $connectionString } } | ConvertTo-Json | Set-Content -Path $settingsFile -Encoding utf8

Write-Host 'Wrote ConnectionStrings:Linkbase to src/Linkbase/appsettings.Development.local.json'
