param([int]$Processor = -1, [switch]$JunctionOnly, [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA ('Groundwork/WP17-ETW-' + (Get-Date -Format yyyyMMdd-HHmmss))))

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Start this script from an administrative PowerShell.'
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$assembly = Join-Path $repo 'artifacts/bin/Groundwork.LandformPrototype/release/Groundwork.LandformPrototype.dll'
$directory = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
if ($Processor -ge 0) {
    $topology = @(& (Join-Path $PSScriptRoot 'Get-Wp17CpuSets.ps1'))
    $class = ($topology | Measure-Object EfficiencyClass -Maximum).Maximum
    $classes = @($topology.EfficiencyClass | Sort-Object -Unique)
    if ($classes.Count -lt 2 -or !($topology | Where-Object {
        $_.Group -eq 0 -and $_.Processor -eq $Processor -and $_.EfficiencyClass -eq $class
    })) {
        throw "Logical processor $Processor is not in the performance class of group 0."
    }
    $topology | Export-Csv (Join-Path $directory 'topology.csv') -NoTypeInformation
}
$profile = Join-Path $PSScriptRoot 'Scheduler.wprp'
& dotnet build (Join-Path $PSScriptRoot 'Groundwork.LandformPrototype.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Prototype build failed.' }
$etl = Join-Path $directory 'scheduler.etl'
$sweeps = Join-Path $directory 'sweeps.csv'
'sweep_process,sweep_thread,qpc_frequency,seed,scenario,block,sweep,variant,start_qpc,end_qpc,thread_cycles' | Set-Content $sweeps
$record = @('-start', "$profile!WP17Scheduler")
$benchmark = @($assembly, 'paired', $sweeps)
if ($Processor -ge 0) {
    $record += @('-start', 'Power')
    $benchmark += [string]$Processor
} elseif ($JunctionOnly) {
    throw 'Specify a performance processor for the focused capture.'
}
$expectedRows = 17280
if ($JunctionOnly) {
    $expectedRows = 5760
    $benchmark += 'junction'
    Write-Host 'Measuring the junction only. Keep the desktop idle until [FERTIG].'
}
& wpr @record -filemode
if ($LASTEXITCODE -ne 0) { throw 'Scheduler recording failed to start.' }
try {
    & dotnet @benchmark > (Join-Path $directory 'percentiles.csv') 2> (Join-Path $directory 'bricks.log')
    if ($LASTEXITCODE -ne 0) { throw 'Prototype measurement failed.' }
} finally {
    & wpr -stop $etl
    if ($LASTEXITCODE -ne 0) { throw 'Scheduler recording could not be saved.' }
}
if (!(Test-Path $etl) -or @(Get-Content $sweeps).Count -ne ($expectedRows + 1)) {
    throw "Capture incomplete. Inspect $directory."
}
Write-Host "[FERTIG] $directory"
