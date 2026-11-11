# WP11 cross-repository handover

**Status: workstation integration verified. Reference qualification and owner acceptance are pending.**

This record covers the [WP11 contract](architecture/wp11-procedural-planet-contract.md) and [G00](https://github.com/DaveBeusing/Groundwork/issues/90) at the exact source commits below.

## Frozen inputs

| Input | Identity | Evidence |
| --- | --- | --- |
| Groundwork master and package source | `3578bcc7143e33c9450f1eeb7e5dc586a0828b25` | [Windows CI, attempt 1](https://github.com/DaveBeusing/Groundwork/actions/runs/43032545158/attempts/1) |
| WYU master | `5df3e45fa2463c826973f224780cb375f2ad42ad` | [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/43079572431) |
| Ten packages | `0.1.0-dev.43032545158.1` | [Immutable prerelease](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43032545158.1-immutable) |
| Manifest SHA-256 | `564B16515EA2312ED841FF4D3FBCC57AD7BD950D5B145CBC8FA787A49CB83E5C` | Exact WYU workflow pin |
| Producer artifact ZIP SHA-256 | `F2E6EA7A422802B8BCD33DFAB5E7C8F66FCA81C66E9B9496F4C5A2ABDD69163A` | Immutable archive record |
| Archive record SHA-256 | `4806B1E512D433F28DEAA9511BBB38C67AA02A7D030461CB2947ED5D1C2DD00A` | Exact WYU workflow pin |
| Installer and manifest SHA-256 | `18D86F574BAB7731F4475EF1E56B14C66EE90029251538D071DFA99722E6E188` and `399044D3963559E2E7A955895FE30E5CA4D80E0080715BAB82EBEBA9AE02DF5D` | Clean WYU master build, 209 payload files verified |
| SDK, bundled runtime, compiler | .NET `10.0.403` / `10.0.14`, Inno Setup `6.7.3` | Locked restore and self-contained Windows x64 Release publish |

Each package below uses the exact version above. The downloaded archive's 13 assets matched their GitHub SHA-256 and size. The consumer independently checked the pinned manifest and package contents, then restored with a fresh external package cache against the committed solution and Windows x64 installer lockfiles.

| Package | SHA-256 |
| --- | --- |
| Groundwork.Core | `916CEA445DBE9982AFDE72A851C307593D016FA4F7137C7B311700FBB62F878C` |
| Groundwork.Diagnostics | `CE884F6F6819223BDC93E3B9F075FC2C6FCBE716608F0BD366A7FBD92F80E614` |
| Groundwork.Ecs | `67B071F6E904ACC67DBDE2C5514A87FA1AFDBABD72427AD569757013145D987A` |
| Groundwork.Jobs | `D67B5CF4077A5F54F4A9260AEDF17CCEFB133B2061162E5610B70C9741E3607B` |
| Groundwork.Persistence | `D897E70EED26E1A8D173658EF583C959A8A10C17292EE82A4DA34AD30683746D` |
| Groundwork.Platform.Windows | `B722CF3BD8355161366984FC69BBD56D3DEC0BDF761BE391B1B3CEC259A653DD` |
| Groundwork.Rendering | `8D2D735ADFB4B7CC2F8356F6DF74D15CCA0D40DE06AF45601D1653B12417134D` |
| Groundwork.Rendering.D3D12 | `B76652690F77CE766C25CC26700D32407CC518AC65449BC134732576F8217230` |
| Groundwork.Simulation | `0D8666014BD41032C2A12A3372AA766C9EFA839370FD7F26C6ED846E834CB8B2` |
| Groundwork.Voxels | `7C84B2A9A9B935741E8E9F0A9B733A3FB4AC48DAE850387BF74A8DF8577AE858` |

## Workstation results

Windows 11 Enterprise x64 build 26300, Intel Core Ultra 9 285HX (24 cores), 68,137,205,760 bytes RAM. NVIDIA RTX PRO 5000 Blackwell Laptop driver `32.0.15.9653` and Microsoft WARP driver `10.0.26100.9711`. The workstation remained connected. The installed rehearsal used a non-elevated token and isolated world data on the existing Windows account.

| Check | Result |
| --- | --- |
| Groundwork locked Release build and regression | 503 tests passed. The WARP volume transition and Cube-Sphere seam readbacks verified depth/color and rebase. |
| Groundwork D3D12 desktop | Four passed. WARP and NVIDIA each passed `VolumeReadbackWithDebugLayerAndDred` and `VolumePresentRetainsGenerationUntilFenceAndRejectsStaleView`. Both enabled Debug Layer and DRED. The readback and fence cases reported 8 and 5 warnings respectively per adapter, zero errors and zero live child objects. |
| WYU verified consumer | Seven gameplay tests, headless lifecycle and legacy save recovery passed. |
| WYU exploration | Seeds 42 and 20261111 matched their spawn and route checkpoints across WARP and NVIDIA. Each run completed 480 ticks, promoted its destination brick to full collision detail, rebased, minimized/restored and released the window. |
| WYU edit and NTFS recovery | The WARP test removed terrain at a brick edge, added ochre stone, shifted coverage away and back, and reopened generation 2 / terrain revision 3. After terminating the writer with a blocked rename, both committed members retained their hashes. Rebuilt overlay SHA-256 `122fa2b5b3ff88b58394a9e533576f9e49f624c8212bddfea714bb64c0579786`, material, collision and player pose matched. The incompatible fixture stayed byte-identical. |
| WYU flight and continuation | Seed 42 produced the same semantic trace on WARP and NVIDIA. The edited pad moved 1.8864 m, orbit drift peaked at 1.6443 m, and touchdown occurred at tick 20664. Fresh processes revalidated the local contact and overlay. Abort after the first far frame preserved generation 1. |
| Installed WYU | Per-user install, Start menu and bundled runtime, reinstall repair, and uninstall with data preservation passed. On NVIDIA, the ordinary controls created seed 42, crossed an origin change, edited terrain, saved and restarted, then launched, orbited, landed and continued generation 2. Invalid seed, unavailable edit target, exclusive writer contention, incompatible and damaged slots, and visible graphics/streaming dialogs passed. Eleven screenshots and bounded logs were retained. 502 walking samples reported p50 18.2 ms and p95 28.1 ms. |

The scripted desktop runs checked Debug Layer and DRED. WYU renderer retirement reported zero errors, corruption, discarded messages and live GPU child objects. Debug warnings ranged from 1 to 446 in the WYU scripted runs. The renderer snapshot provides counts. The installed walking overlay measured loop cadence, which leaves the engine's production frame and resource budgets open.

The SHA-256 digests of the matched semantic checkpoints below use UTF-8 with LF separators and no trailing newline.

| Trace | SHA-256 |
| --- | --- |
| Exploration, seed 42 | `BC244ACE029C3A641EB46188D545C92577CBF323DE375FE300F6CB2BA090273F` |
| Exploration, seed 20261111 | `9A1EF3ACD1281FF2E8665A01485BA497E7DE1B7C7AB81199470F7B6054E2B24B` |
| Flight, seed 42 | `AE1017CE9D0CD3681045F4FAE340B7CDF3B6C1A6509DDB8D0102753F904601B7` |

## Contract coverage

| Requirement | Evidence at this baseline |
| --- | --- |
| R01 Identity and frames | `PlanetLatticeTests` covers canonical addresses. The WARP seam fixture and the WYU walking route exercise rebases. |
| R02 Generation | `PlanetDensityFieldTests` and `PlanetJobDeterminismTests` cover golden samples and job order. WYU also checks three reference seeds. |
| R03 Sparse volumes | `PlanetBrickStoreTests` covers material and density ownership, limits and leases. |
| R04 Geometry | `PlanetVolumeMesherTests`, `VolumeMeshPlanTests` and `WarpCubeSphereTransitionsMatchCollisionAcrossRebase` cover mixed LOD and chart boundaries. |
| R05 Streaming | `VolumeStreamingTests` and `VolumeStreamingFaultTests` cover admission, publication and fault coherence. The production pressure route remains in Q01/Q02. |
| R06 Collision and movement | `VolumeCollisionTests` and the WYU walk, sweep and flight fixtures exercise committed support and missing-coverage holds. |
| R07 Editing and persistence | `VolumeEditTests`, `VolumeShapeRestoreTests`, `VolumeCheckpointTests` and the WYU edit/restart script cover terrain revisions and NTFS recovery. |
| R08 Presentation and game | The WARP readbacks, native desktop passes and installed WYU route exercise the complete consumer path. |
| R09 Diagnostics and validation | The CI and workstation evidence below the production gate is available. Reference-class measurements, accepted limits and owner sign-off remain open. |

## Work packages and open decisions

- Contract and C00 are merged. Groundwork A01–A03, B01, C01–C03 and D01–D02 are implemented in [PRs #91–#99](https://github.com/DaveBeusing/Groundwork/pulls?q=is%3Apr+is%3Amerged+WP11). Q01 instrumentation and Q02 regression work landed in [PRs #100–#106](https://github.com/DaveBeusing/Groundwork/pulls?q=is%3Apr+is%3Amerged+WP11).
- WYU W01–W05 are on master through [#35](https://github.com/DaveBeusing/wyu/pull/35), [#36](https://github.com/DaveBeusing/wyu/pull/36), [#37](https://github.com/DaveBeusing/wyu/pull/37), [#38](https://github.com/DaveBeusing/wyu/pull/38) and [#39](https://github.com/DaveBeusing/wyu/pull/39). Their issue records [#28–#32](https://github.com/DaveBeusing/wyu/issues?q=is%3Aissue+WP11) remain open.
- The planet world selector uses the WP11 volume checkpoint namespace. The legacy alpha mission and its save namespace remain separate. Existing legacy flight fixtures remain for their route. Incompatible WP11 data is protected, and a new world uses a distinct slot.
- [Q01 #88](https://github.com/DaveBeusing/Groundwork/issues/88) and [Q02 #89](https://github.com/DaveBeusing/Groundwork/issues/89) stay open. The [combined-pressure workstation report](architecture/wp11-q01-workstation-capacity.md) records the protected build and GPU ownership measurements. Freeze production resource and latency limits only after the 8-core/32-GiB-class reference run and the Q02 route against those limits.
- The clean offline rehearsal on a newly provisioned Windows 11 system remains open. The installed checks here use the self-contained artifact on the workstation.
- [G00 #90](https://github.com/DaveBeusing/Groundwork/issues/90) remains open. Closure still requires the final qualification evidence, four independent specialist reviews followed by integration review, resolution of P0/P1 findings, and owner acceptance of the exact reviewed heads and artifact provenance.

## Reproduction

Use clean checkouts at the two commits above on an interactive Windows desktop with Graphics Tools installed. Set the evidence directory outside both checkouts and supply the verified Inno Setup compiler path.

~~~powershell
$evidence = Join-Path $env:TEMP ('wp11-g00-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null

# Groundwork checkout
./scripts/Verify-Bootstrap.ps1 -TestResultsDirectory (Join-Path $evidence 'groundwork-trx')
dotnet test tests/Groundwork.Rendering.D3D12.Tests -c Release --no-build --no-restore --filter 'FullyQualifiedName~VolumeReadbackWithDebugLayerAndDred|FullyQualifiedName~VolumePresentRetainsGenerationUntilFenceAndRejectsStaleView' --logger 'trx;LogFileName=desktop.trx' --results-directory (Join-Path $evidence 'groundwork-desktop')

# WYU checkout
$archive = Join-Path $evidence 'groundwork-archive'
$installer = Join-Path $evidence 'installer'
gh release download archive-groundwork-0.1.0-dev.43032545158.1-immutable --repo DaveBeusing/Groundwork --dir $archive
$verify = `{
    ArtifactDirectory = $archive
    ExpectedManifestSha256 = '564B16515EA2312ED841FF4D3FBCC57AD7BD950D5B145CBC8FA787A49CB83E5C'
    ExpectedSourceCommit = '3578bcc7143e33c9450f1eeb7e5dc586a0828b25'
    ExpectedVersion = '0.1.0-dev.43032545158.1'
    InstallerCompiler = $verifiedCompiler
    InstallerOutputDirectory = $installer
    SaveSmoke = $true
    PrototypeSaveSmoke = $true
}
./eng/Test-VerifiedGroundworkConsumer.ps1 @verify
./eng/Test-VolumeExploration.ps1
./eng/Test-VolumeSave.ps1
./eng/Test-ProceduralFlight.ps1
./eng/Test-OfflineInstaller.ps1 -InstallerDirectory $installer -ExpectedManifestSha256 $approvedInstallerManifestHash -PlanetPlaytest
~~~

The last command uses the independently recorded digest of the installer manifest under test. WYU's installed test writes its screenshots and bounded logs to the temporary directory it prints.
