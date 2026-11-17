# Groundwork Engine

Groundwork Engine is a C# game engine developed by **undefined behavior Studios**.
It targets .NET 10 and uses Win32 for windowing and input on Windows, with a
Direct3D 12 rendering backend.

[Architecture](#architecture) · [Example game](#example-game) · [Build and verification](#build-and-verification) · [Roadmap](docs/roadmap.md)

## Architecture

The [solution](Groundwork.slnx) contains the engine modules and their test projects.
Engine project names use the `Groundwork.` prefix.

| Area | Modules | Responsibilities |
| --- | --- | --- |
| Runtime | `Core`, `Simulation`, `Ecs`, `Jobs`, `Diagnostics` | Host lifecycle contracts, fixed-step simulation, entity and component storage, job scheduling, state publication, and fault reporting |
| Windows host | `Platform.Windows` | Win32 window lifecycle, input translation, and the presentation target |
| Graphics | `Rendering`, `Rendering.D3D12` | Renderer contracts, GPU uploads, drawing, swap-chain presentation, and fence-based resource retirement |
| Terrain | `Voxels` | Seeded volumetric planet generation, meshing across levels of detail, terrain streaming, collision queries, and terrain edits |
| Storage | `Persistence` | Versioned world checkpoints and recovery from interrupted saves |

The simulation uses explicit tick phases and commit barriers to publish world
state. Terrain generation and meshing can run as jobs. Collision and rendering
consume published terrain data.

[Runtime contracts](docs/architecture/runtime-contracts.md) · [WP01 foundation module boundaries](docs/architecture/module-boundaries.md) · [D3D12 renderer contract](docs/architecture/wp02-d3d12-renderer-contract.md)

## Example game

[Worlds Yet Unseen (WYU)](https://github.com/DaveBeusing/wyu) is an example game
built with Groundwork Engine. Its Windows client consumes exact versions of
Groundwork NuGet packages from verified archives. The game repository owns
gameplay rules, content, controls, and the user interface.

The [volumetric planet handover](docs/wp11-handover.md) documents the integration
for terrain streaming, collision, edits, and save/reload.

## Build and verification

Build and verify the solution on Windows with the .NET 10 SDK, Git, and
PowerShell 7. The [build and verification guide](docs/build-bootstrap.md)
covers locked restore, Release builds, test execution, and package provenance.
Pull requests target `master`.

## Roadmap and handovers

The [Groundwork and Worlds Yet Unseen roadmap](docs/roadmap.md) tracks work
packages, dependencies, and acceptance evidence across both repositories.

- [Windows alpha handover](docs/wp10-handover.md)
- [Volumetric planet handover](docs/wp11-handover.md)
- [Underground geology handover](docs/wp12-handover.md)
- [Planetary landforms handover](docs/wp17-handover.md)

## License

Groundwork is licensed under the [Apache License, Version 2.0](LICENSE)
(SPDX identifier: `Apache-2.0`).
