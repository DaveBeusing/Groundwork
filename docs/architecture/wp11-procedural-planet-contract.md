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
- Generate immutable meshes outside render and simulation critical sections. Publish mesh + matching collision + origin + camera generation atomically through explicit leases/fences. Do not expose partial generations.
- Verify topological seams in pure tests; WARP color/depth readback and NVIDIA hardware with Debug Layer/DRED on changes to provider paths.

### R05 – Streaming and bounded scheduling

- Select local volumetric detail by view distance and required collision/support, and remote spherical coarse representation by LOD; maintain a support halo that covers admitted player motion and edit operations.
- Bound active/staged tiles, workers, queues, retained readers, CPU mesh and GPU allocations including retiring resources. Explicit initial guardrail defaults for a reference Windows desktop: up to 4 generation jobs, 2 meshing jobs, 1 replaceable staged generation, no unbounded waiting queue; 512 MiB total terrain CPU payload and 256 MiB native terrain geometry. These are **design ceilings to validate and revise by measured evidence before implementation approval**, not performance claims.
- Camera/intent changes supersede obsolete candidates without abandoning owned work; stale results are rejected. Under pressure, keep last coherent terrain and report backpressure; never spin, allocate without quota, or publish unsupported movement.
- Generation and meshing execute off 60 Hz simulation tick and UI/render owners. Cancellation, pause, terminal stop, GPU retirement, device removal and failed task paths release resources only when safe.

### R06 – Movement and collision

- Collision queries consume the same committed volumetric generation and edits as the visible geometry, with independent finite shape sweeps (not solely radial support). Unknown coverage is explicit and blocks unsupported movement.
- Support grounded walking and existing radial gravity without assuming a single ground point on each ray. The synthetic cavity, steep slope, overhang, seam, rebase and unloading route must pass.
- No player pose or contact state may be committed ahead of a matching presented/acknowledged collision generation. Same frame revisions and generator content identifiers must match through host submission.

### R07 – Editing and persistence

- Single-player edits are ordered by published tick and canonical cell/brush identity. Implement add/remove operation(s) and deterministic conflict/revision checks; reject stale edits atomically. Edit impact includes neighbouring bricks, meshes, collision and persisted overlay.
- New save schema and planet lineage may replace prior formats completely. No mandatory migration. Encode descriptor, generator version/parameter identity, terrain edit overlay, player/world checkpoint and checksums with bounded canonical ordering.
- Reuse or replace verified NTFS atomic publication, slot lease, retry-idempotency, stale-base rejection and corrupt/interrupted-write recovery; do not weaken these guarantees. Failed or unsupported saves remain intact and are reported as incompatible; they are not loaded with an incorrect generator.
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
- Keep 60-Hz fixed simulation deterministic; timing measurements never alter canonical world output.

## WP11-C00 architecture clarifications — review candidate (2026-11-08)

These clarifications resolve five author-side preflight gaps sufficiently for **independent architecture review**. They are proposed contract decisions, **not** four completed specialist reviews or measured performance acceptance. A material change requires a new frozen head and fresh/affected independent reviews. C00 must record any final amendment, decision rationale and owner sign-off before implementation.

### C00-1 — Canonical volume addressing across faces and LOD (R01, R03, A01/A03)

**Proposed design choice:** Make a single planet-centred signed **Int64 Cartesian lattice** authoritative for volumetric density sample coordinates and sparse brick keys. A planet descriptor fixes the cell size (initial reference 1 metre per finest cell), orientation, centre, coordinate origin and generator-versioned sampling quantization. For each axis use Euclidean floor division, including negative values, to derive a brick index and the local cell index in `[0, brickEdge)`. The initial candidate brick has 32 cells per axis; its 33-by-33-by-33 vertex-sample grid includes shared boundary samples. Those samples are *logically addressed* by their global integer lattice triples and evaluated identically for all adjacent bricks; no independent face-local copy may be saved as a second authoritative edit.

