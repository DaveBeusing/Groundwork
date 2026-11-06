param(
 [Parameter(Mandatory)][string]$PackageDirectory,
 [Parameter(Mandatory)][string]$Version,
 [Parameter(Mandatory)][string]$SourceCommit,
 [Parameter(Mandatory)][string]$BuildId,
 [Parameter(Mandatory)][string]$OutputManifest
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($SourceCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid source commit.' }
if ($Version -notmatch '^0\.1\.0-dev\.[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*$') { throw 'Invalid prerelease version.' }
if ([string]::IsNullOrWhiteSpace($BuildId)) { throw 'Build identity required.' }
$ids = @('Groundwork.Core','Groundwork.Platform.Windows','Groundwork.Simulation','Groundwork.Ecs','Groundwork.Jobs','Groundwork.Diagnostics','Groundwork.Rendering','Groundwork.Rendering.D3D12','Groundwork.Voxels')
$files = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File)
if ($files.Count -ne $ids.Count) { throw "Expected $($ids.Count) packages, found $($files.Count)." }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$entries = @()
foreach ($id in $ids) {
 $filename = "$id.$Version.nupkg"
 $match = @($files | Where-Object { $_.Name -ceq $filename })
 if ($match.Count -ne 1) { throw "Missing expected package $filename" }
 $zip = [System.IO.Compression.ZipFile]::OpenRead($match[0].FullName)
 try {
   $nuspec = @($zip.Entries | Where-Object { $_.Name -eq "$id.nuspec" })
   if ($nuspec.Count -ne 1) { throw "Invalid nuspec $filename" }
   $stream = [System.IO.StreamReader]::new($nuspec[0].Open())
   try { [xml]$xml = $stream.ReadToEnd() } finally { $stream.Dispose() }
   $pkgId = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="id"]')
   $pkgVersion = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="version"]')
   if ($null -eq $pkgId -or $pkgId.InnerText -cne $id -or $null -eq $pkgVersion -or $pkgVersion.InnerText -cne $Version) { throw "Unexpected package metadata $filename" }
   if ($id -ceq 'Groundwork.Rendering.D3D12') {
     if ($null -eq $zip.GetEntry('shader-provenance.json')) { throw 'Provider package is missing shader provenance.' }
     if (@($zip.Entries | Where-Object { $_.Name -in @('dxc.exe','dxv.exe','dxcompiler.dll','dxil.dll') }).Count -ne 0) {
       throw 'Provider package contains build-only DXC binaries.'
     }
   }
 } finally { $zip.Dispose() }
 $entries += [ordered]@{
   packageId = $id
   packageVersion = $Version
   filename = $filename
   sha256 = (Get-FileHash -LiteralPath $match[0].FullName -Algorithm SHA256).Hash
   sizeBytes = [long]$match[0].Length
 }
}
if (Test-Path -LiteralPath $OutputManifest) { throw 'Manifest already exists.' }
$manifest = [ordered]@{
 schemaVersion = 1
 sourceCommit = $SourceCommit.ToLowerInvariant()
 buildId = $BuildId
 buildConfiguration = 'Release'
 generatedAtUtc = [DateTime]::UtcNow.ToString('o')
 packages = $entries
}
$json = ConvertTo-Json -InputObject $manifest -Depth 8
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputManifest),$json,[System.Text.UTF8Encoding]::new($false))
Write-Host "Manifest SHA256: $((Get-FileHash -LiteralPath $OutputManifest -Algorithm SHA256).Hash)"
