# WP01 — ECS, jobs and supervision: contract clarification for review

**Status:** Proposed API-level contract, not implemented or signed off. Supplements [WP01-C00](https://github.com/DaveBeusing/Groundwork/issues/1) and [runtime contracts](runtime-contracts.md). Existing approved policies remain authoritative. The C00/L2/L3A/L3B/L5 owners must review the concrete choices below before implementation.

## 1. Entity and read access

- `EntityId` identifies an immutable `WorldId`, slot and generation. Equality includes all three fields. A stale, foreign-world or retired slot never aliases a live entity. A generation overflow permanently retires the slot; world IDs are not recycled while stale references may remain.
- The L3A API must distinguish `MissingComponent`, `StaleEntity`, `WrongWorld` and `WorldDisposed` without returning writable live references.
- Borrowed read leases carry world identity and an epoch. Lease acquisition and disposal are explicit; dereference after disposal or after the next synchronization barrier fails. A barrier MUST NOT advance while an active borrowed lease can still be dereferenced. The scheduler admits only barrier-bounded borrowed-read jobs, waits for their completion within a configured finite budget, and on timeout cancels the jobs and stops/pauses the world if lease release cannot be proven. Cancellation alone does not revoke an in-flight memory reference; the barrier MUST NOT proceed until release is confirmed. A lease cannot be retained by a job scheduled across barriers.
- Long-lived work copies a **bounded immutable subset** and records the source world, epoch, targeted entity/component and expected version. At commit, the writer validates identity and version again. An expired or changed target rejects the result; no implicit rebase.
- **Review choice:** Select concrete public types, numeric widths, allocator and lifetime enforcement technique. Do not expose an unsafe ref that can outlive the lease.

## 2. Job descriptors, ordering and atomic commit

- Before dispatch, each job submits an immutable descriptor containing a unique job identity, scheduled tick/phase/system, world and partition scope, exact read resource keys, intended command-target keys, dependency IDs, cancellation scope and an upper bound on result size.
- Resource keys must distinguish world-wide resources, component type and entity/partition scope. Wildcard keys conflict with narrower keys they cover. An undeclared read or intended command target is invalid; the scheduler cannot silently broaden permissions.
- Dependency IDs form an acyclic graph. Validate unknown IDs and cycles before dispatch. Jobs with independent read-only or disjoint scopes may execute concurrently. Queues and worker counts remain bounded.
- Workers only publish private result buffers. Each result is a **batch** with source identity, ordered sequence, expected versions and commands. Commands cannot be applied incrementally before validation.
- The writer validates each complete batch for permissions, world identity, generations, target versions, dependency success and conflict rules before applying it. Conflicting unordered writes to the same entity/component (or overlapping structural targets) reject all implicated batches atomically, leaving their target state unchanged. Conflict detection MUST run across the entire barrier candidate set before applying any batch; a sequential first-writer commit cannot be used to discover later conflicts. Unrelated validated batches may commit only if they do not depend on rejected work.
- Commit order is independent of job completion order: sort by stable `(tick, phase ordinal, system ordinal, partition ordinal, source sequence)` plus a unique deterministic tie-breaker. Duplicate keys are a validation error, not an implicit race winner. Declare phase ordinals at composition time.
- **Review choice:** Freeze resource-key representation, batch grouping and exact structural-conflict overlap rules. Confirm preflight is complete enough to guarantee no partial commit, and use a staging/transactional strategy where commit operations could otherwise throw after partial mutation. If atomicity cannot be guaranteed, stop the world and report it unsafe rather than continuing.

## 3. Barriers and tick visibility

Proposed base tick phases, each with explicitly named boundaries:

1. Drain bounded host input into the next tick's immutable input snapshot.
2. Apply previously accepted structural commands at `BeginTick`.
3. Execute eligible 20-Hz gameplay systems in declared order; base simulation systems execute every 60-Hz tick.
4. Finish/cancel jobs using borrowed leases before `CommitResults`; collect eligible completed result batches, validate, resolve conflicts and commit using the deterministic ordering contract.
5. Publish previous/current immutable observer snapshots at `PublishState`. Rendering may interpolate these snapshots but cannot mutate the world.

No command produced after a barrier is visible retroactively in that barrier. Async snapshot results can only commit at a future eligible `CommitResults` barrier after version validation. The L2/L3A/L3B review must decide whether systems within a tick require additional phase-specific barriers; any such barrier must be named and documented before implementation. The existing 60/20-Hz, five-step catch-up and 250-ms debt cap remain unchanged.

## 4. Fault isolation and supervisor protocol

- Every failure report includes fault class, severity, tick/phase, world, job/source, affected targets, correlation ID and primary error; cleanup errors are separate. At minimum distinguish `Cancelled`, `DependencyFailed`, `InvalidAccess`, `StaleResult`, `WriteConflict`, `RecoverableJobFault`, `UnsafePartialCommit` and `UnknownCritical`.
- A failed, cancelled or invalid job publishes no commands; dependent jobs are cancelled/failed explicitly. Unrelated work may finish but only commit into a coherent world.
- The supervisor decides among `ContinueAtSafeBarrier`, `PauseForRecovery` and `StopWorld` semantics. Continue requires proof of a coherent world, no unresolved required dependencies and no partial writes. Unknown critical or unsafe partial commit forbids continuation; pause/stop is mandatory.
- Supervisor decisions are serialized at a safe barrier, recorded with correlation and never made implicitly by a worker. Timeouts/cancellation of the supervisor path fail safe to pause/stop. No automatic retry, priority overwrite or last-writer-wins.
- **Review choice:** Freeze the supervisor interface and which fault classes are eligible for continuation; confirm bounded shutdown and diagnostic backpressure behavior.

## 5. Verification and acceptance gate

The proposed signatures are intentionally **not** represented as compiled C# APIs. Before Issue #1 can close, C00 must record L2/L3A/L3B/L5 and integration-owner review evidence, approved revision, and any explicitly gated deferrals. Tests belong to implementation lanes, not this documentation PR.

Required future tests: stale/cross-world IDs, generation exhaustion, lease invalidation, late versioned results, undeclared accesses, DAG cycles, deterministic ordering under randomized completion, conflict rejection without partial mutation, supervisor continue/pause/stop, cancellation and shutdown, and stable 60/20-Hz phase visibility.

**No sign-off, executable implementation or passing test is asserted by this document.**

## 6. Technical review findings (2026-11-03)

This review compares the proposal against the merged WP01 runtime, ownership and ADR-0002 through ADR-0005 policies. It is a **document-level technical review**, not a substitute for independent owner sign-off or executable verification.

- **Resolved in this revision — lease safety:** cancellation does not itself invalidate a borrowed pointer. Barrier advancement now requires proven lease release; timeout is a world safety fault, not permission to proceed.
- **Resolved in this revision — cross-batch conflict detection:** evaluate the full candidate set before committing, so deterministic order cannot silently turn a late conflicting writer into a stale-version rejection after an earlier write was applied.
- **Resolved in this revision — atomicity:** prevalidation alone does not guarantee atomic commit if application can fail mid-batch; implementation must use an infallible post-validation commit path or transactional staging, otherwise stop the world.
- **Still blocking contract freeze:** exact resource-key overlap semantics (especially wildcard, structural destroy and partition overlap), deterministic unique ordering key, lease/version token types, supervisor decision interface and the intra-tick visibility/barrier matrix require joint L2/L3A/L3B/L5 owner sign-off. These are not proven by this document.
- **Evidence absent:** no implementation, compile check, automated tests, independent owner approvals or cross-repository consumer validation has been established by this review. Do not mark Issue #1 accepted until its review gates are satisfied.
