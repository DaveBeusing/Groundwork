param(
    [Parameter(Mandatory)][string]$Tables,
    [Parameter(Mandatory)][string]$Output,
    [ValidateRange(10, 1000)][int]$Repetitions = 100,
    [switch]$RequireReferenceHardware,
    [switch]$GeometryOnly,
    [switch]$CollisionComparison
)
$ErrorActionPreference = 'Stop'
$expected = '83A5511346B54C42E4E66DEC916D3971C92F4FBDA1C7878CBAD5901A820DCAB4'
if ((Get-FileHash -LiteralPath $Tables).Hash -cne $expected) { throw 'Unexpected Transvoxel table hash.' }
$Tables = (Resolve-Path -LiteralPath $Tables).Path
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,OSArchitecture
$cpu = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
$installedMemory = (Get-CimInstance Win32_PhysicalMemory | Measure-Object Capacity -Sum).Sum
$physicalMemory = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
$cores = ($cpu | Measure-Object NumberOfCores -Sum).Sum
if ($RequireReferenceHardware -and ($cores -ne 8 -or $installedMemory -ne 32GB)) {
    throw "Reference run requires 8 physical cores and 32 GiB installed RAM. Detected $cores cores and $installedMemory installed bytes."
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
function Measure-Extraction {
    for ($i = 0; $i -lt $cases.Count; $i++) {
        $case = $cases[$i]
        $order = if ($i % 2 -eq 0) { 'mc','dc' } else { 'dc','mc' }
        foreach ($algorithm in $order) {
            $row = dotnet $dll $Tables $algorithm $case.fixture $case.layout $Repetitions
            if ($LASTEXITCODE) { throw "Probe failed for $algorithm $($case.fixture)/$($case.layout)." }
            $result = $row | ConvertFrom-Json
            $audit = $result.audit
            $expectedComponents = if ($case.fixture -eq 'cavity') { 2 } else { 1 }
            if ($audit.openEdges -or $audit.nonManifoldEdges -or $audit.windingEdges -or
                $audit.degenerates -or $audit.missingDualFaces -or $audit.components -ne $expectedComponents) {
                throw "Unexpected topology for $algorithm $($case.fixture)/$($case.layout): $row"
            }
            if ($algorithm -eq 'mc' -and $case.layout -ne 'uniform' -and -not $audit.TransitionTriangles) {
                throw "No Transvoxel transition triangles for $($case.fixture)/$($case.layout)."
            }
            $result
            Write-Host $row
        }
    }
}
Push-Location $root
$previousTiered = $env:DOTNET_TieredCompilation
try {
    dotnet build (Join-Path $PSScriptRoot 'Groundwork.Wp11Meshing.Probe.csproj') -c Release --nologo
    if ($LASTEXITCODE) { throw 'Probe build failed.' }
    $dll = Join-Path $root 'artifacts/bin/Groundwork.Wp11Meshing.Probe/release/Groundwork.Wp11Meshing.Probe.dll'
    $env:DOTNET_TieredCompilation = '0'
    $rows = @()
    $cases = @(
        @{ fixture = 'cavity'; layout = 'uniform' },
        @{ fixture = 'cavity'; layout = 'face' },
        @{ fixture = 'overhang'; layout = 'uniform' },
        @{ fixture = 'overhang'; layout = 'face' },
        @{ fixture = 'bridge'; layout = 'face' },
        @{ fixture = 'seam'; layout = 'face' },
        @{ fixture = 'seam'; layout = 'corner' },
        @{ fixture = 'junction'; layout = 'corner' },
        @{ fixture = 'seam'; layout = 'pressure' }
    )
    if ($GeometryOnly -or $CollisionComparison) {
        $geometryCases = @(
            @{ fixture = 'bridge'; layout = 'uniform' },
            @{ fixture = 'bridge'; layout = 'face' },
            @{ fixture = 'seam'; layout = 'uniform' },
            @{ fixture = 'seam'; layout = 'face' },
            @{ fixture = 'seam'; layout = 'corner' },
            @{ fixture = 'junction'; layout = 'corner' }
        )
        foreach ($case in $geometryCases) {
            $modes = if ($CollisionComparison) { 'geometry','collision-refined' } else { 'geometry' }
            foreach ($mode in $modes) {
                $row = dotnet $dll $Tables mc $case.fixture $case.layout $mode
                if ($LASTEXITCODE) { throw "Geometry audit failed for $($case.fixture)/$($case.layout)/$mode." }
                $rows += $row | ConvertFrom-Json
                Write-Host "Geometry audit completed for $($case.fixture)/$($case.layout)/$mode."
            }
        }
    } else {
        $rows = @(Measure-Extraction)
    }
    if ($CollisionComparison) {
        foreach ($refined in ($rows | Where-Object refinementEnabled)) {
            $baseline = $rows | Where-Object {
                -not $_.refinementEnabled -and $_.fixture -eq $refined.fixture -and $_.layout -eq $refined.layout
            }
            if ($refined.fixture -ne 'bridge') {
                if ($refined.CellSize -ne 1 -or $refined.checksum -ne $baseline.checksum) {
                    throw "Resolution changed for the $($refined.fixture)/$($refined.layout) control."
                }
                continue
            }
            $audit = $refined.topology
            $core = $refined.geometry.collision.'bridge-core'
            $sweeps = $refined.geometry.movingBodies.summary
            if ($refined.CellSize -ne 0.5 -or -not $refined.ThinFeatureWitness -or $audit.components -ne 1 -or
                $audit.openEdges -or $audit.windingEdges -or $audit.nonManifoldEdges -or $audit.degenerates -or
                $refined.geometry.intersections.Count -or
                $core.Rays -ne 49 -or $core.CoreOccupancySamples -ne 1568 -or
                $core.CrossingCountMismatches -or $core.MissingCoreOccupancySamples -or
                ($sweeps | Measure-Object sweeps -Sum).Sum -ne 76 -or
                ($sweeps | Where-Object { $_.statusDifferences -or $_.indeterminate -or $_.overTolerance })) {
                throw "Thin bridge collision regression for $($refined.layout)."
            }
        }
    }
    [ordered]@{
        timestampUtc = (Get-Date -AsUTC -Format o)
        os = $os
        cpu = $cpu
        physicalMemoryBytes = $physicalMemory
        installedMemoryBytes = $installedMemory
        referenceHardwareRequired = [bool]$RequireReferenceHardware
        geometryOnly = [bool]($GeometryOnly -or $CollisionComparison)
        collisionComparison = [bool]$CollisionComparison
        sdk = (dotnet --version)
        runtime = (dotnet --list-runtimes | Where-Object { $_ -like 'Microsoft.NETCore.App 10.*' } | Select-Object -Last 1)
        sourceBase = '1d6f439c17abf2ebdb40fe7970a3e995cf5c286f'
        tablesCommit = '51a494f03c5b024cd153b596bcc7152eb3cc93a6'
        tablesSha256 = $expected
        tieredCompilation = $false
        results = $rows
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output
} finally {
    $env:DOTNET_TieredCompilation = $previousTiered
    Pop-Location
}
