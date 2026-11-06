# WP05 walkable region contract

**Status** Proposed for review before implementation. Builds on the
[roadmap](../roadmap.md), [WP04 handover](../wp04-handover.md),
[voxel data contract](wp03-two-chunk-terrain-contract.md), and
[mesh replacement contract](wp04-mesh-replacement-contract.md).

## Scope and ownership

WP05 streams a bounded, seeded region of unit voxels on a flat world. A
first-person controller in WYU can cross several chunk boundaries, stop at
an obstacle and return as previously unloaded chunks are generated again.
The route uses immutable generated terrain. The WP04 edit demo remains
available. Persistence, mutable streamed terrain, jumping, slopes,
dynamic-body physics, LOD and planetary coordinates belong to later work.

Groundwork owns region selection, chunk lifetime and admission, meshing,
static voxel queries, and deterministic movement resolution. WYU owns the
seed and generator selection, controller and camera, input mapping, and
composition with Core, the scheduler and renderer. Portable voxel work uses
immutable data and has no dependency on Rendering, Windows or WYU. The host
tracks dispatched work through Core before giving it to an executor.
Rendering receives portable immutable geometry and translations. The provider
owns native buffers and has no dependency on voxel data.

Keep the WP03 fixed pair and WP04 pair replacement paths working for existing
callers. WP05 adds an opt-in bounded region handoff. Public names and
signatures remain part of implementation review.

## Region, loading and publication

- Use the existing signed chunk grid and 16 × 16 × 16 occupancy snapshots.
  For an integer cell coordinate `v`, compute `c = floor(v / 16)` and
  `local = v - 16*c` in `[0, 15]`. A cell occupies the half-open interval
  from its integer coordinate to the next integer on each axis. WP05's
  generator serves a finite X/Z rectangle in the single chunk layer
  `Y = 0`. Known outside-world space has an explicit policy. Below the
  layer and beyond the horizontal world boundary it blocks collision, and
  above the layer it is air within the horizontal bounds. For meshing,
  space beyond the generated world is known air and can expose a face.
- The coordinator alone owns the active region, desired region and work
  admission. Anchor the region to the horizontal chunk containing the
  published player center. The **inner 3 × 3** square supplies drawable
  meshes and collision data. A one-chunk ring expands the required data
  to **5 × 5** for neighbor reads. Intersect both sets with the finite
  world bounds. Request missing chunks from the anchor outward by
  Chebyshev distance, then by signed X and Z. Reuse immutable snapshots
  shared by the old and new sets.
- Each desired anchor change advances a nonzero intent generation and
  replaces the waiting descriptor. A load reservation carries its
  coordinate and a unique load identity. An unchanged resident chunk
  keeps its revision and load identity. A reload gets a new load identity
  even when its generated contents and revision match. Loads can finish
  in any order. Adopt a completion only if its reservation is still valid
  and the coordinate is still needed by the current intent. Reject duplicate
  and stale completions. An obsolete work item keeps its slot and lease
  until it actually settles. Check arithmetic and stop before an identity
  counter wraps.
- The current region remains published while the target loads and meshes.
  Once every required snapshot and every target mesh is ready, publish the
  new CPU region as one immutable value at the simulation boundary after
  successful tick publication. Bootstrap uses the same handoff before
  accepting motion. Queries during a tick see one stable region. A failed
  world publication leaves the previous region and anchor authoritative.
  Release leaving chunks and old CPU geometry once their last region,
  worker, query and handoff ownership ends. No eviction may invalidate a
  capture still in use. There is no historical chunk cache.
- Generation, meshing, conversion and native upload run outside simulation
  barriers and ECS observer leases. Transient executor or quota rejection
  leaves current intent pending for a later progress wake or tick.
  Superseding intent cancels obsolete work cooperatively. If the latest
  target cannot reserve capacity while older work drains, hold only its
  descriptor. Permanent errors surface through the host's Core supervision
  with the affected coordinate and generation. Keep the last coherent
  region until stop and admit no partial replacement.

## Meshing and renderer handoff

Mesh each inner chunk from its immutable center and six face-neighbor
observations. Use the existing deterministic traversal, winding and
solid-to-air rule. A shared solid face is suppressed on both sides. A
solid-to-air face is emitted once by its solid owner. Inside the generated
world, a missing neighbor blocks the build. Known outside-world air has
an explicit marker distinct from a missing chunk. The mesh result carries
its intent and all source identities, including the halo. Match them against
the current target before publishing. Build and stage the whole inner set
for WP05 so seams and unloads change together. Canonically order instances
by signed X and Z. Empty chunks remain represented in region coverage
even when their mesh has no draw indices.

