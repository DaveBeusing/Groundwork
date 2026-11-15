# Rendering parity architecture — proposed implementation map

**Status:** Proposed planning packet; no ADR accepted and no implementation verified
**Date:** 2026-11-15
**Baseline:** Groundwork master a8d9e94f3890299de24739688b6ff2582c53dbf8
**Source:** Engine Architecture & Performance Audit GWE-2026-001 and Rendering & Performance Parity Requirements GWE-TR-001

## Scope and constraints

The architecture proposals cover (1) render-pass and resource custody, (2) a reusable PBR material contract, and (3) deterministic texture assets plus bounded GPU residency. They target comparable visual quality and measured frame times for frozen WYU-class reference scenes, **not** blanket feature/performance parity with Unreal or Unity.

- [ADR-0008: Pass and resource custody](../adr/ADR-0008-render-pass-resource-custody.md)
- [ADR-0009: PBR material identity](../adr/ADR-0009-pbr-material-model.md)
- [ADR-0010: Texture assets and residency](../adr/ADR-0010-texture-assets-residency.md)

All three decisions are proposals. Their implementation is gated on independent review and explicit owner approval per ADR-0007. The existing D3D12 renderer, ushort voxel material identity, frame generation leases, fence retirement, immutable package provenance, 60 Hz authority and protection of incompatible saves remain in force.

## Existing issues: extend, do not duplicate

