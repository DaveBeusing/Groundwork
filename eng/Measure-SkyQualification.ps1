param(
    [string]$OutputDirectory,
    [switch]$LocalProbe,
    [switch]$AllowDirty,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GROUNDWORK_TWILIGHT_PREVIEW -cne '1') {
    throw 'Set GROUNDWORK_TWILIGHT_PREVIEW=1 to measure the Direct Twilight Preview.'
}
if ($env:GROUNDWORK_SKY_LOOKUP_PROTOTYPE -eq '1') {
    throw 'Sky lookup prototype is diagnostic only and cannot enter sky qualification. Use the explicit lookup oracle fixture.'
}
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'Sky qualification requires Windows x64, PowerShell 7, .NET SDK 10, and a D3D12 NVIDIA adapter.'
}
if ($AllowDirty -and -not $LocalProbe) {
    throw '-AllowDirty is only available with -LocalProbe; strict runs require clean reviewed source.'
}

$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
Push-Location -LiteralPath $repository
$savedEnvironment = @{}
$runExitCode = 1
$transcribing = $false
$createdOutput = $false
$run = $null
try {
    $gitRoot = (& git rev-parse --show-toplevel).Trim()
    if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($gitRoot) -ne $repository) {
        throw 'Run from a Git checkout whose root contains this eng directory.'
    }
    $sourceCommit = (& git rev-parse HEAD).Trim()
    $status = @(& git status --porcelain)
    if ($status.Count -ne 0 -and -not $AllowDirty) {
        throw 'The checkout is dirty. Use a clean reviewed commit for reference measurement; -AllowDirty is for development probes only.'
    }
    if ($SkipBuild -and -not $LocalProbe) {
        throw '-SkipBuild is only available for development probes; strict runs must build the recorded source.'
    }
    if (-not $OutputDirectory) {
        $OutputDirectory = Join-Path $repository ('artifacts/sky-qualification/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N'))
    }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) {
        throw 'OutputDirectory already exists. Use a new directory so failed and earlier runs are preserved.'
    }
    New-Item -ItemType Directory -Path $output | Out-Null
    $createdOutput = $true
    Start-Transcript -LiteralPath (Join-Path $output 'console.log') | Out-Null
    $transcribing = $true
    $run = [ordered]@{
        StartedUtc = [DateTime]::UtcNow.ToString('O')
        SourceCommit = $sourceCommit
        Branch = (& git branch --show-current).Trim()
        Remote = @(& git remote -v)
        WorkingTreeStatus = $status
        Mode = $(if ($LocalProbe) { 'local-probe-not-qualification' } else { 'strict-sky-pass-only' })
        TwilightPreview = $true
        Width = 1920
        Height = 1080
        PairsPerPhase = $(if ($LocalProbe) { 1 } else { 5 })
        SamplesPerVariantPerPair = $(if ($LocalProbe) { 128 } else { 2000 })
        WarmupPerVariantPerPair = 64
        Cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors)
        OperatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        DotnetInfo = @(& dotnet --info)
        Scope = 'Synthetic sky-pass snapshots, not installed flight, total GPU relative, Present/handoff, pressure, full application CPU or complete GPU memory qualification.'
        RunExitCode = $null
    }
    $run | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'run.json') -Encoding utf8
    foreach ($name in @('GROUNDWORK_SKY_MEASURE', 'GROUNDWORK_SKY_STRICT', 'GROUNDWORK_SKY_SOURCE_HEAD', 'GROUNDWORK_SKY_OUTPUT')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    $env:GROUNDWORK_SKY_MEASURE = '1'
    $env:GROUNDWORK_SKY_STRICT = $(if ($LocalProbe) { '0' } else { '1' })
    $env:GROUNDWORK_SKY_SOURCE_HEAD = $sourceCommit + $(if ($status.Count -ne 0) { ' + DIRTY (see run.json)' } else { '' })
    $env:GROUNDWORK_SKY_OUTPUT = $output
    $project = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
    if (-not $SkipBuild) {
        & dotnet restore $project --locked-mode --disable-parallel
        if ($LASTEXITCODE -ne 0) { throw "Locked restore failed: $LASTEXITCODE" }
        & dotnet build $project -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Release build failed: $LASTEXITCODE" }
    }
    Write-Host 'Keep the machine idle: stop games, captures, GPU tools and concurrent benchmarks. This script cannot exclude unrelated GPU processes.'
    & dotnet test $project -c Release --no-build --no-restore --filter 'FullyQualifiedName~NvidiaSkyPassQualificationCollectsRetired1080pTimestamps' --logger 'trx;LogFileName=qualification.trx' --results-directory $output
    $runExitCode = $LASTEXITCODE
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    if ($null -ne $run) {
        $run['RunExitCode'] = $runExitCode
        $run['FinishedUtc'] = [DateTime]::UtcNow.ToString('O')
        $run | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'run.json') -Encoding utf8
    }
    if ($transcribing) { Stop-Transcript | Out-Null }
    if ($createdOutput) {
        $hashes = foreach ($file in @('run.json', 'console.log', 'samples.csv', 'summary.json', 'qualification.trx')) {
            $path = Join-Path $output $file
            if (Test-Path -LiteralPath $path) { Get-FileHash -LiteralPath $path -Algorithm SHA256 | Select-Object @{Name='File';Expression={$file}}, Hash }
        }
        $hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'hashes.json') -Encoding utf8
        Write-Host "Preserved run evidence: $output (exit $runExitCode)"
    }
    Pop-Location
}
exit $runExitCode
