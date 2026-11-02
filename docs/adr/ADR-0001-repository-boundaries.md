# ADR-0001 — Repository, module and dependency boundaries

**Status:** Proposed for architecture review (WP01-C00)  
**Decision owner:** C00 architecture/integration lead, affected B00 and WYU L6  
**Date:** 2026-11-02  
**Trace:** [Groundwork #1](https://github.com/DaveBeusing/Groundwork/issues/1), [WYU #1](https://github.com/DaveBeusing/wyu/issues/1)

## Context

Groundwork must be an independent, reusable .NET 10 game engine; Worlds Yet Unseen (WYU) is an independently versioned game built on its published interfaces. Source linking, game-dependent engine code, circular dependencies and implicit shared working directories would prevent autonomous development and reproducible builds.

## Decision

- Both GitHub repositories are **public** and use **`master`** as their intended integration branch. Changes use scoped branches and reviewed PRs. This ADR does not establish branch protection.
- The proposed WP01 modules and their allowed compile-time dependencies are defined in [module-boundaries](../architecture/module-boundaries.md). `Groundwork.Core` references the BCL only; platform, simulation, ECS, jobs and diagnostics may depend on Core. Cross-module edges require explicit review. No Groundwork project may reference WYU or a specific future graphics or physics implementation.
- `WorldsYetUnseen.Client` is the game-side composition root. It consumes immutable, exactly version-pinned `Groundwork.*` NuGet packages, not cross-repository `ProjectReference` or sibling paths. Groundwork and WYU must independently restore/build from clean checkouts after package artifact staging.
- Module names are proposals pending owner review; no placeholder modules are created by WP01-C00.
- Generic ECS entities, specialized dense voxel data and ordinary object-oriented tool/UI services can coexist. The engine does not force every voxel cell into ECS.
- Windows is the first target; Direct3D 12 is a *later* backend choice, not a dependency embedded into Core.

## Consequences and alternatives

Positive: independent releases, enforced package boundaries when implemented, and clear lane ownership. Cost: package staging and end-to-end compatibility checks. Rejected: monorepo/shared source checkout, direct engine-to-game reference, and speculative unused assemblies.

## Verification / review gate

B00 validates a DAG without cycles; WYU L6 demonstrates locked, exact-package independent restore; C00 obtains integrator and affected lane review. No build, package feed or PR approval is claimed by this document.