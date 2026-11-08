# WP11 – Procedural volumetric planet generation: implementation contract

**Status:** PROPOSED — architecture review and explicit owner approval required before implementation.
**Applies to:** Groundwork (engine) and Worlds Yet Unseen (game), Windows x64 / .NET 10 / Direct3D 12 single-player alpha.
**Baseline:** accepted WP10, roadmap on Groundwork master; existing WP08/09 radial 64 m fixture is historical, **not a compatibility target**.
**Ownership:** Groundwork owns reusable planet addressing, deterministic sampling, sparse voxel volume, meshing, runtime admission, rendering integration, persistence primitives and diagnostics. WYU owns seeded recipes, content/material semantics, controls, user experience, game save payloads, start location and integrated playtest.

## Decision: deliberate clean break

WP11 may replace or remove public and internal planet APIs, legacy radial-only terrain assumptions, save schemas, sample keys, packaging boundaries, smoke modes and tests whenever doing so improves the long-term architecture. Do not carry adapters merely to preserve WP08/09/WP10 prototypes. Replace obsolete tests with stronger tests of the new contract; preserve correctness, resource ownership, crash safety, deterministic simulation and GPU retirement invariants. Existing test cases are not grounds to retain a flawed design.

Previous alpha save data need not load or migrate. However, an incompatible save must be reported explicitly and left intact; never silently reinterpret, overwrite or delete it. New save lineage/namespace and an intentional "start new world" flow prevent accidental loss. Replacing the old flight mission is permitted; substitute a new end-to-end takeoff/orbit/landing regression against the new planetary terrain. Do not claim an obsolete test path remains supported.

## Product outcome

A player can start a new world from an exact seed and generator version, walk through varied large-scale terrain without seams, remove/add ground, leave and return, save/quit/restart and recover the identical world. The planet is suitable for Earth-scale coordinates without allocating global voxel grids. A volume-based contract allows future caves, overhangs, deep geology and tunnels. WP11 must test synthetic enclosed cavities/overhangs in the engine, but authored/procedural cave ecosystems, ores, vegetation and weather are deferred.

## Mandatory requirements

### R01 – Identity, descriptors and coordinate frames

- Immutable PlanetDescriptor: stable world/planet identity, 64-bit world seed, explicit algorithm/generator version, terrain storage schema, radius in metres, generator parameters or canonical parameter digest, coordinate convention and material catalog identity.
- Authoritative planet-centred positions and movement are double precision. Integer canonical sample/brick addresses use checked 64-bit coordinates; never use floating-point render coordinates as save or edit keys. Use a local rebased float render frame only after double-precision subtraction.
- Canonical face/edge/corner mapping must be unique across cube-sphere charts and LOD; define tie-breaking, overflow checks, pole and antimeridian behaviour if exposed, and deterministic enumeration/order. Local 3D brick addresses must not be ambiguous at face seams.
- Qualify at 6,400,000 m planet radius; 64 m fixture may be deleted. Any supported radius must be explicitly validated against numeric and address-domain limits.
- The authoritative identity of a volumetric sample depends on planet descriptor and canonical 3D lattice address, **not LOD**, worker timing or camera location. A derived mesh/LOD artifact additionally includes its LOD, revision and render-origin identity, but those are not save/edit keys.

### R02 – Deterministic generation

- Define pure generator entry points for canonical spatial sampling: signed density (negative solid, positive empty, zero surface), base material, and optional large-scale climate/landform attributes reserved for future consumers. No window, GPU, wall-clock time, shared mutable RNG or WYU types in the Groundwork interfaces.
- Use a specified deterministic 64-bit seeded hash/PRNG, domain-separated streams and quantized/checked output where necessary. Explicitly version math/noise functions and evaluation ordering. Jobs completed in different orders yield identical canonical results.
- Generator composes large-scale landmasses, mid-scale mountains/valleys and local relief with stable continuity. WYU configures values/recipes; Groundwork hosts generic graphs/algorithms.
- A planar radial height function can be one term of the initial density recipe, but the exposed format, storage, collision and mesh APIs MUST be volumetric; no permanent one-height-per-direction restriction.
- Identical seed/version/parameters produce equal canonical samples and hashes across runs and supported Windows x64 CPUs; different seeds must change meaningful terrain features in fixed test samples. Parallel vs serial generation produces identical bytes.

### R03 – Sparse volumetric representation

- Use sparse indexed 3D bricks/tiles, not a dense planet-wide allocation or voxel-per-ECS-entity. Initial fine brick target: 32 cells per axis, with explicit shared boundary samples; finer physical resolution is a descriptor parameter, initially 1 m/cell near the player, subject to measured tuning.
- Model immutable generated base content plus a separate bounded sparse edit overlay with canonical addresses, revision and source version. Treat ungenerated, empty, solid and loading/unavailable distinctly. Material IDs are explicit, stable, versioned, and are not UI lore.
- 3D volumetric samples must support a synthetic fully enclosed cavity, overhang and disconnected solid shell. No code may assume exactly one radial intersection.
- Validate work descriptors, counts, overflows, allocation sizes, source version and lease ownership before allocations; identical bricks may be shared immutably.

### R04 – Meshing and geometric seams

- Extract a manifold/consistently oriented zero-isosurface from bricks; select and document an algorithm with a proven multi-resolution transition scheme during architecture review. Algorithm selection must include comparative deterministic fixtures, not a late ad-hoc seam patch.
- Shared vertices, tangents/normals, materials, LOD transitions and chart edges must agree; no cracks, T-junction visibility, degenerate triangles or inverted faces. Cavity/overhang fixtures must be visible and have matching collision.
- Generate immutable meshes outside render and simulation critical sections. Publish matching mesh, collision and origin identity at Core finalization. Admit the visible frame separately through the commit permit in C00-3.
- Verify topological seams in pure tests; WARP color/depth readback and NVIDIA hardware with Debug Layer/DRED on changes to provider paths.

