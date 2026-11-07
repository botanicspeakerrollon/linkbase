# Proves recovery works without touching the real database, which it only reads from. In a throwaway container it:
#   1. restores the backup, which must match the real database exactly;
#   2. rebuilds from the export files, whose climbing data must match, and lists what a rebuild doesn't bring back.
# Exits non-zero if either fails. The container is always removed.
param(
    [string]$Backup
)

. (Join-Path $PSScriptRoot 'db-common.ps1')

$real = Resolve-DbContainer ''
if (-not $Backup) { $Backup = & (Join-Path $PSScriptRoot 'backup-db.ps1') }

$climbing = 'area', 'crag', 'route', 'ascent', 'partner', 'ascent_partner', 'trip', 'external_ref'
$other = 'graph_scene', 'review_item', 'import_batch'

function Get-Counts([string]$Container, [string]$Database = '$POSTGRES_DB') {
    $sql = (($climbing + $other) | ForEach-Object { "SELECT '$_', count(*) FROM $_" }) -join ' UNION ALL '
    $counts = @{}
    foreach ($row in (Invoke-Sql $Container "$sql;" $Database)) {
        $table, $count = $row -split '\|'
        $counts[$table] = [int]$count
    }

    return $counts
}

# Every ascent as text, so a difference in any field shows up, whatever the ids are.
$digestSql = @'
SELECT md5(string_agg(line, E'\n' ORDER BY line)) FROM (
    SELECT concat_ws('|', ar.name, c.name, r.name, r.grade, r.grade_system, r.discipline, r.length_m, r.pitches,
                     a.date, a.date_precision, a.style, a.style_raw, a.notes, a.my_rating, a.source, t.name,
                     (SELECT string_agg(p.name, ',' ORDER BY p.name)
                      FROM ascent_partner ap JOIN partner p ON p.id = ap.partner_id
                      WHERE ap.ascent_id = a.id)) AS line
    FROM ascent a
    JOIN route r ON r.id = a.route_id
    JOIN crag c ON c.id = r.crag_id
    JOIN area ar ON ar.id = c.area_id
    LEFT JOIN trip t ON t.id = a.trip_id
) lines;
'@

$failures = New-Object System.Collections.ArrayList
function Compare-Stage([string]$Stage, [hashtable]$Expected, [hashtable]$Actual, [string[]]$Tables) {
    foreach ($table in $Tables) {
        $mark = if ($Expected[$table] -eq $Actual[$table]) { 'ok  ' } else { [void]$failures.Add("$Stage $table"); 'DIFF' }
        Write-Host ('  {0} {1,-16} real {2,6}   {3} {4,6}' -f $mark, $table, $Expected[$table], $Stage, $Actual[$table])
    }
}

$image = docker inspect --format '{{.Config.Image}}' $real
$name = "linkbase-rehearsal-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$password = [guid]::NewGuid().ToString('N')
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()

$report = Join-Path $script:RepoRoot 'data/samples/reconciliation.md'
$savedReport = if (Test-Path $report) { [IO.File]::ReadAllBytes($report) } else { $null }

Invoke-Native docker @('run', '-d', '--name', $name, '-e', 'POSTGRES_DB=linkbase', '-e', 'POSTGRES_USER=linkbase', '-e', "POSTGRES_PASSWORD=$password", '-p', "127.0.0.1:${port}:5432", $image) | Out-Null
try {
    # The image's first-run setup also accepts connections briefly, but only over its socket, so this waits for TCP.
    for ($attempt = 0; ; $attempt++) {
        docker exec $name pg_isready -h 127.0.0.1 -U linkbase -d linkbase | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        if ($attempt -gt 60) { throw 'The rehearsal database never became ready.' }
        Start-Sleep -Seconds 1
    }

    $realCounts = Get-Counts $real
    $realDigest = Invoke-Sql $real $digestSql

    Write-Host "`nRestoring $Backup"
    $started = Get-Date
    & (Join-Path $PSScriptRoot 'restore-db.ps1') -Backup $Backup -Container $name -Yes | Out-Null
    Write-Host ("  took {0:N0} seconds" -f ((Get-Date) - $started).TotalSeconds)
    Compare-Stage 'restored' $realCounts (Get-Counts $name) ($climbing + $other)
    $same = (Invoke-Sql $name $digestSql) -eq $realDigest
    Write-Host "  every ascent identical: $same"
    if (-not $same) { [void]$failures.Add('restored ascents') }

    Write-Host "`nRebuilding from the export files"
    $started = Get-Date
    Invoke-Sql $name 'CREATE DATABASE rebuild;' | Out-Null
    $connection = "Host=127.0.0.1;Port=$port;Database=rebuild;Username=linkbase;Password=$password"
    Push-Location $script:RepoRoot
    try {
        foreach ($step in @(
            @('ef', 'database', 'update', '--project', 'src/Linkbase', '--configuration', 'Release', '--connection', $connection),
            @('run', '--project', 'src/Linkbase', '-c', 'Release', '--no-launch-profile', '--', 'import', 'all'))) {
            # Beats user secrets, so the import can only reach the throwaway database.
            $env:ConnectionStrings__Linkbase = $connection
            $output = & dotnet @step
            Remove-Item Env:ConnectionStrings__Linkbase
            if ($LASTEXITCODE -ne 0) {
                $output | Select-Object -Last 15 | Write-Host
                throw "dotnet $($step[0]) failed with exit code $LASTEXITCODE"
            }
        }
    } finally {
        Pop-Location
    }

    Write-Host ("  took {0:N0} seconds" -f ((Get-Date) - $started).TotalSeconds)
    $rebuilt = Get-Counts $name 'rebuild'
    Compare-Stage 'rebuilt' $realCounts $rebuilt $climbing
    $same = (Invoke-Sql $name $digestSql 'rebuild') -eq $realDigest
    Write-Host "  every ascent identical: $same"
    if (-not $same) { [void]$failures.Add('rebuilt ascents') }

    Write-Host '  Not brought back by a rebuild, so only a backup keeps these:'
    foreach ($table in $other) {
        Write-Host ('       {0,-16} real {1,6}   rebuilt {2,6}' -f $table, $realCounts[$table], $rebuilt[$table])
    }
    Write-Host ('       decided duplicates: real {0}, rebuilt {1}' -f
        (Invoke-Sql $real "SELECT count(*) FROM review_item WHERE status <> 'open';"),
        (Invoke-Sql $name "SELECT count(*) FROM review_item WHERE status <> 'open';" 'rebuild'))
} finally {
    docker rm -f $name | Out-Null

    # The rebuild's import writes this report over the real one.
    if ($null -ne $savedReport) { [IO.File]::WriteAllBytes($report, $savedReport) }
}

if ($failures.Count -gt 0) {
    Write-Host "`nFAILED: $($failures -join ', ')"
    exit 1
}

Write-Host "`nRecovery works."
