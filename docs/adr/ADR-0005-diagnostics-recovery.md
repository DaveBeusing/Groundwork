# ADR-0005 — Diagnostics and supervisor-controlled fault isolation

**Status:** Proposed ADR; supervised error-isolation policy **user-approved** on 2026-11-02  
**Owners:** C00, L2, L3A, L3B and L5  
**Trace:** [Groundwork #1](https://github.com/DaveBeusing/Groundwork/issues/1), [#8](https://github.com/DaveBeusing/Groundwork/issues/8)

## Context

Aborting the entire game for each recoverable worker error sacrifices availability; silently continuing with partly committed mutations corrupts state. Error paths must be observable and consistency-preserving.

## Decision

- **User-approved option B: controlled failure isolation and supervisor decision.** A faulted, cancelled or invalid-dependency job MUST NOT publish command results. Downstream dependent jobs are skipped/cancelled with an observable status. Other unrelated work may finish but is only published if the world remains coherent.
- The single writer MUST validate each *entire atomic command batch* before application, checking identity, expected versions, permission declarations, dependency validity and write conflicts. Rejected stale/incompatible batches cause **no partial mutations** and emit structured errors including affected targets and sources.
- A simulation supervisor receives the fault and chooses the **semantic** outcome `ContinueAtSafeBarrier`, `PauseForRecovery` or `StopWorld`. These are illustrative names, **not existing types**. Continue is allowed only at a verified consistent barrier with required dependencies intact. A critical/unknown failure or unprovably recoverable partial commit MUST pause or stop simulation.
- No implicit retries, command rebasing, priority overrides or exception swallowing. Any future retry must be separately approved, bounded, version-checked, cancellation-aware and demonstrably idempotent. Preserve primary errors; report cleanup failures separately.
- Error events expose severity, code, subsystem, tick/time, job/phase/source/target, correlation ID and redacted context. Timings/counters include dropped elapsed time, backlog, job queue/dependency cancellations, rejected batches, recoverable pauses and fatal stops. Logging must not block simulation hot paths; bounded buffers and overflow/sink faults are observable; no secrets in logs.
- Shutdown and fault propagation have finite cancellation/drain semantics; no barrier deadlocks.

## Alternatives / consequences

Rejected: total stop for every worker failure, last-writer-wins, indiscriminate automatic retries and ignoring partial state faults. Supervised isolation permits safe recovery when validated, but requires explicit supervisor classifications, observability and atomicity proof.

## Review gate / tests

L3A specifies atomic preflight and commit units; L3B implements dependency cancellation; L2 implements supervisor pause/stop integration; L5 owns error-code/metric schemas. Test recoverable isolated error, fatal consistency failure, fault and cancellation without partial commit, stable independent work, shutdown under fault and original/cleanup error reporting. No running diagnostics or recovery engine is claimed by this ADR.