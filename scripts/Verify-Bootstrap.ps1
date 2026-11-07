# Verify the shipping graph, perform a locked build and optionally run the contract tests.
param([string] $TestResultsDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$modules = @('Core', 'Platform.Windows', 'Simulation', 'Ecs', 'Jobs', 'Diagnostics', 'Rendering', 'Rendering.D3D12', 'Voxels', 'Persistence')
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
    $paths = @($solution.SelectNodes('/Solution/Folder[@Name="/src/"]/Project') | ForEach-Object { $_.GetAttribute('Path') })
    $testPaths = @($solution.SelectNodes('/Solution/Folder[@Name="/tests/"]/Project') | ForEach-Object { $_.GetAttribute('Path') })
    Assert-True ($paths.Count -eq $modules.Count) 'Solution must contain the approved shipping projects.'
    Assert-True ($solution.SelectNodes('//Project').Count -eq ($paths.Count + $testPaths.Count)) 'Unexpected solution project entry.'
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
        $packages = @($project.SelectNodes('//PackageReference'))
        if ($module -eq 'Rendering.D3D12') {
            Assert-True ($packages.Count -eq 1 -and $packages[0].GetAttribute('Include') -ceq 'TerraFX.Interop.Windows' -and
                $packages[0].GetAttribute('Version') -ceq '[10.0.26100.6]') 'Invalid D3D12 binding reference.'
        } else {
            Assert-True ($packages.Count -eq 0) ("Unexpected shipping package reference: " + $id)
        }
        $refs = @($project.SelectNodes('//ProjectReference') | ForEach-Object { $_.GetAttribute('Include') })
        if ($module -in @('Core', 'Voxels')) {
            Assert-True ($refs.Count -eq 0) ("$id must not have project references.")
        } elseif ($module -eq 'Persistence') {
            Assert-True (($refs.Count -eq 1) -and ($refs[0] -ceq '../Groundwork.Voxels/Groundwork.Voxels.csproj')) 'Invalid Persistence project dependencies.'
        } elseif ($module -eq 'Rendering.D3D12') {
            $allowed = @('../Groundwork.Core/Groundwork.Core.csproj', '../Groundwork.Platform.Windows/Groundwork.Platform.Windows.csproj',
                '../Groundwork.Rendering/Groundwork.Rendering.csproj', '../Groundwork.Voxels/Groundwork.Voxels.csproj')
            Assert-True ($refs.Count -eq $allowed.Count -and @($refs | Sort-Object -Unique).Count -eq $refs.Count -and
                @($refs | Where-Object { $_ -cnotin $allowed }).Count -eq 0) 'Invalid D3D12 project dependencies.'
        } else {
            Assert-True (($refs.Count -eq 1) -and ($refs[0] -eq '../Groundwork.Core/Groundwork.Core.csproj')) ("Invalid dependency for " + $id)
        }
        Assert-True (-not ($projectPath -match 'WorldsYetUnseen')) 'Groundwork must not contain game projects.'
    }
    $tracked = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
    $approvedTests = @{}
    foreach ($module in $modules) {
        $id = 'Groundwork.' + $module
        $approvedTests["tests/$id.Tests/$id.Tests.csproj"] = @("../../src/$id/$id.csproj")
    }
    $approvedTests['tests/Groundwork.Simulation.Tests/Groundwork.Simulation.Tests.csproj'] += @(
        '../../src/Groundwork.Diagnostics/Groundwork.Diagnostics.csproj',
        '../../src/Groundwork.Ecs/Groundwork.Ecs.csproj',
        '../../src/Groundwork.Jobs/Groundwork.Jobs.csproj'
    )
    Assert-True ($testPaths -contains 'tests/Groundwork.Core.Tests/Groundwork.Core.Tests.csproj') 'Core tests are missing from the solution.'
    Assert-True ($testPaths -contains 'tests/Groundwork.Rendering.Tests/Groundwork.Rendering.Tests.csproj') 'Rendering tests are missing from the solution.'
    Assert-True ($testPaths -contains 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj') 'D3D12 tests are missing from the solution.'
    Assert-True ($testPaths -contains 'tests/Groundwork.Voxels.Tests/Groundwork.Voxels.Tests.csproj') 'Voxel tests are missing from the solution.'
    Assert-True ($testPaths -contains 'tests/Groundwork.Persistence.Tests/Groundwork.Persistence.Tests.csproj') 'Persistence tests are missing from the solution.'
    Assert-True (@($testPaths | Sort-Object -Unique).Count -eq $testPaths.Count) 'Duplicate solution test project entry.'
    foreach ($path in $testPaths) {
        Assert-True ($approvedTests.ContainsKey($path)) ("Unexpected test project: " + $path)
        [xml] $project = Get-Content -LiteralPath $path -Raw
        $refs = @($project.SelectNodes('//ProjectReference') | ForEach-Object { $_.GetAttribute('Include') })
        $expectedRefs = $approvedTests[$path]
        Assert-True (($refs.Count -eq $expectedRefs.Count) -and
            (@($refs | Sort-Object -Unique).Count -eq $refs.Count) -and
            (@($refs | Where-Object { $_ -cnotin $expectedRefs }).Count -eq 0)) ("Invalid test dependencies: " + $path)
    }
    foreach ($file in Get-ChildItem -LiteralPath 'tests' -Filter '*.csproj' -Recurse -File) {
        $relativePath = [System.IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        Assert-True ($testPaths -contains $relativePath) ("Test project missing from solution: " + $relativePath)
    }
    $lockHashes = @{}
    foreach ($path in ($paths + $testPaths)) {
        $lock = (Split-Path -Parent $path).Replace('\', '/') + '/packages.lock.json'
        Assert-True (($tracked -contains $lock) -and (Test-Path -LiteralPath $lock -PathType Leaf)) ("Missing tracked lock: " + $lock)
        $lockHashes[$lock] = (Get-FileHash -LiteralPath $lock).Hash
    }
    $generated = @($tracked | Where-Object { $_ -match '(^|/)(bin|obj|artifacts|dist)/|\.(nupkg|snupkg|dll|exe)$' })
    Assert-True ($generated.Count -eq 0) ('Generated files are tracked: ' + ($generated -join ', '))
    Invoke-Native 'dotnet' @('--version')
    Invoke-Native 'dotnet' @('restore', 'Groundwork.slnx', '--configfile', 'NuGet.Config', '--locked-mode')
    foreach ($lock in $lockHashes.Keys) {
        Assert-True ((Get-FileHash -LiteralPath $lock).Hash -eq $lockHashes[$lock]) ("Restore modified lock: " + $lock)
    }
    Invoke-Native 'dotnet' @('build', 'Groundwork.slnx', '-c', 'Release', '--no-restore')
    Write-Host 'PASS: Groundwork structure, locked restore and Release build.' -ForegroundColor Green
    if ($TestResultsDirectory) {
        New-Item -ItemType Directory -Path $TestResultsDirectory | Out-Null
        foreach ($path in $testPaths) {
            $name = [System.IO.Path]::GetFileNameWithoutExtension($path)
            $results = Join-Path $TestResultsDirectory $name
            New-Item -ItemType Directory -Path $results | Out-Null
            Invoke-Native 'dotnet' @('test', $path, '-c', 'Release', '--no-build', '--no-restore',
                '--filter', 'TestCategory!=WindowsDesktopSmoke', '--logger', 'trx;LogFileName=results.trx', '--results-directory', $results)
            [xml] $report = Get-Content -LiteralPath (Join-Path $results 'results.trx') -Raw
            $counters = $report.SelectSingleNode('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
            Assert-True ($null -ne $counters) ("Missing test counters: " + $name)
            $executed = [int] $counters.GetAttribute('executed')
            $passed = [int] $counters.GetAttribute('passed')
            $failed = [int] $counters.GetAttribute('failed')
            Assert-True (($executed -gt 0) -and ($passed -gt 0) -and ($failed -eq 0)) ("No passing executed test, or a failed test: " + $name)
            Write-Host "$name executed $executed tests and passed $passed."
        }
    }
} finally {
    Pop-Location
}
