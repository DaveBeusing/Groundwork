# ADR-0002 — Runtime lifecycle and thread ownership

**Status:** Proposed for architecture review (WP01-C00)  
**Decision owner:** C00 / L1 / L2 / L3A / L3B  
**Date:** 2026-11-02  
**Trace:** [Groundwork #1](https://github.com/DaveBeusing/Groundwork/issues/1)

WP01 implementation and Windows lifecycle evidence are recorded in the
[foundation handover](../wp01-handover.md).

## Context

Windows window handling, simulation state and asynchronous jobs have incompatible ownership requirements. A common lifecycle and safe shutdown policy are needed before independently developed modules are combined.

## Decision

- Define module lifecycle semantics **initialize → start → stop → dispose**, with cancellation and async cleanup; the names `InitializeAsync`, `StartAsync`, `StopAsync`, `DisposeAsync` are API **proposals**, not implemented signatures.
- Initialize/start in topological module dependency order; stop/dispose in reverse order. Stop/dispose must be safe after partial startup or repeated requests; invalid starts/restarts produce explicit errors.
- Preserve the original startup failure while separately reporting cleanup failures. No production service may silently resolve to a no-op fallback; capability validation must fail clearly.
- The Windows message pump and native window live on the host UI thread; it must not perform synchronous disk/network operations or be blocked by worker completion. Host shutdown and focus/resize input remain responsive.
- ECS mutations are applied by **one logical world writer** at documented barriers; this need not be one permanently pinned operating-system thread. Workers read immutable views/snapshots and write their own result buffers. Rendering consumers never mutate the live world.
- Ordinary ECS barriers close borrowed-lease admission and drain only borrowed/live-resource readers; independently owned snapshot-only jobs may span barriers without barrier-triggered cancellation or waiting, with later version/dependency validation. Shutdown closes general admission/publication, signals cancellation of all owned work (including snapshot jobs) and uses bounded drain, **but cancellation alone does not prove borrowed readers released memory**. If the deadline expires with a live lease, enter terminal quarantine, stop new world mutations, retain strong ownership of referenced buffers and return an explicit incomplete-cleanup fault; do not free those buffers or report a successful `DisposeAsync`. Reclamation is permitted only after proven quiescence or safe process termination. Deadlocking waits across the simulation barrier/UI thread are forbidden. Snapshot/result buffers still reachable by an uncooperative worker also remain strongly owned by a process custodian with incomplete-cleanup reporting and no late publication; unrelated storage may be reclaimed after its own quiescence is proved. Repeated stop/dispose is idempotent and cannot double-free.

## Alternatives / consequences

Rejected: unmanaged mutable world writes from any job, blocking message pump, forceful process exit on normal stop, and anonymous module teardown order. The cost is explicit synchronization and composition-root wiring; the benefit is testable ownership and recovery.

## Review and verification

Owners must finalize interface signatures, finite timeout policy, invalid-state transitions and partial-initialization cleanup. Tests must cover startup faults, shutdown during queued work, repeated stop/dispose, cancellation and window close/focus behavior. See [runtime-contracts](../architecture/runtime-contracts.md).

### 2026-11-04 review correction (proposed)

The lifecycle promise to dispose all resources on failed startup is **conditional on safe quiescence**. On a permanently non-cooperative borrower, the host finishes its bounded wait with an observable incomplete-shutdown result and the process-owned quarantine custodian retains any referenced managed/native storage. This makes cleanup truthfully incomplete rather than unsafe; the primary fault and cleanup fault must be reported separately. See [ECS/job contract §11.2](../architecture/ecs-jobs-review-contract.md) for the proposed implementation contract and fault-injection tests. No resource cleanup has been executed by this documentation PR.
