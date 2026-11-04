param(
 [Parameter(Mandatory)][string]$ManifestPath,
 [Parameter(Mandatory)][string]$PackageDirectory,
 [Parameter(Mandatory)][string]$FeedDirectory,
 [Parameter(Mandatory)][string]$ExpectedManifestSha256,
 [Parameter(Mandatory)][string]$ExpectedSourceCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($ExpectedManifestSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $ExpectedSourceCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid trusted reference.' }
if ((Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash -ine $ExpectedManifestSha256) { throw 'Untrusted manifest hash.' }
$m = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($m.schemaVersion -ne 1 -or $m.sourceCommit -ine $ExpectedSourceCommit) { throw 'Wrong manifest schema or source commit.' }
$ids = @('Groundwork.Core','Groundwork.Platform.Windows','Groundwork.Simulation','Groundwork.Ecs','Groundwork.Jobs','Groundwork.Diagnostics')
$packages = @($m.packages)
if ($packages.Count -ne 6) { throw 'Unexpected package count.' }
$sourceDir = (Resolve-Path -LiteralPath $PackageDirectory).Path
$feedDir = (Resolve-Path -LiteralPath $FeedDirectory).Path
if ($feedDir -ieq $sourceDir) { throw 'Source and feed may not be the same.' }
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.nupkg' -File)
if ($sourceFiles.Count -ne 6) { throw 'Extra or missing package files.' }
$seen = @()
$plan = @()
foreach ($p in $packages) {
 $id = [string]$p.packageId
 $v = [string]$p.packageVersion
 if ($ids -cnotcontains $id -or $seen -ccontains $id) { throw 'Unknown or duplicate package identity.' }
 $seen += $id
 if ($v -notmatch '^0\.1\.0-dev\.[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*$') { throw 'Invalid package version.' }
 $filename = "$id.$v.nupkg"
 if ([string]$p.filename -cne $filename -or [string]$p.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Manifest identity invalid.' }
 $src = Join-Path $sourceDir $filename
 if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { throw "Missing $filename" }
 if ((Get-Item -LiteralPath $src).Length -ne [long]$p.sizeBytes) { throw 'Package size mismatch.' }
 if ((Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash -ine [string]$p.sha256) { throw "Package SHA256 mismatch: $filename" }
 $folder = Join-Path (Join-Path $feedDir $id.ToLowerInvariant()) $v.ToLowerInvariant()
 $target = Join-Path $folder ("$($id.ToLowerInvariant()).$($v.ToLowerInvariant()).nupkg")
 if (Test-Path -LiteralPath $target) {
   if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine [string]$p.sha256) { throw 'Immutable package coordinate collision.' }
 }
 $plan += [pscustomobject]@{ src=$src; folder=$folder; target=$target; hash=[string]$p.sha256 }
}
# Validate all inputs and existing identities before writing. A disk-error partial feed must be discarded.
foreach ($item in $plan) {
 New-Item -ItemType Directory -Path $item.folder -Force | Out-Null
 if (-not (Test-Path -LiteralPath $item.target)) {
   $temp = Join-Path $item.folder ('.tmp-' + [guid]::NewGuid().ToString('N'))
   try {
     Copy-Item -LiteralPath $item.src -Destination $temp -ErrorAction Stop
     if ((Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash -ine $item.hash) { throw 'Copy corrupted.' }
     [System.IO.File]::Move($temp,$item.target)
   } finally {
     if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
   }
 }
 if ((Get-FileHash -LiteralPath $item.target -Algorithm SHA256).Hash -ine $item.hash) { throw 'Final feed validation failed.' }
 # NuGet hierarchical feeds need the expanded nuspec and SHA512 index beside nupkg.
 # Derive both exclusively from the verified package bytes; never trust mutable sidecars.
 Add-Type -AssemblyName System.IO.Compression
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $packageName = [System.IO.Path]::GetFileNameWithoutExtension($item.target)
 $cut = $packageName.IndexOf('.0.1.0-dev.', [System.StringComparison]::OrdinalIgnoreCase)
 if ($cut -lt 1) { throw 'Invalid canonical package filename.' }
 $id = $packageName.Substring(0, $cut)
 $nuspecTarget = Join-Path $item.folder "$id.nuspec"
 $shaTarget = "$($item.target).sha512"
 $zip = [System.IO.Compression.ZipFile]::OpenRead($item.target)
 try {
   $nuspec = @($zip.Entries | Where-Object { $_.Name -ieq "$id.nuspec" })
   if ($nuspec.Count -ne 1) { throw 'Verified archive lacks exactly one expected nuspec.' }
   $reader = [System.IO.StreamReader]::new($nuspec[0].Open())
   try { $nuspecText = $reader.ReadToEnd() } finally { $reader.Dispose() }
 } finally { $zip.Dispose() }
 $hasher = [System.Security.Cryptography.SHA512]::Create()
 try {
   $sha512 = [Convert]::ToBase64String($hasher.ComputeHash([System.IO.File]::ReadAllBytes($item.target)))
 } finally { $hasher.Dispose() }
 if (Test-Path -LiteralPath $nuspecTarget) {
   if ([System.IO.File]::ReadAllText($nuspecTarget) -cne $nuspecText) { throw 'Existing nuspec conflict.' }
 } else {
   [System.IO.File]::WriteAllText($nuspecTarget, $nuspecText, [System.Text.UTF8Encoding]::new($false))
 }
 if (Test-Path -LiteralPath $shaTarget) {
   if ([System.IO.File]::ReadAllText($shaTarget).Trim() -cne $sha512) { throw 'Existing SHA512 sidecar conflict.' }
 } else {
   [System.IO.File]::WriteAllText($shaTarget, $sha512, [System.Text.Encoding]::ASCII)
 }
}
Write-Host 'PASS: six package hashes and immutable feed coordinates verified.'
