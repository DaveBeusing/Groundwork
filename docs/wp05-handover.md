# WP05 walkable region handover

On 2026-11-06, Groundwork and Worlds Yet Unseen (WYU) passed the WP05 Windows
integration checks from separate clean `master` clones with fresh package
caches and locked restores. Owner acceptance follows
[ADR-0007](adr/ADR-0007-solo-review-governance.md).

## Baselines and package provenance

| Baseline | Exact identity | Evidence |
| --- | --- | --- |
| Groundwork `master` and package source | `269ab9e1128c15f5764f0dded499e8e02db4110c` | [Windows CI run 42292324031, attempt 1](https://github.com/DaveBeusing/Groundwork/actions/runs/42292324031/attempts/1) |
| WYU `master` | `9b43829e61e62fab4eb2e38820f1595cdcd1724c` | [Windows consumer CI run 42295643169](https://github.com/DaveBeusing/wyu/actions/runs/42295643169) |
| WYU exact pins | `[0.1.0-dev.42292324031.1]` for all nine Groundwork packages | [Immutable release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42292324031.1-immutable) |

| Provenance file | SHA-256 |
| --- | --- |
| Producer CI artifact ZIP | `24BC64FE2EF66337C910A83846B2BA1C834901F32833E88432E6EDDEF71A7B57` |
| Manifest | `4294F71A1B42B9D6ED132B203D6553B673AB8B62C23FB23E37A16B77E03EA43E` |
| Archive record | `22957F7AA16EA11B4C3060E9DFEEBE63FC510B6DE6D0FB906DA1DF89C5515F48` |
| Apache-2.0 license | `B1D2870F1A00E4D7F56576E5F0870CBA109E041F8AF66866CF5B499478E654E7` |

All twelve fresh release downloads matched the immutable release's asset
sizes and digests. The nine packages and manifest matched the original
producer ZIP. Groundwork passed eight package-integrity rejection cases and
the isolated locked consumer, including stale lock and missing feed rejection.
WYU passed its version, missing package and altered package rejection cases.

## Groundwork on Windows

Windows 11 Enterprise x64 build `10.0.26300`, .NET SDK `10.0.403`,
and system D3D12 Debug Layer `10.0.26100.9549`. Both Release builds
completed with zero build warnings and errors. Groundwork verified
deterministic shader compilation with DXC `1.9.2609.5`.

| Gate | Passed | Coverage |
| --- | ---: | --- |
| Automated Groundwork suite | 253 | Includes 50 Voxels cases, 25 portable renderer cases and all six mandatory WARP readbacks |
| Desktop suites | 39 | 30 D3D12 and 9 Windows platform cases |
| Focused WARP region replacement | 3 | Two color/depth readbacks and stop with a privately staged region |
| Focused NVIDIA region replacement | 2 | Color/depth readback and stop with a privately staged region |
| Focused WARP retirement faults | 4 | Delayed retirement, failed signal and confirmed device removal |

The desktop total combines 26 D3D12 regressions, four strict WP05
qualification cases and nine platform cases. In a single unfiltered
desktop process, the four strict cases reported an inactive Debug Layer.
All four passed with diagnostics active in the fresh, focused processes.
The commands below separate those cases from the other desktop tests.
Focused counts overlap the automated and desktop totals. The portable route
ran twice with identical checkpoints and traces. The pressure fixture filled
all 50 snapshot slots and exercised both load reservations. Other fixtures
verified stale-result rejection, failed publication, and custody through stop.
The three fake-renderer cases verified the instance bound, whole-region
replacement, generation ordering, and stop veto.

The native readbacks used all nine instance slots, including empty slots.
They verified color and nearest depth on both sides of a retained seam and
verified that leaving geometry cleared. On each adapter, the native window
presented regions 1 and 2 using frame sequence 1. Core stopped while region 3
was privately staged. Region 3 never installed or presented. Both buffers
held 131,072 aligned bytes through the stop deadline and reached zero
buffers and bytes after late settlement.

WARP selected Microsoft Basic Render Driver (`1414:008C`, driver
`10.0.26100.9711`). NVIDIA selected RTX PRO 5000 Blackwell Generation Laptop
GPU (`10DE:2C38`, driver `32.0.15.9653`). Debug Layer and DRED were active
in all five focused region runs and both WYU route runs.

| Case | Debug warnings | Errors / corruption / discarded | Live-object report HRESULT | Live GPU child objects |
| --- | ---: | --- | --- | ---: |
| WARP region readbacks | 4 each | 0 / 0 / 0 | `0x00000000` | 0 |
| WARP region stop | 2 | 0 / 0 / 0 | `0x00000000` | 0 |
| NVIDIA region readback | 4 | 0 / 0 / 0 | `0x00000000` | 0 |
| NVIDIA region stop | 2 | 0 / 0 / 0 | `0x00000000` | 0 |
| WYU NVIDIA route, verified consumer and visual repeat | 890, 878 | 0 / 0 / 0 | `0x00000000` | 0 |

## WYU route from the archived packages

Both NVIDIA runs used seed `0x57505535` and generator version `1`. They
completed 914 motion ticks, crossed X planes `-16, 0, 16, 32` in both
directions, stayed grounded at Y 4, and clamped 14 wall-contact commands.
Both runs presented generations 1 through 9. The west chunk was unloaded
and reloaded with a new load identity (12 to 63) and matching fingerprint
`0x29A35A922878BCA5`. The east chunk left coverage on return. The second run's
window captures showed the terrain and the wall approach.

| Checkpoint | Motion ticks | Position in voxels |
| --- | ---: | --- |
| Outbound | 424 | `(32.5, 4, 7.5)` |
| Wall | 448 | `(33.75, 4, 7.5)` |
| Along the wall | 480 | `(33.75, 4, 11.5)` |
| Return | 914 | `(-20.5, 4, 11.5)` |

| Run | Readiness holds | Observed present intervals (samples, min / mean / max ms) | Peak working set (bytes) | Last frame before native close |
| --- | ---: | --- | ---: | ---: |
| Verified consumer | 42 | 884, 2.071 / 17.734 / 117.488 | 196,214,784 | 959 |
| Visual repeat | 37 | 873, 12.688 / 17.993 / 49.532 | 196,648,960 | 953 |

Both runs reached 30 snapshot slots, two reserved loads, one mesh slot, and
16,290,720 accounted CPU mesh bytes including reservations. Present intervals
are host observations from `Stopwatch`. They measure observed cadence in
these runs. Native close released the HWND, renderer, region workers and
Core resources. The pinned provider exposes severity totals in its final
debug summary. The individual warning IDs are unavailable in these results.

The verified consumer also passed headless startup and shutdown, controlled
fault shutdown, the renderer-free window smoke and the D3D12 cube smoke.
The separate WP04 terrain smoke passed with the same WP05 package pins.
WARP verification used the Groundwork provider. The packaged WYU route
used NVIDIA.

## Reproduce

Use clean checkouts of the commits above, PowerShell 7 and an unlocked
Windows x64 desktop with Graphics Tools. In Groundwork use a new cache
and results directory.

```powershell
$env:CI = 'true'
$env:NUGET_PACKAGES = Join-Path ([IO.Path]::GetTempPath()) ('gw-wp05-' + [guid]::NewGuid())
$results = Join-Path (Get-Location) 'artifacts/wp05-results'
./scripts/Verify-Bootstrap.ps1 -TestResultsDirectory (Join-Path $results 'automated')
if ($LASTEXITCODE) { throw 'Groundwork bootstrap failed.' }

$d3d12 = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
foreach ($category in 'D3D12RegionReplacementWarp', 'D3D12RegionReplacementHardware',
    'D3D12MeshRetirementFaults') {
    dotnet test $d3d12 -c Release --no-build --no-restore --filter "TestCategory=$category" `
        --logger "trx;LogFileName=$category.trx" --results-directory $results
    if ($LASTEXITCODE) { throw "$category failed." }
}
$desktop = 'TestCategory=WindowsDesktopSmoke&TestCategory!=D3D12RegionReplacementWarp&TestCategory!=D3D12RegionReplacementHardware'
dotnet test $d3d12 -c Release --no-build --no-restore --filter $desktop `
    --logger 'trx;LogFileName=DesktopWithoutRegionDiagnostics.trx' --results-directory $results
if ($LASTEXITCODE) { throw 'D3D12 desktop regressions failed.' }
dotnet test tests/Groundwork.Platform.Windows.Tests/Groundwork.Platform.Windows.Tests.csproj `
    -c Release --no-build --no-restore --filter 'TestCategory=WindowsDesktopSmoke' `
    --logger 'trx;LogFileName=PlatformWindows.trx' --results-directory $results
if ($LASTEXITCODE) { throw 'Windows platform desktop tests failed.' }
```

Verify that the WP05 WARP readback appears as passed in the automated TRX.
Use the fresh archive as input to `eng/Test-PackageIntegrity.ps1` and
`eng/Test-IsolatedNuGetConsumer.ps1`. In WYU, follow the
[pinned archive instructions](https://github.com/DaveBeusing/wyu/blob/9b43829e61e62fab4eb2e38820f1595cdcd1724c/docs/build-bootstrap.md)
and add `-WindowSmoke -D3D12Smoke -WalkableSmoke` to the verified consumer
command. Launch the same Release assembly with `--walkable-smoke` to repeat
the visible route. Both builds used clean source at the recorded commits.
Only the Groundwork handover and test instructions were edited for review.

## Integrated changes

[Groundwork #47](https://github.com/DaveBeusing/Groundwork/pull/47) established
the [WP05 contract](architecture/wp05-walkable-region-contract.md).
[Groundwork #48](https://github.com/DaveBeusing/Groundwork/pull/48) implemented
portable loading, meshing, unloading and collision.
[Groundwork #50](https://github.com/DaveBeusing/Groundwork/pull/50) integrated
region replacement into D3D12.
[WYU #9](https://github.com/DaveBeusing/wyu/pull/9) pinned the archive and
integrated the seeded walkable region.
