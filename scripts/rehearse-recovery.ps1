# Proves recovery works without touching the real database, which it only reads from. In a throwaway container it:
#   1. restores the backup, which must match the real database exactly;
#   2. rebuilds from the export files, whose climbing records must match, and lists what a rebuild doesn't bring back.
# Exits non-zero if either fails. The container is always removed.
param(
    [string]$Backup
)

. (Join-Path $PSScriptRoot 'db-common.ps1')

$real = Resolve-DbContainer ''
if (-not $Backup) { $Backup = & (Join-Path $PSScriptRoot 'backup-db.ps1') }

# The climbing records are node types in the generic store. The other tables are counted as they are.
$climbing = 'Area', 'Crag', 'Route', 'Ascent', 'Partner', 'Trip', 'external_ref'
$other = 'graph_scene', 'import_batch'

function Get-Counts([string]$Container, [string]$Database = '$POSTGRES_DB') {
    $sql = (($climbing + $other) | ForEach-Object {
        if ($_ -cmatch '^[A-Z]') { "SELECT '$_', count(*) FROM node WHERE type = '$_'" } else { "SELECT '$_', count(*) FROM $_" }
    }) -join ' UNION ALL '
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
    SELECT concat_ws('|', cc.country, crag.name, r.name, r.props->>'grade', r.props->>'grade_system', r.props->>'discipline',
                     r.props->>'length_m', r.props->>'pitches', a.props->>'date', a.props->>'date_precision', a.props->>'style',
                     a.props->>'style_raw', a.props->>'notes', a.props->>'my_rating', a.props->>'source', trip.name,
                     (SELECT string_agg(p.name, ',' ORDER BY p.name)
                      FROM node_link w JOIN node p ON p.id = w.target_id
                      WHERE w.source_id = a.id AND w.rel_type = 'WITH')) AS line
    FROM node a
    JOIN node_link of_link ON of_link.source_id = a.id AND of_link.rel_type = 'OF'
    JOIN node r ON r.id = of_link.target_id
    JOIN node_link at_link ON at_link.source_id = r.id AND at_link.rel_type = 'AT'
    JOIN node crag ON crag.id = at_link.target_id
    LEFT JOIN climbing_crag_country cc ON cc.crag_id = crag.id
    LEFT JOIN node_link trip_link ON trip_link.source_id = a.id AND trip_link.rel_type = 'ON_TRIP'
    LEFT JOIN node trip ON trip.id = trip_link.target_id
    WHERE a.type = 'Ascent'
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
            @('run', '--project', 'src/Linkbase', '-c', 'Release', '--no-launch-profile', '--', 'climbing', 'import', 'default', 'all', '--fresh', '--same-database'))) {
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
} finally {
    docker rm -f $name | Out-Null
}

if ($failures.Count -gt 0) {
    Write-Host "`nFAILED: $($failures -join ', ')"
    exit 1
}

Write-Host "`nRecovery works."