### R05 – Streaming and bounded scheduling

- Select local volumetric detail by view distance and required collision/support, and remote spherical coarse representation by LOD; maintain a support halo that covers admitted player motion and edit operations.
- Bound active/staged tiles, workers, queues, retained readers, CPU mesh and GPU allocations including retiring resources. Explicit initial guardrail defaults for a reference Windows desktop: up to 4 generation jobs, 2 meshing jobs, 1 replaceable staged generation, no unbounded waiting queue; 512 MiB total terrain CPU payload and 256 MiB native terrain geometry. These are **design ceilings to validate and revise by measured evidence before implementation approval**, not performance claims.
- Camera/intent changes supersede obsolete candidates without abandoning owned work; stale results are rejected. Under pressure, keep last coherent terrain and report backpressure; never spin, allocate without quota, or publish unsupported movement.
- Generation and meshing execute off 60 Hz simulation tick and UI/render owners. Cancellation, pause, terminal stop, GPU retirement, device removal and failed task paths release resources only when safe.

### R06 – Movement and collision

- Collision queries consume the same committed volumetric generation and edits as the visible geometry, with independent finite shape sweeps (not solely radial support). Unknown coverage is explicit and blocks unsupported movement.
- Support grounded walking and existing radial gravity without assuming a single ground point on each ray. The synthetic cavity, steep slope, overhang, seam, rebase and unloading route must pass.
- Movement requiring new coverage holds until the collision generation is committed and its first matching visible frame is accepted by the provider. The frame, generator and collision identities must match through host submission.

### R07 – Editing and persistence

- Single-player edits follow the sealed intent order in C00-1C. Implement add/remove operations and deterministic conflict/revision checks. Reject stale edits as whole transactions. Edit impact includes neighbouring bricks, meshes, collision and the persisted overlay.
- New save schema and planet lineage may replace prior formats completely. No mandatory migration. Encode descriptor, generator version/parameter identity, terrain edit overlay, player/world checkpoint and checksums with bounded canonical ordering.
- Use the C00-5 classification precedence with NTFS atomic publication, exclusive slot lease, retry idempotency and stale-base rejection. Unclassified or incompatible saves are preserved and block writes.
- Save during pending generation/edit, unload/reload, exit/restart, incompatible data, partial/corrupt writes and lost acknowledgement must be covered by deterministic tests.

### R08 – Render and game composition

- Integrate the new representation with D3D12 and the existing Core ownership/watchdogs. A provider architecture change is allowed but requires measured and hardware-qualified clean shutdown.
- WYU supplies a genuinely seed-dependent landform recipe with plains, hills, valleys and mountains; at least one safe seeded start/landing area and visible landmarks. Do not implement vegetation, climate simulation or ore extraction in WP11.
- A playable opt-in procedural-world path and a new flight/touchdown integration trace demonstrate surface, orbit, descent and edits. Cutover of default alpha flow is allowed after explicit product acceptance; removing obsolete prototype routes is allowed with documentation update.
- All engine packages are immutable and verified; WYU consumes exact built versions via NuGet with locked restores, not source links.

### R09 – Diagnostics and validation

- Diagnostics expose seed, generator/schema version, planet identifier, camera origin, tiles and meshes active/staged/retained, queue admission, generated/meshed bytes, memory high-water, terrain generation time, LOD and collision coverage holds. No unbounded logs on hot paths.
- Tests: golden sample hashes across deterministic fixtures, serial/parallel and repeated runs; extreme coordinate precision; all twelve cube edges and eight cube corners; mixed-LOD transitions and cavity/overhang; edit/unload/reload/save/restart; cancellation and stale work; low-memory quota rejection; writer termination; WARP readback and NVIDIA debug live-object shutdown.
- Benchmark reproducibly on a documented 8-core/32-GiB class Windows machine plus optional workstation. Record adapter/driver, Windows/runtime/SDK, seed, route, generator version, CPU/GPU memory, generation/meshing latency distributions, waits, queue peaks, p50/p95/p99 frame times, draw calls and sample count. Hard performance acceptance thresholds are set from baseline evidence in the architecture review prior to sign-off; no invented FPS claim.
- Keep 60-Hz fixed simulation deterministic under the same player input and authoritative admission journal. Timing measurements are not semantic inputs.

## WP11-C00 architecture clarifications — review candidate (2026-11-08)

These clarifications resolve five author-side preflight gaps sufficiently for **independent architecture review**. They are proposed contract decisions, **not** four completed specialist reviews or measured performance acceptance. A material change requires a new frozen head and fresh/affected independent reviews. C00 must record any final amendment, decision rationale and owner sign-off before implementation.

### Owner-confirmed design baselines — 2026-11-08

The owner confirmed three **WP11-C00 architecture directions** for independent review, on top of the earlier clean-break approval. These are approved design baselines **to challenge**, not verified implementation results or final PR approval:

1. **Canonical volumetric addresses:** A single planet-centred signed Int64 Cartesian lattice owns all density vertices, cells, edit identities and brick keys. Cube-sphere faces, render LOD and rebased float origins never own or alter persistent voxel identities. A 32-cell brick and 1 m finest-cell reference are **initial tunable parameters**, not permanent gameplay resolution limits.
2. **Collision Coverage Admission:** Streaming anticipates the swept movement envelope, velocity and stopping distance. Never treat unknown collision coverage as empty. If a movement or terrain interaction needs missing or non-admitted coherent mesh/collision coverage, hold or reject **only that affected action** at the last valid state, while independent simulation systems continue at their fixed tick. Do not accrue and later burst-replay movement deltas. In orbit, use appropriate broad-phase/body/vehicle collision representations, escalating to surface volume coverage before atmospheric descent, touchdown or proximity contact.
3. **Meshing reference candidate:** Use a pluggable meshing interface and **Marching Cubes with validated Transvoxel-style mixed-LOD transitions as the first reference implementation/prototype**. Develop a constrained Dual Contouring / Surface Nets comparison on identical deterministic adversarial fixtures. The production algorithm, seam policy and hard memory/latency budgets remain **unselected and unmeasured** until the comparative C00 gate is completed.

