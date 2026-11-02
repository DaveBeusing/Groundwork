# ADR-0002 — Runtime lifecycle and thread ownership

**Status:** Proposed for architecture review (WP01-C00)  
**Decision owner:** C00 / L1 / L2 / L3A / L3B  
**Date:** 2026-11-02  
**Trace:** [Groundwork #1](https://github.com/DaveBeusing/Groundwork/issues/1)

## Context

Windows window handling, simulation state and asynchronous jobs have incompatible ownership requirements. A common lifecycle and safe shutdown policy are needed before independently developed modules are combined.

## Decision

- Define module lifecycle semantics **initialize → start → stop → dispose**, with cancellation and async cleanup; the names `InitializeAsync`, `StartAsync`, `StopAsync`, `DisposeAsync` are API **proposals**, not implemented signatures.
- Initialize/start in topological module dependency order; stop/dispose in reverse order. Stop/dispose must be safe after partial startup or repeated requests; invalid starts/restarts produce explicit errors.
- Preserve the original startup failure while separately reporting cleanup failures. No production service may silently resolve to a no-op fallback; capability validation must fail clearly.
- The Windows message pump and native window live on the host UI thread; it must not perform synchronous disk/network operations or be blocked by worker completion. Host shutdown and focus/resize input remain responsive.
- ECS mutations are applied by **one logical world writer** at documented barriers; this need not be one permanently pinned operating-system thread. Workers read immutable views/snapshots and write their own result buffers. Rendering consumers never mutate the live world.
- Shutdown signals cancellation; bounded draining/cancellation completes before resource disposal. Deadlocking waits across the simulation barrier/UI thread are forbidden.

## Alternatives / consequences

Rejected: unmanaged mutable world writes from any job, blocking message pump, forceful process exit on normal stop, and anonymous module teardown order. The cost is explicit synchronization and composition-root wiring; the benefit is testable ownership and recovery.

## Review and verification

Owners must finalize interface signatures, finite timeout policy, invalid-state transitions and partial-initialization cleanup. Tests must cover startup faults, shutdown during queued work, repeated stop/dispose, cancellation and window close/focus behavior. See [runtime-contracts](../architecture/runtime-contracts.md).