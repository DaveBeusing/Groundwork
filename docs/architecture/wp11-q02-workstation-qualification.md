# WP11-Q02 workstation qualification

Groundwork #89 tracks cross-module failure and recovery qualification under the WP11 contract. The original #173 workstation qualification started from `master` at `6dd1320f6f4e009384f315887cbd74b1c615fac9`. The evidence below covers the merged edited-Present fault, the archived #88 budget acceptance, the installed Q02 pressure process and targeted source-built fault and exhaustion probes. The pinned Q02 installed save and restore passed on WARP. **#89 remains open.** The earlier installed handover records a different package composition. [Issue #89](https://github.com/DaveBeusing/Groundwork/issues/89) · [WP11 contract](wp11-procedural-planet-contract.md) · [Earlier handover](../wp11-handover.md)

## R01–R09 test matrix

| Requirement | Verified evidence | Remaining qualification |
| --- | --- | --- |
| R01 Identity, descriptors and frames | `PlanetLatticeTests` checks canonical lattice addresses and overflow boundaries. `ExtremeRadiusBricksKeepGoldenBytesAcrossJobOrders` includes the 6,400,000 m case. `WarpCubeSphereTransitionsMatchCollisionAcrossRebase` joins canonical geometry, collision and WARP pixels through rebase. The installed Q02 pressure checkpoints identify generation, view, renderer epoch and matching frame through cutover. The fresh installed restore matched durable world, slot, lineage and terrain revision. | Installed restore coverage is one isolated world on the pinned WARP route. |
| R02 Deterministic generation | `PlanetDensityFieldTests` checks fixed samples and distinct seeds. `ExtremeRadiusBricksKeepGoldenBytesAcrossJobOrders` checks three fixed seeds at two radii, including serial and reordered Jobs execution, against brick goldens. The #88 archived audit matched 15,360 brick fingerprints and 1,920 mesh entries to the pinned natural baseline. The installed restore verified the seed-42 slot and rebuilt the exact overlay under pinned packages. | Multi-seed determinism is covered by the source-built fixtures. |
| R03 Sparse volumetric representation | `PlanetBrickStoreTests` checks quotas and lease accounting. `ShapeEditAndNtfsRestorePreserveMeshAndCollision` exercises enclosed cavity, overhang and detached solid through actual volume data, editing and restore. The #88 slot audit covered all six retirement slots and 396 simultaneous lease slots under the configured limit of 400. The installed Q02 process stayed within all nine measured reservation dimensions. | The installed pressure archive lacks candidate, journal and overlay high-water counts. |
| R04 Meshing and seams | `PlanetVolumeMesherTests`, `VolumeMeshPlanTests` and the WARP chart and cavity readbacks cover transitions, seam/corner geometry and provider output after rebase. `VolumeReadbackWithDebugLayerAndDred` repeats cavity readback on WARP and NVIDIA. Archived #88 cold and natural runs matched the comparable geometry fingerprints. The installed restore matched cut and fill density, material and both collision probes. | Whole-mesh fingerprints remain covered by the source-built fixtures. |
| R05 Streaming and bounded scheduling | `Q02GeneratorDelayAndQuotaPressurePreservePublishedTerrain` and `Q02CompletedOldMeshCannotReplaceNewerPublishedTerrain` verify held/failed generators, quota denial and stale candidates. `VolumeUploadRemainsBlockedWhileProtectedTerrainWorkRunsOnFullCpuQueue` and `ProtectedEditedGenerationPresentsBetweenOptionalVisualQuanta` cover isolated pressure. The installed Q02 process sustained the same speculative jobs through consuming `BeginTick` across all three protected paths. Separate native and managed probes cover independent GPU overlap and production-vector FirstEdit credit exhaustion. | The installed process and the independent GPU overlap fixture provide separate evidence. Simultaneous independent copy pressure during the installed route was unmeasured. |
| R06 Movement and collision | `VolumeCollisionTests` covers finite sweeps and holds on missing coverage or exhausted work. The WARP seam fixture checks matching collision and visible geometry. `VolumePresentRetainsGenerationUntilFenceAndRejectsStaleView`, the edited-Present fault, the native credit test and the installed Q02 pressure route exercise matched receipt gating. The installed restore matched the durable pose, tick and sampled collision heights. | The installed restore uses two fixed collision rays. |
| R07 Editing and persistence | `VolumeEditTests` and `VolumeShapeRestoreTests` cover sealed edit heads and restored geometry and collision. `NtfsChildProcessInterruptionPreservesTheDurableCut`, its V2/V3 companions and `NtfsIntentCloseWaitRetriesSharingViolationsAndKeepsOtherFailuresVisible` exercise real abrupt exits and NTFS sharing behavior using disposable slots. `DecodedDurableCheckpointRebuildsTheEditedCollisionAndRestoredPose` checks the durable cut after decode. The FirstEdit probes establish atomic rejection at admission and publication. The installed Q02 restart restored the edited generation 2 with unchanged durable files and verified intent retirement. | Abrupt interruption evidence remains in the separate NTFS subprocess fixtures. |
| R08 Render and game composition | `VolumeReadbackWithDebugLayerAndDred`, `VolumeFaultRetainsRegistrationUntilConfirmedRemoval` and `InvalidatedRendererCannotCommitAfterLossOrReopenItsEpoch` cover native output, registration custody and epoch closure. `Int01ProtectedEditedPresentSurvivesSuccessiveStalePropAndMaterialCuts` verifies the first edited Present after two optional binding invalidations. `Int01SavedEditRestoresCanonicalGeometryAndTexturedPixels` joins restored data to renderer pixels. PR #180 and the native credit probe add edited Present and recovery evidence. The locked installed consumer passed the Q02 pressure route and presented the restored terrain on WARP. | Installed edited-world restart on NVIDIA and the broader Continue journey remain separate. Product cutover and owner acceptance remain open. |
| R09 Diagnostics and validation | The four #88 archives establish the accepted resource, latency and retry limits. The installed Q02 pressure process met the observed limits. Both installed WARP restore phases reported zero live GPU child objects. The workstation tests preserve adapter, driver, TRX and diagnostic evidence. `WarpRetirementFaultKeepsTerminalEvidenceWithOptionalSink` and `DiagnosticDispatcherTests` cover saturated and failing optional sinks. | The reference owner waived the additional Q02 pressure run on the #88 reference computer. The restore results are scoped to persistence and renderer settlement. |