The existing six cube-sphere charts are **non-authoritative view/LOD addresses**, not identities for volumetric samples or persisted edits. Far spherical patches query the same versioned volume field through a documented canonical sampling transform. A face edge or corner cannot own a different edited value because edits are keyed solely by the planet identity and 3D integer lattice address. Rendering may use local-origin rebasing and floats, but render positions must never be fed back into canonical world keys.

Specify whether edits address *cells* or *density lattice vertices* for each operation; forbid ambiguous half-cell coordinates. At a brick boundary, exactly one canonical cell owner changes material/occupancy, while every mesh neighbour dependent on shared density samples is invalidated. Distinguish generation identity from rendering LOD and local-origin revision. For every conversion require explicit ties-to-even rounding or another approved deterministic tie rule; define overflow and nonfinite-input rejection **before** any cast or allocation. Use checked Int64 arithmetic for coordinate/brick construction and validated domain limits for radius/resolution. Exceeding the supported domain must fail closed, not wrap.

**Required evidence:** planar negative-coordinate tests; 3D brick faces/edges/corners (including triple-neighbour samples); cube-chart 12 edges/8 corners; level transitions; local-origin rebases; 6,400,000 m radius stress; duplicate-free canonical edit keys; roundtrip persistence of edits near seams. The mapping is a design decision subject to the independent review, not a proven implementation.

### C00-2 — Numeric determinism and generation versioning (R02, A02)

**Proposed rule:** Canonical seed hashing, spatial noise, density quantization and persisted output are specified algorithms, not merely calls to an unversioned OS or .NET RNG. Use a fixed, test-vectored 64-bit integer hash/PRNG with separate named streams for macro terrain, mid-scale relief, detail and materials. Canonical sample coordinates and generator parameters are serialized with explicit endianness and version. Use signed fixed-point / checked integer arithmetic and a frozen round-to-nearest, ties-to-even rule for authoritative density results where precision/range allows. If a floating computation is unavoidable, its numerical steps, input quantization, output rounding, FMA/reassociation policy, nonfinite rejection and cross-CPU golden vectors must be explicitly qualified before treating its bytes as authoritative.

Never key generation or ordering on worker scheduling, clock time, platform thread IDs, locale, unordered dictionary traversal or GPU output. A generator-algorithm change increments the generator identity; equal seed with a changed algorithm is **not** silently treated as the same saved world. At minimum the golden harness must run repeated fresh-process, serial, reversed and randomized job-order evaluations on supported Windows x64 CPUs, comparing canonical sample and serialized chunk hashes; distinguish binary invariants from allowed timing differences. Do not assert cross-architecture bitwise identity without tests.

### C00-3 — Simulation-authoritative coverage versus GPU presentation (R04–R06, C01–C03)

**Proposed admission state machine:**

1. `Requested` → reserve bounded immutable generated bricks and matching edit revision.
2. `Prepared` → complete required mesh topology, collision data and camera/local-origin stamp; candidate remains non-authoritative.
3. `Submitted` → renderer accepts matching candidate revision/descriptor; a submission alone is **not** presentation.
4. `PresentedConfirmed` → renderer acknowledges an actual matching mesh/camera presentation, with valid native ownership/fence evidence.
5. `Committed` → at the next permitted simulation/Core publication barrier, atomically replace active mesh/collision/origin coverage and install the matching generation.
6. `Retired` → old CPU/GPU versions release only after all readers, jobs, draws and Present operations have settled per Core/GPU custody contracts.

An admitted `Committed` generation can support many subsequent deterministic 60 Hz simulation ticks. **A new GPU Present must not be required before each simulation tick**. The first admission of *new* coverage waits for confirmed matching presentation, and any movement requiring not-yet-admitted coverage holds the last valid pose. A delayed, canceled or failed presentation cannot authorize the new collision generation. The simulator must distinguish a stream-readiness hold from ordinary zero-input movement and record it in the trace.