For canonical edits, assign **material identifiers to uniquely owned cells** and **geometry-defining signed density to globally identified lattice vertices**. Any occupancy/solid-empty query is derived from the authoritative density field; independent cell occupancy cannot contradict it. A brush is a deterministic ordered transaction with a canonical bounding set, matching material changes and one expected world revision. Recompute/invalidate every neighbouring brick that samples changed density, including edges/corners and LOD dependents. Never save a duplicate authoritative edit under a cube-face or render-LOD key.

For collision admission, stage matching immutable geometry and collision. Commit at the Core barrier, then use the commit permit for the first visible frame and release affected movement only on matching provider acceptance. Follow C00-1C and C00-3. A readiness hold never accumulates movement deltas or pauses unrelated systems.

Reviewers must independently attempt to falsify these assumptions; any P0/P1 findings reopen the relevant decision. No specialist review, Windows execution, meshing prototype, hardware benchmark or final merge approval is implied by this record.

### C00-1A — Authoritative density and material semantics (R02–R04, R06/R07)

**Owner-approved architectural rule (2026-11-08):** The versioned, signed, three-dimensional density field is the **sole authoritative source for geometric solidity and the zero-isosurface**: negative density means solid, positive means empty, and zero defines the surface with deterministic tie-breaking. There is no independently authoritative per-cell occupancy flag able to contradict that field. Sparse-data availability is a separate state (`Ungenerated`, `Generating`, `Known`, `Unavailable`) and **unknown density must never be interpreted as empty**.

A uniquely addressed cell may own a versioned *material identifier* and gameplay attributes, but material identity is not another solid/empty predicate. Material interpretation at and around an isosurface must be deterministic and tied to the same density/edit revision. A density edit that adds or removes geometry must update or invalidate dependent material state as one coherent edit transaction; it cannot leave a persisted "empty" occupancy bit contradicting solid density. If a legacy `occupancy` field is retained temporarily for a prototype, it must be *derived output* or removed in the WP11 clean break, never a second source of truth.

The generator base, density-edit overlay and material overlay together form one immutable versioned **TerrainState**. All mesh extraction, collision geometry, editing queries and checkpoints read a coherent committed TerrainState. No subsystem may decide geometric solidity from independently mutable cell flags or reconstruct an incompatible combination of revisions. Versioned fixed-point/quantized density and the exact zero rule must be specified before implementation.

**Required negative fixtures:** contradictory material/old-occupancy value versus density sign; a shared vertex on a brick face/edge/corner; narrow tunnel, hollow cavity, overhang and detached solid; edit→mesh→collision→save→restart must yield the same solid/empty interpretation and geometry revision.

### C00-1B — Atomic, conflict-safe volumetric edit publication (R03/R04, R06/R07)

**Owner-approved architectural rule (2026-11-08):** Each add/remove/sculpt edit is a **single deterministic world-scoped transaction**, with `WorldId/PlanetId`, canonical edit identifier and tick, expected committed terrain revision, ordered global density-vertex and material-cell writes, and a complete dependency/invalidation footprint (brick faces/edges/corners, normal stencils, LOD transitions, collision and save overlay). Global sample identity is unique even when multiple workers mesh neighbouring bricks.

Workers may calculate immutable candidates and derived meshes in parallel. C00-1C defines deterministic eligibility, terrain conflict authority and the shared publication and save cut. C00-3 defines renderer readiness and the visible path. An edit or its derived data cannot partially change authoritative readers, collision or saves.

**Required negative fixtures:** two overlapping edit jobs arriving in reverse order, a stale expected revision, disjoint concurrent edits, one edit crossing eight finest-level brick corners, a shared boundary vertex and mixed LOD seam, cancellation after some meshes finish, a failed Present, a save during staging, and unload/restart after an acknowledged commit. In every case, the committed state and persisted overlay must be whole, deterministic and crash-safe, or unchanged.

**Review gate:** Both C00-1A and C00-1B are decisions for the reviewed candidate, not a declaration of validation. Independent L3A-ECS must scrutinize them at the same frozen head as the other specialists. Any P0/P1 contradiction reopens the relevant contract before final approval.

### C00-1C — One authoritative terrain publication boundary (L3A-01 resolution candidate)

**Single authoritative commit and tick.** The world-scoped Terrain Publication
Coordinator owns typed terrain conflict scopes and the sealed edit queue. Workers
produce immutable candidates only. The Core authority gate finalizes
`CommitResults(T)` as the single swap of an immutable `WorldPublication`.
It contains the authoritative finalized tick T, world/planet identity, ECS and
player state, density/material overlays, terrain revision and collision/mesh
identity. Token validation, affected pose validation, ECS dispositions, edit
receipt and publication are one outcome. Validate source terrain revision and
dependency versions against the current root. Unrelated ECS updates do not
invalidate a candidate. Shared readers pin one publication under a bounded lease.

The scheduler consumes the fixed-step debt and advances its authoritative
finalized tick exactly once for a successful finalization. A finalized stamp
survives Stop or observer failure. `TryPublishState` is a downstream observer
delivery with a separate delivered tick. Delivery failure or Stop may suppress
delivery and report T−1 as the last observer tick. Neither can uncommit T,
refund its time or run T twice. A pre-swap Stop leaves T−1 and the old root.
Save/restore and replay use the authoritative finalized tick, never observer
delivery. On restart begin at the next tick of the restored durable checkpoint
with a new wall-clock origin and no old pending step debt.