## Scope of the failure cases

The native tests deliberately inject controlled failure observations and use the real D3D12 queue for the surrounding draws and copies. The fence sentinel, a failed signal and confirmed device removal have distinct custody outcomes. The healthy mixed edit test and the held-upload/recovery test are separate fixture runs. The subsequent installed pressure and independent GPU overlap evidence cover different C00-4 scenarios.

Run the workstation cases in an interactive Windows x64 Release session with Graphics Tools, a hardware NVIDIA adapter and WARP available. Adapter-parameterized cases run on both WARP and NVIDIA. The seam and injected removal cases use WARP, with the removal data rows selecting the failure point. Run filtered groups sequentially in fresh test processes. The native harness logs actual DXGI adapter and driver and checks shutdown, with debug messages and live-object counts where the Debug Layer is active. NTFS cases terminate their own test child process and use isolated test directories.

`Q02WarpFailedMaximumTextureSignalKeepsProtectedEditUnpublishedUntilSafeSettlement` adds a windowed WARP fault while the renderer holds a drawable coarse texture, a staged prop cell and a sealed edit with its CPU reservation. A real 2 MiB copy executes before the test returns a failed signal. The test observes the pending destination and staging backing, both still charged after the injected completion sentinel. It requires terminal stop, cancellation of the undispatched edit, the same committed Core root and stamp and no edit receipt or texture installation. It then requests actual removal of its disposable WARP device and verifies settlement of tracked texture, staging, terrain and prop allocations. PR #180 adds the distinct case that loses the post-Present signal of a newly edited generation under mixed visual pressure.

The original results below cover source builds. The earlier WYU handover, the #88 reference archives and the Q02 installation each retain their package and test identities.


## Original workstation results

