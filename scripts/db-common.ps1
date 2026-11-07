# Shared by the backup, restore and rehearsal scripts. Dot-source it, don't run it.
$ErrorActionPreference = 'Stop'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

# The compose database, unless a script is pointed at another container.
function Resolve-DbContainer([string]$Container) {
    if ($Container) { return $Container }

    Push-Location $script:RepoRoot
    try { $id = docker compose ps -q db } finally { Pop-Location }
    if (-not $id) { throw 'The database is not running. Start it with: docker compose up -d --wait' }
    return $id
}

# Stops on failure, which native commands don't do by themselves.
function Invoke-Native([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command $($Arguments[0]) failed with exit code $LASTEXITCODE" }
}

# Runs SQL in the container's own database, or a named one, and returns the rows as lines.
function Invoke-Sql([string]$Container, [string]$Sql, [string]$Database = '$POSTGRES_DB') {
    $rows = $Sql | docker exec -i $Container sh -c "psql -v ON_ERROR_STOP=1 -U `$POSTGRES_USER -d $Database -At"
    if ($LASTEXITCODE -ne 0) { throw "psql failed with exit code $LASTEXITCODE" }
    return $rows
}
