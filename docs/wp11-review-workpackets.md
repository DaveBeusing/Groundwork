# WP11-C00 isolated architecture review work packets

**Review target:** [Groundwork PR #77](https://github.com/DaveBeusing/Groundwork/pull/77) against `master`.
**Candidate commit:** Use the **latest review freeze** recorded in [C00 issue #78](https://github.com/DaveBeusing/Groundwork/issues/78), issued *after* the five C00 clarifications were incorporated into the contract. Earlier freeze comments are superseded. Each reviewer must verify the live PR head equals that exact SHA before starting; do not review `master` in place or silently accept later commits.
**Status:** Four specialist packets prepared; **none counts as an executed review**. This document is a dispatch instruction, not a review report.

## Owner-confirmed focal decisions to challenge (2026-11-08)

All four independent reviewers must challenge — rather than assume correctness of — these newly confirmed design baselines in the [WP11 contract](docs/architecture/wp11-procedural-planet-contract.md):

- **Identity and edits:** Unique planet-centred signed Int64 Cartesian coordinates for volume cells and density vertices; material edits target cells, sculpting targets vertices; deterministic multi-brick invalidation and no duplicate authoritative keys at cube-face/LOD joins. The 32-cell / 1 m finest-resolution parameters are tunable.
- **Collision coverage:** Predictive movement-envelope streaming and fail-closed unknown coverage. Hold/reject only the affected action at the last valid state while independent fixed-step systems continue; do not replay accumulated movement after a coverage hold. Admitted mesh/collision/origin stay coherent via the recorded publication barrier. Review orbit/approach/touchdown collision handoff.
- **Meshing benchmark:** Marching Cubes + validated Transvoxel-style transition cells is the **first reference**, not final production approval. Compare independently against a constrained Dual Contouring/Surface Nets candidate on the same adversarial fixtures. Document memory, geometry/collision consistency and performance for both; budgets remain provisional and unmeasured.

Focused checks:
- **L3A-ECS:** negative floor-division, brick boundary ownership, vertex/cell edit distinctions, duplicated seam writes, invalidation including corners and LOD dependencies.
- **L3B-JOBS:** predictive reservation/backpressure, bounded work ahead of movement, deterministic edit ordering and cancellation, stale candidate rejection and resource accounting.
- **L2-SIM:** admission barrier vs GPU Present, targeted action hold without advancing pose or replay-burst, fixed-tick independence of unrelated systems and flight-to-surface collision handoff.
- **L5-FAULTS:** corrupted/unknown coverage fail-closed, save lineage integrity, GPU and job ownership on failed Present/device loss, quota failures, and benchmark proof-vs-assumption.

The **review freeze is the exact live PR head recorded in issue #78 after these documentation changes**, not the previous `bb635383...` candidate. Treat earlier freeze comments as superseded. No reviewers have run as part of preparation.

## P1 owner decisions to scrutinize — single geometry authority and atomic edits

The owner approved the following contract additions on 2026-11-08 in the WP11 proposal. They remain **unverified** and must be challenged independently:

- **C00-1A:** Signed, versioned density is the sole geometric solid/empty source. Material at a unique cell is metadata and cannot independently override the density field. Unknown/unavailable density is not empty. Validate zero-sign/tie semantics, deterministic isosurface/material assignment and explicit rejection of contradictory legacy occupancy representations.
- **C00-1B:** One edit spanning many bricks is a world-scoped, canonical, revision-checked **atomic transaction**. Derived mesh/collision/LOD effects and save snapshot are coherent; no partial publication, no worker-dependent conflict winner, and no bypass of Core publication/lease ownership.
- **L3A-ECS leading P1 questions:** What exact terrain read/write set and conflict owner applies to shared density vertices (which are not ECS entities)? How do renderer readiness, the Core publication barrier, post-commit presentation and retained reader leases enforce the world snapshot and visible admission rules? What is the unambiguous material-on-empty/zero-density policy? Construct failure traces for overlapping concurrent edits and stale save/mesh results.
- **All specialists:** Do not infer that author consent closes P1 correctness concerns or meets ADR-0007 independent review requirement. Report potential contradictions by severity and request corrections before implementation.

## Independence and evidence protocol

Assign each packet to a *separately invoked reviewer context/agent*. A reviewer receives only the packet for their role plus source documents listed below; it must not read or inherit other reviewers' work, findings, comments, or output until its independent report is sealed. Do not fabricate reviewer accounts or GitHub approvals. Maximum four concurrent specialist review/implementation agents.

Each reviewer must: (a) verify repository/PR/base/head SHA; (b) inspect target contract, relevant existing code and linked ADRs; (c) challenge requirements with concrete negative cases; (d) produce a self-contained report; and (e) state what was actually run. Source/contract review only is acceptable for this documentation proposal; **never claim runtime benchmarks, Windows CI, NVIDIA/WARP tests or agent invocations occurred without evidence**.

Mandatory report header: role, invoked tool/context/session ID, model/reviewer identity, review time (UTC), repository/PR number, exact frozen head/base SHA, scope, source paths and links, method. List numbered findings with P0/P1/P2/P3, contract section/file, proof or scenario, impact, required correction, and disposition recommendation. Verdict is PASS, PASS_WITH_NOTES or BLOCK. Include unresolved questions, executed tests (or explicitly none), reference hardware availability and review artifact link. P0/P1 must be fixed before merge. Reviews after material head edits must be rerun for affected scope; record any changed head.

## Shared frozen review sources (read independently)

- `docs/architecture/wp11-procedural-planet-contract.md` — R01–R09, **C00-1 through C00-5 architecture clarifications**, DAG, acceptance. Reviewers independently challenge these candidate choices; they must not treat them as validated by the author preflight.
- `docs/wp11-issues.md` and `docs/roadmap.md` — issue tracking and milestone.
- `docs/adr/ADR-0007-solo-review-governance.md` — isolated review and owner acceptance.
- `docs/adr/ADR-0006-packaging-release.md`, `docs/adr/ADR-0001-repository-boundaries.md`.
- `docs/architecture/wp08-spherical-planet-contract.md`, `docs/architecture/wp09-surface-to-orbit-contract.md`, `docs/architecture/wp06-save-recovery-contract.md`.
- Actual implementation as of the recorded base SHA, including `src/Groundwork.Voxels/`, `src/Groundwork.Persistence/`, `src/Groundwork.Rendering.D3D12/`, `src/Groundwork.Core/`, `src/Groundwork.Jobs/` and relevant tests. Inspect only relevant files for the role.
- WYU `src/WorldsYetUnseen.Client/`, `src/WorldsYetUnseen.Gameplay/`, `docs/World_Vision.md` when game integration/compatibility is relevant.
- Do not use notes from other reviewers or any preliminary triage discussion as an input to the independent report.

## Packet 1 — L3A-ECS / authoritative volumetric world and ownership

**Task:** Independently review R01, R03, R04, R06 and R07 against Core/ECS snapshot and ownership models. Determine whether sparse bricks, canonical sample keys, material snapshots and edit overlays can safely coexist with current generation IDs/lease semantics while discarding obsolete radial APIs.

Inspect and challenge:
- 64-bit integer voxel/brick mapping at 6,400,000 m planetary radius; unique cross-face boundary representation; sample identity across LOD; negative coordinate flooring, overflow, numeric error and planetary rebase rules.
- Whether density samples, immutable bricks, neighbour halos, edit overlays and material IDs have precisely defined ownership and lifetime without voxel-per-ECS-entity storage.
- Atomic publication of edited geometry/collision versus existing readers, structural mutation batching, revision monotonicity and rollback on failure.
- Multi-layer surfaces, enclosed cavity, disconnected solid and overhang; ensure tests do not assume a single radial support hit.
- Limits for retained immutable snapshots during worker, collision, reader and GPU retirement; edit/save coherence.

**Required deliverable:** concrete blocking omissions, an implementable identity/data-contract recommendation, adversarial fixture list and explicit verdict.

## Packet 2 — L3B-JOBS / scheduling, streaming and determinism

**Task:** Independently review R02, R04, R05, R07, R09 and dependencies against current Groundwork.Jobs, Core admission and bounded work ownership contracts.

Inspect and challenge:
- Exact queue and reservation count at every state (admitted, running, completed/unconsumed, superseded, retiring), two-stage generation/meshing DAG and bounded updates on rapid camera/terrain edits.
- How serial/parallel/out-of-order processing can produce identical sampled bytes, mesh ordering, edit commit and persistent overlays.
- Concurrent read/write ownership and absence of direct unsupervised writes to shared ECS or terrain snapshots; rejection of unordered conflicting command batches.
- Cancellation, fault propagation and no deadlock when jobs finish during pause/stop, Core deadline or device removal. Quota rejection must not spin or abandon reservations.
- CPU/GPU high-water accounting, 512 MiB / 256 MiB provisional ceilings and conditions under which benchmark-derived ceilings can be frozen.

**Required deliverable:** scheduling/ownership DAG, bounded-resource failure scenarios, deterministic admission/commit rules, severity-ranked findings and verdict.

## Packet 3 — L2-SIM / simulation, coordinate continuity and presented coverage

**Task:** Independently review R01, R02, R05, R06, R08 and R09 against 60/20 Hz fixed-step simulation, Core safe barriers, radial gravity and WP09 flight semantics.

Inspect and challenge:
- Authoritative doubles, planet radius 6,400 km, local render rebasing, object/terrain velocities, max update range, precision under orbit + return.
- Define exact point at which staged terrain and collision become *simulation-authoritative*; distinguish first-time presentation acknowledgment from continuous rendered frame pacing. Verify that renderer latency cannot change the recorded player input and that the effective hold, commit and release ticks are recorded as authoritative replay admissions.
- How collision holds freeze the entire affected transaction while independent world ticks continue. Verify authoritative finalization separately from observer delivery. Swept-volume collision must admit ceiling/overhang/cavity contacts and gravity transitions.
- Edit+save consistency at a named published tick barrier, origin and recipe identity, and revisits after flight.
- Ensure deterministic trace semantics across different job completion order even where render readiness varies; identify what is allowed to differ (wait counts, elapsed time).

**Required deliverable:** explicit simulation/publication state machine, replay invariants, unsafe interleavings and verdict.

## Packet 4 — L5-FAULTS / fault safety, persistence, native GPU lifecycle

**Task:** Independently review R03–R09 and WP06/WP10 recovery contracts for supervised failures, version mismatch, allocation pressure, corrupted saves and D3D12 device retirement.

Inspect and challenge:
- Error taxonomy: UnsupportedVersion, IncompatibleOldSave, Corrupt, Busy, Cancelled, UnknownCoverage, QuotaExceeded, StaleCandidate, DeviceRemoved and terminal failure; each must have fail-closed state and diagnostic ownership.
- Strict no-silent-overwrite of old alpha saves; one-world lineage, atomic alternating NTFS commits, flush/verify semantics, lock, retry idempotency, interrupted writer, two writers and lost acknowledgments.
- CPU/GPU lease retention when stage/upload/present/retire fails, Signal failure, late worker during stop and device removal sentinel; do not release without proof.
- Structured bounded diagnostics, logging faults, exposure of exact version/seed without sensitive data, and reliable cleanup if OS/UI/disk becomes unavailable.
- Whether benchmarks and final performance/resource gates are actually specified/measurable before implementation (avoid treating provisional ceilings as measured).

**Required deliverable:** fault matrix with expected state/ownership after each injected failure, P0/P1 blockers, untested platform areas and verdict.

## Integration review — sequential, after all four specialist reports

Do **not** start until all four isolated specialist reports are recorded against the same frozen candidate revision. Integrator reads all reports, maps each finding to fixed / accepted-with-rationale / deferred-with-owned-issue / blocking, verifies consistent module boundaries and no new contradictions, and proposes required contract patches and re-review scope. It must not override an unresolved P0/P1.

Before closing C00: record benchmark-derived limits and selection evidence for meshing/LOD; obtain separately recorded explicit owner acceptance of the final head SHA; satisfy actual GitHub checks/protections; then merge and log merge SHA. Merely granting a general architecture direction approval does not equal final reviewed-head sign-off.

## Completion checklist

- [ ] Freeze PR #77 exact candidate commit; report base and head in C00 issue.
- [ ] Launch isolated L3A-ECS review; preserve report identity.
- [ ] Launch isolated L3B-JOBS review; preserve report identity.
- [ ] Launch isolated L2-SIM review; preserve report identity.
- [ ] Launch isolated L5-FAULTS review; preserve report identity.
- [ ] Run *subsequent* integration review and close blockers.
- [ ] Re-review material changes on the new SHA.
- [ ] Record measured meshing/streaming/resource budgets and decisions.
- [ ] Obtain owner acceptance of the final reviewed head SHA.
- [ ] Merge PR #77 only when permitted by configured checks and governance.
