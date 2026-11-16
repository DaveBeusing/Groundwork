[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateRange(1, 30)][int] $Pairs = 5,
    [ValidateSet('Hardware', 'Warp')][string] $Adapter = 'Hardware',
    [string] $MachineLabel = 'local-workstation'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw 'Use a new output directory.' }
$project = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
$pairBefore = $env:GROUNDWORK_TEX03_PAIR
$adapterBefore = $env:GROUNDWORK_TEX03_ADAPTER
$observations = [Collections.Generic.List[object]]::new()

function Measure-Percentiles([double[]] $Values) {
    $sorted = @($Values | Sort-Object)
    [ordered]@{
        p50 = $sorted[[int][Math]::Ceiling(0.50 * $sorted.Count) - 1]
        p95 = $sorted[[int][Math]::Ceiling(0.95 * $sorted.Count) - 1]
        p99 = $sorted[[int][Math]::Ceiling(0.99 * $sorted.Count) - 1]
    }
}

function Get-PairConfiguration($Observation) {
    [ordered]@{
        fixture = $Observation.fixture
        adapter = $Observation.adapter
        runtime = $Observation.runtime
        processArchitecture = $Observation.processArchitecture
        width = $Observation.width
        height = $Observation.height
        warmupFrames = $Observation.warmupFrames
        measuredFrames = $Observation.measuredFrames
        stopwatchFrequency = $Observation.stopwatchFrequency
        debugLayerEnabled = $Observation.debugLayerEnabled
        dredEnabled = $Observation.dredEnabled
        shaderSha256 = $Observation.shaderSha256
        texture = $Observation.texture
        policy = $Observation.policy
    }
}