Deterministic tests must replay a fixed *accepted input/action sequence and semantic world checkpoints* under controlled readiness schedules. Wall-clock readiness delays and numbers of hold ticks may vary and should be reported separately; never claim raw tick-by-tick equivalence across uncontrolled GPU/worker timing. An authoritative edit mutation may commit its logical state at a named tick, but new collision-affecting visibility is admitted only as one coherent generation; define whether interaction input is deferred/rejected until then. Stop, failed Signal and removal sentinel retain ownership until safe settlement evidence.

**Required evidence:** randomized worker ordering, delayed Present, stopped renderer, rapid rebase, edit during streaming, interrupted frame, stale candidate rejection and a full walk→flight→landing route with matching generation stamps.

### C00-4 — Meshing and performance-budget decision gate (R04/R05/R09, B01/Q01)

The concrete volume surface algorithm and mixed-resolution transition mechanism are an **explicit C00 decision**, not an implicit promise. Compare at least **(a) Marching Cubes with a validated crack-free mixed-resolution transition such as Transvoxel-style transition cells**, and **(b) a dual-contouring/surface-nets family with explicit adaptive boundary topology**. An alternative is valid if equivalent seam/winding and cavity evidence is provided. Document topology consistency, material/normal interpolation, thin structures, sharp features, difficult mixed-LOD corners, cube-chart edges, deterministic vertex ordering, memory traffic and CPU meshing cost. A naive independent per-brick triangulation without boundary stitching cannot pass.

Provisional ceilings from R05 — 4 generating jobs, 2 meshing jobs, 1 staged generation, 512 MiB CPU terrain and 256 MiB GPU geometry — are **planning limits only**. Before C00 acceptance, choose documented reference hardware and scene seeds, benchmark both candidates or a defensible reduced prototype, publish actual high-water allocation and p50/p95/p99 times, and freeze explicit acceptance ceilings with justification. Count CPU active, pending, leased, obsolete and retiring buffers and GPU upload, active, candidate and retirement buffers; keep the workload bounded and reject reservations before allocation. A measurement not run must be marked `NOT MEASURED` and is a remaining C00 gate, not estimated proof. Geometry and memory budget tuning may change the 32-cell/1 m reference after measurements without a backward compatibility obligation.

**Required evidence:** enclosed cavity, overhang, thin bridge, multiple intersecting LOD transition edges, face corners and deliberately saturated streaming; repeatable machine+build+seed and allocation logs. Approval of a preferred candidate is contingent on these artifacts.

### C00-5 — Explicit new-save namespace and incompatibility safety (R07, D02/W03)

WP11 uses a **new world lineage and checkpoint schema**. The proposed Windows root is `%LOCALAPPDATA%\\WorldsYetUnseen\\Worlds\\v2\\<opaque-world-id>\\`, separated from existing alpha WP06/07/09/10 slots. Treat the root and `v2` segment as a *namespace identifier*, not an assertion that every earlier format was version 1. Choose/validate opaque world IDs and canonical path containment; prohibit path traversal, symlink escapes, ambiguous case or slot collisions. A newly created world reserves a unique empty slot before first write; an existing slot cannot be silently reused by "New World" even if parsing its header fails.

The checkpoint header binds a world ID and planet descriptor, generator algorithm/version, parameter digest, material catalog and storage schema. On Continue, inspect occupied committed slots before creation/publication. If a file is from a legacy or unsupported version, a different seed/lineage, has an ambiguous header, or cannot be safely classified, return explicit `Incompatible` / `Unsupported` / `Corrupt` / `AccessError` (as appropriate), **leave every byte intact and do not write**. Offer a **separate unique new-world slot**; migration is out of scope. Never interpret old data using the new generator or manufacture an empty world over it.

Revalidate slot identity on save as well as load; preserve the existing or stronger NTFS lease, idempotent retry ID, stale-base detection, atomic alternating committed files, flush/verify and recovery discipline. A failed/uncertain scan blocks publication rather than falling back to reset. Test occupied unsupported slots, two writers, corrupt/partial headers, access denial, loss of acknowledgment, interruption around atomic publication and correct recovery without unintended overwrites.

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