**Dependent ECS effects.** A brush declares every jointly promised gameplay
effect, including inventory and contact effects, with brush ID and source
revision. Stage these effects privately, outside the generic ECS dispositions.
The same finalization commits all or none. If any effect cannot be applied at
this cut, including a structural command requiring BeginTick deferral, reject
the whole brush before the swap. Never enqueue its effects in the generic
deferred batch. Standalone unrelated ECS batches retain their usual phase
semantics. A committed receipt is derived from the swap, not a second mutation.

**Cross-path ECS conflicts.** At finalization use one conflict check against
the barrier-entry ECS root. It includes versioned component reads and
writes, entity/partition membership and structural wildcards for both the
private brush effects and regular ECS batches. First analyze ordinary
batches using the existing canonical ECS order. For any overlapping
read/write, write/write or structural scope of an ordinary batch eligible
to commit or defer at that barrier, reject the entire brush as
`EcsConflict` and leave the ordinary dispositions unchanged. Read/read is
allowed. The brush cannot commit a structural effect through BeginTick
deferral. For nonconflicting effects revalidate their versions and apply
them to the composed ECS root before the one swap. No receipt can report
success before this decision. Journal the conflicting scope and outcome.
Test two writes to the same inventory component/version, read/write
conflicts and entity destruction/deferral with both worker orders. Assert
the final root, both receipts and a save at the cut. A disjoint ordinary
batch still commits.

**Save cut.** Capture one finalized `WorldPublication` after the outcome of
`CommitResults(T)` under a pre-reserved bounded checkpoint lease. Allocate or
reject capture before the swap, then bind the lease to that finalized root
without a fallible extension callback. Save worker I/O is outside the barrier.
A pre-edit capture is N; a post-edit capture is N+1 even under the view hold.
Each slot admits one writer with a bounded FIFO of captures, in (finalized tick,
capture ordinal) order. A capture older than the last admitted cut cannot
overwrite it. Bind its expected slot base at admission to the writer, retain
the identical retry identity and payload until settlement, and never repackage
a stale capture with a fresh base. Slot generation, edit ID and terrain
revision are distinct. A coherent ungrounded pose is saveable; restore rebuilds
collision and starts a fresh hold/coverage admission before movement, retaining
the saved pose and velocity. An edit receipt proves the Core commit. Only the
separate durable slot receipt proves persistence.

**Order and semantic replay.** At input tick seal the bounded per-world order
by (tick, canonical brush ID) before dispatch. Overflow rejects that tick's
edit set as a whole. Identical retries return the retained outcome; conflicting
IDs and requests below the closed tick watermark reject. Only the first
unresolved intent may enter renderer staging or commit. Speculative successor
work is advisory, cannot consume protected capacity, and cannot determine a
terminal disposition. After a commit revalidate each successor, including
disjoint intents, against the new revision and reject Stale rather than rebase.
An explicit resubmission uses a new ID. The next edit cutover starts only after
the prior view has opened or the affected epoch has settled.

Live execution may produce different effective ticks with different readiness.
The semantic replay input is the recorded player input **and** an ordered
journal of authoritative admissions. At each BeginTick and CommitResults
ingress cutoff, the Core authority assigns applicable events (tick, phase,
ordinal), recording full identities and outcome: intent admission/reject,
hold close/open, Ready eligibility, commit/reject, rebase, stop/epoch fault,
watchdog and save capture or denial with its selected cut. Events arriving
after a cutoff take effect at the next eligible barrier. Revocation can fence
a current attempt immediately at the authority gate; the ordered journal
records which swap it preceded or followed. If watchdog expiry wins the gate
before the swap, cancel the affected pending epoch without any later commit in
that epoch; if the swap wins, preserve the committed root and fault its view.
Compare semantic state under equal recorded events, not equal raw input alone.
Replay waits outside simulated time for required matching artifacts at the
recorded cut (bounded by a test timeout); missing or mismatching evidence fails
replay. Unrecorded early artifacts cannot advance the effective tick. Live
unrelated systems do not wait on this replay-only rule.

**Bounded semantic evidence.** Core assigns a monotone gate sequence
to semantic operations, including ingress cutoffs, immediate revocation,
finalization and hold release. Persist the gate order within the recorded
tick/phase, including revocation after the cutoff but before the swap,
or after the swap before evidence delivery. Replay injects each event at
that gate position and verifies its disposition. Record the finalized
stamp and its mandatory record together under the authority gate. There
is no external logger callback at this point.

Reserve bounded journal capacity before accepting any regular operation
that can change authoritative WP11 state. Use preallocated record storage
and a bounded FIFO for the journal consumer. Acknowledged handoff frees
credits, and a slow consumer cannot drop or overwrite semantic records.
Keep a separate pre-reserved terminal evidence cell for a revoke or
journal fault even if the ordinary queue is full. Gate sequence and
terminal cell are covered by the same authority as the state transition.
On saturation or loss of evidence, revoke affected admission and stop the
affected journal epoch before further WP11 publication. Report the last
complete sequence and mark the capture incomplete. An incomplete capture
cannot qualify as a replay pass. The semantic journal has its own
capacity and is independent of diagnostic logging. Test full-queue
revocation, consumer failure, cutoff-before-revoke-before-swap, and
swap-before-revoke-before-handoff. A later fault cannot erase an already
recorded swap.

**Required fixtures:** reverse completion, Ready/watchdog in both orders,
stale and duplicate IDs, cancelled head, ECS effects on reject and deferral,
Stop between finalization and observer delivery, save before/after swap and
after display fault, reverse I/O completion and ungrounded restore. Assert
journal identity, consistent finalized tick, root, collision and exact saved
payload, plus bounded settlement.

### C00-1 — Canonical volume addressing across faces and LOD (R01, R03, A01/A03)

