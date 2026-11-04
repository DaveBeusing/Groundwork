# WP01 ownership, review and release handover

**Status:** Proposed implementation coordination plan for [Groundwork WP01-C00](https://github.com/DaveBeusing/Groundwork/issues/1). **No owner sign-offs or enforcement are claimed by this document.**

**Solo-owner review exception (2026-11-04):** [ADR-0007](../adr/ADR-0007-solo-review-governance.md) defines the owner-approved process of **four isolated AI specialist passes**, followed sequentially by one integrator pass and explicit final owner acceptance on the reviewed SHA. Review roles below are not automatically distinct human GitHub accounts. The exception replaces the requirement for a separate human GitHub `APPROVE` where the project has a sole owner, but does **not** waive mandatory configured branch protections, CI gates, actual review evidence or blocking finding resolution. **Maximum four concurrent agents.**

## Work lanes and exclusive write scopes

| Lane / issue | Owned scope | Interfaces and handover requirements |
| --- | --- | --- |
| C00 / [#1](https://github.com/DaveBeusing/Groundwork/issues/1) | `docs/architecture/**`, `docs/adr/**` | Approve contracts and record reviewed revision before coding |
| B00 / [#2](https://github.com/DaveBeusing/Groundwork/issues/2) | Root `Directory.Build.props`, `global.json`, solutions, project files, `.editorconfig`, package map | Freeze assembly IDs, build settings, SDK and 0.1.0 baseline in both repos via separate PRs |
| L1 / [#3](https://github.com/DaveBeusing/Groundwork/issues/3) | Windows host/input implementation and tests | Match Core lifecycle and input contract |
| L2 / [#4](https://github.com/DaveBeusing/Groundwork/issues/4) | Clock/scheduler code and tests | Match fixed step, overload, ordering and supervisor pause/stop |
| L3A / [#5](https://github.com/DaveBeusing/Groundwork/issues/5) | ECS storage, identities, queries and tests | Guarantee generation retirement, borrowed views and atomic commits |
| L3B / [#6](https://github.com/DaveBeusing/Groundwork/issues/6) | Bounded job scheduler, dependency graph and tests | Implement explicit access declarations, snapshots, fault propagation |
| L4 / [#7](https://github.com/DaveBeusing/Groundwork/issues/7) | CI/workflows, packaging/staging tools and integrity tests | Implement SHA-256 manifest trust, local feed, retention and locked-consumer validation |
| L5 / [#8](https://github.com/DaveBeusing/Groundwork/issues/8) | Diagnostics, counters, benchmark harness | Expose structured failures and measured performance |
| G00 / [#9](https://github.com/DaveBeusing/Groundwork/issues/9) | Integration evidence and release/handover | Validate clean build, cross-repo package consumption, report failures |
| WYU L6 / [wyu#1](https://github.com/DaveBeusing/wyu/issues/1) | WYU game host, package pin, lock file and consumer tests | Consume immutable Groundwork NuGet artifacts only |

A lane MUST NOT edit another lane's exclusive paths without a coordinated scoped change. Root files are **B00 only**; changes to a frozen contract require a new ADR/PR and notice to affected lanes. Each PR MUST declare its work package, scope, API implications, tests, compatibility impact and rollback/known limitations. Branches target `master`.

## Review gates

1. **C00 (this PR):** apply ADR-0007: freeze the head SHA; perform four separate L3A/L3B/L2/L5 AI specialist reviews (maximum four concurrent), then a sequential integration review including L1/B00/L4/WYU boundary checks. Record each real report, findings/fixes and current reviewed SHA. The single owner must explicitly accept that revision. Merging without review evidence is prohibited.
2. **B00:** only after C00 approval, establish reproducible project skeletons, supported .NET 10 SDK pin, root versions `0.1.0`, warning/analyzer policy and build commands. No per-commit SemVer bump.
3. **L1–L5 and WYU L6:** only after interfaces are frozen and B00 foundation is ready, implement in parallel according to exclusive scopes. Integrator coordinates shared contract changes.
4. **L4/G00 compatibility:** independent clean Windows build/test; package provenance, SHA-256 manifest, fresh-cache WYU locked restore, negative tests and no engine source link. Document provider retention limits, benchmark evidence, diagnostics.
5. **Release:** separate **future** workflow asks for Major/Minor/Patch, commits new `Directory.Build.props` source version, runs gates and archives release packages/manifests durably. This documentation PR does not implement release automation.

## Decision register and remaining API-level review

| Decision | Policy | Remaining reviewer work |
| --- | --- | --- |
| Generational `EntityId` | **User-approved** world + slot + generation, never wrap, retire exhausted slots | L3A: numeric widths, world allocator, ID API, tests |
| ECS reads | **User-approved** borrowed view ends at next commit barrier; async owned snapshot with version check | L3A/L3B: lease enforcement and version-token representation |
| Job access | **User-approved** explicit immutable pre-dispatch read/output/target declarations, fine-grained scopes | L3B: descriptor schema, DAG validation and benchmark granularity |
| Mutation conflicts | **User-approved** reject conflicting batch atomically, no priority | L3A/L3B: conflict domain, batch boundaries, ordered commit and preflight |
| Recovery | **User-approved** isolate faults and supervisor-controlled resume/pause/stop | L2/L3A/L3B/L5: fault classifications, exact supervisor API and finite shutdown |
| Timing | **User-approved** 60/20 Hz, 5 catch-up steps, 250-ms debt cap, slowdown fallback | L2: metrics, input/commit phase boundaries, synthetic-clock tests |
| Packaging | **User-approved** local NuGet + SHA-256 trusted manifest, exact pins/lockfile, 90-day dev retention, durable releases | B00/L4/WYU L6: configs, scripts, CI, source mapping, reproducibility |

Other explicitly deferred topics: Direct3D 12 provider implementation (WP02), voxel world representation/physics/persistence (later packages), GitHub Packages release distribution and branch protection enforcement. None blocks **opening** the architecture PR; absent ADR-0007 specialist/integration review evidence or explicit final owner acceptance blocks **merging** it as an authoritative baseline.

## Architecture PR acceptance checklist

- [ ] All nine requested documentation files exist with clear `proposed` vs `approved decision` semantics.
- [ ] Reviewers confirm no circular module dependencies or Groundwork reference to WYU.
- [ ] Four isolated L3A/L3B/L2/L5 AI review reports plus one subsequent integration report cover all affected lanes (including L1/B00/L4/WYU interfaces), document findings, and identify the reviewed head SHA according to ADR-0007.
- [ ] Contract examples are expressly conceptual unless built and tested.
- [ ] Local package consumer pathway and failure cases are auditable without relying on an existing feed.
- [ ] No generated files, engine code or root build version changes are introduced by C00.
- [ ] Integrator reports review evidence and fix dispositions, and the sole owner explicitly accepts the final head SHA; notify affected work lanes before parallel implementation.

**Evidence:** Checklist remains unchecked until reviewers actually complete it. C00 issue stays open until reviewed contract PR and supporting sign-off are recorded.

## Review evidence standard and execution limit

For a solo-owner repository, references above to 'component owner review', 'reviewer sign-off' and 'approved revision' require genuine specialist review artifacts + integration analysis + explicit owner acceptance as described in [ADR-0007](../adr/ADR-0007-solo-review-governance.md); they do not imply independent human GitHub approvals. A review plan, checklist or single author's own commentary is insufficient proof. If technical reviewers are unavailable, leave the architecture issue open rather than recording fabricated evidence. Never run more than four agents concurrently, including nested tasks. Code changes retain risk-based tests and required GitHub checks; no fixed coverage quota is imposed.
