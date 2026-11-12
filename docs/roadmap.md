# Groundwork and Worlds Yet Unseen roadmap

WP01 through WP10 established the Windows single-player alpha. WYU's
long-term goal is first-person survival and exploration on procedural,
destructible planets, with modular construction and travel from the
surface to orbit. See the [WYU product vision](https://github.com/DaveBeusing/wyu/blob/master/docs/World_Vision.md).


Groundwork owns reusable simulation, world and mesh data, rendering and
resource management. WYU owns rules, content, controls and the user interface.
WYU integrates through exact versions of independently packaged Groundwork
modules. See [repository boundaries](adr/ADR-0001-repository-boundaries.md)
and [package provenance](adr/ADR-0006-packaging-release.md).

## Milestones

| Milestone | Groundwork delivers | WYU delivers | Acceptance |
| --- | --- | --- | --- |
| WP01 · Runtime foundation (complete) | Windows lifecycle, fixed-step simulation, ECS, jobs, supervision and verified packages | Minimal independently packaged Windows host | [Clean integration handover](wp01-handover.md) |
| WP02 · D3D12 rendering (complete) | Portable renderer boundary, Windows target lease, D3D12 provider and diagnostics | Opt-in D3D12 cube | [WARP, NVIDIA and native shutdown evidence](wp02-handover.md) |
| WP03 · Static two-chunk terrain (complete) | Immutable voxel data, seam-aware meshing and a fixed D3D12 mesh pair | Visible terrain composed from two adjacent chunks | [Clean source and package integration on Windows](wp03-handover.md) |
| WP04 · Editable terrain (complete) | Revision-based rebuild of both neighboring meshes, atomic replacement and safe GPU retirement | Repeatable voxel edit at the existing chunk boundary | [Clean Windows integration handover](wp04-handover.md) |
| WP05 · Walkable region (complete) | Bounded chunk loading, meshing, unloading and collision queries for active terrain | First-person movement in a small seeded region | [Clean Windows integration handover](wp05-handover.md) |
| WP06 · Durable world changes (complete) | Versioned storage for seed, generator version and terrain edits, with interrupted-write recovery | Save and resume position, inventory and edited terrain | [Windows handover](wp06-handover.md) and [format and recovery contract](architecture/wp06-save-recovery-contract.md) |
| WP07 · Playable prototype (complete) | Stable interfaces and diagnostics for the features the game exercises | Explore the landing region, mine stone and assemble a rescue beacon | [Windows handover and playtest outcomes](wp07-handover.md) and [prototype contract](architecture/wp07-playable-prototype-contract.md) |
| WP08 · Spherical planet (complete) | Planet coordinates, local precision, consistent gravity and terrain detail transitions | Traversable route on a small spherical world | [Clean Windows handover](wp08-handover.md) and [spherical planet contract](architecture/wp08-spherical-planet-contract.md) |
| WP09 · Surface to orbit (complete) | Local and planetary reference-frame transitions, graded rendering and controlled streaming | Takeoff, orbit and return to a visibly marked, edited landing site | [Windows flight and landing pad handover](wp09-handover.md) and [surface-to-orbit contract](architecture/wp09-surface-to-orbit-contract.md) |
| WP10 · Windows alpha (accepted) | Stability, diagnostics, package compatibility and reproducible builds | Per-user installer, onboarding, persistent settings, rescue-to-orbit mission, ESC sidebar and F1 performance panel | [Owner acceptance and evidence](wp10-handover.md), [Windows alpha contract](architecture/wp10-windows-alpha-contract.md) and [UI playtest](https://github.com/DaveBeusing/wyu/pull/26) |

## WP11 – Procedural volumetric planet generation (workstation integration verified)

WP11 deliberately replaces the radial-only fixture with a seed-dependent, sparse three-dimensional planet terrain system. **No compatibility obligation** applies to old planet APIs, alpha save schemas, smoke modes, or fixtures: these can be replaced when it improves the architecture. Old save data must nevertheless be detected as incompatible and preserved rather than silently overwritten. Existing tests can be rewritten, but new deterministic, GPU ownership, crash recovery, precision, and flight/landing tests must prove the replacement.

- **WP11-A:** architecture freeze; planet identity, canonical 64-bit addresses, deterministic volumetric density, and sparse brick representation.
- **WP11-B:** seed-dependent landforms, cavity/overhang synthetic fixtures, watertight multi-LOD meshing and chart seams.
- **WP11-C:** bounded generation/meshing jobs, streaming, collision, coherent D3D12 presentation, instrumentation and budgets.
- **WP11-D:** deterministic terrain editing, new save/recovery schema, WYU playable/flight integration and clean Windows handover.

The [WP11 contract](architecture/wp11-procedural-planet-contract.md) was accepted in PR #77. Groundwork generation, streaming, collision, D3D12 and NTFS checkpoints are merged, along with WYU W01–W05. The [workstation handover](wp11-handover.md) records the clean Windows integration, installer and package provenance. Q01 production budgets, Q02 reference qualification and owner acceptance remain open. The [issue map](wp11-issues.md) records the dependencies and final gates.

## WP12 – Underground geology and caves (installed route verified)

The [WP12 contract](architecture/wp12-underground-geology-contract.md) covers seeded geology, cave streaming, collision and checkpoint recovery. A01, B01 and C01 are merged in Groundwork, and W01 is merged in WYU. The [G00 handover](wp12-handover.md) records the installed NVIDIA route through descent, ore mining, underground restart and the return to supported surface. WP11 reference production qualification and [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89) remain open.

## WP13 – Biomes (workstation integration verified)

The [WP13 contract](architecture/wp13-biome-rendering-contract.md) defines canonical near and orbital terrain colors. Groundwork's optional evaluator and WYU's shared biome palette are merged. The [Windows handover](wp13-handover.md) records both seeds, verified package provenance, flight and regression results, and the remaining qualification evidence. Package E and WP16 consume this integration.

## Approved rendering and simulation packages

The issues below are open and track approved scope. Each pair records its acceptance and performance limits. Start the Groundwork work against its listed prerequisites. WYU pins the exact verified Groundwork archive for the integration.

| Package | Groundwork delivers | WYU delivers | Dependencies | Issues |
| --- | --- | --- | --- | --- |
| [Package E · Day/night and regional fog](architecture/package-e-distance-fog-contract.md) | Optional distance fog in the D3D12 terrain pass | Fixed-step day/night cycle, regional fog and Save/Continue | Groundwork fog + B → WYU. | [Groundwork #111](https://github.com/DaveBeusing/Groundwork/issues/111) · [WYU #41](https://github.com/DaveBeusing/wyu/issues/41) |
| Package G · Dynamic sky and atmosphere | Shared D3D12 sky pass with sunlight scattering, stars and orbital limb | Continuous sky from surface to orbit and back | Groundwork atmosphere pass + E → WYU. | [Groundwork #112](https://github.com/DaveBeusing/Groundwork/issues/112) · [WYU #42](https://github.com/DaveBeusing/wyu/issues/42) |
| WP15 · Sunlight and terrain shadows | Directional terrain lighting and two near-field shadow cascades | Reference-planet lighting and shadow tests across edits and rebases | Groundwork lighting + E → WYU. Verify alignment with G when available. | [Groundwork #113](https://github.com/DaveBeusing/Groundwork/issues/113) · [WYU #43](https://github.com/DaveBeusing/wyu/issues/43) |
| WP16 · Instanced props and vegetation | Bounded GPU instancing, cell visibility and instance-buffer streaming | Biome-dependent rocks, scree, grass and trees with distance LOD | Groundwork instancing + B → WYU. | [Groundwork #114](https://github.com/DaveBeusing/Groundwork/issues/114) · [WYU #44](https://github.com/DaveBeusing/wyu/issues/44) |
| WP17 · Connected ridges and valleys | Versioned landform profile shared by volumetric terrain, WP12 depth and orbital relief | Reference seeds and a ridge/valley route from the ground to orbit | Groundwork landform + WP12-A01 interfaces and B01/C01 acceptance → WYU. | [Groundwork #115](https://github.com/DaveBeusing/Groundwork/issues/115) · [WYU #45](https://github.com/DaveBeusing/wyu/issues/45) |
| WP18 · Loose-object physics | Bounded 60 Hz simulation for spherical bodies against published voxel terrain | Repeatable loose-rock scene with terrain edits, sleeping and rebases | Groundwork physics on WP11 → WYU. WP16 instancing is optional. | [Groundwork #116](https://github.com/DaveBeusing/Groundwork/issues/116) · [WYU #46](https://github.com/DaveBeusing/wyu/issues/46) |

WP16, WP17 and WP18 can begin on their respective foundations. The order of their issue numbers does not define an implementation dependency.

## Open qualification work

The fresh Windows 11 offline run remains a follow-up to the accepted WP10 milestone. The [handover](wp10-handover.md) records its scope and existing evidence.

WP11 closes after the [reference-system measurements](https://github.com/DaveBeusing/Groundwork/issues/88), [pressure qualification](https://github.com/DaveBeusing/Groundwork/issues/89) and [owner acceptance](https://github.com/DaveBeusing/Groundwork/issues/90). Package-specific performance claims require the benchmark evidence specified in the linked issues.

## Planning and verification

- Review the next milestone contract before implementation. Each contract states
  ownership, failure behavior, budgets and acceptance.
- The WP06 address probe distinguishes flat save addresses from the
  planetary address domain used in WP08.
- Agree on reference hardware and budgets before accepting a performance
  claim. For streaming, record memory, queue bounds and frame times on a
  repeatable route. For deterministic state, record the seed, generator
  version and input sequence.
- Close integrated milestones with green Windows CI on the relevant
  commits, exact Groundwork package pins, a WYU demonstration and a
  handover. Changes to the native render path require WARP and hardware
  evidence with the active Debug Layer.
- Treat WP07 as a product decision point. Playtests determine whether to
  prioritize more gameplay depth or a larger world. Consider multiplayer,
  additional platforms and additional rendering backends separately after
  the Windows single-player alpha.

Milestones express dependencies and acceptance conditions. Calendar dates
and performance targets are set when their prerequisites are understood.
