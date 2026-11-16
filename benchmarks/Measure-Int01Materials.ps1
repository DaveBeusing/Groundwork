[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateRange(1, 30)][int] $Pairs = 5,
    [ValidateRange(1, 10000)][int] $Frames = 2000,
    [ValidateSet('Hardware', 'Warp')][string] $Adapter = 'Hardware',
    [string] $MachineLabel = 'local-workstation',
    [switch] $DebugLayer
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw 'Use a new output directory.' }
$project = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
$environmentBefore = @{}
foreach ($name in 'GROUNDWORK_INT01_OUTPUT', 'GROUNDWORK_INT01_PAIR', 'GROUNDWORK_INT01_FRAMES', 'GROUNDWORK_INT01_ADAPTER', 'GROUNDWORK_INT01_DEBUG') {
    $environmentBefore[$name] = [Environment]::GetEnvironmentVariable($name)
}

function Measure-Percentiles([double[]] $Values) {
    [Array]::Sort($Values)
    [ordered]@{
        p50 = $Values[[int][Math]::Ceiling(0.50 * $Values.Length) - 1]
        p95 = $Values[[int][Math]::Ceiling(0.95 * $Values.Length) - 1]
        p99 = $Values[[int][Math]::Ceiling(0.99 * $Values.Length) - 1]
    }
}

function Measure-Route($Route, [double] $MicrosecondsPerTick) {
    [ordered]@{
        cpuRecordUs = Measure-Percentiles @($Route.measurements.cpuRecordTicks | ForEach-Object { $_ * $MicrosecondsPerTick })
        gpuTerrainUs = Measure-Percentiles @($Route.measurements.gpuTerrainMicroseconds)
        turnUs = Measure-Percentiles @($Route.measurements.turnTicks | ForEach-Object { $_ * $MicrosecondsPerTick })
    }
}

