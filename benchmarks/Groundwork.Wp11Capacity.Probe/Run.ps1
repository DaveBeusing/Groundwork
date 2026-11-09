param(
    [Parameter(Mandatory)][string]$Output,
    [ValidateRange(1, 10)][int]$Runs = 3,
    [ValidatePattern('^[0-9A-Fa-f]{4}$')][string]$Vendor = '10DE'
)
$ErrorActionPreference = 'Stop'
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $root
try {
    dotnet build (Join-Path $PSScriptRoot 'Groundwork.Wp11Capacity.Probe.csproj') -c Release --nologo
    if ($LASTEXITCODE) { throw 'Capacity probe build failed.' }
    $dll = Join-Path $root 'artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll'
    $results = for ($run = 1; $run -le $Runs; $run++) {
        $json = dotnet $dll $Vendor
        if ($LASTEXITCODE) { throw "Capacity probe failed in process $run." }
        $json | ConvertFrom-Json
    }
    [ordered]@{
        timestampUtc = (Get-Date -AsUTC -Format o)
        sdk = (dotnet --version)
        os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,OSArchitecture
        cpu = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
        installedMemoryBytes = (Get-CimInstance Win32_PhysicalMemory | Measure-Object Capacity -Sum).Sum
        graphics = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion)
        runs = $Runs
        results = @($results)
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output
} finally {
    Pop-Location
}
