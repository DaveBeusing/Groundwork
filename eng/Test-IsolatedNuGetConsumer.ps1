param(
 [Parameter(Mandatory)][string]$PackageDirectory,
 [Parameter(Mandatory)][string]$ManifestPath,
 [Parameter(Mandatory)][string]$ExpectedManifestSha256,
 [Parameter(Mandatory)][string]$ExpectedSourceCommit,
 [Parameter(Mandatory)][string]$Version
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$stage = Join-Path $PSScriptRoot 'Stage-VerifiedPackages.ps1'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('groundwork-consumer-' + [guid]::NewGuid().ToString('N'))
$feed = Join-Path $root 'feed'
$source = Join-Path $root 'consumer'
$cache = Join-Path $root 'cache'
New-Item -ItemType Directory -Force -Path $feed,$source,$cache | Out-Null
$oldCache = $env:NUGET_PACKAGES
try {
 & $stage -ManifestPath $ManifestPath -PackageDirectory $PackageDirectory -FeedDirectory $feed -ExpectedManifestSha256 $ExpectedManifestSha256 -ExpectedSourceCommit $ExpectedSourceCommit
 $env:NUGET_PACKAGES = $cache
 $escapedFeed = [System.Security.SecurityElement]::Escape($feed)
 $config = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
 <packageSources>
  <clear />
  <add key="verified-groundwork" value="$escapedFeed" />
 </packageSources>
 <packageSourceMapping>
  <clear />
  <packageSource key="verified-groundwork">
   <package pattern="Groundwork.*" />
  </packageSource>
 </packageSourceMapping>
</configuration>
"@
 $nugetConfig = Join-Path $source 'NuGet.Config'
 [System.IO.File]::WriteAllText($nugetConfig,$config)
 $project = Join-Path $source 'Consumer.csproj'
 $projectText = @"
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup>
  <TargetFramework>net10.0-windows</TargetFramework>
  <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
 </PropertyGroup>
 <ItemGroup>
  <PackageReference Include="Groundwork.Core" Version="[$Version]" />
 </ItemGroup>
</Project>
"@
 [System.IO.File]::WriteAllText($project,$projectText)
 & dotnet restore $project --configfile $nugetConfig --force-evaluate
 if ($LASTEXITCODE -ne 0) { throw 'Isolated consumer restore failed.' }
 if (-not (Test-Path (Join-Path $source 'packages.lock.json'))) { throw 'Missing generated lockfile.' }
 & dotnet restore $project --configfile $nugetConfig --locked-mode
 if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
 & dotnet build $project -c Release --no-restore
 if ($LASTEXITCODE -ne 0) { throw 'Consumer build failed.' }
 [System.IO.File]::WriteAllText($project,$projectText.Replace("[$Version]",'[0.1.0-dev.0.0]'))
 & dotnet restore $project --configfile $nugetConfig --locked-mode
 if ($LASTEXITCODE -eq 0) { throw 'Stale lock was accepted.' }
 [System.IO.File]::WriteAllText($project,$projectText)
 Remove-Item -LiteralPath $cache -Recurse -Force
 New-Item -ItemType Directory -Path $cache | Out-Null
 $empty = Join-Path $root 'empty'
 New-Item -ItemType Directory -Path $empty | Out-Null
 [System.IO.File]::WriteAllText($nugetConfig,$config.Replace($escapedFeed,[System.Security.SecurityElement]::Escape($empty)))
 & dotnet restore $project --configfile $nugetConfig --locked-mode --force-evaluate
 if ($LASTEXITCODE -eq 0) { throw 'Missing feed was accepted.' }
 $global:LASTEXITCODE = 0
 Write-Host 'PASS: isolated consumer, locked restore, stale lock and missing feed rejected.'
} finally {
 $env:NUGET_PACKAGES = $oldCache
 Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