**Proposed design choice:** Make a single planet-centred signed **Int64 Cartesian lattice** authoritative for volumetric density sample coordinates and sparse brick keys. A planet descriptor fixes the cell size (initial reference 1 metre per finest cell), orientation, centre, coordinate origin and generator-versioned sampling quantization. For each axis use Euclidean floor division, including negative values, to derive a brick index and the local cell index in `[0, brickEdge)`. The initial candidate brick has 32 cells per axis; its 33-by-33-by-33 vertex-sample grid includes shared boundary samples. Those samples are *logically addressed* by their global integer lattice triples and evaluated identically for all adjacent bricks; no independent face-local copy may be saved as a second authoritative edit.

The existing six cube-sphere charts are **non-authoritative view/LOD addresses**, not identities for volumetric samples or persisted edits. Far spherical patches query the same versioned volume field through a documented canonical sampling transform. A face edge or corner cannot own a different edited value because edits are keyed solely by the planet identity and 3D integer lattice address. Rendering may use local-origin rebasing and floats, but render positions must never be fed back into canonical world keys.

Material edits address uniquely owned *cells*; solid/empty shape editing and density sculpting address globally identified *lattice vertices*. A cell's material cannot independently override the solidity inferred from density. Brushes enumerate both effects in a deterministic, versioned, atomically admitted transaction; forbid ambiguous half-cell coordinates. At a brick boundary, exactly one canonical cell owner changes material metadata, while every mesh neighbour dependent on shared density samples is invalidated. Distinguish generation identity from rendering LOD and local-origin revision. For every conversion require explicit ties-to-even rounding or another approved deterministic tie rule; define overflow and nonfinite-input rejection **before** any cast or allocation. Use checked Int64 arithmetic for coordinate/brick construction and validated domain limits for radius/resolution. Exceeding the supported domain must fail closed, not wrap.

**Required evidence:** planar negative-coordinate tests; 3D brick faces/edges/corners (including triple-neighbour samples); cube-chart 12 edges/8 corners; level transitions; local-origin rebases; 6,400,000 m radius stress; duplicate-free canonical edit keys; roundtrip persistence of edits near seams. The mapping is a design decision subject to the independent review, not a proven implementation.

### C00-2 — Numeric determinism and generation versioning (R02, A02)

**Proposed rule:** Canonical seed hashing, spatial noise, density quantization and persisted output are specified algorithms, not merely calls to an unversioned OS or .NET RNG. Use a fixed, test-vectored 64-bit integer hash/PRNG with separate named streams for macro terrain, mid-scale relief, detail and materials. Canonical sample coordinates and generator parameters are serialized with explicit endianness and version. Use signed fixed-point / checked integer arithmetic and a frozen round-to-nearest, ties-to-even rule for authoritative density results where precision/range allows. If a floating computation is unavoidable, its numerical steps, input quantization, output rounding, FMA/reassociation policy, nonfinite rejection and cross-CPU golden vectors must be explicitly qualified before treating its bytes as authoritative.

Never key generation or ordering on worker scheduling, clock time, platform thread IDs, locale, unordered dictionary traversal or GPU output. A generator-algorithm change increments the generator identity; equal seed with a changed algorithm is **not** silently treated as the same saved world. At minimum the golden harness must run repeated fresh-process, serial, reversed and randomized job-order evaluations on supported Windows x64 CPUs, comparing canonical sample and serialized chunk hashes; distinguish binary invariants from allowed timing differences. Do not assert cross-architecture bitwise identity without tests.

### C00-3 — Simulation-authoritative coverage versus GPU presentation (R04–R06, C01–C03)

**Core and renderer admission.** The Core authority gate is the linearization
point for hold state, Ready consumption, root swap, view revocation,
visible-submit registration and acknowledgement acceptance. The renderer may
make a short gate operation to record a submit or revoke a view. No driver call,
GPU wait or callback executes under the gate. A terminal loss observed by
software revokes at this gate as the first fault action, before queuing
diagnostics or recovery. Concurrent loss/rebase/Stop and swap are ordered
there. A physically earlier but unobservable loss cannot revoke retrospectively.
Tokens and receipts bind world, candidate, source/target terrain revision,
committed terrain/collision identity as applicable, renderer epoch, monotone
view generation, origin and frame identity. Use a new view generation even
when reusing the same committed terrain and origin.

1. `Held`: before requesting the mask or changing view authority, Core closes
   the affected action gate at BeginTick. Derive the affected scope from the
   conservative sweep/contact dependency envelope including gravity and stopping
   distance. Re-evaluate the scope for subsequent actions and fail closed for
   unknown coverage. Hold the entire affected movement transaction, including
   pose, velocity integration, gravity, jump edge, contact changes and dependent
   gameplay effects. Explicitly discard held jump edges and movement deltas.
   Retain only current continuous input. Unrelated actors/systems continue at
   their normal fixed rate.
2. `ReadyToCommit`: build matching mesh/collision under the reserved budget.
   GPU uploads are fenced. An accepted neutral mask frame for the current view
   and settlement or fencing of preceding submissions are required. Staged
   terrain may render offscreen. It cannot enter the visible swap chain.
3. `Committed`: the Core barrier consumes one matching Ready token and swaps
   the root as in C00-1C. Validate the retained pose and contact dependencies
   against the new collision. Reject unknown coverage or penetration. If
   support disappears, clear the contact and set ungrounded in that same swap,
   retaining pose and velocity until an eligible movement tick. The commit
   emits a visible permit. A rejected or cancelled pre-swap attempt leaves N
   authoritative. Invalidate its token and restore N through the same held
   view re-admission path, or terminate the affected path.
4. `Registered`: before the visible driver submission, the renderer registers
   a frame using the permit at the authority gate. If revoked first, registration
   fails. Registration first means already in flight, with ownership retained
   until safe settlement even if revocation follows. Do not use that frame to
   reopen a revoked view. Rebase revokes the old view and closes its hold
   before a new view can be requested. On terminal stop close admission
   immediately and retain custody. Unrelated fixed ticks are not gated by
   renderer settlement.