function Assert-MatchingPairConfiguration($Baseline, $Current, [int] $Pair) {
    foreach ($field in $Baseline.Keys) {
        $expected = ConvertTo-Json -InputObject $Baseline[$field] -Depth 6 -Compress
        $actual = ConvertTo-Json -InputObject $Current[$field] -Depth 6 -Compress
        if ($expected -cne $actual) { throw "Configuration field '$field' changed in pair $Pair." }
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
    $runtimes = @(dotnet --list-runtimes | ForEach-Object { ($_ -split ' \[', 2)[0] })
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify installed .NET runtimes.' }
    New-Item -ItemType Directory -Path $target | Out-Null
    [ordered]@{
        schema = 1
        machineLabel = $MachineLabel
        sourceCommit = $commit
        sourceModified = $dirty.Count -ne 0
        windows = [Environment]::OSVersion.Version.ToString()
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        sdk = $sdk
        installedRuntimes = $runtimes
        cpu = $cpu
        memoryBytes = $memory
        requestedAdapter = $Adapter
        pairs = $Pairs
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $target 'metadata.json') -Encoding utf8

    dotnet restore $project --locked-mode *> (Join-Path $target 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed. See $target/restore.log." }
    dotnet build $project -c Release --no-restore --nologo *> (Join-Path $target 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Release build failed. See $target/build.log." }

    $configuration = $null
    $env:GROUNDWORK_TEX03_ADAPTER = $Adapter
    for ($pair = 1; $pair -le $Pairs; $pair++) {
        $env:GROUNDWORK_TEX03_PAIR = $pair.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        $name = "pair-$pair.trx"
        $log = Join-Path $target "pair-$pair.log"
        $testArgs = @('test', $project, '-c', 'Release', '--no-build', '--no-restore', '--nologo',
            '--results-directory', $target, '--filter', 'FullyQualifiedName~Tex03PairedNativeMipTimings', '--logger', "trx;LogFileName=$name")
        dotnet @testArgs *> $log
        if ($LASTEXITCODE -ne 0) { throw "Pair $pair failed. See $log and $target/$name." }
        [xml] $trx = Get-Content -LiteralPath (Join-Path $target $name) -Raw
        $test = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
        if ($test.Count -ne 1 -or $test[0].outcome -ne 'Passed') { throw "Pair $pair did not record a successful test." }
        $output = $test[0].SelectSingleNode("*[local-name()='Output']/*[local-name()='StdOut']").InnerText
        $rows = @($output -split '\r?\n' | Where-Object { $_ -match '^TEX03 \{' })
        if ($rows.Count -ne 1) { throw "Pair $pair did not record exactly one measurement." }
        $row = $rows[0].Substring('TEX03 '.Length) | ConvertFrom-Json
        $expectedRoutes = if ($pair % 2) { 'fixed-mip0', 'bounded-mip-tiers' } else { 'bounded-mip-tiers', 'fixed-mip0' }
        if ($row.schema -ne 2 -or $row.pair -ne $pair -or $row.routes.Count -ne 2 -or
            $row.routes[0].route -cne $expectedRoutes[0] -or $row.routes[1].route -cne $expectedRoutes[1] -or
            $row.routes[0].order -ne 1 -or $row.routes[1].order -ne 2 -or
            $row.measuredFrames -le 0 -or $row.stopwatchFrequency -le 0 -or
            $row.adapter.isSoftware -ne ($Adapter -eq 'Warp') -or
            $row.routes[0].samples.Count -ne $row.measuredFrames -or $row.routes[1].samples.Count -ne $row.measuredFrames) {
            throw "Pair $pair did not contain the expected measurements."
        }
        if ([string]::IsNullOrWhiteSpace($row.adapter.driverVersion)) {
            throw "Pair $pair did not report an adapter driver version."
        }
        $currentConfiguration = Get-PairConfiguration $row
        if ($null -eq $configuration) { $configuration = $currentConfiguration }
        else { Assert-MatchingPairConfiguration $configuration $currentConfiguration $pair }
        $observations.Add($row)
        Write-Host "Collected pair $pair of $Pairs on $($row.adapter.description)."
    }

    ConvertTo-Json -InputObject $observations.ToArray() -Depth 16 |
        Set-Content -LiteralPath (Join-Path $target 'observations.json') -Encoding utf8
    $frequency = $configuration.stopwatchFrequency
    $routes = foreach ($routeName in 'fixed-mip0', 'bounded-mip-tiers') {
        $routesForMode = @($observations | ForEach-Object { $_.routes | Where-Object route -eq $routeName })
        $frames = @($routesForMode | ForEach-Object { $_.samples })
        $copyFrames = @($frames | Where-Object copySubmitted)
        [pscustomobject]@{
            route = $routeName
            frameCount = $frames.Count
            copyCount = $copyFrames.Count
            cpuRecordUs = Measure-Percentiles @($frames | ForEach-Object { $_.cpuRecordTicks * 1000000 / $frequency })
            gpuTerrainUs = Measure-Percentiles @($frames.gpuTerrainMicroseconds)
            turnUs = Measure-Percentiles @($frames | ForEach-Object { $_.turnTicks * 1000000 / $frequency })
            copyWaitUs = Measure-Percentiles @($copyFrames | ForEach-Object { $_.copyWaitTicks * 1000000 / $frequency })
            residentBytes = Measure-Percentiles @($frames.residentBytes)
            descriptorHighWater = ($routesForMode.descriptorHighWater | Measure-Object -Maximum).Maximum
            residentHighWaterBytes = ($routesForMode.residentHighWaterBytes | Measure-Object -Maximum).Maximum
            residentAndCandidateHighWaterBytes = ($routesForMode.residentAndCandidateHighWaterBytes | Measure-Object -Maximum).Maximum
            stagingHighWaterBytes = ($routesForMode.stagingHighWaterBytes | Measure-Object -Maximum).Maximum
            retiringHighWaterBytes = ($routesForMode.retiringHighWaterBytes | Measure-Object -Maximum).Maximum
            combinedBackingHighWaterBytes = ($routesForMode.combinedBackingHighWaterBytes | Measure-Object -Maximum).Maximum
            meanUploadedBytesPerPair = ($routesForMode.uploadedBytes | Measure-Object -Average).Average
        }
    }
    $deltas = foreach ($entry in $observations) {
        $baseline = $entry.routes | Where-Object route -eq 'fixed-mip0'
        $tiered = $entry.routes | Where-Object route -eq 'bounded-mip-tiers'
        [pscustomobject]@{
            pair = $entry.pair
            cpuRecordP50Us = $tiered.cpuRecordUs.p50 - $baseline.cpuRecordUs.p50
            gpuTerrainP50Us = $tiered.gpuTerrainUs.p50 - $baseline.gpuTerrainUs.p50
            uploadedBytes = [long]$tiered.uploadedBytes - [long]$baseline.uploadedBytes
            finalResidentBytes = $tiered.samples[-1].residentBytes - $baseline.samples[-1].residentBytes
        }
    }
    [ordered]@{ schema = 2; configuration = $configuration; routes = @($routes); pairDeltasTieredMinusFixed = @($deltas) } |
        ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $target 'summary.json') -Encoding utf8
    $routes | Format-Table route, frameCount, copyCount, residentHighWaterBytes, combinedBackingHighWaterBytes, meanUploadedBytesPerPair
}
finally {
    $env:GROUNDWORK_TEX03_PAIR = $pairBefore
    $env:GROUNDWORK_TEX03_ADAPTER = $adapterBefore
    Pop-Location
}
