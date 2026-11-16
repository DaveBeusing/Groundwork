#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateRange(1, 100)][int] $Samples = 10,
    [ValidateSet('Hardware', 'Warp', 'Both')][string] $Adapter = 'Hardware',
    [string] $ExpectedCommit,
    [string] $BaselineDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64' -or
    ![Environment]::UserInteractive -or (Get-Process -Id $PID).SessionId -eq 0) {
    throw 'Run in an interactive Windows x64 desktop session.'
}

$root = Split-Path -Parent $PSScriptRoot
$target = [IO.Path]::GetFullPath($OutputDirectory)
$project = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
$adapters = @(if ($Adapter -eq 'Both') { 'Hardware'; 'Warp' } else { $Adapter })
$rows = [Collections.Generic.List[object]]::new()
$baseline = if ($BaselineDirectory) {
    Get-Content -LiteralPath (Join-Path $BaselineDirectory 'summary.json') -Raw | ConvertFrom-Json
}
$previousAdapter = $env:GROUNDWORK_R09_ADAPTER
$previousMode = $env:GROUNDWORK_R09_DIAGNOSTICS

function Write-Json($Value, [string] $File) {
    ConvertTo-Json -InputObject $Value -Depth 16 | Set-Content -LiteralPath (Join-Path $target $File) -Encoding utf8
}

function Measure-Distribution([object[]] $Records, [string] $Start, [string] $End) {
    foreach ($record in $Records) {
        if ($record.$Start -le 0 -or $record.$End -lt $record.$Start -or $record.frequency -le 0) {
            throw "Missing or unordered timestamp: $Start to $End."
        }
    }
    $ms = @($Records | ForEach-Object { ($_.$End - $_.$Start) * 1000.0 / $_.frequency } | Sort-Object)
    [ordered]@{
        count = $ms.Count
        p50 = $ms[[int][Math]::Ceiling(0.50 * $ms.Count) - 1]
        p95 = $ms[[int][Math]::Ceiling(0.95 * $ms.Count) - 1]
        p99 = $ms[[int][Math]::Ceiling(0.99 * $ms.Count) - 1]
        max = $ms[-1]
    }
}

