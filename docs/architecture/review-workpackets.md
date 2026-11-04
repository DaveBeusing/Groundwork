# WP01-C00 isolated reviewer work packets

**Status:** Ready to dispatch; reviews are **not yet executed**. Governing policy: [ADR-0007](../adr/ADR-0007-solo-review-governance.md).  
**Target:** [Groundwork PR #11](https://github.com/DaveBeusing/Groundwork/pull/11), base `master`.  
**Concurrency limit:** Maximum **four agents total** at one time. The integration review begins **only after** the four specialist reports exist.

## Common read-only reviewer contract

Execute each reviewer in a **distinct, fresh agent context**. Give it the exact frozen PR head SHA at invocation time; do not substitute the SHA shown in this template and do not use a floating branch as the review identity. Reviewers must independently inspect the same source commit and the merged ADR/architecture contracts on `master`. Avoid sharing other reviewers' findings until the integration phase.

Review files:
- `docs/architecture/ecs-jobs-review-contract.md` (PR candidate)
- `docs/architecture/runtime-contracts.md`
- `docs/architecture/module-boundaries.md`
- `docs/architecture/ownership.md`
- `docs/adr/ADR-0001-repository-boundaries.md` through `ADR-0007-solo-review-governance.md`, where available on the candidate branch
- linked Groundwork issue #1 and relevant lane issues #2–#9, plus WYU #1 for cross-repo boundaries

Reviewer MUST NOT modify repository files, merge or approve a PR, or assert that tests ran when only documentation was examined. Output a full report in English Markdown with role, tool/session, candidate SHA, examined sources, review time, verdict (`PASS`, `PASS_WITH_NOTES`, `BLOCK`), findings (P0/P1/P2/P3; file/heading; concrete risk; suggested fix), missing evidence and focused test requirements. Store the report in the PR discussion or a separately linked immutable review artifact. A reviewer must be able to explain how its checks differ from simply paraphrasing the documentation.

## L3A — ECS identity and data safety

Check world/slot/generation uniqueness and exhaustion; stale/cross-world references; lease/epoch lifetime, release and cancellation; component version tokens; committed vs provisional IDs; structural queries and mutations; whole-batch atomicity, especially mixed structural/component batches deferred until `BeginTick`. Develop counterexamples such as failed deferred structural application after component writes, an invalid lease retained across a barrier, duplicate generations, reentrant structural commands, and invalid memory reclamation. Report any unsafe partial-publication path as P0/P1.

## L3B — job scheduling and conflicts

Check the immutable `JobDescriptor` scope, declared reads/intended writes, resource-key hierarchy/partition overlaps, directed acyclic dependency graph, stable source ordinals, candidate-set conflict detection, command ordering not based on completion timing, queue saturation/backpressure, cancellation/dependency-fault propagation and deadlocks. Test the semantics mentally against multiple read-only jobs, disjoint partitions, overlapping entity destruction, ordered dependent writers in the same barrier, stale async results and duplicate order keys. Report ambiguous conflict authority or nondeterministic outcomes as blockers.

## L2 — simulation lifecycle and phase ordering

Check `Initialize/Start/Stop/Dispose` and rollback after partial initialization; UI message pump ownership; monotonic timing; base fixed 60 Hz and gameplay 20 Hz; configurable 5-step catch-up and 250-ms backlog cap; pause/resume; interpolation. Verify the visibility matrix `AcquireInput → BeginTick → Gameplay20 → Base60 → CommitResults → PublishState` against the ADRs, the delay of structural/mixed batches, lease draining and world-stop behavior. Report any unstated same-tick visibility or unbounded barrier wait as P1.

## L5 — fault classification and observability

Check supervisor input/output, no silent fault suppression, safe `ContinueAtSafeBarrier`, `PauseForRecovery` and `StopWorld` choices; always stop on `UnsafePartialCommit`; handling of `UnknownCritical`, stale results, failures in shutdown, queue overflow and diagnostic sink recursion. Require structured source/target/correlation metadata without credentials, bounded queues, meaningful fallback for critical evidence, coherent-state proof before continue, and a finite shutdown policy.

## Sequential integration reviewer — C00/cross-lane

After obtaining all four frozen-SHA specialist reports, inspect them together and verify Groundwork module DAG, ownership, WYU exact immutable NuGet pin/provenance and independent build boundary, future Windows host integration, and consistency of errors/timing/ECS/jobs. Reproduce each P0/P1 finding against the precise candidate text; reconcile overlapping claims, propose changes and run a delta-review on impacted specialist areas if the PR changes. Produce a disposition matrix mapping each finding to `fixed`, `accepted-with-rationale`, `deferred-to-owned-issue` or `blocking`, with an identified final candidate SHA. No unreviewed P0/P1 may be waived by the integrator.

## Owner acceptance and closeout

The owner writes a final comment on the PR or Issue #1 along the lines of `I approve Groundwork WP01-C00 architecture revision <HEAD_SHA>, having reviewed the linked four specialist reports and integration review; all blocking findings are closed.` This is a **solo owner acceptance record**, not a GitHub `APPROVE` event. Do not add this comment on the owner's behalf before the reports actually exist and are reviewed.

Then verify any real configured GitHub checks/protections, merge PR #11 only when permitted, record merge SHA and handover into Issue #1, and close with `completed`. No implementation tests or packages should be falsely claimed as WP01-C00 outputs.

## Required follow-up scenarios after 2026-11-04 corrections

These are **review prompts and future implementation tests**, not reports of executed test runs.

| Finding | Specialist(s) | Required adversarial review / later test |
| --- | --- | --- |
| P1-01 deferred commit dependencies | L3A, L3B, L2 | A mixed batch queues at tick T, fails validation at T+1; its three-level dependent chain cannot publish at T and is rejected transitively |
| P1-02 incomplete cleanup/quarantine | L3A, L2, L5 | A cancelled non-cooperative worker still holds a borrowed pointer when Stop/Dispose times out; storage remains owned/quarantined and incomplete cleanup is observable |
| P1-03 coherence forgery/staleness | L3A, L5 | Worker fabricates proof or reuses writer proof after another commit; supervisor must reject it and cannot choose safe continuation |
| P2-04 in-place/no-op barrier | L3A, L2 | A lease acquired before a no-op or non-relocating component commit is invalidated at the next declared barrier |
| P2-05 deterministic admission | L3B, L2 | Randomize arrival/finish order with identical eligible batches and verify stable ordering; reject unstable spawn identity and do not claim full cross-tick replay |

All four isolated reviewer agents must analyze the same **updated frozen commit** after corrective edits and write separate evidence. A reviewer reading this matrix is **not** considered to have executed a test; the sequential integration review validates the cross-lane dependencies. Subsequent changes require corresponding delta-reviews.
