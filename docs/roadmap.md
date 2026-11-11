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

## WP12 – Underground geology and caves (proposed)

The [WP12 contract](architecture/wp12-underground-geology-contract.md) defines seeded strata, caves and ore deposits, underground streaming and collision, and save and recovery. The Windows playtest follows a surface entrance into the cave, through an ore edit and underground restart, then back to the surface. Contract approval precedes implementation. WP11 reference qualification and production acceptance remain open.

## After WP10

The fresh Windows 11 offline run remains open as a follow-up to the
accepted WP10 milestone. The [handover](wp10-handover.md) records its
scope and the existing evidence.

WP11 closes after the reference-system run, the qualification review and owner acceptance. WP13 addresses biomes and vegetation.

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
