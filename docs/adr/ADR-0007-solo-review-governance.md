# ADR-0007 — Solo-owner review governance

**Status:** Proposed for merge; process choice approved by the product owner on 2026-11-04.  
**Scope:** Groundwork WP01-C00 and subsequent changes; apply the same approach to the WYU integration only after adopting it in that repository.  
**Tracks:** [Groundwork #1](https://github.com/DaveBeusing/Groundwork/issues/1), [PR #11](https://github.com/DaveBeusing/Groundwork/pull/11).

## Context and decision

Groundwork is currently run as a solo-owner project using distinct development and review agents. A GitHub `APPROVE` submitted by another human is unavailable when there are no other contributors; GitHub does not permit the PR author to approve their own PR. The previously documented blanket requirement for independent human/GitHub approvals is **superseded by this explicit solo-project exception**. This exception does not bypass technical validation, misrepresent AI reviews as GitHub approvals, or override mandatory GitHub repository protections.

**Policy:** Use four isolated specialist review passes and one sequential integration review, followed by **explicit owner acceptance** on the current revision and validated automated gates. No more than four review or implementation agents may work concurrently, including when task internals are parallelized.

## Review stages

1. **Freeze the candidate:** record repository, PR, base/head commit SHA, document paths, approved ADRs, linked issues, and acceptance criteria. Every reviewer must see the same frozen candidate SHA; if the PR changes, affected reviews must be rerun.
2. **Four specialist reviews (may run in parallel):**
   - `L3A-ECS`: world-scoped generational IDs, borrowing/leases and epochs, version tokens, structural mutations, batch atomicity and rollback safety.
   - `L3B-JOBS`: declared read/write scopes, partitions, dependency DAG, deterministic admission and commit ordering, queue bounds, cancellation/deadlocks.
   - `L2-SIM`: fixed 60/20 Hz semantics, named commit barriers, phase visibility, five-step/250-ms catch-up, pause/stop and timing.
   - `L5-FAULTS`: supervisor classification/decisions, error propagation, safe stop/recovery, diagnostic backpressure and failure during shutdown.
3. **Integration review (after all four reports):** check findings across module dependency DAG, thread ownership, package/version rules, Groundwork/WYU separation, and that fixes did not create new contradictions. It must *not* silently override specialist blockers.
4. **Resolve findings:** author changes in scoped PRs; re-review each materially changed section and integration-dependent contracts against the new SHA. A reviewer should not author the same corrective patch they attest to independently.
5. **Owner sign-off:** the repository owner records explicit approval of the final head SHA and the review evidence in the PR or linked issue. An earlier conversation-level architecture preference is not automatically sign-off on a newer patch.
6. **Merge and handover:** enforce repository branch protections and required checks (if configured). Merge only after gates pass; record merge SHA, linked issues, open deferrals and notify affected work lanes.

## What qualifies as an isolated AI review

A review requires an independently invoked agent/context, separate from the implementation/editing context, with its own review instructions, frozen source and reference documents. Do not feed it prior reviewers' conclusions before its independent pass. Reviewer labels are **roles**, not invented GitHub accounts or evidence that an agent was executed. The integration reviewer sees all four reports only after they are recorded.

A single assistant reading the same file four times **does not count** as four independent agents. A conversation summary, drafted review prompt, or comment by the PR author is not a completed specialist review. Never submit a GitHub `APPROVE` on behalf of a non-existent person or imply a formal GitHub approval.

## Required evidence per specialist and integration review

Each report MUST include:
- reviewer role, execution context/tool/session identifier, model or reviewer identity, review time, PR and exact head SHA;
- scope, source documents and method (static contract review, code review, tests actually run);
- numbered findings with `P0` critical, `P1` blocking, `P2` important or `P3` advisory severity, precise file/heading, proof or reproducible scenario, risk and recommended fix;
- `PASS`, `PASS_WITH_NOTES` or `BLOCK` verdict; no unsupported PASS claims;
- unresolved questions and whether required tests/CI/Windows-specific verification were unavailable;
- link to the immutable review artifact or full report text in the PR/issue.

**Integration review** MUST map all findings to their disposition (`fixed`, `accepted-with-rationale`, `deferred-to-owned-issue`, `blocking`) and the final reviewed head SHA. Document the reason and owner for every deferred finding.

## Quality and merge gates

- No unresolved P0 or P1 finding may be merged. P2 findings may be deferred only if scoped, justified and assigned to a follow-up issue; P3 is advisory.
- For documentation-only architecture PRs, contract completeness and review consistency are the required substantive gates; do not invent build/test results or require a nonexistent implementation to be executed.
- For code PRs, build/tests and quality/security checks MUST run and pass on the **actual candidate**; testing is risk-based, not a fixed coverage percentage. Changes to ECS, concurrency, physics/time, persistence or security need relevant negative, stress/property or fault-injection tests.
- Performance-sensitive changes require reproducible benchmarks, tracked environment/sample metadata and comparisons to the established baseline. Option B target: one million managed entities, at least 100,000 simulated at 60 Hz, measured rather than presumed; voxel cells are **not** ECS entities. A representative 8-core/32-GB gaming-class machine is the primary reference, with high-core workstation scaling runs separately. Absence of reference hardware should be reported, not invented as a pass.
- All required configured GitHub checks and branch protection conditions remain binding. If current protection requires an independent GitHub user approval that cannot be provided, **do not bypass it**; change the rule transparently through the repo owner or obtain a real reviewer.
- Owner acceptance is recorded for the exact reviewed commit, and successful merge evidence is logged separately.

## WP01-C00 transition

For [PR #11](https://github.com/DaveBeusing/Groundwork/pull/11), the product owner approved this solo-agent process, **not** completion of the four specialist runs. Before merging, execute and preserve all four isolated review reports, a subsequent integration report, process any blocking corrections and obtain explicit final owner sign-off on the unchanged reviewed head SHA. Then close [Issue #1](https://github.com/DaveBeusing/Groundwork/issues/1) only when the documented acceptance criteria are satisfied.

The earlier owner/role sign-off wording in `docs/architecture/ownership.md` and the supplemental ECS contract is interpreted under this ADR as **review role evidence + explicit solo-owner acceptance**, not as fictional human or GitHub reviews. All prior engine correctness constraints remain in force. This document proposes governance; it does not claim any agents were invoked or any checks succeeded.