The portable handoff accepts at most nine instances in a complete region
generation. Reuse WP04's distinction between desired, installed and last
successfully presented generations. Declare newer intent before dispatching
its region mesh build, drop superseded pending geometry and reject stale,
undeclared, duplicate or oversized submissions. Admission is nonblocking.
An older installed set remains a coherent fallback while a candidate waits.
A successful commit replaces the full set at once, including removal of
leaving instances. A clear-only frame does not advance presented coverage.

D3D12 uses the existing render owner, target and watchdog admission and
retirement evidence. Stage privately and recheck generation and terminal
conditions at commit. Retire old GPU references only after the last draw
and admitted Present are proven complete or confirmed device removal
permits settlement under WP04. A failed signal or fence sentinel alone
cannot authorize release. Stop closes all new admissions, drops unowned
pending inputs, wakes owners and lets Core supervise bounded settlement.
Late workers and native operations keep custody until they can release safely.

## Initial resource limits

These are proposed hard ownership limits for WP05, including transitions and
shutdown. Track current and high-water counts. Reserve before allocation or
dispatch, including for work whose completion has been delayed. Releasing a
reservation requires settling its work and dropping the associated references.

| Resource per scene | Proposed limit |
| --- | --- |
| Active draw and collision coverage | At most 9 inner chunks |
| Active and target data coverage | At most 25 snapshots per region and 50 distinct snapshot slots across active, target and retained worker/query inputs |
| Raw voxel data | 50 × 4,096 bytes for snapshots plus two 4,096-byte generation scratch buffers |
| Outstanding load work | Two reserved loads total, including completed results awaiting consumption. Pick further work from the current bounded footprint |
| Outstanding region mesh work | One worker or unconsumed result, plus one replaceable target descriptor with no captured snapshots |
| Mesh geometry | WP03 vertex and index caps per chunk, at most 9 instances per region |
| CPU mesh payload | 96 MiB total for owned position/index arrays, conversion copies, temporary geometry and handoff references. Count shared storage once and reserve transient copies |
| Provider geometry | One pending CPU region reference, at most one candidate in preparation, and at most two native geometry sets including staged or retiring sets |
| Native geometry bytes | 64 MiB total for geometry allocations including alignment, uploads and retained staging |

The byte limits cover terrain geometry and raw occupancy payloads. Report
process working set and frame timing separately with the adapter, driver,
build, sample count and route. The contract makes no frame-time claim.
If headroom cannot be reserved, defer the current intent and retain the
active fallback. Reject content exceeding a per-chunk or per-generation
limit with a diagnostic. Repeated anchor changes must not create an
unbounded FIFO or another retained footprint.

## Collision and movement

- Query only the immutable active **inner** occupancy during a tick.
  Distinguish `Solid`, `Air`, and `Unavailable`. Halo data can inform
  meshing, while entering it requires published inner coverage.
  Unknown space blocks movement at its boundary. A player never acquires
  floor support from missing data. If support at the last accepted pose
  becomes unavailable, keep that pose, report blocked streaming and
  wait for coverage. Keep support and footprint covered across a region
  handoff. If a publish would violate that invariant, defer it.
- Use an upright axis-aligned box with half-width and half-depth 256 and
  height 1,792 in units of **1/1,024 voxel**. Store the horizontal center
  and the bottom of the box. Sweep X, then Z, then Y against occupied unit
  cells. Clamp each axis to the nearest blocking plane before advancing
  the next one. Contact at an exact plane is allowed. Positive overlap
  on both perpendicular axes is required to obstruct movement, so moving
  tangentially along a wall works. Sweep the entire displacement. The
  primitive accepts at most eight voxels per axis per call and enumerates
  at most 96 candidate cells per axis, including endpoint contact. Check
  coordinate arithmetic before visiting cells. Reject invalid poses and
  displacements outside these bounds before modifying the state.
