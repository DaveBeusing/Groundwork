# WP03 two-chunk terrain handover

On 2026-11-06, Groundwork and Worlds Yet Unseen (WYU) passed the Windows
integration gates from separate clean `master` clones. The source and package
consumer restores used separate fresh NuGet caches and committed lockfiles.
Owner acceptance of this handover follows [ADR-0007](adr/ADR-0007-solo-review-governance.md).

## Source and package baselines

| Baseline | Exact identity | Evidence |
| --- | --- | --- |
| Groundwork `master` and package source | `d7645731c18693e4c8e42ebe05e794ea30b0454e` | [Windows CI run 42225951792, attempt 1](https://github.com/DaveBeusing/Groundwork/actions/runs/42225951792/attempts/1) |
| WYU `master` | `a1ee1f623b9082602d81bc0dbd5bdcbbc912e635` | [Windows consumer CI run 42229221868](https://github.com/DaveBeusing/wyu/actions/runs/42229221868) |
| WYU package pins | `[0.1.0-dev.42225951792.1]` for all nine Groundwork packages | [Immutable release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42225951792.1-immutable) |

| Archive evidence | SHA-256 |
| --- | --- |
| Producer CI artifact ZIP | `E1F86A55E84D7F4431C0767D40D3043E5E1040F2F65B3BFF7337E802A8EFC490` |
| Build manifest | `15DBEE87CE4BC3936757676E365D93ECF3F252F7C5C255B2615CB25BFEB0E841` |
| Archive record | `FFE62598133A308E8561B9107FF65578B51D275947B5EADDAABD3FB0837513F6` |
| Apache-2.0 license | `B1D2870F1A00E4D7F56576E5F0870CBA109E041F8AF66866CF5B499478E654E7` |

GitHub reported the release as immutable and bound to the source commit.
All twelve newly downloaded assets matched GitHub's sizes and SHA-256
digests. The nine packages and manifest also matched the entries in the
freshly downloaded producer ZIP byte for byte. WYU restores Groundwork
from the verified local feed and `TerraFX.Interop.Windows` at
`10.0.26100.6` from NuGet.org. Keep the archive available while a consumer
pins this version.

## Clean Windows results

Windows 11 Enterprise x64 build `10.0.26300`, .NET SDK `10.0.403`, and
an interactive desktop with Windows Graphics Tools. The system D3D12
Debug Layer was `10.0.26100.9549`. DXGI selected the NVIDIA RTX PRO 5000
Blackwell Generation Laptop GPU (`10DE:2C38`, driver `32.0.15.9653`).
Both source Release builds reported zero build warnings and errors.
Groundwork also verified the signed TerraFX and DXC `1.9.2609.5` packages
and deterministic shader compilation.

| Gate | Passed | Coverage |
| --- | ---: | --- |
| Groundwork automated tests | 211 | Core 35, Diagnostics 33, ECS 14, Jobs 25, Windows 28, Rendering 16, D3D12 19, Simulation 20, Voxels 21 |
| Groundwork desktop suites | 23 | 14 provider and 9 Windows platform cases |
| Focused Groundwork WARP terrain rerun | 7 | Seam color and depth, empty meshes, clear-only, presentation lifecycle and blocked resize |
| Focused Groundwork NVIDIA terrain rerun | 3 | Seam color and depth, presentation lifecycle and blocked resize |
| Groundwork package integrity and isolated consumer | Passed | Nine verified packages, eight integrity rejection cases, locked build, stale lock and missing feed rejection |
| WYU verified consumer | Passed | Nine exact package pins, locked restore, Release build, incompatible/missing/tampered package rejection and five host smoke modes |

The focused counts overlap the automated and desktop suites. The WARP
and NVIDIA readbacks sampled both meshes and nine pixels at the visible
join, then verified that the near mesh retained its numeric depth when the
far mesh drew later. The automated mesher tests cover internal face
suppression, canonical ordering, signed coordinates and rebuilding after a
neighbor revision changes. The desktop tests exercised resize, minimize,
restore and native close. When a native resize call was held, Core stopped
while the render owner and window retained custody. The tests then released
the call and verified complete settlement.

All ten focused Groundwork cases and the WYU terrain smoke reported active
Debug Layer and DRED. WARP selected Microsoft Basic Render Driver
(`1414:008C`, driver `10.0.26100.9711`). The debug reports were:

| Adapter and case | Warnings | Errors / corruption / discarded | Report HRESULT | Live GPU child objects |
| --- | ---: | --- | --- | ---: |
| WARP terrain readbacks (5 cases) | 2 each | 0 / 0 / 0 | `0x00000000` | 0 |
| WARP terrain presentation lifecycle | 3 | 0 / 0 / 0 | `0x00000000` | 0 |
| WARP terrain blocked resize | 1 | 0 / 0 / 0 | `0x00000000` | 0 |
| NVIDIA terrain color/depth readback | 2 | 0 / 0 / 0 | `0x00000000` | 0 |
| NVIDIA terrain presentation lifecycle | 3 | 0 / 0 / 0 | `0x00000000` | 0 |
| NVIDIA terrain blocked resize | 1 | 0 / 0 / 0 | `0x00000000` | 0 |
| WYU NVIDIA terrain smoke | 2 | 0 / 0 / 0 | `0x00000000` | 0 |

The blocked resize tests observed Core stop after 1,016.384 ms on WARP and
1,007.155 ms on NVIDIA. After releasing the call, settlement took 31.697 ms
and 95.564 ms respectively. These are single observations from this run.

The WYU terrain smoke presented frame 3 at 944 × 501 on NVIDIA and sent
native `WM_CLOSE`. The renderer stopped, the HWND was destroyed, and Core
released its resources. In a separate interactive `--terrain` launch from
the same build, the raised part of the second chunk was visible in the
876 × 501 client area after resize. Native close returned exit code 0.
The cube, renderer-free window, headless and controlled-fault smoke tests
also passed.

## Coverage boundaries

The Groundwork readback uses controlled portable geometry for numeric
color and depth assertions. The WYU run builds the actual voxel fixture
from the published packages and exercises native presentation and shutdown.
The blocked-operation desktop fixture holds resize. A dedicated regression
for overflow during partial upload of extreme translated coordinates
remains outside this run. The provider rejects that input, and the WP03
chunk bounds keep the fixture within exactly representable coordinates.

## Reproduce

Use clean clones of the two baseline commits, PowerShell 7 and an unlocked
Windows x64 desktop with Graphics Tools. Run the following in Groundwork
with a fresh results directory and package cache.

```powershell
$env:CI = 'true'
$env:NUGET_PACKAGES = Join-Path ([IO.Path]::GetTempPath()) ('gw-wp03-' + [guid]::NewGuid())
$results = Join-Path (Get-Location) 'artifacts/wp03-results'
./scripts/Verify-Bootstrap.ps1 -TestResultsDirectory (Join-Path $results 'automated')
if ($LASTEXITCODE -ne 0) { throw 'Groundwork bootstrap verification failed.' }
$d3d12 = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
foreach ($category in 'WindowsDesktopSmoke','D3D12TerrainWarp','D3D12TerrainHardware') {
    dotnet test $d3d12 -c Release --no-build --no-restore --filter "TestCategory=$category" `
        --logger "trx;LogFileName=$category.trx" --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw "$category failed." }
}
dotnet test tests/Groundwork.Platform.Windows.Tests/Groundwork.Platform.Windows.Tests.csproj `
    -c Release --no-build --no-restore --filter 'TestCategory=WindowsDesktopSmoke' `
    --logger 'trx;LogFileName=PlatformWindows.trx' --results-directory $results
