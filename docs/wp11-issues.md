# WP11 implementation issue map

**Status: workstation integration verified.** The contract and C00 are complete. Groundwork A01–A03, B01, C01–C03 and D01–D02 and WYU W01–W05 are merged. The [workstation handover](wp11-handover.md) records the exact inputs and tests. Q01, Q02 and G00 remain open through reference qualification and owner acceptance.

**Breaking-change decision:** Old radial-only code APIs, 64 m prototype, incompatible checkpoint formats and obsolete tests **may be replaced rather than adapted**. New tests must cover the relevant correctness and safety invariants. Old unsupported saves are explicitly identified and preserved, with no silent overwrite or automatic migration.

## Tracked work

| Phase | Work package | Issue | Deliverable | Depends on | Requirement |
| --- | --- | --- | --- | --- | --- |
| Architecture | C00 | [Groundwork #78](https://github.com/DaveBeusing/Groundwork/issues/78) | Freeze clean-break volumetric architecture | WP10 accepted | C00 approval + review evidence |
| Core generation | A01 | [Groundwork #79](https://github.com/DaveBeusing/Groundwork/issues/79) | Planet identity and canonical addresses | C00 | R01 |
| Core generation | A02 | [Groundwork #80](https://github.com/DaveBeusing/Groundwork/issues/80) | Versioned deterministic density/landforms | C00, A01 | R02 |
| Core generation | A03 | [Groundwork #81](https://github.com/DaveBeusing/Groundwork/issues/81) | Sparse bricks, material samples, leases | C00, A01, A02 contracts | R03 |
| Geometry | B01 | [Groundwork #82](https://github.com/DaveBeusing/Groundwork/issues/82) | Watertight volume meshes and LOD seams | C00 meshing choice, A01, A02, A03 | R04 |
| Runtime | C01 | [Groundwork #83](https://github.com/DaveBeusing/Groundwork/issues/83) | Bounded streaming and coherent publication | A01, A03, B01 contracts | R05 |
| Runtime | C02 | [Groundwork #84](https://github.com/DaveBeusing/Groundwork/issues/84) | Volumetric collision and movement admission | A03, B01, C01 | R06 |
| Runtime | C03 | [Groundwork #85](https://github.com/DaveBeusing/Groundwork/issues/85) | D3D12 volume presentation and safe GPU retirement | B01, C01; coordinates C02 | R04, R05, R08 |
| Edit and persistence | D01 | [Groundwork #86](https://github.com/DaveBeusing/Groundwork/issues/86) | Atomic 3D edits, remesh + collision publication | A01, A03, B01, C01; coordinates C02/C03 | R03, R07 |
| Edit and persistence | D02 | [Groundwork #87](https://github.com/DaveBeusing/Groundwork/issues/87) | New WP11 save codec and NTFS recovery | A01, A03, D01 | R07 |
| Quality | Q01 | [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88) | Terrain telemetry and reproducible benchmarks | C00; integrate incrementally | R05, R09 |
| Quality | Q02 | [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89) | Adversarial cross-module qualification | A01-A03, B01, C01-C03, D01-D02, Q01 | R01–R09 |
| Game | W01 | [WYU #28](https://github.com/DaveBeusing/wyu/issues/28) | Seeded reference planet and safe spawn | C00, A01/A02 contracts | R02, R08 |
| Game | W02 | [WYU #29](https://github.com/DaveBeusing/wyu/issues/29) | Playable generated-world host and selection UI | W01, A01-A03, B01, C01-C03 | R05, R06, R08 |
| Game | W03 | [WYU #30](https://github.com/DaveBeusing/wyu/issues/30) | Terrain editing, save, continue | W02, D01, D02 | R07, R08 |
| Game | W04 | [WYU #31](https://github.com/DaveBeusing/wyu/issues/31) | Rebuilt launch/orbit/landing regression | W02, C02, C03; W03 for save test | R06, R08 |
| Game | W05 | [WYU #32](https://github.com/DaveBeusing/wyu/issues/32) | Installed Windows alpha playtest and failure UX | W03, W04; Q02 evidence | R08, R09 |
| Integration | G00 | [Groundwork #90](https://github.com/DaveBeusing/Groundwork/issues/90) | Final verified-package handover and owner acceptance | Q01, Q02, W05; all tracks | Final gates |

## Mandatory execution sequence

1. **Architecture gate**: [Groundwork #78](https://github.com/DaveBeusing/Groundwork/issues/78) freezes generation data model, coordinate encoding, meshing baseline, prototype evidence, downstream capacity and seam gates, save incompatibility behavior, and review/owner sign-off on final proposal SHA.
2. **Generative kernel**: [Groundwork #79](https://github.com/DaveBeusing/Groundwork/issues/79), [Groundwork #80](https://github.com/DaveBeusing/Groundwork/issues/80) and [Groundwork #81](https://github.com/DaveBeusing/Groundwork/issues/81) establish deterministic volume data and addresses. [WYU #28](https://github.com/DaveBeusing/wyu/issues/28) creates WYU-specific reference planet recipes as soon as the contracts are accepted.
3. **Geometry and runtime**: [Groundwork #82](https://github.com/DaveBeusing/Groundwork/issues/82) then [Groundwork #83](https://github.com/DaveBeusing/Groundwork/issues/83), [Groundwork #84](https://github.com/DaveBeusing/Groundwork/issues/84) and [Groundwork #85](https://github.com/DaveBeusing/Groundwork/issues/85) establish mesh, streaming, collision and presentation. [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88) provides measurements alongside those PRs.
4. **New playable planet**: [WYU #29](https://github.com/DaveBeusing/wyu/issues/29) integrates generated terrain into the client; no mandatory compatibility facade for old WP08/09 APIs.
5. **Edits and recovery**: [Groundwork #86](https://github.com/DaveBeusing/Groundwork/issues/86) then [Groundwork #87](https://github.com/DaveBeusing/Groundwork/issues/87), followed by [WYU #30](https://github.com/DaveBeusing/wyu/issues/30), deliver coherent editing and an intentionally new, atomic checkpoint schema. Unsupported saves are not loaded or destroyed.
6. **Flight and user qualification**: [WYU #31](https://github.com/DaveBeusing/wyu/issues/31) replaces the old small-planet flight fixture and [WYU #32](https://github.com/DaveBeusing/wyu/issues/32) qualifies the installed client. [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89) performs adversarial engine verification.
7. **Milestone completion**: [Groundwork #90](https://github.com/DaveBeusing/Groundwork/issues/90) collects reviewed SHAs, actual CI and hardware tests, immutable NuGet artefacts, deterministic traces, resource benchmarks, owner approval and the final handover. Close WP11 only on demonstrated evidence.

## Parallelism and scope controls

- No more than four development/review agents concurrently; four independent specialist review reports and one subsequent integration review are required by ADR-0007 for each approved review gate.
- Keep ownership in separate Groundwork and WYU PRs, targeting **master**. Each code PR must list touched modules, dependencies, associated issue, exact test executions, failing/stale tests replaced and explicit known limitations.
- Groundwork provides generic reusable subsystems; WYU owns content, presets, gameplay, UI and player routes.
- Never conflate planning issues with acceptance or claim automated reviews/tests or approval before they occur.
- The remaining fresh Windows 11 offline verification follow-up from accepted WP10 continues separately; WP11 does not secretly close or replace it.

## Acceptance checkpoints

- **Contract accepted:** No unresolved fundamental model or algorithm ambiguity, C00 reference thresholds and downstream qualification criteria approved, all required reviews and owner sign-off.
- **Procedural engine accepted:** Stable seed/version, deterministic 3D density, sparse storage, 6,400 km numeric support, crack-free mesh, safe collision/publication, bounded workload and robust device settlement.
- **Persistent interactive world accepted:** Game route from a selected seed, terrain edit and unload/reload, exit/save/Continue from fresh process, exact world/pose and edit result.
- **Flight accepted:** New-world takeoff, orbit and touchdown using the same generator lineage and edits; trace repeatability and proper restart.
- **Release evidence accepted:** Green Windows CI for both repos, WARP and NVIDIA D3D12 Debug Layer/DRED evidence, zero live GPU child objects on clean exit, package hashes and explicit owner acceptance of reviewed heads.

**Qualification dependencies.** C00-4 assigns chart identity to A01, render seams to B01 and collision seams to C02. C01 and C03 must demonstrate bounded admission and settlement. Q01 starts alongside C01 and needs C01/C03 before freezing production limits. Q02 validates the integrated route and seams against Q01 limits. Q02 precedes W05. Q01/Q02/W05 precede G00, and Q01/Q02 qualification precedes production cutover.

All issue URLs above are real issue records created for this plan. The planning contract and issue titles are proposals, not implementation results.