5. `VisibleAccepted`: successful provider Present for the registered content
   frame plus successful submission/retirement Signal yields evidence, not
   action release. Busy, test-only Present, occlusion and failed Signal do not
   qualify. Core consumes evidence at BeginTick, revalidates the current
   root terrain/collision, epoch, view, origin, registration and hold under
   the gate and opens only that view's hold. The committed frame replaces
   the neutral mask. If revoked after opening, close the gate at once and
   fence any affected in-progress action at its next commit check. The first
   allowed movement is one normal tick with current input, including gravity
   if ungrounded. No backlog. Physical scanout and fence completion remain
   distinct from provider acceptance. Previously submitted pixels may linger.
6. `Recovery/Retired`: failed or delayed first Present after commit retains
   N+1 and its saveability. Keep the view masked/closed until re-admitted.
   At BeginTick the authority may issue a replacement permit for the same
   committed terrain/collision root without an edit swap, using a fresh view
   generation, matching rebuilt/reprojected mesh, mask acceptance and new
   epoch/origin. This also re-admits N after an aborted precommit attempt.
   Never reopen on a stale ACK. A terminal fault can close the affected view
   or stop the host. Release old, cancelled and in-flight resources only at
   proven reader/worker/native settlement. Failed Signal or a removal sentinel
   is insufficient. On deadline expiry report incomplete cleanup and retain
   the owner and credits in custody until safe evidence exists.

**Required fixtures:** mask success then pre-swap cancel, revoke before and
after registration, both observed device-loss/swap orders, post-swap failed
Present, rebase before/after ACK queueing, replacement permit without new
terrain revision, stale ACK, Stop before/after observer delivery, loss of
support while standing, running/jumping while digging, unknown swept coverage,
and first eligible gravity tick. Assert each gate, pose/contact, tick,
checkpoint cut, in-flight ownership and final release.

### C00-4 — Meshing and performance-budget decision gate (R04/R05/R09, B01/Q01)

The **owner-approved first reference candidate** is Marching Cubes with validated Transvoxel-style transitions, behind a replaceable meshing interface. It is **not yet the selected production algorithm**. The production surface algorithm and mixed-resolution transition mechanism are an **explicit measured C00 decision**, not an implicit promise. Compare at least **(a) Marching Cubes with a validated crack-free mixed-resolution transition such as Transvoxel-style transition cells**, and **(b) a dual-contouring/surface-nets family with explicit adaptive boundary topology**. An alternative is valid if equivalent seam/winding and cavity evidence is provided. Document topology consistency, material/normal interpolation, thin structures, sharp features, difficult mixed-LOD corners, cube-chart edges, deterministic vertex ordering, memory traffic and CPU meshing cost. A naive independent per-brick triangulation without boundary stitching cannot pass.

Provisional ceilings from R05 — 4 generating jobs, 2 meshing jobs, 1 staged generation, 512 MiB CPU terrain and 256 MiB GPU geometry — are **planning limits only**. Before C00 acceptance, choose documented reference hardware and scene seeds, benchmark both candidates or a defensible reduced prototype, publish actual high-water allocation and p50/p95/p99 times, and freeze explicit acceptance ceilings with justification. Count CPU active, pending, leased, obsolete and retiring buffers and GPU upload, active, candidate and retirement buffers; keep the workload bounded and reject reservations before allocation. A measurement not run must be marked `NOT MEASURED` and is a remaining C00 gate, not estimated proof. Geometry and memory budget tuning may change the 32-cell/1 m reference after measurements without a backward compatibility obligation.

C00 owns the reduced pre-approval comparison and capacity prototype.
It has no dependency on the production B01/Q01/Q02 packages. Freeze
the prototype evidence before opening those production gates. Q01 and
Q02 subsequently qualify the implementation with the frozen criteria.

**Protected progress credits.** The world coordinator admits a complete
resource vector before dispatch. It atomically reserves job/queue and lease
slots, CPU peak including retained results, upload bytes, resident old and
new GPU geometry, retirement slots/bytes, and temporary space for the
mask and recovery. Any unavailable credit denies the entire reservation
with explicit Backpressure before allocation. This outcome is journaled.
No actor waits or retries in a resource or authority critical section.

Partition the bounded total into disjoint protected envelopes for the
first unresolved edit, required movement coverage, and view recovery,
plus a speculative remainder. Their sum and active/retained baseline
must fit the measured ceilings. Later intents and streaming speculation
cannot borrow protected credits, including when the corresponding path
is idle. Recovery has its own capacity even if the edit still holds old
and new geometry. If a valid worst-case envelope cannot be reserved,
reject before cutover, or keep the affected action held on the existing
coherent root. Budget sizing and an admissible reference route must be
demonstrated before C00 approval. Higher priority never bypasses the
sealed edit order.

Account for every allocation exactly once throughout prepare, upload,
publication, cancellation and retirement. A transfer is acknowledged
before the prior owner releases its charge. A worker-slot release does
not free its result, lease or GPU charge. Cancelled speculative work
retains its credits until real settlement. Resume on confirmed credit
release or a recorded bounded disposition, never a polling retry loop.
At deadline, report incomplete cleanup and keep unsettled credits
charged. An exhausted protected recovery path stays masked/held or
stops safely. Q01/Q02 must test full speculative load, head and recovery
progress with old geometry retained, and per-transition balances at
each limit and ±1 byte/slot. The provisional numeric values are not
measurements.

**Required evidence:** enclosed cavity, overhang, thin bridge, multiple intersecting LOD transition edges, face corners and deliberately saturated streaming; repeatable machine+build+seed and allocation logs. Approval of a preferred candidate is contingent on these artifacts.

