param(
 [Parameter(Mandatory)][string]$ManifestPath,
 [Parameter(Mandatory)][string]$PackageDirectory,
 [Parameter(Mandatory)][string]$ExpectedManifestSha256,
 [Parameter(Mandatory)][string]$ExpectedSourceCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stageScript = Join-Path $PSScriptRoot 'Stage-VerifiedPackages.ps1'
$manifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$packages = (Resolve-Path -LiteralPath $PackageDirectory).Path
if ($ExpectedManifestSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $ExpectedSourceCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Expected hashes must be complete.' }
# Never mutate caller's artifacts; perform all negative cases on disposable copies.
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('groundwork-l4-negative-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -ErrorAction Stop | Out-Null
$cases = 0
function Invoke-ShouldReject([string]$CaseName, [string]$Manifest, [string]$PackageSource, [string]$Feed, [string]$ExpectedDigest, [string]$Commit) {
 New-Item -ItemType Directory -Force -Path $Feed | Out-Null
 $rejected = $false
 try {
    & $stageScript -ManifestPath $Manifest -PackageDirectory $PackageSource -FeedDirectory $Feed -ExpectedManifestSha256 $ExpectedDigest -ExpectedSourceCommit $Commit
 } catch {
    $rejected = $true
 }
 if (-not $rejected) { throw "FAIL: invalid input accepted: $CaseName" }
 Write-Host "PASS: $CaseName rejected"
}
try {
 # Check the positive case before running any tests.
 $baselineFeed = Join-Path $fixtureRoot 'positive-feed'
 New-Item -ItemType Directory -Path $baselineFeed | Out-Null
 & $stageScript -ManifestPath $manifest -PackageDirectory $packages -FeedDirectory $baselineFeed -ExpectedManifestSha256 $ExpectedManifestSha256 -ExpectedSourceCommit $ExpectedSourceCommit
 $packageCount = @(Get-ChildItem -LiteralPath $packages -Filter '*.nupkg' -File).Count
 if (@(Get-ChildItem -LiteralPath $baselineFeed -Recurse -Filter '*.nupkg' -File).Count -ne $packageCount) { throw 'Positive staging produced the wrong number of packages.' }

 $modifiedManifest = Join-Path $fixtureRoot 'modified.json'
 Copy-Item -LiteralPath $manifest -Destination $modifiedManifest
 Add-Content -LiteralPath $modifiedManifest -Value 'tampered'
 Invoke-ShouldReject 'modified manifest' $modifiedManifest $packages (Join-Path $fixtureRoot 'feed-modified') $ExpectedManifestSha256 $ExpectedSourceCommit
 $cases++

 $missingManifest = Join-Path $fixtureRoot 'absent.json'
 Invoke-ShouldReject 'missing manifest' $missingManifest $packages (Join-Path $fixtureRoot 'feed-absent') $ExpectedManifestSha256 $ExpectedSourceCommit
 $cases++

 $wrongSha = ('0' * 64)
 if ($wrongSha -ieq $ExpectedManifestSha256) { $wrongSha = '1' * 64 }
 Invoke-ShouldReject 'untrusted manifest digest' $manifest $packages (Join-Path $fixtureRoot 'feed-digest') $wrongSha $ExpectedSourceCommit
 $cases++

 $wrongCommit = ('0' * 40)
 if ($wrongCommit -ieq $ExpectedSourceCommit) { $wrongCommit = '1' * 40 }
 Invoke-ShouldReject 'wrong source commit' $manifest $packages (Join-Path $fixtureRoot 'feed-source') $ExpectedManifestSha256 $wrongCommit
 $cases++

 $tamperedDir = Join-Path $fixtureRoot 'tampered-packages'
 New-Item -ItemType Directory -Path $tamperedDir | Out-Null
 Get-ChildItem -LiteralPath $packages -Filter '*.nupkg' -File | Copy-Item -Destination $tamperedDir
 $one = @(Get-ChildItem -LiteralPath $tamperedDir -Filter '*.nupkg' -File | Sort-Object Name)[0]
 [System.IO.File]::AppendAllText($one.FullName,'tampered')
 Invoke-ShouldReject 'tampered package' $manifest $tamperedDir (Join-Path $fixtureRoot 'feed-tampered') $ExpectedManifestSha256 $ExpectedSourceCommit
 $cases++

 $missingDir = Join-Path $fixtureRoot 'missing-package'
 New-Item -ItemType Directory -Path $missingDir | Out-Null
 Get-ChildItem -LiteralPath $packages -Filter '*.nupkg' -File | Copy-Item -Destination $missingDir
 $one = @(Get-ChildItem -LiteralPath $missingDir -Filter '*.nupkg' -File | Sort-Object Name)[0]
 Remove-Item -LiteralPath $one.FullName
 Invoke-ShouldReject 'missing package' $manifest $missingDir (Join-Path $fixtureRoot 'feed-missing') $ExpectedManifestSha256 $ExpectedSourceCommit
 $cases++

 $wrongVersionDir = Join-Path $fixtureRoot 'wrong-version'
 New-Item -ItemType Directory -Path $wrongVersionDir | Out-Null
 Get-ChildItem -LiteralPath $packages -Filter '*.nupkg' -File | Copy-Item -Destination $wrongVersionDir
 $one = @(Get-ChildItem -LiteralPath $wrongVersionDir -Filter '*.nupkg' -File | Sort-Object Name)[0]
 Rename-Item -LiteralPath $one.FullName -NewName ($one.BaseName + '.wrong.nupkg')
 Invoke-ShouldReject 'wrong package version/filename' $manifest $wrongVersionDir (Join-Path $fixtureRoot 'feed-version') $ExpectedManifestSha256 $ExpectedSourceCommit
 $cases++

 # Existing coordinates must never be replaced with bytes different from the signed-off digest.
 $collisionFeed = Join-Path $fixtureRoot 'feed-collision'
 Copy-Item -LiteralPath $baselineFeed -Destination $collisionFeed -Recurse
 $installed = @(Get-ChildItem -LiteralPath $collisionFeed -Recurse -Filter '*.nupkg' -File | Sort-Object FullName)[0]
 [System.IO.File]::AppendAllText($installed.FullName,'tampered')
 Invoke-ShouldReject 'immutable coordinate collision' $manifest $packages $collisionFeed $ExpectedManifestSha256 $ExpectedSourceCommit
 $cases++

 Write-Host "PASS: $cases negative package-integrity cases rejected."
} finally {
 Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}
