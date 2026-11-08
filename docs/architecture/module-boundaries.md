# WP01 module boundaries and dependency contracts

> Historical WP01 proposal. The [WP01 handover](../wp01-handover.md) records
> the implemented and verified foundation. The proposal below preserves
> the review context.

**Status:** Review proposal for [WP01-C00](https://github.com/DaveBeusing/Groundwork/issues/1). These are proposed boundaries, not existing compiled assemblies. Implementation is gated on review and merge of this documentation.  
**Repositories:** public `DaveBeusing/Groundwork` and `DaveBeusing/wyu`; both target `master`.  
**Runtime:** C# / .NET 10, Windows first. Direct3D 12 is a **WP02 target** behind an abstraction, not a WP01 dependency. C++ requires a measured performance justification.

## Dependency direction

```text
WorldsYetUnseen.Client (in wyu; consumes immutable packages)
  |--> Groundwork.Platform.Windows --> Groundwork.Core
  |--> Groundwork.Simulation       --> Groundwork.Core
  |--> Groundwork.Ecs              --> Groundwork.Core
  |--> Groundwork.Jobs             --> Groundwork.Core
  '--> Groundwork.Diagnostics      --> Groundwork.Core
```

The arrows mean `depends on`. The composition root in WYU may reference multiple Groundwork packages. Groundwork modules MUST NOT depend on WYU, its game assets, source paths or types. Cross-engine dependencies beyond `Core` require an explicit architecture review: do not infer them from this diagram. The module DAG MUST remain acyclic. Do not create empty speculative assemblies.

| Proposed package / assembly | Responsibility | Permitted compile dependency | Lane |
| --- | --- | --- | --- |
| `Groundwork.Core` | Lifecycle, cross-module public contracts and primitives | BCL only | C00 / B00 |
| `Groundwork.Platform.Windows` | Win32 host loop, native window ownership, input adapter | Core | L1 |
| `Groundwork.Simulation` | Monotonic timing, fixed ticks, pause and overload policy | Core | L2 |
| `Groundwork.Ecs` | World-scoped generational IDs, storage, queries, deferred writes | Core | L3A |
| `Groundwork.Jobs` | Bounded DAG scheduling, cancellation and worker outcomes | Core | L3B |
| `Groundwork.Diagnostics` | Structured event sinks, metrics and benchmark seams | Core | L5 |
| `WorldsYetUnseen.Client` (WYU repo) | Game composition and engine integration | Exactly pinned Groundwork packages | WYU L6 |

**Rules:** Namespaces, assemblies and package IDs MUST use `Groundwork.*` consistently. `Groundwork.Core` MUST NOT reference Windows handles, a concrete renderer or physics backend. Generic ECS entities are **not** a representation of every voxel cell: densely packed world/chunk data remains a separate future subsystem. UI and tools may be object-oriented; no forced ECS-everywhere design.

## Repository and package boundaries

- WP01 B00 will create `Directory.Build.props` in **each repository** with independent base `0.1.0`; no such file is established by this documentation PR. Normal commits MUST NOT increment SemVer.
- Groundwork produces immutable `.nupkg` files with uniquely assigned package ID + version; dev packaging uses a unique `0.1.0-dev.<run-id>.<attempt>`-style prerelease override without changing the committed source version.
- WYU MUST use an exact, approved Groundwork package version (including prerelease identifier when used), a tracked `packages.lock.json` and `dotnet restore --locked-mode`. No cross-repository `ProjectReference` or local linked engine source.
- Package SHA-256 is verified against a separately trusted source manifest **before** staging into a configurable local NuGet feed outside either Git worktree. A hash shipped only with an untrusted package does not authenticate it. No GitHub Packages deployment is assumed.
- Feed source mapping MUST constrain `Groundwork.*` to that validated feed; restore tests MUST use a fresh cache and fail closed on absent/wrong/modified packages, manifests or locks.
- Normal dev artifacts: 90-day retention where supported, with preservation of still-pinned artifacts. Releases: durable storage without configured auto-expiry. Neither retention nor release CI is implemented by this PR.

## Out-of-scope boundaries

WP01 does **not** implement graphics, planet generation, voxel chunk sizing/meshing, collisions/physics solvers, networking, persistence or game mechanics. Future WP02 rendering and later world systems MUST fit the approved contracts without retroactively depending on WYU from the engine.

## Cross-repo acceptance evidence (to be supplied by implementers)

1. Clean Groundwork build without WYU source; no tracked `bin/`, `obj/` or distributables.
2. A clean separate WYU checkout restores its **explicitly selected**, SHA-256-verified Groundwork package from a trusted artifact and configured feed, with a fresh package cache.
3. Module DAG inspection reports no cycles or game references in Groundwork.
4. Negative tests fail for wrong versions, altered bytes, missing manifests, unapproved fallback feeds and stale lock files.
5. Branches and reviewed PRs target `master`; branch protection is a desired policy, **not assumed enforced**.

See [runtime contracts](runtime-contracts.md), [ownership](ownership.md) and [ADR-0001](../adr/ADR-0001-repository-boundaries.md).
