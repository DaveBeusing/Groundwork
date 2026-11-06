param([Parameter(Mandatory)][string]$PackagePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expected = 'A02D560C0EF0D64541F5A746661D8A0C2BC98AC45C3A4C380B2F3BC038E1DAA7'
if ((Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash -cne $expected) {
    throw 'Restored TerraFX package differs from the reviewed D3D12 binding.'
}
& dotnet nuget verify $PackagePath --all
if ($LASTEXITCODE -ne 0) { throw 'TerraFX NuGet signature verification failed.' }

$assembly = 'lib/net10.0/TerraFX.Interop.Windows.dll'
$expanded = Join-Path (Split-Path -Parent $PackagePath) $assembly
$zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $stream = $zip.GetEntry($assembly).Open()
    try { $embedded = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
    if ((Get-FileHash -LiteralPath $expanded -Algorithm SHA256).Hash -cne $embedded) {
        throw 'Expanded TerraFX assembly differs from the verified package.'
    }
} finally { $zip.Dispose() }
