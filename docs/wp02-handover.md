# WP02 D3D12 handover

Groundwork and Worlds Yet Unseen (WYU) passed the Windows integration run
from separate clean `master` clones on 2026-11-06. Each clone used a fresh
NuGet package cache and its committed lockfiles. The clones remained clean
after verification. Owner acceptance of this handover follows
[ADR-0007](adr/ADR-0007-solo-review-governance.md).

## Source and package baselines

| Baseline | Exact identity | Evidence |
| --- | --- | --- |
| Groundwork `master` and archive source | `7eaefbfd2bba96618617c9dcfeb6dbcd6a0de828` | [Windows CI push, run 42210884016, attempt 1](https://github.com/DaveBeusing/Groundwork/actions/runs/42210884016/attempts/1) |
| WYU `master` | `53fd17fb73374de1313ff93f0d445caae2470f92` | [Windows consumer CI push, run 42213033157](https://github.com/DaveBeusing/wyu/actions/runs/42213033157) |
| WYU Groundwork pins | `[0.1.0-dev.42210884016.1]` for all eight Groundwork packages | [Immutable release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42210884016.1-immutable) |

| Archive evidence | SHA-256 |
| --- | --- |
| Producer CI artifact ZIP | `7D05C466A385743B48E6F82C8165E8F05A4D69FD6C9EC5BA24DDE264407AAC87` |
| Build manifest | `1A4899932E28C69391BB6C3FA4DBEBDD9D597CFA8FD2597E027355DFE34EF310` |
| Archive record | `5E653B394673FCB7194F2D2221BAB187AFD9E2D81BF0B71182998193CEF6B1CD` |

GitHub confirmed archive immutability. All eleven newly downloaded assets
matched GitHub's sizes and digests. Both consumer gates staged the eight
packages from the pinned manifest and used separate verified feeds. The
WYU restore resolved `TerraFX.Interop.Windows` at `10.0.26100.6` from
NuGet.org and all Groundwork packages from the archive feed. WYU compiles
against package references alone. Keep the archive available while any
consumer pins this version.

## Clean Windows results

Windows 11 Enterprise x64 build `10.0.26300`, Intel Core Ultra 9 285HX
(24 logical processors), 63.46 GiB RAM, .NET SDK `10.0.403`, and an interactive
desktop with Windows Graphics Tools. DXGI selected the NVIDIA RTX PRO 5000
Blackwell Generation Laptop GPU (`10DE:2C38`, driver `32.0.15.9653`). The
system debug layer was `10.0.26100.9549`. Both Release builds completed with
zero build warnings or errors. Groundwork verified the signed TerraFX and DXC
`1.9.2609.5` packages and checked deterministic compilation of both shaders.

| Groundwork gate | Passed | Coverage |
| --- | ---: | --- |
| Automated suites | 176 | Core 35, Diagnostics 33, ECS 14, Jobs 25, Windows 28, Rendering 7, D3D12 14, Simulation 20 |
| Provider desktop suite | 9 | WARP and NVIDIA presentation, native close, resize custody and diagnostics |
| Platform desktop suite | 9 | Native HWND lifecycle, focus, target lifetime and UI wake |
| Focused desktop reruns | 5 | Three NVIDIA cases, one WARP debug-layer case and one WARP DRED case in separate processes by category |
| Package integrity and isolated NuGet consumer | Passed | Eight rejection cases, isolated locked build, stale lock and missing feed rejection |

The automated D3D12 suite passed the mandatory WARP color, transform and
depth readbacks. The NVIDIA cases passed color/depth readback, swap-chain
presentation through resize, minimize and restore, and native close. The
resize fixture stalled an admitted operation and verified that the watchdog
stops Core while GPU and HWND ownership stays held. Releasing the fixture
allowed the native resize to run and renderer, window and Core settlement to
complete. Error `#921` did not recur.

| Active Debug Layer and DRED case | Warnings | Errors / corruption / discarded | Live-object report | Live child objects |
| --- | ---: | --- | --- | ---: |
| Groundwork NVIDIA color and depth readback | 2 | 0 / 0 / 0 | `0x00000000` | 0 |
| Groundwork NVIDIA presentation lifecycle | 3 | 0 / 0 / 0 | `0x00000000` | 0 |
| Groundwork NVIDIA blocked resize | 0 | 0 / 0 / 0 | `0x00000000` | 0 |
| Groundwork WARP debug-layer readback | 2 | 0 / 0 / 0 | `0x00000000` | 0 |
| WYU NVIDIA D3D12 smoke | 2 | 0 / 0 / 0 | `0x00000000` | 0 |

NVIDIA presentation measured 5.279 ms to submit and 39.975 ms from submit
to observed first present, including test polling. Native close reached
settled stop after 157.527 ms. The blocked resize triggered observed Core
stop after 1004.485 ms and settled 71.188 ms after the fixture released the
call. These are single observations on this test host.

WYU passed the headless, controlled-fault, renderer-free desktop and D3D12
smokes. The D3D12 smoke presented cube frame 8 at 944 × 501, sent native
`WM_CLOSE` and verified renderer, HWND and Core release. A separate normal
`--d3d12` launch showed the rotating cube in the original and maximized
window and exited with code 0 after `Alt+F4`. WYU also rejected incompatible
versions, a missing archive package and tampered bytes.

## Coverage boundaries

Controlled removal of a disposable WARP device returned `0x887A0005`.
Both DRED queries returned `0x00000000`, with zero breadcrumb records on
that run. Physical device reset and end-to-end Core stop after actual
provider device loss remain outside this test. Blocked `Present` is covered
at the watchdog layer. The native custody test stalls an admitted resize.
External HWND destruction is tested at the Windows presentation-target layer.

Synthetic timing tests cover 60/20 Hz and bounded catch-up. Separate
renderer admission and ECS tests cover latest-only backpressure and observer
lease release. The WYU renderer receives value frames derived from published
ticks and holds no observer lease. A combined observer-lease/backpressure
fixture is outside the current coverage. The clean integration run found no
blocking defect, so this handover requires no runtime or package changes.

## Reproduce

Use fresh clones at the baseline commits above, PowerShell 7 and an unlocked
Windows x64 desktop. Install Windows Graphics Tools for active debug-layer
evidence. From Groundwork, use a new cache and results directory.

```powershell
$env:CI = 'true'
$env:NUGET_PACKAGES = Join-Path ([IO.Path]::GetTempPath()) ('gw-wp02-' + [guid]::NewGuid())
$results = Join-Path (Get-Location) 'artifacts/wp02-results'
./scripts/Verify-Bootstrap.ps1 -TestResultsDirectory (Join-Path $results 'automated')
$d3d12 = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
foreach ($category in 'D3D12Hardware','D3D12WarpDebugLayer','D3D12WarpDred','WindowsDesktopSmoke') {
    dotnet test $d3d12 -c Release --no-build --no-restore --filter "TestCategory=$category" `
        --logger "trx;LogFileName=$category.trx" --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw "$category failed." }
}
dotnet test tests/Groundwork.Platform.Windows.Tests/Groundwork.Platform.Windows.Tests.csproj `
    -c Release --no-build --no-restore --filter 'TestCategory=WindowsDesktopSmoke' `
    --logger 'trx;LogFileName=PlatformWindows.trx' --results-directory $results
```

Run each D3D12 filter in a separate process so debug configuration precedes
device creation. Check the TRX output for active Debug Layer and zero live
child objects. In the WYU clone, follow the
[pinned archive and Windows host instructions](https://github.com/DaveBeusing/wyu/blob/53fd17fb73374de1313ff93f0d445caae2470f92/docs/build-bootstrap.md)
and add `-WindowSmoke -D3D12Smoke` to the verified consumer command.

## Integrated changes

The [renderer contract](architecture/wp02-d3d12-renderer-contract.md) and
[native toolchain](architecture/wp02-native-toolchain.md) govern the provider.
The implementation landed through [contract #30](https://github.com/DaveBeusing/Groundwork/pull/30),
[renderer foundation #31](https://github.com/DaveBeusing/Groundwork/pull/31),
[toolchain #32](https://github.com/DaveBeusing/Groundwork/pull/32),
[provider #33](https://github.com/DaveBeusing/Groundwork/pull/33), and
[hardware qualification and the post-Present fence #34](https://github.com/DaveBeusing/Groundwork/pull/34).
[WYU #5](https://github.com/DaveBeusing/wyu/pull/5) pins the archive and adds
the opt-in D3D12 host.