Push-Location -LiteralPath $root
try {
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the source commit.' }
    $dirty = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect source status.' }
    $sdk = dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the .NET SDK.' }
    $cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    $memory = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    New-Item -ItemType Directory -Path $target | Out-Null
    [ordered]@{
        schema = 1
        machineLabel = $MachineLabel
        sourceCommit = $commit
        sourceModified = $dirty.Count -ne 0
        windows = [Environment]::OSVersion.Version.ToString()
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        sdk = $sdk
        cpu = $cpu
        memoryBytes = $memory
        requestedAdapter = $Adapter
        pairs = $Pairs
        measuredFramesPerVariantPerPhase = $Frames
        debugLayerAndDred = [bool]$DebugLayer
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $target 'metadata.json') -Encoding utf8
    dotnet restore $project --locked-mode *> (Join-Path $target 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed. See $target/restore.log." }
    dotnet build $project -c Release --no-restore --nologo -p:ContinuousIntegrationBuild=true *> (Join-Path $target 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Release build failed. See $target/build.log." }

    $pool = @{}
    $runSummaries = [Collections.Generic.List[object]]::new()
    $fixtureKey = $null
    $env:GROUNDWORK_INT01_ADAPTER = $Adapter
    $env:GROUNDWORK_INT01_DEBUG = if ($DebugLayer) { '1' } else { '0' }
    $env:GROUNDWORK_INT01_FRAMES = $Frames.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    for ($pair = 1; $pair -le $Pairs; $pair++) {
        $env:GROUNDWORK_INT01_PAIR = $pair.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        $raw = Join-Path $target "pair-$pair.json"
        $env:GROUNDWORK_INT01_OUTPUT = $raw
        $trxName = "pair-$pair.trx"
        $log = Join-Path $target "pair-$pair.log"
        $testArgs = @('test', $project, '-c', 'Release', '--no-build', '--no-restore', '--nologo', '--results-directory', $target,
            '--filter', 'FullyQualifiedName~Int01PairedNativeMaterialTimings', '--logger', "trx;LogFileName=$trxName")
        dotnet @testArgs *> $log
        if ($LASTEXITCODE -ne 0) { throw "Pair $pair failed. See $log and $target/$trxName." }
        [xml] $trx = Get-Content -LiteralPath (Join-Path $target $trxName) -Raw
        $test = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
        if ($test.Count -ne 1 -or $test[0].outcome -ne 'Passed') { throw "Pair $pair did not record a successful test." }
        $observation = Get-Content -LiteralPath $raw -Raw | ConvertFrom-Json
        if ($observation.schema -ne 1 -or $observation.pair -ne $pair -or $observation.width -ne 1920 -or
            $observation.height -ne 1080 -or $observation.measuredFramesPerVariantPerPhase -ne $Frames -or
            $observation.debugLayerAndDred -ne [bool]$DebugLayer -or
            ($observation.phases.phase -join ',') -ne 'near,orbit') { throw "Pair $pair did not contain the expected phases." }
        $currentKey = [ordered]@{
            fixture = $observation.fixture
            phases = @($observation.phases | Select-Object phase, sceneSha256, meshes, triangles, assets)
            shaderSha256 = $observation.shaderSha256
            adapter = $observation.adapter
            runtime = $observation.runtime
            debugLayerAndDred = $observation.debugLayerAndDred
            frequency = $observation.stopwatchFrequency
        } | ConvertTo-Json -Depth 10 -Compress
        if ($null -eq $fixtureKey) { $fixtureKey = $currentKey }
        elseif ($fixtureKey -cne $currentKey) { throw "Scene, shaders, adapter or runtime changed in pair $pair." }

        $microsecondsPerTick = 1000000.0 / $observation.stopwatchFrequency
        foreach ($phase in $observation.phases) {
            $order = if ($pair % 2) { 'appearance-off,textured-pbr' } else { 'textured-pbr,appearance-off' }
            if (($phase.routes.route -join ',') -ne $order) { throw "Pair $pair did not alternate its route order." }
            foreach ($route in $phase.routes) {
                if ($route.measurements.Count -ne $Frames) { throw "Pair $pair did not contain $Frames measured frames." }
                $runSummaries.Add([pscustomobject]@{
                    pair = $pair
                    phase = $phase.phase
                    route = $route.route
                    order = $route.order
                    timing = Measure-Route $route $microsecondsPerTick
                    backing = $route.backing
                })
                $key = "$($phase.phase)/$($route.route)"
                if (-not $pool.ContainsKey($key)) {
                    $pool[$key] = @{
                        cpu = [Collections.Generic.List[double]]::new()
                        gpu = [Collections.Generic.List[double]]::new()
                        turn = [Collections.Generic.List[double]]::new()
                    }
                }
                foreach ($row in $route.measurements) {
                    $pool[$key].cpu.Add($row.cpuRecordTicks * $microsecondsPerTick)
                    $pool[$key].gpu.Add($row.gpuTerrainMicroseconds)
                    $pool[$key].turn.Add($row.turnTicks * $microsecondsPerTick)
                }
            }
        }
        Write-Host "Collected near and orbit pair $pair of $Pairs on $($observation.adapter.description)."
    }

    $pooled = foreach ($phase in 'near', 'orbit') {
        foreach ($mode in 'appearance-off', 'textured-pbr') {
            $timings = $pool["$phase/$mode"]
            [pscustomobject]@{
                phase = $phase
                route = $mode
                frames = $timings.cpu.Count
                cpuRecordUs = Measure-Percentiles $timings.cpu.ToArray()
                gpuTerrainUs = Measure-Percentiles $timings.gpu.ToArray()
                turnUs = Measure-Percentiles $timings.turn.ToArray()
            }
        }
    }
    [ordered]@{
        schema = 1
        fixture = $fixtureKey | ConvertFrom-Json
        pairs = $Pairs
        framesPerVariantPerPhase = $Frames
        pooled = @($pooled)
        runs = $runSummaries.ToArray()
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $target 'summary.json') -Encoding utf8
    $pooled | Format-Table phase, route, frames, @{ Name = 'GPU terrain p95 us'; Expression = { $_.gpuTerrainUs.p95 } }
}
finally {
    foreach ($name in $environmentBefore.Keys) {
        [Environment]::SetEnvironmentVariable($name, $environmentBefore[$name])
    }
    Pop-Location
}