- Resolve motion on the 60 Hz fixed simulation base tick, independent of
  the default 20 Hz gameplay cadence. The WP05 fixture
  commands at most 128 horizontal units per tick along one axis. Use
  gravity of 8 units/tick² and cap downward speed at 256 units/tick.
  Downward solid contact sets grounded and zeroes vertical speed.
  A streaming-blocked displacement is not accumulated or replayed.
  An unavailable render target also does not accumulate movement debt.
- The rendered WYU demo permits motion only where the swept footprint
  and support cells are in both the active collision region and the
  last successfully presented inner coverage. Until the first valid
  present, hold the spawn pose. While a replacement is pending, motion
  within the intersection can continue. Headless tests use the active
  collision region and need no renderer. This guard keeps WYU from
  walking onto collision data that is still missing from the displayed
  scene. Use matching occupancy identities if coverage overlaps.

## Fixed route and fixture

Use seed `0x57505535`, generator version `1`, X chunk range `[-4, 4]`
and Z chunk range `[-2, 2]`. Chunk Y is zero. The generator marks Y 0
through 3 solid and Y 4 through 15 air, then places a wall at world X 34,
Z 7 through 9, Y 4 through 7. Add visible off-route markers at Y 4 and 5
where Z is at least 13 and the unsigned value
`(seed ^ (uint32(x) * 73856093) ^ (uint32(z) * 19349663)) mod 17`
is zero. Interpret signed X and Z as two's-complement unsigned values
and multiplication modulo 2³². The route never touches those markers.
Each reload must regenerate identical occupancy for the same seed,
version and coordinate.

Spawn at `(-20.5, 4, 7.5)` in voxel units with zero velocity.
Run the following input phases on successfully published base ticks. The route
harness supplies zero horizontal input at each phase start and each anchor
change until the new anchor is active and, in the windowed run, presented.
Those explicit holds are logged separately from the listed motion ticks.
Fail if a readiness hold exceeds the harness's 10-second wall-clock
watchdog. The deterministic fixture controls executor completions and
advances logical ticks independently of wall time.

| Phase | Motion input | Ticks | Expected position at end |
| --- | --- | ---: | --- |
| Outbound | +X, 1/8 voxel per tick | 424 | `(32.5, 4, 7.5)` |
| Wall contact | +X, same speed | 24 | `(33.75, 4, 7.5)` |
| Along the wall | +Z, same speed | 32 | `(33.75, 4, 11.5)` |
| Return | −X, same speed | 434 | `(-20.5, 4, 11.5)` |

Verify all four crossed X planes (`-16, 0, 16, 32`) in both directions,
grounding and feet at Y 4 throughout, no solid penetration, and the last
14 wall-contact commands clamping at X 33.75. At the east checkpoint,
the old western edge at chunk X `-4` must leave custody after earlier work
settles. On return, that chunk must be loaded again with equal cell bytes
and a new load identity. At final settlement the eastern edge at X `4`
must likewise leave custody. Stop after the return and verify that no work,
mesh or GPU allocation is still owned after settlement.

## Acceptance before handover

| Gate | Required evidence |
| --- | --- |
| Portable lifecycle | Deterministic route at least twice with identical checkpoints. Delay and reorder loads, supersede a finished mesh, force capacity pressure and fail world publication. Verify bounded reservations, stale rejection, complete publication and unload/reload. Close during generation and meshing and check custody before and after settlement. |
| Seam and collision | Cover signed boundaries on both axes, solid/air faces across all neighbor directions, known exterior and missing neighbors, movement across a loaded seam, blocking at an unavailable seam, delayed floor coverage, large in-range sweeps, wall tangency and exact contact. Assert numeric endpoints and occupancy invariants on every route tick. |
| Renderer | Fake owner verifies atomic set replacement, coverage removal, stale and oversized rejection, and installed/presented distinctions. Windows WARP readback verifies color/depth on both sides of a retained seam and removal of leaving geometry. WARP and NVIDIA exercise replacement and stop with active Debug Layer and DRED, checking GPU retirement, native shutdown and live-object reports. |
| WYU integration | Restore exact verified Groundwork packages from clean Windows checkouts. Run the fixed route on NVIDIA with Debug Layer and DRED and log seed, input ticks, readiness holds, presented generations, checkpoints and resource high-water marks. Verify visible terrain and wall contact and complete native shutdown. Record frame-time and working-set observations with environment metadata. |

Implementation order after contract approval is portable region and collision
with deterministic tests, bounded renderer handoff and native qualification,
then package archival, WYU integration and the clean Windows handover.
