# First-time local setup: creates a random database password in .env (git-ignored)
# and stores the matching connection string in user secrets. Safe to re-run.
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

# On success the tool echoes the value, so its output is kept quiet, but a failure must still be seen: it's shown with
# the password blanked out.
$output = dotnet user-secrets set 'ConnectionStrings:Linkbase' $connectionString --project (Join-Path $root 'src/Linkbase') 2>&1
if ($LASTEXITCODE -ne 0) {
    $output | ForEach-Object { "$_".Replace($password, '********') } | Write-Host
    throw "Storing ConnectionStrings:Linkbase failed with exit code $LASTEXITCODE"
}

# Checks it's really there, looking at the names only, never the values.
$names = dotnet user-secrets list --project (Join-Path $root 'src/Linkbase') 2>&1 | ForEach-Object { ("$_" -split ' = ', 2)[0] }
if ($names -notcontains 'ConnectionStrings:Linkbase') {
    throw "ConnectionStrings:Linkbase isn't in user secrets after storing it. Names there: $($names -join ', ')"
}

Write-Host 'Stored ConnectionStrings:Linkbase in user secrets'
