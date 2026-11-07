# Writes a timestamped folder holding a database dump and the files a rebuild needs. Only reads from the database,
# so it's safe while the app is running. Backups contain personal data: keep them private.
param(
    [string]$Destination,
    [string]$Container
)

. (Join-Path $PSScriptRoot 'db-common.ps1')

$db = Resolve-DbContainer $Container
if (-not $Destination) { $Destination = Join-Path $script:RepoRoot 'backups' }
$folder = Join-Path $Destination (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Path $folder -Force | Out-Null

Invoke-Native docker @('exec', $db, 'sh', '-c', 'pg_dump -U $POSTGRES_USER -d $POSTGRES_DB --format=custom --file=/tmp/linkbase.dump && pg_restore --list /tmp/linkbase.dump > /dev/null')
Invoke-Native docker @('cp', '-q', "${db}:/tmp/linkbase.dump", (Join-Path $folder 'linkbase.dump'))
Invoke-Native docker @('exec', $db, 'rm', '/tmp/linkbase.dump')

foreach ($file in 'data/samples/import.csv', 'data/samples/ticks.csv', 'data/corrections.json') {
    $path = Join-Path $script:RepoRoot $file
    if (Test-Path $path) {
        Copy-Item $path $folder
    } else {
        Write-Warning "$file wasn't found, so it isn't in this backup."
    }
}

$size = (Get-ChildItem $folder | Measure-Object Length -Sum).Sum
Write-Host ("Backed up {0:N1} MB. Copy this folder somewhere off this PC." -f ($size / 1MB))
Write-Output $folder
