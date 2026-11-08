param(
    [Parameter(Mandatory)][string]$Tables,
    [Parameter(Mandatory)][string]$Output,
    [ValidateRange(10, 1000)][int]$Repetitions = 100,
    [switch]$RequireReferenceHardware,
    [switch]$GeometryOnly
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
    if ($GeometryOnly) {
        $geometryCases = @(
            @{ fixture = 'bridge'; layout = 'uniform' },
            @{ fixture = 'bridge'; layout = 'face' },
            @{ fixture = 'seam'; layout = 'uniform' },
            @{ fixture = 'seam'; layout = 'face' },
            @{ fixture = 'seam'; layout = 'corner' },
            @{ fixture = 'junction'; layout = 'corner' }
        )
        foreach ($case in $geometryCases) {
            $row = dotnet $dll $Tables mc $case.fixture $case.layout geometry
            if ($LASTEXITCODE) { throw "Geometry audit failed for $($case.fixture)/$($case.layout)." }
            $rows += $row | ConvertFrom-Json
            Write-Host "Geometry audit completed for $($case.fixture)/$($case.layout)."
        }
    } else {
        $rows = @(Measure-Extraction)
    }
    [ordered]@{
        timestampUtc = (Get-Date -AsUTC -Format o)
        os = $os
        cpu = $cpu
        physicalMemoryBytes = $physicalMemory
        installedMemoryBytes = $installedMemory
        referenceHardwareRequired = [bool]$RequireReferenceHardware
        geometryOnly = [bool]$GeometryOnly
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
