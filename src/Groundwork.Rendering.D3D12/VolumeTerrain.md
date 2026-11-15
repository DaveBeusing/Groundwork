# WP11 volumetric terrain presentation

Create the provider with `D3D12Renderer.CreateWithVolumeTerrain` and the same
`VolumeStreamingCoordinator` used by simulation. Give every replacement renderer
a newer epoch. The provider binds exclusively to its fresh held epoch. The host uses one monotone render sequence across masks, terrain
frames and camera updates.

1. Close coverage with `ChangeView`. Submit a neutral mask for that identity via
   `TrySubmitVolumeMask`. The provider confirms the mask after a successful
   normal Present and both queue fences. Stale, busy, occluded and test-only
   presentations cannot confirm it.
2. Complete private C01 terrain work and call `TryStageVolumeTerrain`. The
   provider reserves the generation's remaining CPU packing credits and native
   buffer credits. The render owner allocates and fills the private upload heap.
   Poll `IsVolumeTerrainReady` and use `TryPrepareVolumePublication` at the sealed
   CommitResults barrier after both preparation and the matching retired mask.
   A capacity or preparation failure leaves the old root authoritative.
3. Submit the committed generation via `TrySubmitVolumeTerrain`. The camera is
   supplied in authoritative planet coordinates and carries the exact view and
   origin identity. The provider installs the already prepared immutable triangle
   streams. Its staged reader transfers to the frame registration without consuming
   another lease slot.
   It checks the current registration at GPU installation and draw admission.
4. Core can reopen coverage only at a later `BeginTick` after the provider has
   acknowledged the registered frame. The acknowledgement follows successful
   Present and proven draw and Present fence retirement. After presentation,
   `TrySubmitVolumeCamera` updates the camera for that generation in its existing
   origin. Each camera sequence is registered at draw admission. A new origin uses the full held-view sequence.

The owner admits a portable plan for the selected fixed pass sequence. Cached
topologies use symbolic resource slots. The owner rechecks live pointers and
depth generation for the current target and checks the exact registered view
immediately before executing the closed command list. Shadow cache keys become
eligible for import after the draw receives a fence. A padded readback buffer
represents the logical packed color and depth rows. Extraction never consumes
row or plane padding. The existing prop stream gate validates the occupied
cells and metadata in its mapped arena before a prop draw.

A rejected first-content recording that has never executed retains one exact
registration. The owner retries on an eligible wake, up to three rejections
and within the operation timeout. Idle and suspended waits honor the same
monotonic deadline. Settlement drops the retained registration and sends Core
one recovery request. At a later finalized BeginTick, Core checks that the
registration still identifies the current root and view before advancing the
view within the same renderer epoch. The host uses the new view to request a
mask and prepare the same terrain revision through the existing recovery lane.
Only the replacement's normal Present and retirement can reopen movement.

The geometry resource is an upload heap mapped and filled by the CPU. There
is no separate GPU upload command or staging resource. The provider bounds
the triangle count and checks the aligned buffer charge against all three C01
native, upload and retirement credits. The actual allocation is checked again
on the render owner. One candidate and one installed generation fit within the
two native buffer slots and the 64 MiB aggregate limit. Managed packing is bounded by the region and per-mesh vertex limits and the
generation's unused CPU credits. This covers the packed arrays and peak scratch
for one batch. Packing and native preparation do not hold the admission lock. A caller can retry capacity
backpressure with a new sequence, or choose a smaller selection.

The registration remains held with its installed geometry until safe replacement
or shutdown. Resize waits for both fences. On stale installation the unsubmitted
private buffer is disposed before its registration. Failed Signal, an incomplete
fence or a fence sentinel keep the installed buffers and registrations in
custody. Verified device removal or proven queue settlement permits release.
Provider shutdown revokes the matching renderer epoch before publishing terminal
diagnostics. Recovery binds a newer epoch.

`VolumeTerrainTests` exercises WARP and NVIDIA readbacks on actual mixed-LOD
transition geometry across the +X/+Y chart boundary and a cavity with floor and roof. It verifies the changed
density revision at near and far camera distances, rebase, stale view rejection,
resize and window close, delayed retirement and later-tick acknowledgement. Fault
cases cover draw Signal, Present HRESULT, Present Signal and watchdog expiry. The desktop
qualification runs in a fresh process so Debug Layer and DRED can be enabled
before the first device is created. The mandatory CI fixture is
`WarpVolumeReadbackCoversTransitionAndCavityAcrossRebase`.
