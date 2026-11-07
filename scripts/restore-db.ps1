# Replaces everything in the database with a backup. Stop the app first. Backs up the current database before touching
# it, and restores in one transaction, so a failed restore leaves the data as it was.
param(
    [Parameter(Mandatory = $true)][string]$Backup,
    [string]$Container,
    [switch]$Yes
)

. (Join-Path $PSScriptRoot 'db-common.ps1')

# Backups from before the rename hold logbook.dump.
$dump = @('linkbase.dump', 'logbook.dump') | ForEach-Object { Join-Path $Backup $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dump) { throw "There's no linkbase.dump in $Backup" }
$db = Resolve-DbContainer $Container

if (-not $Yes) {
    $answer = Read-Host "This replaces everything in the database with $Backup. Type RESTORE to continue"
    if ($answer -cne 'RESTORE') {
        Write-Host 'Nothing changed.'
        return
    }
}

if (-not $Container) {
    $safety = & (Join-Path $PSScriptRoot 'backup-db.ps1')
    Write-Host "Saved the current database to $safety first."
}

Invoke-Native docker @('cp', '-q', $dump, "${db}:/tmp/linkbase.dump")
Invoke-Native docker @('exec', $db, 'sh', '-c', 'pg_restore -U $POSTGRES_USER -d $POSTGRES_DB --clean --if-exists --no-owner --single-transaction /tmp/linkbase.dump')
Invoke-Native docker @('exec', $db, 'rm', '/tmp/linkbase.dump')
Write-Host 'Restored. If the app is newer than the backup, run: dotnet ef database update --project src/Linkbase'
