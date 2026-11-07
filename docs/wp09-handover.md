# WP09 Windows flight integration handover

The [surface-to-orbit contract](architecture/wp09-surface-to-orbit-contract.md)
defines the reference flight from the edited landing site to orbit and
back to the same site. This handover records the clean Windows
qualification of the flight, renderer transitions and checkpoint recovery.

## Verified baselines

Both repositories were cloned independently from `master` with clean
working trees. The qualification used Windows 11 Enterprise
`10.0.26300` x64, .NET SDK `10.0.403`, fresh NuGet caches and locked
restores.

| Baseline | Exact identity | Evidence |
| --- | --- | --- |
| Groundwork `master` and package source | `ae494c51eead61feb032ccdfdfed1089c04dc04a` | [Windows CI, attempt 1](https://github.com/DaveBeusing/Groundwork/actions/runs/42417550607/attempts/1) |
| WYU `master` | `aec27c0d3b93824594c0732c107f88fb54e56d7d` | [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/42419041418), [WYU PR #20](https://github.com/DaveBeusing/wyu/pull/20) |
| Exact NuGet pin, all ten modules | `[0.1.0-dev.42417550607.1]` | [Immutable Groundwork archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42417550607.1-immutable) |

All 13 freshly downloaded assets matched their published SHA-256
digests. The WYU consumer checked the manifest, individual package
hashes and exact version pins before restoring from a separate feed.

| Provenance file | SHA-256 |
| --- | --- |
| Original producer ZIP | `3ABF542276CA93C84FD510272D0C5147457E895A27DA46B623D04D95D6A7322B` |
| Package manifest | `1FA434525FFF4382D4C7485C8A8F32F1EB68D929A59A773152078B8A901AEAFB` |
| Archive record | `FAED845933CBCC10C3313B3AA61D1CE62FD7EB546737C2B7D7964C98435315F4` |

## Clean Windows results

Both Release builds completed with zero warnings and errors. The
Groundwork Windows gate passed 394 tests across ten suites, including the portable flight and checkpoint
codecs, the Windows NTFS process-interruption fixtures and the mandatory
WARP readback. The WYU gate exercised the portable gameplay rules,
headless startup, controlled-fault shutdown and WP06/WP07 save recovery.

Two WARP and one NVIDIA readback confirmed color and depth across the
near/far/near transitions and shared origin changes. WARP selected
Microsoft Basic Render Driver (`1414:008C`, driver `10.0.26100.9711`).
Four desktop stop cases exercised both adapters. Before installation,
stop held two mesh buffers totaling 2,097,152 bytes. At a delayed
post-Present fence, stop held one buffer of 2,031,616 bytes. All buffers
were released after settlement.

Debug Layer and DRED were active in all seven focused cases. Each
readback recorded ten warnings. Stops before installation recorded
two warnings each and the fence cases three. All seven recorded zero
errors, corruption, discarded messages and live GPU child objects.
Live-object reporting returned HRESULT `0x00000000` and DRED reported
no removal.

### Packaged WYU flight

The guided fixture uses the WP08 recipe with the canonical raised pad
and advances physics at 60 Hz. The test selected NVIDIA RTX PRO 5000
Blackwell Generation Laptop GPU (`10DE:2C38`, driver `32.0.15.9653`).
Debug Layer and DRED were active.

| Checkpoint | Mission tick | Origin revision | Terrain generation |
| --- | ---: | ---: | ---: |
| Departure and near view | 0 | 1 | 1 |
| Far view | 149 | 2 | 3 |
| Start insertion burn | 375 | 3 | 4 |
| Start unpowered coast | 584 | 8 | 9 |
| Complete orbit and begin return | 2348 | 59 | 60 |
| Near view on approach | 2561 | 61 | 63 |
| Start low descent | 2861 | 64 | 66 |
| Land and save generation 2 | 4332 | 65 | 67 |

The coast covered 6.28607695 radians and stayed between 95.616979 and
95.963421 m. Specific orbital energy changed from −209.73850959 to
−209.73851349 m²/s². Angular momentum per unit mass was 1961.88532314 m²/s
at entry and exit to the displayed precision. The landed position was
`(52.98397314502744, 0, 37.10847708241278)` m with identity orientation.

| Trace or checkpoint value | SHA-256 |
| --- | --- |
| Pinned v1 input trace | `7157DA3B7F3DB7C5B303BD4E28AEBE3A86A7D15156BAF431EE1278F3810CD230` |
| Canonical edit overlay fixture | `9B54595844D2993932DDF547EE49A78B1A690E35E0D764BADA742B6BE184FE91` |

Both flights matched all 12 semantic records and the pinned trace.
They peaked at 306 retained patch records. Readiness holds were 167
and 177 ticks. Each flight saved generation 2 and presented frame 441
before native close. Each fresh process reconstructed the edited pad,
verified grounded support and restored the exact saved pose and edit
digest before closing. Each flight recorded 441 debug warnings and
each reload two. All four processes reported zero errors, corruption,
discarded messages and live GPU child objects, with successful
live-object reporting and no device removal.

## Open visual acceptance item

WYU models the pad as raised terrain and shows the current phase in the
window title. The contract also calls for a color and silhouette treatment
that distinguishes the landing pad in the scene. That visual item remains
open. The technical handover records the flight and persistence evidence
separately from visual acceptance.

## Reproduce

On an interactive Windows desktop with Graphics Tools installed, run
the Groundwork gate and the targeted D3D12 tests.

```powershell
$results = Join-Path ([IO.Path]::GetTempPath()) ('gw-wp09-' + [guid]::NewGuid().ToString('N'))
./scripts/Verify-Bootstrap.ps1 -TestResultsDirectory $results
$project = 'tests/Groundwork.Rendering.D3D12.Tests/Groundwork.Rendering.D3D12.Tests.csproj'
dotnet test $project -c Release --no-build --no-restore --filter 'FullyQualifiedName~FlightTerrainReadback|FullyQualifiedName~FlightTransition'
```

From the WYU checkout, download the immutable archive and run the
consumer gate on NVIDIA. The gate launches each flight and reload in
separate processes and compares the pinned trace and checkpoints.

```powershell
$archive = Join-Path ([IO.Path]::GetTempPath()) ('wyu-wp09-' + [guid]::NewGuid().ToString('N'))
gh release download archive-groundwork-0.1.0-dev.42417550607.1-immutable `
  --repo DaveBeusing/Groundwork --dir $archive
./eng/Test-VerifiedGroundworkConsumer.ps1 -ArtifactDirectory $archive `
  -ExpectedManifestSha256 1FA434525FFF4382D4C7485C8A8F32F1EB68D929A59A773152078B8A901AEAFB `
  -ExpectedSourceCommit ae494c51eead61feb032ccdfdfed1089c04dc04a `
  -ExpectedVersion 0.1.0-dev.42417550607.1 -FlightSmoke -SaveSmoke -PrototypeSaveSmoke
```

For an interactive guided run, launch the built WYU client with
`--flight [slot-directory]`. Press Space to start the flight and Esc
to exit. The client saves the craft before departure and after landing.
