# WP11-C00 — L3A-ECS independent architecture review and handoff

**Created:** 2026-11-08  
**Repository:** `DaveBeusing/Groundwork`  
**Architecture PR:** [#77](https://github.com/DaveBeusing/Groundwork/pull/77) (draft; do not merge yet)  
**Tracking issue:** [#78](https://github.com/DaveBeusing/Groundwork/issues/78) (open)  
**Prior contract candidate:** `94228f153bbc379f7df6731e82a05a4f059b14ad`  
**Review target:** The exact **latest frozen PR #77 head SHA recorded in issue #78 after this handoff document is committed**, not an older superseded SHA. Confirm the live PR head matches it before starting.  
**Base branch:** `master`  
**Status:** Review instructions and handoff only. Independent L3A review **NOT RUN**; no formal PASS or GitHub approval.

## A. Independent L3A-ECS review mandate — share this section with the reviewer

Act as a **separately invoked, independent specialist** in voxel terrain, entity-component systems (ECS), immutable snapshots, deterministic world state and parallel edit processing. Challenge the architecture; do not assume the owner's choices are correct.

Examine the candidate WP11 architecture contract in `docs/architecture/wp11-procedural-planet-contract.md`, especially R01, R03, R04, R06, R07 and C00-1A/1B/C00-1, alongside the matching frozen `docs/wp11-review-workpackets.md`, `docs/adr/ADR-0007-solo-review-governance.md`, relevant Core/ECS and Voxel implementation, and persistence/publication contracts. Do not use previous preflight conclusions or other specialist reports.

### Questions to try to falsify

1. **Canonical identity:** Can two bricks, six cube-sphere chart faces, different LODs, or negative/large Int64 coordinates ever disagree about the identity or value of a density vertex or material cell? Specify overflow, division, rounding, border ownership, and radius/resolution cases.
2. **Single geometry authority:** Does the signed versioned density field unambiguously determine solid/empty/zero-surface geometry? Can legacy occupancy flags, material metadata, unknown/unavailable density, or material interpolation contradict geometry used by rendering, collision or persistence?
3. **Transactional edits:** Do multi-brick/multi-LOD edits have complete canonical read/write/dependency footprints (including shared vertices, face/edge/corner neighbors, normals, collision, presentation, saved overlay), a deterministic conflict ordering and a single atomic publication barrier? Can readers see a partial edit?
4. **Independent concurrent edits:** Determine precisely whether a world-global expected revision causes disjoint valid edits to be spuriously rejected. Evaluate alternatives (stable total ordering, deterministic retry/rebase for disjoint read/write sets, fine-grained version checks) without sacrificing safety or deterministic replay. Distinguish correctness bugs from throughput/concurrency restrictions.
5. **Runtime integration:** Identify the actual terrain transaction owner, its relationship to `EcsBatchWriter`/Core barriers, immutable readers/leases, GPU presentation acknowledgement, collision generation admission and retirement. Do not assume that ECS entity write scopes automatically protect non-ECS voxel keys.
6. **Negative/failure scenarios:** Examine reversed worker completion, conflicting same-tick brushes, disjoint simultaneous brushes, stale base revision, change at an eight-brick corner, negative-coordinate seam, mixed LOD, cancel after partial mesh completion, delayed/failed Present, save during staging and restart after a committed edit. Show what is authoritative and what resources remain owned after each case.

### Required output and verdict

Produce an independent self-contained written report with: reviewer identity/model, tool/session identifier, UTC timestamp, repository, PR and exact head/base SHA; source paths and review method; numbered findings at P0/P1/P2/P3 with scenario/evidence, impact and implementable correction; test matrix and any tests actually executed (or **NOT RUN**); unresolved questions; and verdict **PASS**, **PASS_WITH_NOTES**, or **BLOCK**. Do not claim independent execution, Windows CI, code tests, benchmarks or GitHub approval if they have not actually happened.

A P0/P1 finding blocks formal architectural acceptance until corrected and re-reviewed on the affected head. Submit the report in issue #78 or link an immutable report artifact there. Do **not** merge PR #77.

## B. Owner-side session handoff — not an input to the independent reviewer

### Confirmed direction

- New sparse planet-centred signed Int64 3D lattice; unique authoritative sample/edit identity independent of render LOD and cube-sphere chart. Initial 32-cell brick and 1 metre near-player resolution are tunable reference values.
- Versioned signed density exclusively determines geometry; material is metadata, unknown is not empty.
- Cross-brick density/material changes are deterministic, versioned atomic world transactions. Parallel preparation may happen, but no partial terrain/collision/render/save publication.
- Collision Coverage Admission holds only the unsupported action, not the whole fixed-tick simulation.
- Marching Cubes with validated Transvoxel-style transitions is a **reference candidate**; the Dual Contouring/Surface Nets comparison and benchmarks are still pending.

### Open acceptance gates

- Formal independent **L3A-ECS** review has not run; no reviewer report or APPROVE exists.
- Remaining independent **L3B-JOBS**, **L2-SIM**, **L5-FAULTS** reviews and subsequent sequential integration review have not run.
- Meshing comparison, hardware/performance evidence and final resource ceilings remain unmeasured.
- No final owner acceptance of a reviewed SHA; PR #77 remains **DRAFT**, issue #78 remains **OPEN**.
- Do not merge the planning PR or start dependent implementation until ADR-0007 gates are satisfied.

### Resume procedure

1. Read issue #78 for the latest recorded freeze. Confirm PR #77 head equals that SHA, and share **Section A only** plus the frozen sources with a genuinely separate reviewer/agent.
2. Receive the L3A report, record it with exact SHA, triage findings and patch the architecture only if necessary. Re-freeze and re-review any affected scope.
3. Continue L3B, L2 and L5 independently, keeping each report isolated from the others; integrate only after all four reports.
4. Resolve P0/P1, run required prototypes/benchmarks, obtain explicit owner acceptance of the final reviewed head, satisfy configured GitHub checks, then consider merging PR #77.

This file is a durable restart artifact, **not** evidence that any specialist review was executed.
