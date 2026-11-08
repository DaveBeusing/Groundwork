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
- Stable identity of a sample depends on descriptor + canonical address + LOD, not worker timing or camera location.

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

**Owner decision still required:** approve or amend this entire proposed WP11 technical contract and the benchmark-tuned budgets before implementation. No additional product questions block the planning artifacts.
