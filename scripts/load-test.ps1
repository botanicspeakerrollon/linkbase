# Several users at once against the sample models. See "Several users at once" in docs/performance.md.
# Reads only, so it is safe on any database, but run it against the samples container, not the real logbook.
#
#   scripts/load-test.ps1
#   scripts/load-test.ps1 -Users 1,10,50 -Seconds 30 -Think 0
param(
    [int[]] $Users = @(1, 5, 10, 25),
    [int] $Seconds = 15,
    [int] $Think = 200,
    [string] $Report = "load.md",
    # Name=connection pairs. Defaults: the small consultancy model, the demo climbing logbook, the large Swiss sample.
    [string[]] $Models = @(
        "small (consultancy)=Host=127.0.0.1;Port=55433;Database=consultancy;Username=linkbase;Command Timeout=0",
        "medium (demo climbing)=Host=127.0.0.1;Port=55433;Database=climbing_demo;Username=linkbase;Command Timeout=0",
        "large (Swiss public transport)=Host=127.0.0.1;Port=55433;Database=swiss_rail;Username=linkbase;Command Timeout=0"
    )
)

$ErrorActionPreference = "Stop"
$env:LINKBASE_LOAD_MODELS = $Models -join "|"
$env:LINKBASE_LOAD_USERS = $Users -join ","
$env:LINKBASE_LOAD_SECONDS = $Seconds
$env:LINKBASE_LOAD_THINK_MS = [Math]::Max($Think, 1)
$env:LINKBASE_LOAD_REPORT = Join-Path (Get-Location) $Report

dotnet test -c Release --filter "FullyQualifiedName~LoadTests" --logger "console;verbosity=normal"
if ($LASTEXITCODE -eq 0) { Write-Host "Report: $env:LINKBASE_LOAD_REPORT" }
exit $LASTEXITCODE
