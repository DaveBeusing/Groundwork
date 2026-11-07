# WP07 playable prototype handover

The [prototype contract](architecture/wp07-playable-prototype-contract.md)
defines acceptance for the Windows rescue beacon prototype. Owner acceptance
of this handover follows [ADR-0007](adr/ADR-0007-solo-review-governance.md).

## Playtests

| Playtest | Outcome | Evidence |
| --- | --- | --- |
| 1 | Passed, reported by the project owner | The owner tested the build with mouse look, colored deposits, and the new build-site guidance. The preceding trial had exposed an unclear transition from mining to building. The follow-up build identifies the cyan pad after the third deposit and places a floating diamond at the interaction point. |
| 2 | Passed, reported by the project owner | The owner marked the second playtest successful. No separate session notes, build identity, or step-by-step observations were supplied. |

The playtest outcomes are owner reports. Automated route and recovery
results are recorded separately below.

## Technical evidence

| Baseline | Exact identity | Evidence |
| --- | --- | --- |
| Groundwork `master` and package source | `5f3f7bd5c7e2e59cef898d4bea8842afa123e905` | [Groundwork Windows CI](https://github.com/DaveBeusing/Groundwork/actions/runs/42367247347/attempts/1) |
| WYU `master` including the guidance merge | `7cf94149ff64c25a6511ae6238d412b7eff63eba` | [WYU Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/42396133724) |
| WYU guidance candidate | `39b40182bed0b856735682ae6ce85c6d63d43fdc` | [WYU PR #16](https://github.com/DaveBeusing/wyu/pull/16), [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/42393399404) |
| WYU package pins | `[0.1.0-dev.42367247347.1]` for all ten Groundwork packages | [Immutable package archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42367247347.1-immutable) |

The 13 fresh release assets matched their published sizes and SHA-256 digests.

| Provenance file | SHA-256 |
| --- | --- |
| Original producer ZIP | `BA7CD711E192B10A43DDEA383B7344CF74D9CCFB761CCCDFA2854DD2CB97FFA3` |
| Package manifest | `0BBD6B3C94BF6EAB3E7D9D798374D50EA962BA1D9585BE9670F29DC8DA3D26A5` |
| Archive record | `68CC7736814E8E29058FCDA889632609DFB076B0D2CEB74C29812F82C28DFB76` |

The guidance candidate passed the Windows desktop route on NVIDIA with
Debug Layer and DRED. The route checked the objective transition, the
diamond in the presented mesh generation, collision at the anchor, module
placement, signal activation, and native shutdown. Headless WP07 tests
checked restart after mining, partial construction and activation, including
real process interruption before and after atomic publication on local NTFS.
Groundwork separately passed the WARP and NVIDIA color/depth readbacks.

## Clean Windows checkout results

Both repositories were cloned separately from the tested `master` commits.
The builds used Windows 11 Enterprise `10.0.26300`, .NET SDK `10.0.403`,
fresh NuGet caches and locked restores.
Both Release builds completed with zero warnings and errors.

Groundwork passed all 336 tests in its Windows gate, including 81 Persistence,
26 portable Rendering and 24 non-desktop D3D12 cases. The D3D12 cases include
the mandatory WARP vertex-color and depth readback.
The separate region qualification runs passed four WARP and three NVIDIA
cases, including vertex colors with near-depth occlusion and GPU buffer
release on shutdown. The focused WARP cases overlap the Windows gate.

WYU passed the four portable gameplay tests, host startup and controlled-fault
shutdown, and the WP06 and WP07 recovery checks. On local NTFS, killing the
WP07 writer before publication preserved the older checkpoint byte for byte.
Killing it after publication left the complete new checkpoint available.
Fresh processes rebuilt the mined terrain and the partially built and
activated beacon. Invalid and unsupported checkpoints were exercised along
with an immutable capture held while later progress was made.

The desktop route ran twice on NVIDIA RTX PRO 5000 Blackwell Generation
Laptop GPU (`10DE:2C38`, driver `32.0.15.9653`) with Debug Layer and DRED.
All 14 gameplay, guidance and seam checkpoints matched. Each run completed
1,288 movement ticks and crossed X planes `-16, 0, 16, 16, 0, -16`.
Readiness holds were 140 and 138. Both runs peaked at 30 snapshots,
two loads, one mesh reservation and 16,302,816 accounted CPU mesh bytes.

After the third deposit, the objective pointed back to the cyan pad and
named the floating diamond. The route checked the marker and clear collision
at its anchor during building and activation. Both runs installed three
modules, sent the signal and closed the native window. Each live-object
report recorded 736 warnings and zero errors, corruption, discarded
messages or live child objects. Both reports returned HRESULT `0x00000000`.

## Reproduce

From the tested Groundwork checkout, run the normal Windows gate. Run the
focused desktop categories in separate processes with Graphics Tools
installed.

```powershell
./scripts/Verify-Bootstrap.ps1 -TestResultsDirectory $results
$project = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
dotnet test $project -c Release --no-build --no-restore --filter 'TestCategory=D3D12RegionReplacementWarp'
dotnet test $project -c Release --no-build --no-restore --filter 'TestCategory=D3D12RegionReplacementHardware'
```

From the tested WYU checkout, download the immutable release assets to
`$archive`. Run the verified consumer from a Windows desktop with Graphics
Tools and an NVIDIA adapter.

```powershell
& ./eng/Test-VerifiedGroundworkConsumer.ps1 -ArtifactDirectory $archive `
  -ExpectedManifestSha256 0BBD6B3C94BF6EAB3E7D9D798374D50EA962BA1D9585BE9670F29DC8DA3D26A5 `
  -ExpectedSourceCommit 5f3f7bd5c7e2e59cef898d4bea8842afa123e905 `
  -ExpectedVersion 0.1.0-dev.42367247347.1 -SaveSmoke -PrototypeSaveSmoke -PrototypeSmoke
```