### C00-5 — Explicit new-save namespace and incompatibility safety (R07, D02/W03)

WP11 uses a **new world lineage and checkpoint schema**. The proposed Windows root is `%LOCALAPPDATA%\\WorldsYetUnseen\\Worlds\\v2\\<opaque-world-id>\\`, separated from existing alpha WP06/07/09/10 slots. Treat the root and `v2` segment as a *namespace identifier*, not an assertion that every earlier format was version 1. Choose/validate opaque world IDs and canonical path containment; prohibit path traversal, symlink escapes, ambiguous case or slot collisions. A newly created world reserves a unique empty slot before first write; an existing slot cannot be silently reused by "New World" even if parsing its header fails.

**Slot classification precedes recovery.** Under one exclusive slot
lease, classify both committed names on every Load and Save before
selecting the winner or granting write authority. A valid sibling does
not short-circuit the scan, regardless of generation. Classify each as
Missing, ValidCompatible, DamagedCompatible, Protected or AccessError.
DamagedCompatible requires positive evidence from a complete,
independently integrity-checked identity header that binds the supported
format, world/lineage, descriptor and slot identity. A damaged payload
alone may then allow fallback to a valid compatible sibling. Legacy or
unsupported format, incompatible lineage, unknown magic, short or
unverifiable identity, unsafe path or size, and other ambiguous evidence
are Protected. Classify independently of any header generation advertised
by an untrusted file. On access failure or any Protected member block
Continue and all writes to the slot, return a specific reason, and
preserve every byte even when the other member is newer and valid.
Offer a separate new world slot. This precedence supersedes the generic
WP06 malformed-member fallback where it would allow replacement of
protected data.

Recover from DamagedCompatible only with a valid compatible sibling.
With no valid member and any damaged member, block Continue and writes.
With both members Missing, only an explicitly reserved new world may
start at Empty. Check target identity again at publication under the
lease, including reparse protection. Stage to an exclusively created
operation-scoped private temporary file. Never select it as a committed
generation or overwrite an unclassified pre-existing file. Flush and
verify, then atomically publish to the allowed alternating target,
verify and acknowledge. A process interruption before publication leaves
the prior durable cut. After publication, rescan under the lease to
prove the result or return Indeterminate. An identical retry uses the
same payload, base and retry ID. The retry ID is structurally the
(slot identity, expected base generation, operation nonce) tuple.
Reject a request whose tuple disagrees with its slot or declared base.
The base cannot be refreshed for a retry. Store the tuple and verified
payload identity in the committed frame. While the receipt is retained,
identical content returns AlreadyCommitted and different bytes reject.
After both alternating files have rotated past that receipt, an old
tuple rejects Stale because its fixed base is older than the selected
generation. Reusing the nonce with a fresh base creates a different
operation ID, never a retry. An unresolved Indeterminate writer blocks
successor writes until a rescan settles its original base and bytes or
the slot fails closed. Exercise restart after receipt rotation,
changed bytes, changed declared base and no overtaking after lost ACK.

The classification-to-publish invariant assumes exclusive namespace
writers: all supported writer and recovery processes use the same
slot lease, no other supported path mutates slot members, and platform
path containment is verified. D02 must pin the validated directory
chain against rename/reparse substitution, use an exclusive private
temporary file and directory-relative publication, and verify target
identity under the lease before rename. If the platform cannot establish
these conditions, deny writes. The lease coordinates participating
writers. It makes no security guarantee against an arbitrary process
with the same OS write authority. Test ancestor substitution, target
replacement and reparse insertion around classification and publish.

Test every compatible/damaged/protected/access A/B pairing in both
generation orders, especially valid newer plus older unsupported and
valid plus foreign magic, short header or oversize. Assert both file
hashes unchanged when protected, then exercise capture order, stale
base, partial write, flush, rename, lost receipt, retry, process restart
and restoration of a coherent ungrounded checkpoint.

## Independent-review and execution gate for these clarifications

Treat this section as an **author-proposed contract** to be scrutinized, not as independent findings. Independent roles L3A-ECS (identity/ownership), L3B-JOBS (determinism/scheduling/bounds), L2-SIM (presentation admission/tick semantics) and L5-FAULTS (save/GPU/fail-closed) each review the same final frozen commit, then a separate *subsequent* integration reviewer reconciles their signed reports. The proposed concrete choices are reversible if reviews or measurements reveal a superior architecture. No implementation PR may treat the provisional numeric budgets or unselected mesher as already qualified.

## Scope boundaries

**WP11 includes:** typed planet descriptor, deterministic large-scale and local density, sparse 3D data, renderable terrain, seams, streaming, basic edit overlay, new save/recovery, playable WYU seeded route and flight regression, reproducible Windows verification.

**Deferred:** game-quality caves and underground biomes (WP12), ores/geological simulation (WP12), biomes/vegetation/ecosystems (WP13), atmospheric/weather simulation, rescue-pod narrative opening (WP14), building/structural physics (WP15), machines and energy (WP16), multiplayer and other platforms. Synthetic volumetric cavities are **test fixtures**, not shipped cave content.

## Implementation DAG and issue IDs

- **C00** (Groundwork): architectural freeze and clean-break cutover plan; required before public API implementation.
- **A01** (Groundwork): immutable descriptors and canonical coordinate/volume address.
- **A02** (Groundwork): deterministic density/noise/landform engine.
- **A03** (Groundwork): sparse brick storage, material samples and edits-ready ownership.
- **B01** (Groundwork): volume meshing, normals and watertight LOD/chart seams.
- **C01** (Groundwork): bounded streaming, job scheduling and coherent publication.
- **C02** (Groundwork): volume collision, sweeps and movement admission.
- **C03** (Groundwork): D3D12 provider and GPU-retirement integration.
- **D01** (Groundwork): deterministic edits and atomic geometry/collision updates.
- **D02** (Groundwork): new planetary save codec, NTFS slot and recovery.
- **Q01** (Groundwork): telemetry and benchmark/resource qualification.
- **Q02** (Groundwork): adversarial and cross-module qualification fixtures.
- **W01** (WYU): seed-dependent reference planet recipe/content parameters.
- **W02** (WYU): playable generation mode, controls, world selection and diagnostics.
- **W03** (WYU): save/continue and editing integration.
- **W04** (WYU): new surface-to-orbit/landing route and deterministic replay.
- **W05** (WYU): installed alpha playtest and user-facing verification.
- **G00** (Groundwork): cross-repository final integration and handover.