Push-Location -LiteralPath $root
try {
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Run from a Git checkout of Groundwork.' }
    if ($ExpectedCommit -and $commit -ne $ExpectedCommit) { throw "Expected $ExpectedCommit, found $commit." }
    $dirty = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count) { throw 'Use a clean checkout for reference measurements.' }
    if (Test-Path -LiteralPath $target) { throw 'Use a new output directory.' }
    if ($target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Choose an output directory outside the checkout.'
    }
    New-Item -ItemType Directory -Path $target | Out-Null
    $sdk = dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Install the .NET SDK selected by global.json.' }
    $cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    $gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion)
    $memory = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    $metadata = [ordered]@{
        schema = 2
        route = 'wp11-r09-volume-pressure-v1'
        sourceCommit = $commit
        windows = [Environment]::OSVersion.Version.ToString()
        sdk = $sdk
        runtimes = @(dotnet --list-runtimes | ForEach-Object { ($_ -split ' \[', 2)[0] })
        cpu = $cpu
        gpu = $gpu
        memoryBytes = $memory
        samplesPerMode = $Samples
        adapters = $adapters
        buildConfiguration = 'Release'
        harnessSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
    }
    Write-Json $metadata 'metadata.json'
    if ($baseline) {
        if ($baseline.schema -ne $metadata.schema -or $baseline.route -ne $metadata.route) {
            throw 'Baseline summary must use the same R09 route and schema.'
        }
        $baselineMetadata = Get-Content -LiteralPath (Join-Path $BaselineDirectory 'metadata.json') -Raw | ConvertFrom-Json
        $comparable = $baselineMetadata.sourceCommit -eq $baseline.sourceCommit
        foreach ($key in 'windows','sdk','runtimes','cpu','gpu','memoryBytes','samplesPerMode','adapters','buildConfiguration') {
            $expected = ConvertTo-Json -InputObject $metadata[$key] -Compress -Depth 8
            $actual = ConvertTo-Json -InputObject $baselineMetadata.$key -Compress -Depth 8
            $comparable = $comparable -and ($expected -ceq $actual)
        }
        if (!$comparable) { throw 'Baseline metadata must match the reference machine and run configuration.' }
    }
    dotnet restore $project --locked-mode -m:1 *> (Join-Path $target 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed. See $target/restore.log." }
    dotnet build $project -c Release --no-restore -m:1 --nologo *> (Join-Path $target 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Release build failed. See $target/build.log." }

    foreach ($selected in $adapters) {
        $env:GROUNDWORK_R09_ADAPTER = $selected
        for ($sample = 0; $sample -le $Samples; $sample++) {
            $modes = if ($sample % 2 -eq 0) { 'Off', 'On' } else { 'On', 'Off' }
            foreach ($mode in $modes) {
                $env:GROUNDWORK_R09_DIAGNOSTICS = $mode
                $name = "$selected-$mode-$sample"
                $log = Join-Path $target "$name.log"
                Write-Host "R09 $name"
                dotnet test $project -c Release --no-build --no-restore --filter 'FullyQualifiedName~R09ReferencePressureRoute' `
                    --blame-hang-timeout 3m --blame-hang-dump-type none --results-directory $target --logger "trx;LogFileName=$name.trx" --logger 'console;verbosity=detailed' *> $log
                if ($LASTEXITCODE -ne 0) { throw "Route failed. See $log." }
                [xml] $trx = Get-Content -LiteralPath (Join-Path $target "$name.trx") -Raw
                $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
                if ($results.Count -ne 1 -or $results[0].outcome -ne 'Passed') { throw "Route did not pass. See $log." }
                $observations = @(Get-Content -LiteralPath $log | ForEach-Object {
                    if ($_ -match '^\s*R09 (\{.*\})\s*$') { $Matches[1] | ConvertFrom-Json }
                })
                if ($observations.Count -ne 1) { throw "Expected one R09 observation. See $log." }
                $row = $observations[0]
                if ($row.adapter -ne $selected -or $row.mode -ne $mode -or $row.frames.Count -ne 28 -or
                    ($row.stages.phase -join ',') -ne 'coverage-transition,first-edit,recovery' -or
                    $row.settledNativeBytes -ne 0 -or $row.settledReaders -ne 0 -or $row.settledGenerations -ne 0) {
                    throw "Incomplete or mismatched R09 observation. See $log."
                }
                if ($sample -eq 0) { continue }
                $row | Add-Member -NotePropertyName sample -NotePropertyValue $sample
                $rows.Add($row)
                Write-Json ($rows.ToArray()) 'observations.json'
            }
        }
    }

    $cohorts = [ordered]@{}
    foreach ($cohort in $rows | Group-Object adapter, mode) {
        $frames = @($cohort.Group | ForEach-Object {
            $frequency = $_.qpcFrequency
            $_.frames | ForEach-Object { $_ | Add-Member -NotePropertyName frequency -NotePropertyValue $frequency -PassThru }
        })
        $stages = @($cohort.Group | ForEach-Object {
            $frequency = $_.qpcFrequency
            $_.stages | ForEach-Object { $_ | Add-Member -NotePropertyName frequency -NotePropertyValue $frequency -PassThru }
        })
        $phases = [ordered]@{}
        foreach ($phase in $stages | Group-Object phase) {
            $group = $phase.Group
            $phaseResult = [ordered]@{
                workerQueueWaitMs = Measure-Distribution $group workerQueuedAt workerStart
                workerExecutionMs = Measure-Distribution $group workerStart workerEnd
                workerCompletionObservationMs = Measure-Distribution $group workerEnd workerCompletionObservedAt
                protectedProbeWhileUploadHeldMs = Measure-Distribution $group overlapWorkerStart overlapWorkerEnd
                uploadReleaseToReadyObservedMs = Measure-Distribution $group releasedAt nativeReadyObservedAt
                protectedJobs = ($group.protectedJobs | Measure-Object -Sum).Sum
                completedProtectedJobs = ($group.completedProtectedJobs | Measure-Object -Sum).Sum
                publishingJobs = ($group.publishingJobs | Measure-Object -Sum).Sum
                deniedSpeculativeAttempts = ($group.denied | Measure-Object -Sum).Sum
                cumulativePeakChargedTerrainCpuBytes = ($group | ForEach-Object { $_.Peak.CpuBytes } | Measure-Object -Maximum).Maximum
                maxNativeTerrainBytesAtReady = ($group.readyNative | Measure-Object -Maximum).Maximum
            }
            if ($cohort.Group[0].mode -eq 'On') {
                $phaseResult['authorizedTransitionToBeginTickMs'] = Measure-Distribution $group transitionAuthorizedAt consumingBeginTick
                $phaseResult['viewChangeToBeginTickMs'] = Measure-Distribution $group viewChangedAt consumingBeginTick
                $phaseResult['workReadyToAuthorizedReceiptMs'] = Measure-Distribution $group workReady authorizedReceipt
                $phaseResult['authorizedReceiptToBeginTickMs'] = Measure-Distribution $group authorizedReceipt consumingBeginTick
            }
            $phases[$phase.Name] = $phaseResult
        }
        $cohorts[$cohort.Name] = [ordered]@{
            samples = $cohort.Count
            frameSubmitToPresentObservedMs = Measure-Distribution $frames start observed
            peakProcessWorkingSetBytes = ($cohort.Group.PeakWorkingSet64 | Measure-Object -Maximum).Maximum
            pressurePhases = $phases
        }
        if ($cohort.Group[0].mode -eq 'On') {
            $cohorts[$cohort.Name]['frameSubmitToPresentRetiredObservedMs'] = Measure-Distribution $frames start RetiredPresentTimestamp
        }
    }
    $comparison = [ordered]@{}
    foreach ($selected in $adapters) {
        $off = $cohorts["$selected, Off"].frameSubmitToPresentObservedMs
        $on = $cohorts["$selected, On"].frameSubmitToPresentObservedMs
        $comparison[$selected] = [ordered]@{ p50DeltaMs = $on.p50 - $off.p50; p99DeltaMs = $on.p99 - $off.p99 }
    }
    $baselineDelta = [ordered]@{}
    if ($baseline) {
        foreach ($name in $cohorts.Keys) {
            $prior = $baseline.cohorts.PSObject.Properties[$name]
            if (!$prior) { throw "Baseline is missing $name." }
            $baselineDelta[$name] = $cohorts[$name].frameSubmitToPresentObservedMs.p99 -
                $prior.Value.frameSubmitToPresentObservedMs.p99
        }
    }
    Write-Json ([ordered]@{
        schema = $metadata.schema
        route = $metadata.route
        sourceCommit = $commit
        cohorts = $cohorts
        diagnosticsOnMinusOff = $comparison
        baselineSourceCommit = if ($baseline) { $baseline.sourceCommit }
        baselineFrameP99DeltaMs = $baselineDelta
    }) 'summary.json'
    Write-Host "R09 reference evidence: $target"
}
finally {
    $env:GROUNDWORK_R09_ADAPTER = $previousAdapter
    $env:GROUNDWORK_R09_DIAGNOSTICS = $previousMode
    Pop-Location
}
