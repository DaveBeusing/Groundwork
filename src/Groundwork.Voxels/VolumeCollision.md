# WP11-C02 volumetric collision and player publication

C02 (#84) queries the exact immutable B01 mesh retained by a C01 publication.
Positions, velocities and body dimensions are planet-centered double metres.
The narrow phase uses bounded plan-local doubles, including transition triangles;
render floats and cube charts never select a separate collision surface.

## Queries and admission

`VolumeStreamingLease.Raycast(start, end, hits)` returns all distinct ordered
surface intersections (including floor and ceiling), outward geometric normals
and material IDs. The caller owns the hit buffer (at most 256 entries). `IsTransition` identifies actual transition-cell contact geometry (including a tie at shared edges). An
insufficient buffer or work ceiling returns `BudgetExceeded` with Count 0;
never consume a partial buffer as a complete query. A coplanar ray has no
isolated crossing and returns no contact. Use a finite shape sweep for movement.

`Sweep(VolumeCapsule, displacement)` includes the entire capsule axis, not just
its endpoints. Segment/triangle distances and conservative advancement prevent
tunnelling through committed walls, overhangs and LOD transitions. The surface
skin is 0.0001 lattice cells, covering B01's 1/65536-cell quantization. Results
may stop early by that tolerance. An initially embedded capsule is rejected;
there is no unchecked depenetration. Density sign is used only for initial
solid containment, with trilinear samples from the same retained plan.

The entire finite query envelope must lie inside the published window. Unknown
coverage, invalid/non-finite input, exhausted iterations or triangle-test budget
are explicit failures. No synchronous IO, unbounded search, cache or allocation
occurs in a warmed query. Defaults are 2,000,000 triangle tests and 64 advancement
iterations; maxima are 16,000,000 and 256. Capsule radius is at most 32 cells and
axis length at most 64 cells. Counts include scanned triangles, even those
rejected by a separating plane. Quotas are hard work limits, **not** timing claims.

A retained lease may query an older revision for diagnostics. That does not
admit player movement. Disposal prevents subsequent queries, and a query holds
the lease lock throughout its read so plan data cannot be released mid-query.

## Fixed-step host sequence

1. Complete the C01 mask, commit, matching frame acknowledgement and later
   BeginTick handshake. Diagnostic mesh readiness alone cannot release motion.
2. At the finalized BeginTick, call `TryPlanMovement` with the committed player
   pose and this tick's forward/right input. The first accepted call can establish
   a non-penetrating spawn. The returned draft is an unforgeable proposal; no
   authoritative root has changed yet. Paused/faulted/stopped worlds reject it.
3. Reserve bounded semantic evidence when preparing movement. A permit-backed
   MovementCommit/MovementRejected record retains the originating BeginTick stamp, input, prior pose, result,
   complete terrain/view identity and finalized outcome/stamp. A pending outcome
   cannot be handed off. The attempt stamp is preserved separately from the nullable committed outcome stamp, including rejection. A full journal stops this epoch before any pose swap;
   the separate terminal cell remains available. Revocation before a swap and
   Stop after a swap cannot erase or mislabel the eventual record.
4. Seal the same tick's CommitResults and call `TryPrepareMovement`, supplying
   the ordinary gameplay root and batch dispositions if applicable. Core
   finalization atomically publishes `VolumeWorldRoot.Player` with that state and
   the same terrain. `WithState` preserves the player. Observe the committed
   barrier before advancing the next tick, as for any Core publication.
5. A view/rebase, root/generation change, wrong tick, stale supplied pose or Stop
   rejects/revokes the proposal. Failed queries preserve the last pose and discard
   only this tick's movement. They never queue movement for later replay.

The walker uses the existing 60 Hz, 4 m/s, 0.25 m-radius, 1.7 m-height and radial
9.81 m/s² defaults. It checks initial clearance, current support, the full lateral
sweep, destination support and gravity/ceiling contact under one shared work
budget. Walkable contact is within 45 degrees of radial up. Steeper contacts and walls stop the lateral component conservatively while known-covered gravity still integrates. Body endpoints change continuously with
radial orientation; support-plane transport is included in that finite sweep.
A movement through the planet center is rejected rather than flipping the body. The final reconstructed capsule is revalidated after a partial contact.
The API does not implement rigid bodies, step climbing, wall sliding or jumping.

A terrain replacement carries the committed player and must pass initial capsule
clearance before Core publication. A replacement that embeds the player fails
without replacing the old root. D01 owns edit transaction policy and D02 owns the
checkpoint encoding; C03 owns actual GPU frame evidence. The old radial walker
remains for WP08/09 consumers until their WP11 migration, but C02 does not use its
single-height surface interface.

## Verification

Run `dotnet test tests/Groundwork.Voxels.Tests/Groundwork.Voxels.Tests.csproj -c Release`
or `./scripts/Verify-Bootstrap.ps1` for the complete shipping graph and CI filters.
`VolumeCollisionTests` covers multi-hit cavity rays, finite high-speed sweeps,
wall/roof contact, a solid between capsule endpoints, tangency, budgets, unknown
coverage, all twelve canonical chart edges/eight corners with mixed-LOD plans,
retained queries across rebase, Core-only pose mutation, stale drafts, view/stop
revocation, gentle/steep slopes, airborne wall gravity, final off-axis landing
clearance, journal saturation, outcome handoff ordering, invalid-preparation retry,
sub-ULP boundary radii and edit replacement veto. The seam tests position a real
2:1 boundary at each canonical edge/corner and assert transition ray/sphere hits,
both ray directions, finite capsule contact, an immutable changed field revision,
movement hold and re-admission. They do not implement D01 edit transactions.

The warmed-query smoke records environment, 64 samples, p50/p95/p99 and managed
allocation bytes in its TRX output. This is a portable correctness/overhead probe;
Q01/Q02 still own production capacity, larger terrain routes and cross-module
edited/save/renderer qualification. C02 introduces no native rendering changes.

Local baseline (2026-11-11, Release, SDK 10.0.403/runtime 10.0.14): Windows
10.0.26300, Intel Core Ultra 9 285HX, 24 logical processors, 68,137,205,760 bytes
physical RAM. On the 14,720-triangle planar fixture, 16 warmups + 64 finite sphere
queries measured p50 2.7589 ms, p95 2.8327 ms, p99/max 2.8590 ms and 0 managed
allocation bytes for the warmed measured loop. This establishes the first C02
local comparison baseline; the former radial query is not a volumetric solver
and is not a valid like-for-like baseline. These figures do not qualify 60 Hz
production capacity or the absent 8-core/32-GiB reference machine. TRX stdout
from `QuerySmokeReportsLatencyAndManagedAllocations` contains each rerun's values.
