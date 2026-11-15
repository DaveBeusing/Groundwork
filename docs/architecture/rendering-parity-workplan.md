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
| RPA-01 | Pure pass/resource description and validator | Groundwork.Rendering + portable tests | ADR-0008 accepted | Negative hazard/cycle tests; no backend change |
| RPA-02 | Adapt fixed sky/terrain/shadow recording to plan | Rendering.D3D12 + WARP tests | RPA-01 | Readback parity, held generation and safe GPU retirement |
| RPA-03 | Pass resource/fence diagnostic ledger | Rendering.D3D12 + Diagnostics | RPA-01; #88 | Correct retained bytes and opt-in overhead A/B |
| MAT-01 | Portable PBR descriptor and ushort material palette binding | Rendering, Voxels + tests | ADR-0009 accepted | Parameter and generator-ID stability; no native GPU pointers |
| MAT-02 | Untextured analytic PBR shader path | Rendering.D3D12 shaders + tests | MAT-01 | GGX/Smith/Fresnel WARP goldens, baseline fallback |
| MAT-03 | Canonical triplanar mapping and material seam contract | Voxels/Rendering + tests | MAT-01 | LOD, face seams, edits and rebase coordinates |
| TEX-01 | Deterministic cooked texture metadata and importer | Portable tools/contracts + tests | ADR-0010 accepted | Exact content digests, dimensions, sRGB/linear and corrupt-input tests |
| TEX-02 | Bounded D3D12 texture upload, handles and fallback | Rendering.D3D12 + WARP tests | TEX-01; RPA-01 | Readback, stale handles, fence/device-loss, backpressure |
| TEX-03 | Bounded mip residency promotion/eviction | Rendering.D3D12 + diagnostics tests | TEX-02; RPA-02 | Pressure with terrain/props, valid fallbacks and finite GPU high-water |
| INT-01 | One reusable textured voxel/PBR reference fixture | Rendering/Voxels/Windows tests | MAT-02; MAT-03; TEX-02 | Visual goldens, native GPU timing, edit/rebase and package handover |

**Independent testability** does not mean independent merge order. Each slice has its own reproducible positive/negative checks and a limited owned surface. Where cross-module edits are unavoidable, coordinate through the architecture owner; no unreviewed shared-file race. No source or CI changes are included in this architecture packet.

## Formal sequencing

1. Freeze ADR-0008/0009/0010 content and record the four isolated specialist reviews + subsequent integration review. Owner signs off the exact reviewed SHA before accepting the contracts.
2. Complete #88 hardware/reference evidence and #113 shadow rebenchmark in parallel with portable validation prototypes; do not interpret green CI as proven performance parity.
3. Implement RPA-01, MAT-01 and TEX-01 as small independent PRs after contract approval.
4. Implement RPA-02, MAT-02, MAT-03 and TEX-02. Keep current rendering output unchanged in disabled mode.
5. Implement RPA-03, TEX-03 and INT-01 with guarded WARP/NVIDIA and new-reference benchmark evidence. Respect #89 and existing WP11/WP15/WP16 gates.
6. Only then decide a larger render graph, dynamic lighting architecture, HDR or virtual texturing using measured artifacts and separate ADRs.

## Required issue/PR template

Every implementation issue states: goal, exact source paths/lane owner, accepted ADR and dependencies, explicit non-goals, deterministic fixtures/positive + negative tests, failure/rollback behavior, performance measurements and no-regression constraints. Every PR includes exact tested SHA and commands, immutable engine package provenance where consumed by WYU, actual observed outcomes and unfulfilled gates. Four independent specialist reviews, one subsequent integration review, and explicit owner acceptance are required by ADR-0007. Documentation-only proposals are not proof of these reviews.

## Release gate

Do not claim Groundwork graphics or performance parity until frozen matched-content reference scenes on identical hardware establish comparable visual fidelity, CPU/GPU frame p50/p95/p99, memory/streaming behavior and failure recovery. The suggested 1080p/60-FPS and within-15%-of-reference targets from GWE-TR-001 are planning goals only, not adopted production limits.