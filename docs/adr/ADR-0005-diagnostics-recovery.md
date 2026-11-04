# ADR-0005 — Diagnostics and supervisor-controlled fault isolation

**Status:** Proposed ADR; supervised error-isolation policy **user-approved** on 2026-11-02  
**Owners:** C00, L2, L3A, L3B and L5  
**Trace:** [Groundwork #1](https://github.com/DaveBeusing/Groundwork/issues/1), [#8](https://github.com/DaveBeusing/Groundwork/issues/8)

## Context

Aborting the entire game for each recoverable worker error sacrifices availability; silently continuing with partly committed mutations corrupts state. Error paths must be observable and consistency-preserving.

## Decision

- **User-approved option B: controlled failure isolation and supervisor decision.** A faulted, cancelled or invalid-dependency job MUST NOT publish command results. Downstream dependent jobs are skipped/cancelled with an observable status. Other unrelated work may finish but is only published if the world remains coherent.
- The single writer MUST validate each *entire atomic command batch* before application, checking identity, expected versions, permission declarations, dependency validity and write conflicts. Rejected stale/incompatible batches cause **no partial mutations** and emit structured errors including affected targets and sources.
- A simulation supervisor receives the fault and chooses the **semantic** outcome `ContinueAtSafeBarrier`, `PauseForRecovery` or `StopWorld`. These are illustrative names, **not existing types**. Workers supply error reports, **never authoritative coherence evidence**. Only the logical world writer (or approved writer-owned barrier verifier) issues an immutable world/barrier/revision-bound coherence proof after verifying the committed state; the supervisor must check its trusted issuer and current revision before continuation. A failure with an unverifiable partial commit MUST choose `StopWorld`; no stale/forged worker-provided proof authorizes continuation.
- No implicit retries, command rebasing, priority overrides or exception swallowing. Any future retry must be separately approved, bounded, version-checked, cancellation-aware and demonstrably idempotent. Preserve primary errors; report cleanup failures separately.
- Error events expose severity, code, subsystem, tick/time, job/phase/source/target, correlation ID and redacted context. Timings/counters include dropped elapsed time, backlog, job queue/dependency cancellations, rejected batches, recoverable pauses and fatal stops. Logging must not block simulation hot paths; bounded buffers and overflow/sink faults are observable; no secrets in logs.
- Shutdown and fault propagation have finite cancellation/drain semantics; no barrier deadlocks.

## Alternatives / consequences

Rejected: total stop for every worker failure, last-writer-wins, indiscriminate automatic retries and ignoring partial state faults. Supervised isolation permits safe recovery when validated, but requires explicit supervisor classifications, observability and atomicity proof.

## Review gate / tests

L3A specifies atomic preflight and commit units; L3B implements dependency cancellation; L2 implements supervisor pause/stop integration; L5 owns error-code/metric schemas. Test recoverable isolated error, fatal consistency failure, fault and cancellation without partial commit, stable independent work, shutdown under fault and original/cleanup error reporting. No running diagnostics or recovery engine is claimed by this ADR.

### 2026-11-04 fault-path corrections (proposed)

- **Commit outcome taxonomy:** distinguish `ExecutionCompleted`, `CommandAccepted`, `CommitSucceeded`, `CommitRejected`, and `CommitNotApplicable`; a deferred mixed/structural batch is not successfully committed merely because its worker finished or its command buffer entered a queue. On upstream commit rejection, transitively reject/cancel uncommitted commit-dependent jobs, even across ticks. Surface failing upstream and dependent batch IDs in structured diagnostics.
- **Safe continuation:** `WorldCoherenceProof(WorldId, BarrierEpoch, CommittedRevision, BarrierId, ValidationResult)` must come from a trusted writer-owned channel, not a worker's `FaultReport`; a correlation ID is not an authenticator. Stale/wrong-world/mismatched evidence is rejected. Unknown critical or unsafe partial commit fails closed to stop unless a distinct reviewed safe-recovery proof exists where permitted; `UnsafePartialCommit` always stops.
- **Bounded shutdown:** inability to drain an active borrowed lease results in terminal quarantine, retained referenced storage, and an observable incomplete-cleanup fault. Do not free storage the worker might access, and do not report successful disposal. Original faults and cleanup failures remain separately visible.

See [ECS/job contract §11](../architecture/ecs-jobs-review-contract.md) for detailed tests and semantics. This ADR records a proposed contract correction, **not** implemented supervisor behavior or an independent approval.
