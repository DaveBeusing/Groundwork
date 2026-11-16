# WP11-Q02 workstation qualification

[Issue #89](https://github.com/DaveBeusing/Groundwork/issues/89) tracks cross-module failure and recovery qualification. This matrix starts from Groundwork `master` at `6dd1320f6f4e009384f315887cbd74b1c615fac9` and adds the evidence from the test changes in this PR. The [WP11 contract](wp11-procedural-planet-contract.md) defines the acceptance criteria. The prior [handover](../wp11-handover.md) records the installed WYU run on its own pinned versions.

## R01–R09 test matrix

| Requirement | Workstation evidence | Remaining qualification |
| --- | --- | --- |
| R01 Identity, descriptors and frames | `PlanetLatticeTests` checks canonical lattice addresses and overflow boundaries. `ExtremeRadiusBricksKeepGoldenBytesAcrossJobOrders` includes the 6,400,000 m case. `WarpCubeSphereTransitionsMatchCollisionAcrossRebase` joins canonical geometry, collision and WARP pixels through rebase. | Rerun the installed consumer route using the accepted immutable packages. |
| R02 Deterministic generation | `PlanetDensityFieldTests` checks fixed samples and distinct seeds. `ExtremeRadiusBricksKeepGoldenBytesAcrossJobOrders` checks three fixed seeds at two radii, including serial and reordered Jobs execution, against brick goldens. | Keep pinned recipe and runtime identity with the reference and consumer evidence. |
| R03 Sparse volumetric representation | `PlanetBrickStoreTests` checks quotas and lease accounting. `ShapeEditAndNtfsRestorePreserveMeshAndCollision` exercises enclosed cavity, overhang and detached solid through actual volume data, editing and restore. | Reconcile owner state counts and bytes against the accepted production envelopes. |
| R04 Meshing and seams | `PlanetVolumeMesherTests`, `VolumeMeshPlanTests` and the WARP chart and cavity readbacks cover transitions, seam/corner geometry and provider output after rebase. `VolumeReadbackWithDebugLayerAndDred` repeats cavity readback on WARP and NVIDIA. | Qualify the installed workload at the agreed reference limits and accepted route. |
| R05 Streaming and bounded scheduling | `Q02GeneratorDelayAndQuotaPressurePreservePublishedTerrain` and `Q02CompletedOldMeshCannotReplaceNewerPublishedTerrain` verify held/failed generators, quota denial and stale candidates. `VolumeUploadRemainsBlockedWhileProtectedTerrainWorkRunsOnFullCpuQueue` covers held upload plus reserved recovery build. `ProtectedEditedGenerationPresentsBetweenOptionalVisualQuanta` covers protected edit progress under texture and prop work. `EveryResourceDimensionRejectsMinusOneAndAdmitsExactAndPlusOne` covers fixture-level boundary cases. Renderer fault cases below cover retained and terminal custody. | Run the single production pressure route for movement, first sealed edit and recovery, with both pressure sources and old/new ownership maintained at the specified points. Qualify accepted limits and their decisive ±1 byte/slot cases. |
| R06 Movement and collision | `VolumeCollisionTests` covers finite sweeps and holds on missing coverage or exhausted work. The WARP seam fixture checks matching collision and visible geometry. `VolumePresentRetainsGenerationUntilFenceAndRejectsStaleView` and protected edit regressions check the link from admitted terrain identity through retired Present and the movement gate. | Validate player movement, landing and recovery along the reference workload with the accepted budgets. |
| R07 Editing and persistence | `VolumeEditTests` and `VolumeShapeRestoreTests` cover sealed edit heads and restored geometry and collision. `NtfsChildProcessInterruptionPreservesTheDurableCut`, its V2/V3 companions and `NtfsIntentCloseWaitRetriesSharingViolationsAndKeepsOtherFailuresVisible` exercise real abrupt exits and NTFS sharing behavior using disposable slots. `DecodedDurableCheckpointRebuildsTheEditedCollisionAndRestoredPose` checks the durable cut after decode. | Rerun the installed WYU restart and save route with exact-pinned qualified packages. |
| R08 Render and game composition | `VolumeReadbackWithDebugLayerAndDred`, `VolumeFaultRetainsRegistrationUntilConfirmedRemoval` and `InvalidatedRendererCannotCommitAfterLossOrReopenItsEpoch` cover native output, registration custody and epoch closure. `Int01ProtectedEditedPresentSurvivesSuccessiveStalePropAndMaterialCuts` verifies the first edited Present after two optional binding invalidations. `Int01SavedEditRestoresCanonicalGeometryAndTexturedPixels` joins restored data to renderer pixels. | Rerun the installed, locked WYU consumer path with verified package provenance. Product cutover and owner acceptance remain open. |
| R09 Diagnostics and validation | The workstation runs preserve adapter/driver, test identities and TRX logs. `WarpRetirementFaultKeepsTerminalEvidenceWithOptionalSink` and `DiagnosticDispatcherTests` verify evidence through saturated or failing optional sinks. Fault runs and successful pressure runs have separate assertions. | [Q01 #88](https://github.com/DaveBeusing/Groundwork/issues/88) must establish the 8-core/32-GiB reference budgets and latency/retry limits. Q02 must qualify the full route against those budgets before handover or production cutover. |

## Scope of the failure cases

The native tests deliberately inject controlled failure observations and use the real D3D12 queue for the surrounding draws and copies. The fence sentinel, a failed signal and confirmed device removal have distinct custody outcomes. The healthy mixed edit test and the held-upload/recovery test are separate fixture runs. Their results support protected-path behavior within each fixture. The full C00-4 admissible production pressure route remains open.

Run the workstation cases in an interactive Windows x64 Release session with Graphics Tools, a hardware NVIDIA adapter and WARP available. Adapter-parameterized cases run on both WARP and NVIDIA. The seam and injected removal cases use WARP, with the removal data rows selecting the failure point. Run filtered groups sequentially in fresh test processes. The native harness logs actual DXGI adapter and driver and checks shutdown, with debug messages and live-object counts where the Debug Layer is active. NTFS cases terminate their own test child process and use isolated test directories.

`Q02WarpFailedMaximumTextureSignalKeepsProtectedEditUnpublishedUntilSafeSettlement` adds a windowed WARP fault while the renderer holds a drawable coarse texture, a staged prop cell and a sealed edit with its CPU reservation. A real 2 MiB copy executes before the test returns a failed signal. The test observes the pending destination and staging backing, both still charged after the injected completion sentinel. It requires terminal stop, cancellation of the undispatched edit, the same committed Core root and stamp and no edit receipt or texture installation. It then requests actual removal of its disposable WARP device and verifies settlement of tracked texture, staging, terrain and prop allocations. The existing protected-Present failure fixture faults an initial generation. The specific variant that loses the post-Present signal of a newly edited generation under mixed visual pressure remains outside these runs.

The results below cover source builds. The archived WYU handover and a subsequent reference qualification have their own versions and evidence.


## Workstation results

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

For example, run the combined fault case from the repository root in a dedicated test process.

```powershell
dotnet test tests/Groundwork.Rendering.D3D12.Tests -c Release -p:RestoreLockedMode=true `
    --filter 'FullyQualifiedName~Q02WarpFailedMaximumTextureSignalKeepsProtectedEditUnpublishedUntilSafeSettlement' `
    --logger 'trx;LogFileName=cross-system-fault.trx' --logger 'console;verbosity=detailed' `
    --results-directory ./artifacts/q02-reproduction
```

Keep the exact source SHA and checkout status, host and adapter/driver metadata, the Release build and TRX outputs. For a native lane, keep the actual Debug Layer/DRED and live-object output alongside the TRX.
