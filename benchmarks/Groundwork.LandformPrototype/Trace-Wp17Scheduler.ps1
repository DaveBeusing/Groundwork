param([string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA ('Groundwork/WP17-ETW-' + (Get-Date -Format yyyyMMdd-HHmmss))))

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Start this script from an administrative PowerShell.'
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$assembly = Join-Path $repo 'artifacts/bin/Groundwork.LandformPrototype/release/Groundwork.LandformPrototype.dll'
$directory = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$profile = Join-Path $PSScriptRoot 'Scheduler.wprp'
& dotnet build (Join-Path $PSScriptRoot 'Groundwork.LandformPrototype.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Prototype build failed.' }
$etl = Join-Path $directory 'scheduler.etl'
$sweeps = Join-Path $directory 'sweeps.csv'
'sweep_process,sweep_thread,qpc_frequency,seed,scenario,block,sweep,variant,start_qpc,end_qpc,thread_cycles' | Set-Content $sweeps
& wpr -start "$profile!WP17Scheduler" -filemode
if ($LASTEXITCODE -ne 0) { throw 'Scheduler recording failed to start.' }
try {
    & dotnet $assembly paired $sweeps > (Join-Path $directory 'percentiles.csv') 2> (Join-Path $directory 'bricks.log')
    if ($LASTEXITCODE -ne 0) { throw 'Prototype measurement failed.' }
} finally {
    & wpr -stop $etl
    if ($LASTEXITCODE -ne 0) { throw 'Scheduler recording could not be saved.' }
}
if (!(Test-Path $etl) -or @(Get-Content $sweeps).Count -ne 17281) {
    throw "Capture incomplete. Inspect $directory."
}
Write-Host "[FERTIG] $directory"
