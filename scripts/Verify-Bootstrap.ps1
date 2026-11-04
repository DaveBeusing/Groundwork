# WP01-B00: clean local Windows bootstrap verification. No CI claims.
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$modules = @('Core', 'Platform.Windows', 'Simulation', 'Ecs', 'Jobs', 'Diagnostics')
function Assert-True([bool] $condition, [string] $message) {
    if (-not $condition) { throw $message }
}
function Invoke-Native([string] $exe, [string[]] $arguments) {
    Write-Host ('> ' + $exe + ' ' + ($arguments -join ' '))
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw ("{0} failed with exit code {1}" -f $exe, $LASTEXITCODE)
    }
}
Push-Location $root
try {
    Assert-True (Test-Path 'Groundwork.slnx') 'Groundwork.slnx is missing.'
    [xml] $solution = Get-Content 'Groundwork.slnx' -Raw
    $paths = @($solution.SelectNodes('//Project') | ForEach-Object { $_.GetAttribute('Path') })
    Assert-True ($paths.Count -eq $modules.Count) 'Solution must contain the six approved projects.'
    [xml] $props = Get-Content 'Directory.Build.props' -Raw
    $version = $props.SelectSingleNode('//PropertyGroup/Version')
    Assert-True (($null -ne $version) -and ($version.InnerText -eq '0.1.0')) 'Source version must be 0.1.0.'
    foreach ($module in $modules) {
        $id = 'Groundwork.' + $module
        $projectPath = "src/$id/$id.csproj"
        Assert-True (Test-Path $projectPath) ("Missing project: " + $projectPath)
        Assert-True ($paths -contains $projectPath) ("Project missing from solution: " + $projectPath)
        [xml] $project = Get-Content $projectPath -Raw
        $packageId = $project.SelectSingleNode('//PropertyGroup/PackageId')
        Assert-True (($null -ne $packageId) -and ($packageId.InnerText -eq $id)) ("Invalid PackageId: " + $id)
        $refs = @($project.SelectNodes('//ProjectReference') | ForEach-Object { $_.GetAttribute('Include') })
        if ($module -eq 'Core') {
            Assert-True ($refs.Count -eq 0) 'Groundwork.Core must not have project references.'
        } else {
            Assert-True (($refs.Count -eq 1) -and ($refs[0] -eq '../Groundwork.Core/Groundwork.Core.csproj')) ("Invalid dependency for " + $id)
        }
        Assert-True (-not ($projectPath -match 'WorldsYetUnseen')) 'Groundwork must not contain game projects.'
    }
    $tracked = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
    $generated = @($tracked | Where-Object { $_ -match '(^|/)(bin|obj|artifacts|dist)/|\.(nupkg|snupkg|dll|exe)$' })
    Assert-True ($generated.Count -eq 0) ('Generated files are tracked: ' + ($generated -join ', '))
    Invoke-Native 'dotnet' @('--version')
    Invoke-Native 'dotnet' @('restore', 'Groundwork.slnx', '--configfile', 'NuGet.Config', '--force-evaluate')
    Invoke-Native 'dotnet' @('build', 'Groundwork.slnx', '-c', 'Release', '--no-restore')
    Write-Host 'PASS: WP01-B00 Groundwork structure, restore and Release build.' -ForegroundColor Green
    Write-Host 'No runtime tests, package provenance, or Windows runtime behavior are certified by this script.'
} finally {
    Pop-Location
}
