# WP04 editable terrain handover

On 2026-11-06, Groundwork and Worlds Yet Unseen (WYU) passed the Windows
integration gates from separate clean `master` clones. Both checkouts stayed
clean through verification. Owner acceptance follows
[ADR-0007](adr/ADR-0007-solo-review-governance.md).

## Baselines

| Baseline | Exact identity | Evidence |
| --- | --- | --- |
| Groundwork `master` and package source | `49a55eb3447765bca58c6552bf3eebd26f5310ef` | [Windows CI run 42274145071, attempt 1](https://github.com/DaveBeusing/Groundwork/actions/runs/42274145071/attempts/1) |
| WYU `master` | `24473f7fda66710d3f21adf578de03a4df50d704` | [Windows consumer CI run 42278612584](https://github.com/DaveBeusing/wyu/actions/runs/42278612584) |
| WYU package pins | `[0.1.0-dev.42274145071.1]` for all nine Groundwork packages | [Immutable release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42274145071.1-immutable) |

GitHub reported the archive as immutable and the tag resolved to the Groundwork
source commit. All twelve freshly downloaded assets matched the release asset
sizes and SHA-256 digests. The manifest SHA-256 is
`A1F13142A00EDFC29F42AE72A8CA217C598B024C86DA5A83EDD7853AD811EA39`.
The archive record SHA-256 is
`B5C098E4D7E7207ADC476F0033A8CE424594BDF46D372ADB6B9823052C4801CF`.
Keep the archive available while WYU pins this version.

## Clean Windows verification

Windows 11 Enterprise x64 build `10.0.26300`, .NET SDK `10.0.403`,
and system D3D12 Debug Layer `10.0.26100.9549`. Groundwork and WYU
used separate fresh NuGet caches and locked restores. Both Release builds
reported zero build warnings and errors. Groundwork verified deterministic
shader compilation with DXC `1.9.2609.5`.

| Gate | Passed | Coverage |
| --- | ---: | --- |
| Groundwork automated suite | 221 | Includes the portable generation tests, voxel seam meshing, and the mandatory WARP replacement readback |
| Groundwork desktop suites | 35 | 26 D3D12 and 9 Windows platform cases |
| Focused WARP mesh replacement | 7 | Color/depth, stop with a staged pair, redraw and coalescing, packing failure, and commit vetoes |
| Focused NVIDIA mesh replacement | 3 | Color/depth, stop with a staged pair, and redraw and coalescing |
| Focused WARP retirement faults | 4 | Delayed draw and post-Present retirement, failed signal, and confirmed device removal |
| Groundwork package gates | Passed | Nine verified packages, eight integrity rejection cases, isolated locked build, stale lock and missing feed rejection |
| WYU package and host gates | Passed | Nine exact pins, archive rejection checks, locked build, headless, controlled fault, Windows window, D3D12 cube, and terrain smoke |

Focused counts overlap the automated and desktop suites. The Groundwork
readback checked color and nearest depth after the pair changed. On both
adapters, the interrupted replacement held the active and staged GPU buffers
through the Core stop deadline and released both after the test hold ended.
The minimized-window case presented generations 1, 2 and 4 on frame sequence
1, with at most two mesh buffers in custody. On WARP, Core stop, native close
and watchdog expiry each vetoed a staged commit.

The WARP fault tests held observed fence progress at 0 and 1 while the native
fence reached 2. Generation 1 retained its buffer. Two additional WARP cases
confirmed device removal with `0x887A0005`, including a failed post-Present
signal with a fence sentinel and an unobserved successful signal. Both cases
retained the pair until removal confirmation, then released mesh and runtime
ownership. DRED breadcrumb and page-fault queries returned `0x00000000`.
The breadcrumb queries returned zero records.

### Adapter diagnostics

WARP selected Microsoft Basic Render Driver (`1414:008C`, driver
`10.0.26100.9711`). Hardware selected NVIDIA RTX PRO 5000 Blackwell Generation
Laptop GPU (`10DE:2C38`, driver `32.0.15.9653`). Debug Layer and DRED
were active in all 14 focused cases and both WYU D3D12 smoke modes.

| Case | Debug warnings | Errors / corruption / discarded | Live-object report HRESULT | Live GPU child objects |
| --- | ---: | --- | --- | ---: |
| WARP replacement readback, coalescing, interrupted stop | 8, 4, 1 | 0 / 0 / 0 | `0x00000000` | 0 |
| WARP packing failure and three commit vetoes | 0, 1 each | 0 / 0 / 0 | `0x00000000` | 0 |
| WARP delayed retirement (2 cases) | 2 each | 0 / 0 / 0 | `0x00000000` | 0 |
| WARP confirmed removal (2 cases) | See removal evidence above | Severity counts unreported | `0x00000000` | 0 |
| NVIDIA replacement readback, coalescing, interrupted stop | 8, 4, 1 | 0 / 0 / 0 | `0x00000000` | 0 |
| WYU NVIDIA cube and terrain smokes | 2, 10 | 0 / 0 / 0 | `0x00000000` | 0 |

### WYU packaged scene

The NVIDIA terrain smoke installed and presented generations 1, 2, 4 and 5.
Generation 3 had finished meshing but stayed held until generation 4 was
declared. The old pair stayed visible during the hold and generation 3 was
discarded before installation. The native close destroyed the window and
settled the renderer and Core. The first unattended smoke attempts timed out
before logging the first step. The successful rerun activated the exact
test window on the interactive desktop so the smoke could post its inputs.

A separate interactive `--terrain` launch used the same packaged build.
Pressing **T** removed the low chunk's cell `(15,3,4)`, exposing the seam
notch. In an 860 × 485 client-area capture, 3,480 pixels changed within the
notch. Pressing **T** again restored every pixel to baseline, and native
close returned exit code 0. The NVIDIA diagnostic smoke verified generation
ordering and cleanup, and the interactive captures verified visibility.
WARP coverage comes from the Groundwork source tests. WYU's packaged scene
was exercised on NVIDIA.

## Reproduce

Use clean clones at the baselines above, PowerShell 7, and an unlocked Windows
desktop with Graphics Tools. From Groundwork, use a new package cache and
results directory.

```powershell
$env:CI = 'true'
$env:NUGET_PACKAGES = Join-Path ([IO.Path]::GetTempPath()) ('gw-wp04-' + [guid]::NewGuid())
$results = Join-Path (Get-Location) 'artifacts/wp04-results'
./scripts/Verify-Bootstrap.ps1 -TestResultsDirectory (Join-Path $results 'automated')
if ($LASTEXITCODE) { throw 'Groundwork bootstrap failed.' }

$d3d12 = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
foreach ($category in 'D3D12MeshReplacementWarp', 'D3D12MeshReplacementHardware',
    'D3D12MeshRetirementFaults', 'WindowsDesktopSmoke') {
    dotnet test $d3d12 -c Release --no-build --no-restore --filter "TestCategory=$category" `
        --logger "trx;LogFileName=$category.trx" --results-directory $results
    if ($LASTEXITCODE) { throw "$category failed." }
}
dotnet test tests/Groundwork.Platform.Windows.Tests/Groundwork.Platform.Windows.Tests.csproj `
    -c Release --no-build --no-restore --filter 'TestCategory=WindowsDesktopSmoke' `
    --logger 'trx;LogFileName=PlatformWindows.trx' --results-directory $results
if ($LASTEXITCODE) { throw 'Windows platform desktop tests failed.' }
```

Use the freshly downloaded archive as input to Groundwork's
`eng/Test-PackageIntegrity.ps1` and `eng/Test-IsolatedNuGetConsumer.ps1`.
In WYU, follow the
[pinned archive instructions](https://github.com/DaveBeusing/wyu/blob/24473f7fda66710d3f21adf578de03a4df50d704/docs/build-bootstrap.md)
and run the verified consumer with `-WindowSmoke -D3D12Smoke -TerrainSmoke`.
Keep the smoke window in the foreground. If activation is needed, rerun the
same Release assembly with `--terrain-smoke` and activate the
`Worlds Yet Unseen D3D12 Smoke (<pid>)` window when it appears. For the
separate visual check, use `--terrain`, press **T** twice and close the window.

## Integrated changes

The [WP04 contract](architecture/wp04-mesh-replacement-contract.md) landed in
[Groundwork #41](https://github.com/DaveBeusing/Groundwork/pull/41).
[Groundwork #42](https://github.com/DaveBeusing/Groundwork/pull/42) added
portable admission and seam tests, and
[Groundwork #43](https://github.com/DaveBeusing/Groundwork/pull/43) added
D3D12 replacement and the required WARP readback.
[Groundwork #45](https://github.com/DaveBeusing/Groundwork/pull/45) completed
the retirement fault coverage in
[issue #44](https://github.com/DaveBeusing/Groundwork/issues/44).
[WYU #7](https://github.com/DaveBeusing/wyu/pull/7) integrated the pinned
packages and the voxel toggle.

WP05 can build on the bounded mesh replacement path. The next milestone
defines chunk loading, unloading and collision for a walkable region.
