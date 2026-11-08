param(
    [Parameter(Mandatory)][string]$Tables,
    [Parameter(Mandatory)][string]$Output,
    [ValidateRange(10, 1000)][int]$Repetitions = 100
)
$ErrorActionPreference = 'Stop'
$expected = '83A5511346B54C42E4E66DEC916D3971C92F4FBDA1C7878CBAD5901A820DCAB4'
if ((Get-FileHash -LiteralPath $Tables).Hash -cne $expected) { throw 'Unexpected Transvoxel table hash.' }
$Tables = (Resolve-Path -LiteralPath $Tables).Path
$Output = [IO.Path]::GetFullPath($Output)
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
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
    for ($i = 0; $i -lt $cases.Count; $i++) {
        $case = $cases[$i]
        $order = if ($i % 2 -eq 0) { 'mc','dc' } else { 'dc','mc' }
        foreach ($algorithm in $order) {
            $row = dotnet $dll $Tables $algorithm $case.fixture $case.layout $Repetitions
            if ($LASTEXITCODE) { throw "Probe failed for $algorithm $($case.fixture)/$($case.layout)." }
            $rows += $row | ConvertFrom-Json
            Write-Host $row
        }
    }
    $os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,OSArchitecture
    $cpu = Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors
    [ordered]@{
        timestampUtc = (Get-Date -AsUTC -Format o)
        os = $os
        cpu = $cpu
        physicalMemoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        sdk = (dotnet --version)
        runtime = (dotnet --list-runtimes | Where-Object { $_ -like 'Microsoft.NETCore.App 10.*' } | Select-Object -Last 1)
        sourceBase = '1d6f439c17abf2ebdb40fe7970a3e995cf5c286f'
        tablesCommit = '51a494f03c5b024cd153b596bcc7152eb3cc93a6'
        tablesSha256 = $expected
        tieredCompilation = $false
        results = $rows
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output
} finally {
    $env:DOTNET_TieredCompilation = $previousTiered
    Pop-Location
}
