[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateRange(1, 30)][int] $Pairs = 5,
    [ValidateRange(1, 10000)][int] $Frames = 2000,
    [ValidateSet('Hardware', 'Warp')][string] $Adapter = 'Hardware'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw 'Use a new output directory.' }
$project = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
$phaseNames = 'midday', 'low-sun', 'quick-turns'
$environmentBefore = @{}
foreach ($name in 'GROUNDWORK_WP15_OUTPUT', 'GROUNDWORK_WP15_PAIR', 'GROUNDWORK_WP15_FRAMES', 'GROUNDWORK_WP15_ADAPTER') {
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

function Compare-ShadowRoutes([object[]] $Off, [object[]] $On, [double] $MicrosecondsPerTick) {
    $incrementalGpu = for ($index = 0; $index -lt $On.Count; $index++) {
        $On[$index].nearShadowMicroseconds + $On[$index].farShadowMicroseconds +
            $On[$index].terrainMicroseconds - $Off[$index].terrainMicroseconds
    }
    $offFrame = Measure-Percentiles $Off.frameMicroseconds
    $onFrame = Measure-Percentiles $On.frameMicroseconds
    [ordered]@{
        framesPerVariant = $On.Count
        off = [ordered]@{
            gpuFrameUs = $offFrame
            gpuTerrainUs = Measure-Percentiles $Off.terrainMicroseconds
            cpuRecordUs = Measure-Percentiles @($Off | ForEach-Object { $_.cpuRecordTicks * $MicrosecondsPerTick })
        }
        on = [ordered]@{
            gpuFrameUs = $onFrame
            gpuTerrainUs = Measure-Percentiles $On.terrainMicroseconds
            gpuNearShadowUs = Measure-Percentiles $On.nearShadowMicroseconds
            gpuFarShadowUs = Measure-Percentiles $On.farShadowMicroseconds
            gpuShadowTotalUs = Measure-Percentiles @($On | ForEach-Object { $_.nearShadowMicroseconds + $_.farShadowMicroseconds })
            cpuRecordUs = Measure-Percentiles @($On | ForEach-Object { $_.cpuRecordTicks * $MicrosecondsPerTick })
        }
        incrementalGpuUs = Measure-Percentiles @($incrementalGpu)
        gpuFrameDeltaPercent = [ordered]@{
            p50 = 100 * ($onFrame.p50 / $offFrame.p50 - 1)
            p95 = 100 * ($onFrame.p95 / $offFrame.p95 - 1)
            p99 = 100 * ($onFrame.p99 / $offFrame.p99 - 1)
        }
    }
}

Push-Location -LiteralPath $root
try {
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the source commit.' }
    $status = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect source status.' }
    $sdk = dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the .NET SDK.' }
    $powerPlan = powercfg /getactivescheme
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the active Windows power plan.' }

    New-Item -ItemType Directory -Path $target | Out-Null
    [ordered]@{
        schema = 1
        machineLabel = 'local-workstation'
        sourceCommit = $commit
        sourceModified = $status.Count -ne 0
        sourceStatus = $status
        windows = [Environment]::OSVersion.Version.ToString()
        architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        sdk = $sdk
        cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
        memoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        powerPlan = $powerPlan
        requestedAdapter = $Adapter
        pairs = $Pairs
        measuredFramesPerVariantPerPhase = $Frames
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $target 'metadata.json') -Encoding utf8

    dotnet restore $project --locked-mode *> (Join-Path $target 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed. See $target/restore.log." }
    dotnet build $project -c Release --no-restore --nologo -p:ContinuousIntegrationBuild=true *> (Join-Path $target 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Release build failed. See $target/build.log." }

    $pool = @{}
    foreach ($phase in $phaseNames) {
        $pool[$phase] = @{
            off = [Collections.Generic.List[object]]::new()
            on = [Collections.Generic.List[object]]::new()
        }
    }
    $runs = [Collections.Generic.List[object]]::new()
    $configuration = $null
    $env:GROUNDWORK_WP15_ADAPTER = $Adapter
    $env:GROUNDWORK_WP15_FRAMES = $Frames.ToString([Globalization.CultureInfo]::InvariantCulture)
    for ($pair = 1; $pair -le $Pairs; $pair++) {
        $env:GROUNDWORK_WP15_PAIR = $pair.ToString([Globalization.CultureInfo]::InvariantCulture)
        $raw = Join-Path $target "pair-$pair.json"
        $env:GROUNDWORK_WP15_OUTPUT = $raw
        $trxName = "pair-$pair.trx"
        $log = Join-Path $target "pair-$pair.log"
        $testArgs = @('test', $project, '-c', 'Release', '--no-build', '--no-restore', '--nologo',
            '--results-directory', $target, '--filter', 'FullyQualifiedName~Wp15PairedNativeShadowTimings',
            '--logger', "trx;LogFileName=$trxName")
        dotnet @testArgs *> $log
        if ($LASTEXITCODE -ne 0) { throw "Pair $pair failed. See $log and $target/$trxName." }
        $trx = [xml](Get-Content -LiteralPath (Join-Path $target $trxName) -Raw)
        $tests = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
        if ($tests.Count -ne 1 -or $tests[0].outcome -ne 'Passed') { throw "Pair $pair did not record exactly one passing test." }

        $row = Get-Content -LiteralPath $raw -Raw | ConvertFrom-Json
        if ($row.schema -ne 1 -or $row.pair -ne $pair -or $row.width -ne 1920 -or $row.height -ne 1080 -or
            $row.warmupFrames -lt 32 -or $row.routeSteps -ne 32 -or $row.geometryTriangles -le 0 -or
            $row.measuredFramesPerVariantPerPhase -ne $Frames -or
            $row.stopwatchFrequency -le 0 -or $row.debugLayerEnabled -or $row.dredEnabled -or
            $row.adapter.isSoftware -ne ($Adapter -eq 'Warp') -or [string]::IsNullOrWhiteSpace($row.adapter.driverVersion) -or
            ($row.phases.phase -join ',') -cne ($phaseNames -join ',')) {
            throw "Pair $pair has unexpected fixture setup, adapter, diagnostics or phases."
        }
        $key = [ordered]@{
            fixture = $row.fixture
            width = $row.width
            height = $row.height
            warmupFrames = $row.warmupFrames
            routeSteps = $row.routeSteps
            geometryTriangles = $row.geometryTriangles
            processArchitecture = $row.processArchitecture
            measuredFramesPerVariantPerPhase = $row.measuredFramesPerVariantPerPhase
            phases = @($row.phases | Select-Object phase,sceneSha256)
            shaderSha256 = $row.shaderSha256
            adapter = $row.adapter
            runtime = $row.runtime
            stopwatchFrequency = $row.stopwatchFrequency
            debugLayerEnabled = $row.debugLayerEnabled
            dredEnabled = $row.dredEnabled
        } | ConvertTo-Json -Depth 10 -Compress
        if ($null -eq $configuration) { $configuration = $key }
        elseif ($configuration -cne $key) { throw "Fixture, adapter, runtime or shader changed in pair $pair." }

        $microsecondsPerTick = 1000000.0 / $row.stopwatchFrequency
        $order = if ($pair % 2) { 'shadows-off,shadows-on' } else { 'shadows-on,shadows-off' }
        foreach ($phase in $row.phases) {
            if (($phase.routes.route -join ',') -cne $order -or ($phase.routes.order -join ',') -ne '1,2') {
                throw "Pair $pair did not alternate route order for $($phase.phase)."
            }
            $variantOff = $phase.routes | Where-Object route -CEQ 'shadows-off'
            $variantOn = $phase.routes | Where-Object route -CEQ 'shadows-on'
            $adapterKey = ConvertTo-Json -InputObject $row.adapter -Depth 4 -Compress
            if (($phase.probes.step -join ',') -ne '0,17' -or $phase.sceneSha256 -cnotmatch '^[0-9a-f]{64}$' -or
                (ConvertTo-Json -InputObject $variantOff.adapter -Depth 4 -Compress) -cne $adapterKey -or
                (ConvertTo-Json -InputObject $variantOn.adapter -Depth 4 -Compress) -cne $adapterKey -or
                $variantOff.geometryBytes -le 0 -or $variantOff.geometryBytes -ne $variantOn.geometryBytes -or
                $variantOff.shadowBackingBytes -ne 0 -or $variantOn.shadowBackingBytes -le 0) {
                throw "Pair $pair has inconsistent probes or native configuration for $($phase.phase)."
            }
            $off = @($variantOff.measurements)
            $on = @($variantOn.measurements)
            if ($off.Count -ne $Frames -or $on.Count -ne $Frames) { throw "Pair $pair has missing frames for $($phase.phase)." }
            for ($index = 0; $index -lt $Frames; $index++) {
                $expectedStep = $index % $row.routeSteps
                if ($off[$index].step -ne $expectedStep -or $on[$index].step -ne $expectedStep) {
                    throw "Pair $pair has misaligned route steps for $($phase.phase)."
                }
            }
            $badOff = $off | Where-Object {
                $_.shadowPassCount -ne 0 -or $_.nearShadowMicroseconds -ne 0 -or $_.farShadowMicroseconds -ne 0
            } | Select-Object -First 1
            $badOn = $on | Where-Object {
                $_.shadowPassCount -ne 2 -or $_.nearShadowMicroseconds -le 0 -or $_.farShadowMicroseconds -le 0
            } | Select-Object -First 1
            $badTiming = $phase.routes.measurements | Where-Object {
                $_.cpuRecordTicks -le 0 -or -not [double]::IsFinite($_.frameMicroseconds) -or
                -not [double]::IsFinite($_.terrainMicroseconds) -or -not [double]::IsFinite($_.nearShadowMicroseconds) -or
                -not [double]::IsFinite($_.farShadowMicroseconds) -or $_.frameMicroseconds -le 0 -or $_.terrainMicroseconds -le 0 -or
                $_.frameMicroseconds + 0.001 -lt ($_.terrainMicroseconds + $_.nearShadowMicroseconds + $_.farShadowMicroseconds)
            } | Select-Object -First 1
            if ($badOff -or $badOn -or $badTiming) { throw "Pair $pair has invalid timestamps or shadow workload for $($phase.phase)." }
            $runs.Add([pscustomobject]@{
                pair = $pair
                phase = $phase.phase
                routeOrder = $order
                geometryBytes = $variantOn.geometryBytes
                onLiveShadowBackingBytes = $variantOn.shadowBackingBytes
                timing = Compare-ShadowRoutes $off $on $microsecondsPerTick
            })
            $pool[$phase.phase].off.AddRange($off)
            $pool[$phase.phase].on.AddRange($on)
        }
        Write-Host "Collected all three phases for pair $pair of $Pairs on $($row.adapter.description)."
    }

    $pooled = foreach ($phase in $phaseNames) {
        [pscustomobject]@{
            phase = $phase
            timing = Compare-ShadowRoutes $pool[$phase].off.ToArray() $pool[$phase].on.ToArray() $microsecondsPerTick
        }
    }
    [ordered]@{
        schema = 1
        configuration = $configuration | ConvertFrom-Json
        pairs = $Pairs
        framesPerVariantPerPhase = $Frames
        pooled = @($pooled)
        runs = $runs.ToArray()
    } | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $target 'summary.json') -Encoding utf8
    $pooled | Format-Table phase, @{ Name = 'Frames/variant'; Expression = { $_.timing.framesPerVariant } },
        @{ Name = 'Extra GPU p95 us'; Expression = { $_.timing.incrementalGpuUs.p95 } },
        @{ Name = 'GPU frame p95 delta %'; Expression = { $_.timing.gpuFrameDeltaPercent.p95 } }
}
finally {
    foreach ($name in $environmentBefore.Keys) {
        [Environment]::SetEnvironmentVariable($name, $environmentBefore[$name])
    }
    Pop-Location
}
