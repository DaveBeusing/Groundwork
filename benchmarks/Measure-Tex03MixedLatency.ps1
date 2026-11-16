[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateRange(1, 30)][int] $Samples = 5,
    [ValidateSet('Both', 'Hardware', 'Warp')][string] $Adapter = 'Both',
    [ValidateSet('Enabled', 'Disabled')][string] $NativeDiagnostics = 'Disabled',
    [string] $MachineLabel = 'local-workstation'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw 'Use a new output directory.' }
$project = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
$fixture = 'tests/Groundwork.Rendering.D3D12.Tests/VisualEditPressureTests.cs'
$admissionFixture = 'tests/Groundwork.Rendering.D3D12.Tests/VolumeTerrainTests.cs'
$adapters = @(if ($Adapter -eq 'Both') { 'Hardware'; 'Warp' } else { $Adapter })
$sampleBefore = $env:GROUNDWORK_TEX03_MIXED_SAMPLE
$adapterBefore = $env:GROUNDWORK_TEX03_MIXED_ADAPTER
$diagnosticsBefore = $env:GROUNDWORK_TEX03_MIXED_DIAGNOSTICS
$rows = [Collections.Generic.List[object]]::new()

function Measure-Latency([object[]] $Observations, [string] $Start, [string] $End) {
    $sorted = @($Observations | ForEach-Object {
        ($_.qpc.$End - $_.qpc.$Start) * 1000000.0 / $_.qpcFrequency
    } | Sort-Object)
    [ordered]@{
        unit = 'microseconds'
        start = $Start
        end = $End
        p50 = $sorted[[int][Math]::Ceiling(0.50 * $sorted.Count) - 1]
        p95 = $sorted[[int][Math]::Ceiling(0.95 * $sorted.Count) - 1]
        p99 = $sorted[[int][Math]::Ceiling(0.99 * $sorted.Count) - 1]
    }
}

function Summarize-Cohort([object[]] $Cohort) {
    $results = [ordered]@{
        samples = $Cohort.Count
        sampleIds = @($Cohort | ForEach-Object { $_.sample })
        requestBeforeCopyCompletionObservation = @($Cohort | Where-Object {
            $_.qpc.protectedRequestAt -lt $_.qpc.copyCompletionObservedAt
        }).Count
        optionalAttemptsAtRequest = @($Cohort | ForEach-Object { $_.maximumCopy.optionalAttemptsAtRequest })
        optionalAttemptsAtMaskAdmission = @($Cohort | ForEach-Object { $_.maximumCopy.optionalAttemptsAtMaskAdmission })
    }
    if ($Cohort.Count -gt 0) {
        $results['copyStartToCompletionObserved'] = Measure-Latency $Cohort copyStartedAt copyCompletionObservedAt
        $results['copyExecuteToProtectedRequest'] = Measure-Latency $Cohort copyExecutedAt protectedRequestAt
        $results['copyExecuteToFirstRetiredPresent'] = Measure-Latency $Cohort copyExecutedAt retiredPresentTimestamp
        $results['protectedRequestToMaskPlanObserved'] = Measure-Latency $Cohort protectedRequestAt maskPlanObservedAt
        $results['protectedRequestToMaskAdmissionObserved'] = Measure-Latency $Cohort protectedRequestAt maskAdmissionObservedAt
        $results['maskAdmissionObservedToPublicationObserved'] = Measure-Latency $Cohort maskAdmissionObservedAt publicationObservedAt
        $results['protectedRequestToPublicationObserved'] = Measure-Latency $Cohort protectedRequestAt publicationObservedAt
        $results['protectedRequestToFirstPresent'] = Measure-Latency $Cohort protectedRequestAt presentTimestamp
        $results['protectedRequestToFirstRetiredPresent'] = Measure-Latency $Cohort protectedRequestAt retiredPresentTimestamp
        $results['firstRetiredPresentToMovementOpened'] = Measure-Latency $Cohort retiredPresentTimestamp movementOpenedAt
    }
    $results
}