Dependencies: C00 → A01 → A02/A03 → B01. A01/A03 → C01; B01/C01 → C03; A03/C01/B01 → C02; A03/B01/C01 → D01 → D02; A02/W01 → W02 with B01/C01/C02/C03; D01/D02/W02 → W03; C02/C03/W02 → W04; W03/W04 → W05; all lanes → Q02 and G00. Q01 begins alongside C01 and must complete before G00.

## Cross-repository governance

- Strict scoped PRs targeting master; no direct default-branch writes. No more than four concurrent specialist agents per ADR-0007. The four isolated specialist reviews, sequential integration review, configured checks and **explicit final owner sign-off of the reviewed head SHA** remain necessary. Proposed architecture/issues are not owner acceptance or tests passed.
- Mark architecture, code, tests and handover as separate deliverables. Do not merge WP11 implementation before architecture acceptance and validated baseline budgets. A changed contract must receive review before dependents merge.
- Separate Groundwork and WYU CI, exact immutable package provenance and NuGet locks; no GitHub-only runtime version identity. Existing source SemVer in Directory.Build.props remains stable unless explicitly approved for a breaking major/minor release.
- Issue and PR reports must state baselines, paths/ownership, tests actually run, known limitations, failure behavior and acceptance evidence.

## L3A mandatory verification evidence gate (WP11-C00)

These are **mandatory acceptance requirements**, not claims that tests have already been implemented or run. An independent L3A review of this planning contract may validate completeness of the specified design and test obligations; runtime correctness remains a separate implementation gate for A01/A03/D01/Q02. Do not label contract-only inspection as executable test evidence.

1. **Canonical address fixtures:** Implement automated, table-driven Euclidean floor-division and remainder tests at brick edge 32 for coordinates -65, -64, -33, -32, -1, 0, 31, 32, 63 and 64, including Cartesian 3D face/edge/corner combinations. Assert local index in [0,32), canonical sample identity, checked Int64 boundary/overflow behavior, rejection of nonfinite inputs, deterministic rounding and cross-run identity. Cover all 12 cube-chart edges, eight chart corners, mixed LOD and rebased render origins without duplicate authoritative keys.
2. **Eight-brick shared vertex:** Build eight bricks meeting at one canonical vertex. Commit a density edit at that vertex and assert exactly one authoritative address/value, dependency invalidation for all affected bricks, coherent admitted geometry/collision revision, no seams and identical save/restart reconstruction. Include shared faces, edges and mixed LOD transitions.
3. **Atomic edit fault matrix:** Execute overlapping same-tick edits with reversed worker completion, disjoint edits, stale base revision, cancellation after partial derived work, failed/delayed presentation, save while staging and restart after acknowledged commit. Assert no partial authoritative publication, no uncommitted visible candidate, masked or held contact-sensitive actions until matching provider acceptance, deterministic conflict ordering, and bounded settlement of leases and reservations. Explicitly assess whether global revision checks unnecessarily reject disjoint edits and specify safe conflict-scope handling.
4. **Reproducible evidence:** For each executed test record exact repository and commit SHA, test name and command, environment/OS/runtime, expected and observed values, pass/fail output/logs, code references and any **NOT RUN** cases. Attach machine-readable CI artifacts where available. An unexecuted or merely proposed fixture cannot be counted as a passing test.
5. **Review gate:** The independent L3A report must list inspected files, method, reproducible P0–P3 findings, remediation owners, unresolved questions and PASS/PASS_WITH_NOTES/BLOCK. No formal L3A acceptance with unresolved P0/P1. PR #77 remains draft pending required independent specialist and integration reviews and explicit owner acceptance of the final reviewed SHA.

## Acceptance matrix / final handover

1. **Determinism:** same seed/version yields identical serialized golden samples on fresh Windows x64 processes, including different worker completion order; at least three seeded fixtures differ meaningfully.
2. **Planet scale:** 6,400 km radius precision/rebase/face-corner tests pass; no dense planet allocation.
3. **Geometry:** stress seams across all cube faces and mixed LOD; synthetic cavity/overhang correctly meshed and collidable; WARP pixels/depth show no gaps.
4. **World loop:** new game → procedurally generated route → edit → unload/reload → save → stop process → continue; exact semantic world state restored.
5. **Flight loop:** launch from the same generator lineage → orbit → return → touch down → save/restart; matching presented terrain/camera/collision revisions.
6. **Faults:** stale work, quota exhaustion, cancellation, lost save acknowledgment, corruption and graphics shutdown leave coherent state and settle ownership; no silent data overwrite.
7. **Reproducible delivery:** green Windows CI, exact reviewed GitHub commits and package digests, NVIDIA D3D12 Debug Layer/DRED, zero live GPU child objects, resource budgets and benchmark metadata recorded.
8. **Process:** architecture approved, specialist/integration reviews documented, open P0/P1 fixed, owner accepts final verified head, and handover references each accepted issue.

**Owner decision:** The clean-break WP11 direction and planning scope were approved on 2026-11-08 and recorded in PR #77 / issue #78. This is not final post-review acceptance of a frozen SHA. Meshing algorithm selection, benchmark-derived resource ceilings, independent specialist/integration review, configured checks and final owner acceptance remain outstanding before implementation.