Release with locked restore on Windows x64 build 26300, .NET SDK 10.0.403, Intel Core Ultra 9 285HX (24 cores) and 68,137,205,760 bytes of RAM. The disposable checkpoint directories were on NTFS. Native tests reported NVIDIA RTX PRO 5000 Blackwell Laptop driver 32.0.15.9653 and WARP 10.0.26100.9711.

| Lane | Passed cases |
| --- | ---: |
| Voxels | 88 |
| Persistence and real NTFS child-process interruption | 11 |
| Simulation recovery | 2 |
| Diagnostic dispatcher | 10 |
| D3D12 combined optional copy failure | 1 |
| D3D12 seams and native volume | 6 |
| D3D12 held pressure and visual arbitration | 6 |
| D3D12 retirement and terminal custody | 16 |
| D3D12 recovery and texture admission | 19 |
| D3D12 appearance, protected edit and reload | 8 |
| Total | 167 |

The managed tests ran against the implementation and tests in the stated master baseline. The native tests ran with test source `e8983320c6e3222b72a47414b90c31941918bb33`. Only documentation remained in progress at that checkout. The source deltas add and harden the new fixture and document coverage. All 167 selected cases passed across 28 TRX files in the recorded run.

The combined failure recorded unassigned fence 8, prior generation 1 and pending protected generation 2. The observed signal error was `0x80004005`, followed by confirmed removal `0x887A0005`. The sentinel alone left 2,097,152 bytes charged for the candidate and 2,097,152 bytes for staging. The test verified no protected receipt and the original Core root before and after settlement. Combined-fault TRX SHA-256 `f3a818fdd12b2d0de1fae61b484f767660dcc0a009381f54c3000e2704b3a12b`.

The debug volume readback and retirement cases logged both Debug Layer and DRED on WARP and NVIDIA. Their clean shutdown summaries recorded zero D3D12 errors, corruption messages, discarded messages and live child objects. Warning counts were retained in the logs. These results do not set production limits or replace #88.

## Accepted R09 reference budgets from #88

On 18 November 2026 the owner accepted the limits for the tested R09 field. The four installed AtHandoff and Overlap archives contain 40 measured samples and eight warm-ups across the natural and early-cold variants. The reference used an i9-10850K and RTX 5070 (`10DE:2F04`, driver `32.0.16.1088`). The installed Groundwork packages were `0.1.0-dev.44257059676.1` from PR #179's tested merge `a6c5c365cb5329dbeed4288fe6941c94e2197503`, paired with WYU `7e2471be7786f5f8253ee90e65faa80ab94d23d7`. The Groundwork CI package manifest has SHA-256 `B7BDC0DCD758814745942C7A0B979A8C1DC4617EC1FC700A5C56604C3CB9C2BF`. These archives qualify that installed composition. [Budget decision and provenance](https://github.com/DaveBeusing/Groundwork/issues/88#issuecomment-6976137453) · [PR #179](https://github.com/DaveBeusing/Groundwork/pull/179) · [Package CI](https://github.com/DaveBeusing/Groundwork/actions/runs/44257059676)

| Reservation | Terrain CPU bytes | Native, upload and retirement each (MiB) | Density / mesh / queue | Lease slots | Retirement slots |
| --- | ---: | ---: | ---: | ---: | ---: |
| Baseline | 101,039,296 | 16 | 0 / 0 / 0 | 144 | 2 |
| Movement, FirstEdit, Recovery and Speculative (each) | 50,388,576 | 8 | 1 / 1 / 1 | 64 | 1 |
| Total | 302,593,600 | 48 | 4 / 4 / 4 | 400 | 6 |

The coordinator limits are 12 candidates, 12 readers, 256 journal entries and 8,192 overlay samples. The targeted slot audit verified 396 simultaneously occupied lease slots and six retirement slots, including backpressure and release. Speculative work cannot borrow protected credits. Native, upload and retirement are separate reservation dimensions.

| Protected path | Worker `Execute` (ms) | Overall route (ms) | Maximum total attempts |
| --- | ---: | ---: | ---: |
| Movement | < 1,500 | ≤ 3,000 | 3 |
| FirstEdit | < 1,550 | ≤ 3,250 | 2 |
| Recovery | ≤ 2,000 | ≤ 4,000 without flight overlap | 3 |