- [#88](https://github.com/DaveBeusing/Groundwork/issues/88) — complete reproducible CPU/GPU timing, budgets and combined WP11 pressure runs. New PBR/texture workloads must contribute measured instrumentation here.
- [#89](https://github.com/DaveBeusing/Groundwork/issues/89) — cross-subsystem adversarial, cancellation/device-loss, WARP/NVIDIA and recovery gates.
- [#90](https://github.com/DaveBeusing/Groundwork/issues/90) — formal qualified handover and owner acceptance for WP11; do not mark completed by opening architecture issues.
- [#113](https://github.com/DaveBeusing/Groundwork/issues/113) — WP15 shadow absolute and relative GPU budget after current master optimization.
- [#114](https://github.com/DaveBeusing/Groundwork/issues/114) — WP16 dense prop stress and upload admission. Texture work must preserve those limits.
- [#115](https://github.com/DaveBeusing/Groundwork/issues/115) — WP17 visual landform and near/orbit continuity.

## New independently testable slices

| ID | Slice | Source ownership | Dependencies | Mandatory proof |
| --- | --- | --- | --- | --- |

| RPA-01 | Pure pass/resource description and validator | `Groundwork.Rendering/Passes` and corresponding portable tests | ADR-0008 accepted | Deterministic plans, bounded negative hazard/cycle/state tests |
| RPA-02 | Adapt the fixed native sequence and own live state/custody handoff | Rendering.D3D12 and WARP tests | RPA-01 | Readback and pass/barrier parity, current-view rejection and GPU retirement |
| RPA-03 | Pass resource/fence diagnostic ledger | Rendering.D3D12 and Diagnostics | RPA-02, #88 | Exact live/retained counts, unknown retirement, opt-in overhead A/B |
| MAT-01 | Portable PBR descriptor and ushort palette binding | `Groundwork.Rendering/Materials` and corresponding portable tests | ADR-0009 accepted | Parameter and generator-ID stability, visual revision and Save/Continue |
| MAT-02 | Untextured analytic PBR shader path | Rendering.D3D12 shaders and tests | MAT-01 | GGX/Smith/Fresnel WARP goldens, legacy fallback |
| MAT-03 | Canonical triplanar mapping and material seam contract | Voxels/Rendering and portable tests | MAT-01 | Canonical LOD, face seams, unaffected edit and rebase control points |
| TEX-01 | Deterministic cooked metadata and offline importer | `Groundwork.Rendering/Textures` and corresponding portable tests | ADR-0010 accepted | Clean-run digest and bytes, bounded invalid input, layouts and interpretation |
| TEX-02 | Bounded static D3D12 upload, bindings and fallback | Rendering.D3D12 and WARP tests | TEX-01 and RPA-02 | Readback, live state and stale binding rejection, pressure, held fence and device loss |
| TEX-03 | Bounded mip promotion, demotion and physical eviction | Rendering.D3D12 and diagnostics tests | TEX-02, #88 | Actual allocated high-water and reclamation, usable-mip progress under mixed pressure |
| INT-01 | Textured voxel/PBR fixture and minimal near/orbit runtime bindings | Rendering/Voxels/Windows code and tests | RPA-02, MAT-02, MAT-03 and TEX-02 | Validated passes, combined shadows/materials, held older binding, canonical handoff and hardware evidence |

The first three packages own disjoint source and test directories within the existing portable Rendering and Rendering.Tests projects. MAT-01 consumes the documented cooked identity syntax without a code dependency on the TEX-01 importer. No shared project, lock, package or solution edits for those slices without coordination. Keep MAT-02 shader changes separate from native state work in RPA-02, and serialize later native edits when their owned files overlap.

## Formal sequencing

1. Freeze the proposal, record four isolated reports and subsequent integration, resolve blockers, repeat affected reviews and secure explicit owner acceptance of the final head. Merge after applicable checks.
2. After the architecture merge, begin RPA-01, MAT-01 and TEX-01 in isolated branches and worktrees. #88 baseline hardware qualification and #113 shadow rebenchmark can continue independently.
3. Integrate RPA-02 after RPA-01. MAT-02 and MAT-03 can follow MAT-01 within their owned lanes. TEX-02 consumes the RPA-02 native handoff and TEX-01 cooked contract.
4. Follow the dependencies for RPA-03, TEX-03 and INT-01. INT-01 proves the three ADRs together with static textures, the validated pass adapter and runtime near/orbit bindings. TEX-03 separately proves physical residency and mixed pressure. Preserve WP11/WP15/WP16 qualification with #88, #89, #113 and #114.
5. Consider a larger render graph, HDR, expanded lighting, postprocessing or virtual texturing only through separate measured decisions.

## Required issue/PR template

Every implementation issue states: goal, exact source paths/lane owner, accepted ADR and dependencies, explicit non-goals, deterministic fixtures/positive + negative tests, failure/rollback behavior, performance measurements and no-regression constraints. Every PR includes exact tested SHA and commands, immutable engine package provenance where consumed by WYU, actual observed outcomes and unfulfilled gates. Four independent specialist reviews, one subsequent integration review, and explicit owner acceptance are required by ADR-0007. Documentation-only proposals are not proof of these reviews.

## Release gate

Do not claim Groundwork graphics or performance parity until frozen matched-content reference scenes on identical hardware establish comparable visual fidelity, CPU/GPU frame p50/p95/p99, memory/streaming behavior and failure recovery. The suggested 1080p/60-FPS and within-15%-of-reference targets from GWE-TR-001 are planning goals only, not adopted production limits.

## GitHub issue tracker (created 2026-11-15)

The issue records below are planning tickets, not evidence of implementation or ADR approval. Their exact acceptance gates and owned paths are in the issue bodies.

| Slice | GitHub issue | First prerequisite(s) |
| --- | --- | --- |
| RPA-01 | [#153](https://github.com/DaveBeusing/Groundwork/issues/153) | ADR-0008 approval |
| RPA-02 | [#156](https://github.com/DaveBeusing/Groundwork/issues/156) | #153 |
| RPA-03 | [#157](https://github.com/DaveBeusing/Groundwork/issues/157) | #156 and existing #88 |
| MAT-01 | [#154](https://github.com/DaveBeusing/Groundwork/issues/154) | ADR-0009 approval |
| MAT-02 | [#158](https://github.com/DaveBeusing/Groundwork/issues/158) | #154 |
| MAT-03 | [#159](https://github.com/DaveBeusing/Groundwork/issues/159) | #154 |
| TEX-01 | [#155](https://github.com/DaveBeusing/Groundwork/issues/155) | ADR-0010 approval |
| TEX-02 | [#160](https://github.com/DaveBeusing/Groundwork/issues/160) | #155 and #156 |
| TEX-03 | [#161](https://github.com/DaveBeusing/Groundwork/issues/161) | #160; reference #88 |
| INT-01 | [#162](https://github.com/DaveBeusing/Groundwork/issues/162) | #156, #158, #159 and #160 |

Review the [draft ADR PR #152](https://github.com/DaveBeusing/Groundwork/pull/152) before implementing any of these tickets. All implementation commits, tests and acceptance need separate evidence and PRs.