Push-Location -LiteralPath $root
try {
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the source commit.' }
    $status = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect source status.' }
    $sdk = dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the .NET SDK.' }
    $runtimes = @(dotnet --list-runtimes | ForEach-Object { ($_ -split ' \[', 2)[0] })
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify installed .NET runtimes.' }
    New-Item -ItemType Directory -Path $target | Out-Null
    [ordered]@{
        schema = 1
        fixture = 'tex03-wp11-edited-volume-wp16-props-v1'
        machineLabel = $MachineLabel
        sourceCommit = $commit
        sourceModified = $status.Count -ne 0
        sourceStatus = $status
        fixtureSha256 = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
        admissionFixtureSha256 = (Get-FileHash -LiteralPath $admissionFixture -Algorithm SHA256).Hash
        harnessSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
        windows = [Environment]::OSVersion.Version.ToString()
        architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        sdk = $sdk
        installedRuntimes = $runtimes
        cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
        memoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        requestedAdapters = $adapters
        nativeDiagnosticsRequested = $NativeDiagnostics
        resourceSnapshots = $true
        samplesPerAdapter = $Samples
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $target 'metadata.json') -Encoding utf8

    dotnet restore $project --locked-mode *> (Join-Path $target 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed. See $target/restore.log." }
    dotnet build $project -c Release --no-restore --nologo *> (Join-Path $target 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Release build failed. See $target/build.log." }

    $env:GROUNDWORK_TEX03_MIXED_DIAGNOSTICS = $NativeDiagnostics
    foreach ($mode in $adapters) {
        $env:GROUNDWORK_TEX03_MIXED_ADAPTER = $mode
        for ($sample = 1; $sample -le $Samples; $sample++) {
            $env:GROUNDWORK_TEX03_MIXED_SAMPLE = $sample.ToString([Globalization.CultureInfo]::InvariantCulture)
            $name = "mixed-$mode-$sample"
            $log = Join-Path $target "$name.log"
            $testArgs = @('test', $project, '-c', 'Release', '--no-build', '--no-restore', '--nologo',
                '--results-directory', $target, '--filter', 'FullyQualifiedName~Tex03MixedHealthyQueueLatency',
                '--logger', "trx;LogFileName=$name.trx")
            dotnet @testArgs *> $log
            if ($LASTEXITCODE -ne 0) { throw "Sample $name failed. See $log and $target/$name.trx." }
            [xml] $trx = Get-Content -LiteralPath (Join-Path $target "$name.trx") -Raw
            $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
            if ($results.Count -ne 1 -or $results[0].outcome -ne 'Passed') {
                throw "Sample $name did not record exactly one passing test."
            }
            $output = $results[0].SelectSingleNode("*[local-name()='Output']/*[local-name()='StdOut']")
            if ($null -eq $output) { throw "Sample $name did not record test output." }
            $measurements = @($output.InnerText -split '\r?\n' | Where-Object { $_ -match '^TEX03-MIXED \{' })
            if ($measurements.Count -ne 1) { throw "Sample $name did not record exactly one measurement." }
            $json = $measurements[0].Substring('TEX03-MIXED '.Length)
            $row = $json | ConvertFrom-Json
            if ($row.schema -ne 1 -or $row.fixture -ne 'tex03-wp11-edited-volume-wp16-props-v1' -or
                $row.sample -ne $sample -or $row.qpcFrequency -le 0 -or $row.injectedHoldDuringMeasurement -or
                $row.width -le 0 -or $row.height -le 0 -or
                $row.adapter.isSoftware -ne ($mode -eq 'Warp') -or $row.diag.requested -ne ($NativeDiagnostics -eq 'Enabled') -or
                $row.maximumCopy.payloadBytes -ne 2MB -or $row.acceptedEditPresents -ne 1 -or
                $row.firstRetiredPresent.frameSequence -ne $row.firstContentSequence -or
                $row.firstRetiredPresent.presentedSequence -ne $row.firstContentSequence -or
                $row.firstRetiredPresent.retiredPresentSequence -ne $row.firstContentSequence -or
                $row.firstRetiredPresent.retiredPresentFence -ne $row.firstRetiredPresent.presentFence) {
                throw "Sample $name contains inconsistent adapter, copy, or first-Present evidence."
            }
            if ($sample -gt 1 -and ($row.adapter.description -ne $previous.adapter.description -or
                $row.adapter.driverVersion -ne $previous.adapter.driverVersion -or
                $row.adapter.vendorId -ne $previous.adapter.vendorId -or $row.adapter.deviceId -ne $previous.adapter.deviceId -or
                $row.qpcFrequency -ne $previous.qpcFrequency -or $row.width -ne $previous.width -or
                $row.height -ne $previous.height -or $row.maximumCopy.assetId -ne $previous.maximumCopy.assetId -or
                $row.diag.debugLayerEnabled -ne $previous.diag.debugLayerEnabled -or
                $row.diag.dredEnabled -ne $previous.diag.dredEnabled -or
                $row.diag.resourceSnapshots -ne $previous.diag.resourceSnapshots -or
                $row.diag.presentReadback -ne $previous.diag.presentReadback)) {
                throw "Sample $name changed its adapter, driver, diagnostics or workload configuration."
            }
            $previous = $row
            Set-Content -LiteralPath (Join-Path $target "$name.json") -Value $json -Encoding utf8
            $rows.Add($row)
            Write-Host "Collected $name on $($row.adapter.description)."
        }
    }

    ConvertTo-Json -InputObject $rows.ToArray() -Depth 16 |
        Set-Content -LiteralPath (Join-Path $target 'observations.json') -Encoding utf8
    $summary = foreach ($mode in $adapters) {
        $modeRows = @($rows | Where-Object { $_.adapter.isSoftware -eq ($mode -eq 'Warp') })
        [ordered]@{
            adapter = $mode
            samples = $modeRows.Count
            maskAdmissionBeforeCopyCompletionObservation = Summarize-Cohort @($modeRows | Where-Object {
                $_.qpc.maskAdmissionObservedAt -lt $_.qpc.copyCompletionObservedAt
            })
            maskAdmissionAtOrAfterCopyCompletionObservation = Summarize-Cohort @($modeRows | Where-Object {
                $_.qpc.maskAdmissionObservedAt -ge $_.qpc.copyCompletionObservedAt
            })
        }
    }
    [ordered]@{ schema = 1; workload = @($summary) } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $target 'summary.json') -Encoding utf8
}
finally {
    $env:GROUNDWORK_TEX03_MIXED_SAMPLE = $sampleBefore
    $env:GROUNDWORK_TEX03_MIXED_ADAPTER = $adapterBefore
    $env:GROUNDWORK_TEX03_MIXED_DIAGNOSTICS = $diagnosticsBefore
    Pop-Location
}
