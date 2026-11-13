param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$DxcPackagePath,
    [switch]$VerifyDeterminism
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'The Groundwork D3D12 shader build requires Windows x64 PowerShell.'
}

$packageId = 'Microsoft.Direct3D.DXC'
$packageVersion = '1.9.2609.5'
$packageName = "microsoft.direct3d.dxc.$packageVersion.nupkg"
$expectedHash = '73CFA082245918C2CC1EC6740250EB7377E83A757B3B3A3E16E0D71535BFBFC3'
$source = Join-Path $PSScriptRoot '../src/Groundwork.Rendering.D3D12/Shaders'
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$commonFlags = @('-HV', '2021', '-O3', '-WX', '-Zpr')
$shaders = @(
    @{ EntryPoint = 'VSMain'; Profile = 'vs_6_0'; Output = 'Cube.vs.dxil'; Source = 'Cube.hlsl' },
    @{ EntryPoint = 'PSMain'; Profile = 'ps_6_0'; Output = 'Cube.ps.dxil'; Source = 'Cube.hlsl' },
    @{ EntryPoint = 'SkyVS'; Profile = 'vs_6_0'; Output = 'Sky.vs.dxil'; Source = 'Sky.hlsl' },
    @{ EntryPoint = 'SkyPS'; Profile = 'ps_6_0'; Output = 'Sky.ps.dxil'; Source = 'Sky.hlsl' }
)
$toolFiles = @('dxc.exe', 'dxcompiler.dll', 'dxil.dll', 'dxv.exe')
$licenses = @('LICENSE-LLVM.txt', 'LICENSE-MS.txt')

function Build-Shaders([string]$Directory) {
    New-Item -ItemType Directory -Path $Directory | Out-Null

    Copy-Item -LiteralPath (Join-Path $source 'Cube.hlsl'),(Join-Path $source 'Sky.hlsl') -Destination $Directory

    foreach ($shader in $shaders) {
        $inputFile = Join-Path $Directory ($shader.Source)
        $compiled = Join-Path $Directory $shader.Output
        & $compiler -E $shader.EntryPoint -T $shader.Profile @commonFlags -Fo $compiled $inputFile
        if ($LASTEXITCODE -ne 0) { throw "DXC failed for $($shader.Profile)." }
        & $validator $compiled
        if ($LASTEXITCODE -ne 0) { throw "DXV rejected $($shader.Profile)." }
    }
}

$tempRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).ProviderPath)
$scratch = Join-Path $tempRoot ('groundwork-dxc-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $package = Join-Path $scratch $packageName
    if ($DxcPackagePath) {
        Copy-Item -LiteralPath $DxcPackagePath -Destination $package
    } else {
        $uri = "https://api.nuget.org/v3-flatcontainer/microsoft.direct3d.dxc/$packageVersion/$packageName"
        Invoke-WebRequest -Uri $uri -OutFile $package -TimeoutSec 90
    }

    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    if ($hash -cne $expectedHash) { throw "$packageId $packageVersion SHA-256 mismatch." }
    & dotnet nuget verify $package --all
    if ($LASTEXITCODE -ne 0) { throw "$packageId $packageVersion signature verification failed." }

    $tools = Join-Path $scratch 'bin'
    New-Item -ItemType Directory -Path $tools | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        foreach ($name in $toolFiles + $licenses) {
            $entryName = if ($name -in $toolFiles) { "build/native/bin/x64/$name" } else { $name }
            $entry = $archive.GetEntry($entryName)
            if ($null -eq $entry) { throw "Verified DXC package is missing $entryName." }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $tools $name))
        }
    } finally {
        $archive.Dispose()
    }

    $compiler = Join-Path $tools 'dxc.exe'
    $validator = Join-Path $tools 'dxv.exe'
    $first = Join-Path $scratch 'build-a'
    Push-Location -LiteralPath $tools
    try {
        $identity = (& $compiler --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $identity) { throw 'Could not determine the verified compiler identity.' }
        Build-Shaders $first
        if ($VerifyDeterminism) {
            $second = Join-Path $scratch 'independent checkout/build-b'
            Build-Shaders $second
            foreach ($shader in $shaders) {
                $firstHash = (Get-FileHash -LiteralPath (Join-Path $first $shader.Output)).Hash
                $secondHash = (Get-FileHash -LiteralPath (Join-Path $second $shader.Output)).Hash
                if ($firstHash -cne $secondHash) { throw "DXIL differs between source paths for $($shader.Profile)." }
            }
        }
    } finally {
        Pop-Location
    }

    $manifest = [ordered]@{
        packageId = $packageId
        packageVersion = $packageVersion
        packageSha256 = $hash
        compilerIdentity = $identity
        toolHashes = @($toolFiles | ForEach-Object {
            [ordered]@{ file = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $tools $_)).Hash }
        })
        source = 'Cube.hlsl'
        sourceSha256 = (Get-FileHash -LiteralPath (Join-Path $first 'Cube.hlsl')).Hash
        determinismVerified = $VerifyDeterminism.IsPresent
        shaders = @($shaders | ForEach-Object {
            [ordered]@{
                output = $_.Output
                source = $_.Source
                sourceSha256 = (Get-FileHash -LiteralPath (Join-Path $first ($_.Source))).Hash
                arguments = @('-E', $_.EntryPoint, '-T', $_.Profile) + $commonFlags + @('-Fo', $_.Output, ($_.Source))
                sha256 = (Get-FileHash -LiteralPath (Join-Path $first $_.Output)).Hash
            }
        })
    }

    [IO.Directory]::CreateDirectory($output) | Out-Null
    foreach ($shader in $shaders) {
        Copy-Item -LiteralPath (Join-Path $first $shader.Output) -Destination (Join-Path $output $shader.Output) -Force
    }
    foreach ($name in $licenses) {
        Copy-Item -LiteralPath (Join-Path $tools $name) -Destination (Join-Path $output "DXC.$name") -Force
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'shader-provenance.json') -Encoding utf8NoBOM
    Write-Host "Validated $($shaders.Count) Groundwork D3D12 shaders. Determinism checked: $($VerifyDeterminism.IsPresent)."
} finally {
    $resolvedScratch = (Resolve-Path -LiteralPath $scratch).ProviderPath
    if ((Split-Path -Parent $resolvedScratch) -ne $tempRoot) { throw 'Refusing to remove a directory outside the tool scratch area.' }
    Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
}