The held upload release to native readiness and the authorized receipt to consuming `BeginTick` each have a 50 ms limit. Both archived intervals passed 120/120. Recovery flight overlap records the flight lead and return-to-`BeginTick` intervals separately. Retry requires explicit `Busy` or `Backpressure` and newly observed progress on a subsequent eligible tick inside the original deadline. The attempt limits include the first attempt. Speculation receives no retry. Earlier #88 exhaustion probes used real coordinator denials for Movement and Recovery, and injected pre-admission `Busy` for FirstEdit. None of the four archive runs was repeated for #89.

## Follow-up qualification for #89

### First edited Present with a failed fence signal

`Q02WarpFailedFirstEditedPresentSignalRetainsCommittedEditUntilRemoval` uses the first successful native Present of an edited generation with the old reader still held and props and textures prepared. The WARP Present returned `S_OK` for generation 2, revision 2, view 2, epoch 1 and frame 4. Only the subsequent fence 8 signal failed with `0x80004005`. The committed edit and collision root stayed coherent, the failed frame produced no authorized receipt, and `BeginTick` left movement held. The sentinel retained 2,621,440 bytes of terrain backing until device removal was confirmed with `0x887A0005`. All tracked owners and child objects were clear at shutdown. This is a separate `WindowsDesktopSmoke` from the required CI job. [PR #180 and owner approval](https://github.com/DaveBeusing/Groundwork/pull/180#issuecomment-6979633693)

PR #180 reviewed `3731825667db76493577de155dc4432b6d39ebc5`, tested PR merge `e3ef2498b57662a2f4de7a46a8f0b0b327fd6906` and integrated as `3f3e2905a5c3f2af9e8ba3ee2775fbff421d937f` on `master`. The merge and tested candidate share parents and tree. PR and `master` CI completed successfully. The master run recorded 759 passing cases and all 20 required WARP readbacks, plus package integrity and consumer checks. [PR CI](https://github.com/DaveBeusing/Groundwork/actions/runs/44308664782) · [Master CI](https://github.com/DaveBeusing/Groundwork/actions/runs/44311866981)

### Installed Q02 pressure on the workstation

One installed `Normal` / `AtHandoff` process used the pinned Q02 kit on the Core Ultra 9 285HX and RTX PRO 5000 Blackwell Laptop (`10DE:2C38`, driver `32.0.15.9653`). The Q02 kit pins Groundwork `3f3e2905a5c3f2af9e8ba3ee2775fbff421d937f` from the `master` package run, packages `0.1.0-dev.44311866981.1`, and WYU `ddda426f98a078a125d40e99faadfcbe1a34efe0` on `f3c9188237dd5c0b762226574e3cc551bbb69616`, with locked dependencies, .NET SDK `10.0.403` and runtime `10.0.14`. The local kit carries the exact WYU patch and package manifest. The installer manifest pins all 209 payload files. The owner waived the additional installed Q02 pressure run on the i9/RTX 5070 reference computer on 18 November 2026.

The audit matched all 18 checkpoints and all 85 QPC intervals to raw output at 10 MHz. Job IDs 1/2, 4/5 and 7/8 stayed `Running` / `Ready` through each consuming `BeginTick` for Movement, FirstEdit and Recovery. The same jobs settled to 0/0 after the tick. Each matching Present receipt opened movement at the next `BeginTick`. There were 12 passing timing gates, one admission per path with zero denials, and no outstanding owner allocations, child objects or held queues at final shutdown. The exit diagnostics retained 200 warnings, zero errors, zero corruption and zero discarded messages.

| Path | Worker (ms) | Overall route (ms) | Release to native (ms) | Receipt to `BeginTick` (ms) | Receipt → consuming tick |
| --- | ---: | ---: | ---: | ---: | --- |
| Movement | 474.1967 | 699.9477 | 7.9224 | 29.4686 | 4 → 5 |
| FirstEdit | 172.0231 | 223.2332 | 7.9611 | 1.0541 | 8 → 9 |
| Recovery | 203.7904 | 256.4522 | 8.3280 | 7.6609 | 14 → 15 |

Peak terrain CPU reservation was 201,554,304 of 302,593,600 bytes. Native, upload and retirement each peaked at 32 of 48 MiB. Density, mesh and queue each reached 3 of 4. Lease and retirement slots peaked at 259 of 400 and 4 of 6. Four readers were observed. The largest observed generation count was three. Native D3D12 backing separately peaked at 9,109,504 bytes in the 18 checkpoints. The installed process held the protected candidate's own upload after its CPU work, with the old reader retained. The run exercised normal progress and final settlement.

### Independent GPU overlap and production-vector exhaustion

Local Release source builds on `3f3e2905a5c3f2af9e8ba3ee2775fbff421d937f` produced the following engine evidence. The associated source and changes were reviewed independently for code, safety and integration. Their TRX files are distinct from the `master` CI run and the installed package evidence.

- `Q02NativeTextureQueueWaitOverlapsProtectedTerrainWorker` passed for Movement, FirstEdit and Recovery on NVIDIA, with three GPU control cases separately passing. A real `ID3D12CommandQueue::Wait` held an unrelated 2 MiB texture copy on the render queue while the protected CPU worker executed. The completed fence value stayed at 4 throughout the worker, below the submitted copy fence 5, and reached 7 after release and mask observation. Worker durations of 156.7308, 257.2124 and 134.6488 ms were fully within the hold. Each path kept the two speculative jobs through `BeginTick`, recorded exactly one matching receipt and opening, and settled worker, queue, reader, generation and GPU ownership. The three runs each reported six warnings and no errors or remaining child objects. The controlled worker durations describe the engine fixture.
- `Q02SealedFirstEditRespectsR09CreditBoundary` checked the real coordinator with the fixed R09 envelope and one-byte request changes for CPU, native, upload and retirement credits. Four cases denied at one byte above capacity. The eight cases at exact capacity or with one spare byte admitted. A denial returned no work, left the edit pending, and preserved root, stamp and reservation state. `Q02FirstEditCannotPublishUntilRetainedR09BaselineCreditsReturn` checked both release and abort with two occupied baseline retirement slots, movement held and unchanged publication on denial. All 14 new managed cases and 23 controls passed.
- `Q02NativeFirstEditWaitsForR09BaselineCreditRelease` confirmed the same held-reader path with native backing on NVIDIA. The candidate added 1,310,720 bytes for 2,424,832 total live terrain backing. At tick 4 the coordinator recorded `Backpressure` while the renderer returned `StaleGeneration`. Root, stamp and backing stayed unchanged. The last old reader returned 50,388,576 CPU bytes, 8 MiB each of native, upload and retirement credits, 65 lease slots and one retirement slot. The same candidate published at tick 5 and its authorized Present opened movement at `BeginTick` 6. Final balances and GPU child objects were zero. The targeted case and three WARP/NVIDIA controls passed. The engine fixture advances the retry cut. At this denial the ordinary installed WYU edit path finishes the cut unchanged and returns `false`. The installed Q02 pressure mode finishes the cut unchanged and reports a qualification failure. The R09 host retry logic applies to the earlier admission phase.

### Evidence identities

Artifacts in the local evidence bundle are identified by name and SHA-256. The links above point to the durable issue and CI records. All four #88 ZIP hashes matched the supplied checksum CSV. The installed Q02 result ZIP hash was calculated locally after receipt, where no separately supplied expected digest was available for the outer ZIP. Its 19 entries passed CRC verification, and the embedded manifest and runner hashes matched the kit pins.

| Artifact | SHA-256 |
| --- | --- |
| `AtHandoff-early-cold.zip` | `E4635868F6C6AF5D09AA93521328E95462FBCC33FE2F1B9CF9AAE586F36F4DE9` |
| `AtHandoff-natural.zip` | `8E50B07E7A25D282780CF1C8E0F2744561A4E338883E970EA190415D35B8B918` |
| `Overlap-early-cold.zip` | `16C1DA4E04F9383FFDDB993A2C98FE53ED04EC3167CFB8104C3C94D0A0DE3025` |
| `Overlap-natural.zip` | `CEFCE4BE59B69F3B2AAB7A0B93A48B0CA0F159BCF908FAA8C76FC94F10824AEF` |
| `edited-present-fault-final.trx` | `ACA271C8D7B7FE12359AE9C9F0A7D2D33A0E455A1B8730F79195F608CE413314` |
| `WYU-Q02-Kit-3f3e2905.zip` | `932B36D357430C5595232804EC8C146FDC73EC5E60A386B3ABDCB400428368EA` |
| `WYU-Q02-0.1.1-win-x64.exe` | `DE566316B2F5BB3703B76BADF593864AAB599B2D60C2D4E1D46DFB26D1CA9409` |
| `installer-manifest.json` (Q02) | `0AA1B389FDCC944F627AA4889660CBB7BF6C4D5CB5254C187D389DFAEE4BB345` |
| `groundwork-ci-manifest.json` (Q02) | `E645A89A7B42A295E6DA20E0975DAB0872B5B019B5407DA142DB76470385F23A` |
| `WYU-Q02-89-20261118-145013.zip` | `EF7112F2A1F51F8EF7BBE0E991DE7579DE75A5D802DB804E32E6C4EB563853EF` |
| `q02-independent-gpu-overlap-final.trx` | `13192CB466C2E262F397CF1BC247208AA0E697973C286D5ED0DF2FFEEDC114D5` |
| `first-edit-credit-final.trx` | `61811162F3F5F06DD46B0E54BF1AE4A75B50372A17EAEE0B76C3D233A8F42B15` |
| `first-edit-native-final.trx` | `1611B4389A46BBBF4C36781DBD8BBCE90D9628F746F3CB581D18BA42EE3E0013` |
| `WYU-Q02-Restore-3f3e2905.zip` (original companion) | `642ECA6604EC7C08881B870E2F518B6F2EDD349AA38CA7456118F89A69A744D5` |
| `WYU-Q02-Restore-3f3e2905-r2.zip` (corrected companion) | `C70A7913E7E59F4D496EE69F37F8A40C7B05109A1DBCB8D97124A1D9C35C495D` |
| `WYU-Q02-Restore-89-20261118-182718-9249cb8c.zip` (initial path failure) | `C8C483FBCB6DE009C7222A659C648100D4FEA47267488BE258595A5381EDC1F3` |
| `WYU-Q02-Restore-89-20261118-182811-ec22b8fd.zip` (original comparison failure) | `1AA8E0E7CDD1EF53A3014AE9ADCAF0739DF2BA745D0738B1087626B1BA98D640` |
| `WYU-Q02-Restore-89-20261118-183125-c18dbbbf.zip` (verified final restore) | `34CBAB281FE9B9F9809531F6E012D6F44F462D97F1AA5F441072EAC86DF3FADA` |

The local engine fixtures are identified by source digests in addition to their base commit. `Q02IndependentGpuPressureTests.cs` has SHA-256 `DE1843C68144D17B57520F02540D305088D8FF23279D2666F60BE94550D52F12`. `Q02ProductionCreditExhaustionTests.cs` has SHA-256 `3398AB1A07EE76B75D57FBA86CD7C9189FA9284E15D44218526BB3E25481AC11` with `VolumeEditTests.cs` `534B997EE8C4CBF2023FEA072C0BA994136866CF34527BA2793D7AAD7881D8FC`. `Q02NativeBaselineExhaustionTests.cs` has SHA-256 `146E0B80CE5B6B0B6BA5F2F40067C42ECDB0A724AA3FDE4B6155F73EB12D3137` with `VolumeTerrainTests.cs` `582FD7A7B4D82546826574C67869903716F01B39077633659F552F63DCDC7CC0`.

### Installed save and restore acceptance

**PASSED on the workstation on 18 November 2026.** The existing per-user Q02 installation matched all 209 payload entries in the pinned installer manifest. The run used Windows `10.0.26300.0`, Groundwork `3f3e2905` and packages `0.1.0-dev.44311866981.1`, WYU `ddda426f`, bundled .NET `10.0.14` and WARP `1414:008C` driver `10.0.26100.9711`. The installed executable and the original Q02 kit retain their pinned hashes. Companion revision 2 runner SHA-256 `4789BF6CCBBAE8622A7602DD63384A6DE6F848DF2ED9A61429F05876680EDCF9`.

| Phase | Process | Saved cut | D3D12 exit |
| --- | ---: | --- | --- |
| `--volume-save-smoke init` | 49728 | Creates the isolated NTFS slot, edits terrain and commits generations 1 and 2. Revision 3, final tick 11. | Exit 0, 15 warnings, zero errors, corruption, discarded messages or live child objects. |
| `--volume-save-smoke restore` | 24916 | Opens the same slot after the writer exits. Restores generation 2, revision 3 and tick 11. | Exit 0, 3 warnings, zero errors, corruption, discarded messages or live child objects. |

Both processes enabled the Debug Layer and DRED and reported clean terrain and renderer settlement. The slot and world GUID are `102f0b77-341b-4d29-91e3-baab6845be18`, lineage `a38c3a57-459a-41ce-a558-82022f93399b`. The two checkpoint lines match exactly. They report overlay SHA-256 `122FA2B5B3FF88B58394A9E533576F9E49F624C8212BDDFEA714BB64C0579786`, position `(15.999989377671797, 4104.496463273829, 15.999989377671797)`, added collision height `4107.138534545898` and cut collision height `4101.88037109375`. The pinned smoke checks the full pose, exact rebuilt overlay, sampled edit densities and added material. The raw `volume.a`, `volume.b` and slot manifest hashes agree with both snapshots. The archived 129-byte pre-restore `volume.intent` matches the committed generation-2 write and the restore removes it as specified by `VolumeCheckpointSlot.ReadSettled`.

The final ZIP matches its SHA-256 sidecar. All 16 entries passed CRC verification. A separate audit decoded both checkpoint members and verified the hashes, internal checksums, fixture identity, full pose and intent against both process logs and the pinned manifests. The original attempt failed before the first checkpoint with a 269-character native staging path. The next pair restored successfully, while the original wrapper reported a failure because it compared the intent marker as durable state. Those two archives retain their raw results. Revision 2 checks the path before launch, validates and archives the original marker and independently checks its expected retirement. The final pair passed the corrected check. No reference series was repeated.

This run covers the installed WARP edited-volume restart and the two collision probes. Source-built exact-mesh and native restored-renderer fixtures and the NTFS subprocess cases carry their separate evidence. The fuller offline Continue journey and failure UX are tracked by WYU #32. [WYU #32](https://github.com/DaveBeusing/wyu/issues/32)


## Reproduce the workstation runs

From the reviewed checkout, use a fresh evidence directory. Each managed row uses one test process with OR selections separated by `|`. For D3D12, run every selection in a separate test process. The Debug Layer must be enabled before the process creates its first device, so a non-debug readback must use a different host from a debug qualification. For method or class names prefix `FullyQualifiedName~`. Use the category expression as written. Select the listed project under `tests/`.

| Project | Selection |
| --- | --- |
| Groundwork.Voxels.Tests | `PlanetLatticeTests\|PlanetDensityFieldTests\|PlanetJobDeterminismTests\|PlanetBrickStoreTests\|PlanetVolumeMesherTests\|VolumeMeshPlanTests\|VolumeStreamingTests\|VolumeStreamingFaultTests\|VolumeCollisionTests\|VolumeEditTests` |
| Groundwork.Persistence.Tests | `VolumeShapeRestoreTests\|NtfsChildProcessInterruptionPreservesTheDurableCut\|NtfsV2ChildProcessInterruptionKeepsTerrainDepletionInventoryAndPoseInOneCut\|NtfsV3ChildProcessInterruptionKeepsTerrainDepletionInventoryAndPoseInOneCut\|NtfsIntentCloseWaitRetriesSharingViolationsAndKeepsOtherFailuresVisible\|DecodedDurableCheckpointRebuildsTheEditedCollisionAndRestoredPose\|WriterLeaseRejectsSecondWriterAndCanceledStagePreservesCut` |
| Groundwork.Simulation.Tests | `ComposedRecoveryTests` |
| Groundwork.Diagnostics.Tests | `DiagnosticDispatcherTests` |
| Groundwork.Rendering.D3D12.Tests | `Q02WarpFailedMaximumTextureSignalKeepsProtectedEditUnpublishedUntilSafeSettlement` |
| Groundwork.Rendering.D3D12.Tests | `WarpCubeSphereTransitionsMatchCollisionAcrossRebase\|WarpVolumeReadbackCoversTransitionAndCavityAcrossRebase\|VolumeReadbackWithDebugLayerAndDred\|VolumePresentRetainsGenerationUntilFenceAndRejectsStaleView` |
| Groundwork.Rendering.D3D12.Tests | `VolumeUploadRemainsBlockedWhileProtectedTerrainWorkRunsOnFullCpuQueue\|ProtectedEditedGenerationPresentsBetweenOptionalVisualQuanta\|ReadyFirstUseTexturesPropsAndRefinementsShareDrawableTurns` |
| Groundwork.Rendering.D3D12.Tests | `TestCategory=D3D12MeshRetirementFaults\|VolumeFaultRetainsRegistrationUntilConfirmedRemoval\|WarpMeshReplacementWaitsForRetirement\|WarpTextureCopyPreflightsPaddedBytesAndPreservesResourceUntilTrustedFence\|WarpTextureUnassignedSignalRetainsUploadAndDestinationUntilConfirmedRemoval\|LedgerCountsExactRetirementWithoutReusingAResourceAfterSentinel` |
| Groundwork.Rendering.D3D12.Tests | `RejectedRegistrationRecoveryPreservesTheRootAndCannotRevokeASupersedingView\|RejectedFirstContentExpiresWithoutAnotherFrameAndRecoversTheSameRevision\|FirstContentRetrySettlesAfterClosedListExpiryOrSupersedingMask\|InvalidatedRendererCannotCommitAfterLossOrReopenItsEpoch\|WarpHealthyTexturePreparationFailuresKeepTerrainAndPredecessorDrawable\|TextureStreamTests` |
| Groundwork.Rendering.D3D12.Tests | `CanonicalWindowPresentsTheSameMixedMaterialsAcrossNearAndOrbit\|Int01MixedVoxelMaterialsDrawAt1080pWithoutAnotherTerrainFrame\|Int01ProtectedEditedPresentSurvivesSuccessiveStalePropAndMaterialCuts\|Int01SavedEditRestoresCanonicalGeometryAndTexturedPixels` |

The following targeted filters identify the follow-up source tests. The installed pressure and #88 results above are archival evidence. The pinned Q02 companion revision 2 reproduces the installed WARP save and restore route documented above.

| Project | Follow-up selection |
| --- | --- |
| Groundwork.Rendering.D3D12.Tests | `Q02WarpFailedFirstEditedPresentSignalRetainsCommittedEditUntilRemoval` |
| Groundwork.Rendering.D3D12.Tests | `Q02NativeTextureQueueWaitOverlapsProtectedTerrainWorker` |
| Groundwork.Voxels.Tests | `Q02SealedFirstEditRespectsR09CreditBoundary\|Q02FirstEditCannotPublishUntilRetainedR09BaselineCreditsReturn` |
| Groundwork.Rendering.D3D12.Tests | `Q02NativeFirstEditWaitsForR09BaselineCreditRelease` |

For example, run the combined fault case from the repository root in a dedicated test process.

```powershell
dotnet test tests/Groundwork.Rendering.D3D12.Tests -c Release -p:RestoreLockedMode=true `
    --filter 'FullyQualifiedName~Q02WarpFailedMaximumTextureSignalKeepsProtectedEditUnpublishedUntilSafeSettlement' `
    --logger 'trx;LogFileName=cross-system-fault.trx' --logger 'console;verbosity=detailed' `
    --results-directory ./artifacts/q02-reproduction
```

Keep the exact source SHA and checkout status, host and adapter/driver metadata, the Release build and TRX outputs. For a native lane, keep the actual Debug Layer/DRED and live-object output alongside the TRX.
