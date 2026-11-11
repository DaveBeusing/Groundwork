# Runs the existing bounded terrain fixtures in separate test hosts and preserves their observations.
[CmdletBinding()]
param(
    [ValidateRange(1, 30)][int] $Samples = 5,
    [switch] $Desktop,
    [Parameter(Mandatory)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$target = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $target) { throw "Use a new output directory." }
New-Item -ItemType Directory -Path $target | Out-Null
$measureBefore = $env:GROUNDWORK_Q01_MEASURE
$observations = [Collections.Generic.List[object]]::new()
$fixtures = [ordered]@{
    'Voxels' = 'FullyQualifiedName~ProtectedPathsProgressUnderFullSpeculationWhileOldGeometryIsRetained|FullyQualifiedName~ProtectedDispatcherRunsWhileSpeculativeWorkerAndQueueRemainBlocked|FullyQualifiedName~EveryResourceDimensionRejectsMinusOneAndAdmitsExactAndPlusOne'
}
if ($Desktop) {
    $fixtures['Rendering.D3D12'] = 'FullyQualifiedName~VolumePresentRetainsGenerationUntilFenceAndRejectsStaleView'
}

Push-Location -LiteralPath $root
try {
    $env:GROUNDWORK_Q01_MEASURE = '1'
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify source commit.' }
    $dirty = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect source status.' }
    $sdk = dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify .NET SDK.' }
    $cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    $gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion)
    $memory = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    [ordered]@{
        schema = 1
        sourceCommit = $commit
        sourceModified = $dirty.Count -ne 0
        windows = [Environment]::OSVersion.Version.ToString()
        sdk = $sdk
        runtime = @(dotnet --list-runtimes)
        cpu = $cpu
        memoryBytes = $memory
        gpu = $gpu
        samples = $Samples
        desktop = [bool]$Desktop
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $target 'metadata.json')

    foreach ($fixture in $fixtures.Keys) {
        $project = "tests/Groundwork.$fixture.Tests"
        dotnet build $project -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $fixture." }
        for ($sample = 0; $sample -le $Samples; $sample++) {
            $log = Join-Path $target "$fixture-$sample.log"
            dotnet test $project -c Release --no-build --no-restore --filter $fixtures[$fixture] --logger 'console;verbosity=detailed' *> $log
            if ($LASTEXITCODE -ne 0) { throw "Fixture $fixture sample $sample failed. See $log." }
            $rows = @(Get-Content -LiteralPath $log | ForEach-Object {
                if ($_ -match '^\s*Q01 (\{.*\})\s*$') { $Matches[1] | ConvertFrom-Json }
            })
            if ($rows.Count -ne 6 -or @($rows | Group-Object route, phase).Count -ne 6) { throw "Unexpected observation count in $log." }
            if ($sample -eq 0) { continue }
            foreach ($row in $rows) {
                $row | Add-Member -NotePropertyName sample -NotePropertyValue $sample
                $observations.Add($row)
            }
        }
    }

    $observations.ToArray() | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $target 'observations.json')
    $summary = foreach ($group in $observations | Group-Object route, phase) {
        $values = @($group.Group.elapsedMs | Sort-Object)
        [pscustomobject]@{
            route = $group.Group[0].route
            phase = $group.Group[0].phase
            count = $values.Count
            p50Ms = $values[[int][Math]::Ceiling(0.50 * $values.Count) - 1]
            p95Ms = $values[[int][Math]::Ceiling(0.95 * $values.Count) - 1]
            p99Ms = $values[[int][Math]::Ceiling(0.99 * $values.Count) - 1]
            maxMs = $values[-1]
        }
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $target 'summary.json')
    $summary | Format-Table -AutoSize
}
finally {
    $env:GROUNDWORK_Q01_MEASURE = $measureBefore
    Pop-Location
}
