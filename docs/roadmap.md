# Groundwork and Worlds Yet Unseen roadmap

This roadmap starts after the [WP03 handover](wp03-handover.md). WYU's
long-term goal is first-person survival and exploration on procedural,
destructible planets, with modular construction and travel from the surface
to orbit. The first playable goal is a bounded region where a player can
land, orient themselves, edit terrain, recover a resource, build something
and resume the same game later. See the [WYU product vision](https://github.com/DaveBeusing/wyu/blob/a1ee1f623b9082602d81bc0dbd5bdcbbc912e635/README.md).

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
| WP10 · Windows alpha | Stability, diagnostics, package compatibility and reproducible builds | Installable single-player build, onboarding, settings and a complete test mission | Installation and mission pass on a clean Windows system. Supported hardware and known limitations are documented. |

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