if ($LASTEXITCODE -ne 0) { throw 'Windows platform desktop tests failed.' }
```

Run each filter in a separate process so debug configuration precedes
device creation. Verify the active Debug Layer, DRED and live-object counts
in the focused TRX reports. Use the immutable release directory as the
package input to `eng/Test-PackageIntegrity.ps1` and
`eng/Test-IsolatedNuGetConsumer.ps1` with the manifest digest, source commit
and version above. In WYU, follow the
[pinned archive and Windows host instructions](https://github.com/DaveBeusing/wyu/blob/a1ee1f623b9082602d81bc0dbd5bdcbbc912e635/docs/build-bootstrap.md)
and add `-WindowSmoke -D3D12Smoke -TerrainSmoke` to the verified consumer
command. Launch `--terrain` to inspect the two-chunk fixture.

## Integrated changes

The [WP03 contract](architecture/wp03-two-chunk-terrain-contract.md) landed
in [Groundwork #36](https://github.com/DaveBeusing/Groundwork/pull/36).
[Groundwork #38](https://github.com/DaveBeusing/Groundwork/pull/38) implements
the portable renderer handoff, [#37](https://github.com/DaveBeusing/Groundwork/pull/37)
implements chunk storage and meshing, and [#39](https://github.com/DaveBeusing/Groundwork/pull/39)
implements D3D12 rendering and retirement of the pair.
[WYU #6](https://github.com/DaveBeusing/wyu/pull/6) pins the archive and
composes the terrain scene. Both integration clones remained clean after testing.
