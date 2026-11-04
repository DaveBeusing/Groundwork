# WP01 runtime, ECS and job contracts

**Status:** Architecture review proposal; **not implemented**. The approved product policies below are distinct from illustrative API choices awaiting C00/L3A/L3B sign-off. Source: [WP01-C00](https://github.com/DaveBeusing/Groundwork/issues/1).  
**Boundaries:** Groundwork is game-agnostic; WYU is the composition root and consumes immutable engine packages.

## Lifecycle and thread ownership

- Proposed semantic operations: `InitializeAsync(context, ct)` validates resources (no ticks); `StartAsync(ct)` starts; `StopAsync(ct)` requests finite drain/cancellation; `DisposeAsync()` releases resources when quiescence is proved; an outstanding borrowed lease requires a terminal quarantine with explicit incomplete-cleanup reporting instead of unsafe release or a false successful disposal. **Names/signatures are proposals, not compiled API.**
- Start/initialize modules in topological dependency order; stop/dispose in reverse order. Stop/dispose MUST be safe when repeated. Invalid transitions (including unapproved restart) MUST report an error.
- On failed start, preserve the primary exception; stop already-started modules, dispose initialized modules, and report cleanup failures separately. Never use `Environment.Exit` for ordinary shutdown. Runtime shutdown uses cancellation and a finite host policy.
- The Windows message pump and native window lifetime belong to the UI host thread; input crosses via a bounded, owned queue/snapshot transfer. Focus loss releases current input state; close requests propagate shutdown. No busy loop or synchronous file/network work on the pump.
- A single **logical** world writer owns all ECS mutations at named commit barriers, without requiring pinning the writer to a fixed OS thread. Worker jobs compute concurrently from immutable inputs into privately owned result buffers. Renderer-facing consumers get published immutable snapshots and never mutate the live world.

## Timing and tick ordering

- Time deltas come from a monotonic `ITimeSource` or equivalent with injected-clock test seam; UTC wall time is only for timestamps. Fixed simulation base clock: **60 Hz**, gameplay/production default: **20 Hz** (every third base step), both configurable.
- Tick ordering: accept buffered input → apply previously deferred **whole structural/mixed batches** at `BeginTick` → eligible gameplay systems → base-tick systems (both read the same committed tick-start ECS state) → validate/publish pure component batches at `CommitResults`, but queue whole structural/mixed batches for the **next** `BeginTick` → publish immutable observer states. Neither system phase can mutate live ECS data. A batch is never partially published across barriers. This ordering is a **proposal** requiring L2/L3A/L3B integration review for exact intra-tick visibility.
- Maintain integer ticks or drift-safe equivalent. Rendering interpolation factor is in `[0,1)`; rendering MAY be deprioritized during catch-up but MUST NOT advance simulation.
- **Approved overload policy:** fixed 1/60-second steps, at most **5 steps per scheduler iteration**, initial **250 ms maximum pending simulated backlog**. Short stalls use controlled catch-up; on sustained overload cap debt and discard only excess **wall-clock elapsed time**, so game time can temporarily slow. NEVER skip required physics steps or replace fixed dt with variable dt. Expose applied steps, backlog, capped elapsed time and overload episodes. Remain responsive to host events/cancellation. Pause freezes advancement; resume resets clock sampling to avoid huge catch-up.

## Entity identity: approved option B

- `EntityId` semantics MUST combine **world ID, slot and generation** (e.g. conceptual `EntityId(WorldId, Slot, Generation)`; illustrative, not a frozen C# signature).
- Destroyed, stale, invalid and cross-world IDs MUST NOT resolve to a different entity. Slot reuse requires a previously unused incremented generation. Generation MUST NOT wrap: once exhausted, permanently **retire that slot**. At global capacity exhaustion, creation fails explicitly. A disposed world identity MUST NOT be reused while old references might remain observable.
- Structural entity/component mutations during queries are staged through ordered command buffers, never applied silently while invalidating an iterator. Component data is game-agnostic; missing component vs stale entity must be distinguishable. Query order is unspecified except when stable-order mode is explicitly requested.
- World identifier allocator, numeric widths and slot-retirement data structure remain L3A review items; no unqualified guarantee of deterministic cross-platform floating-point physics.

## Read views, snapshots and barriers

- Borrowed immutable `ReadView<T>` semantics are bound to a world and **BarrierEpoch**, which advances at **every** declared ECS synchronization/commit barrier (including no-op barriers and in-place component commits); views expire at the next barrier even without storage movement. Stop new lease admission and prove all borrowers released before advancing. On bounded drain timeout, stop the world and quarantine potentially referenced storage; cancellation is not proof of release. Never reclaim memory reachable by a still-live lease.
- Jobs spanning a barrier MUST hold their own **bounded, immutable subset snapshot**, not a deep-copy of the entire world. Snapshots carry world/epoch, entity/component target and expected version (or equivalent conflict token).
- At a later eligible barrier, the single writer MUST reject outdated, foreign-world, deleted, cancelled or version-mismatched job results with structured diagnostics. No implicit rebase, retry or last-writer-wins.
- L3A/L3B must review concrete lease/epoch enforcement and target-version token representation before code implementation.

## Parallel jobs: approved explicit-access option A

- Each job descriptor MUST declare **before scheduling** the world/partition and component/resource types it reads and its private outputs and intended command targets; declarations become immutable when submitted. Undeclared resource needs are a validation error, not a request for unrestricted access.
- The scheduler validates a bounded dependency **DAG**, rejecting cycles before dispatch. Independent read-only/disjoint jobs MAY run concurrently; outstanding work and worker count are bounded/configurable. Queue overload is observable with explicit rejection/backpressure outside hot threads.
- Workers MUST NOT receive writable live ECS storage, Win32 window handles or shared mutation buffers. Only the single writer applies commands at documented synchronization barriers. Distinguish worker `ExecutionCompleted` from `CommandAccepted` and actual `CommitSucceeded/Rejected` outcomes. An ECS-state-dependent consumer cannot run/publish until the upstream batch **actually commits**, even if a mixed structural batch is queued until the next tick. Only commandless, execution-only dependencies can be satisfied by worker completion. Rejected upstream commits invalidate transitive dependent work; faults and cancellations are never reported as success.
- Deterministic scheduling/commit admission is the **WP01 default**: derive a stable canonical per-world `(tick, phase, system, partition, source/spawn sequence)` key from tick-owner enumeration, reject ambiguous dynamic ordering, and never use worker completion/arrival time or `JobId` allocation order as a tie-breaker. This guarantee applies to equal eligible batch sets at each barrier; asynchronous eligibility may vary by tick and cross-platform floating-point determinism is not promised.
- If unordered sources issue incompatible writes to the same entity/component at one barrier, the writer MUST detect this and atomically reject the affected conflicting **batch**, report targets and both sources, and notify the supervisor. No implicit priority or partial commit. L3A/L3B must define exactly what constitutes a batch and conflict domain, and which unrelated batches may commit.

## Fault handling: approved option B, supervised isolation

- Faulted/cancelled/invalid-dependency jobs publish **no** result commands. The writer prevalidates each atomic batch (identity, versions, access declarations, dependencies, conflict rules) before committing. If no atomic rollback/consistency guarantee is possible after a partial failure, the world is unsafe and the simulation MUST stop rather than continue.
- The supervisor receives a structured failure with job/system/phase/tick, target, sources, classification, correlation and primary exception. Worker fault reports cannot certify world consistency; only the logical writer (or approved trusted barrier verifier) may issue current `WorldCoherenceProof` bound to world, barrier epoch and committed revision, which the supervisor must verify before continuation. It may decide `ContinueAtSafeBarrier`, `PauseForRecovery` or `StopWorld`. These names denote **decision semantics, not existing enum members**.
- Continuation is allowed **only** after a verified coherent world state, no unresolved required dependency, and no unsafe partial commit. Unknown, critical or irrecoverable consistency faults require pause/stop; dependent jobs are cancelled, unrelated work may complete only if the committed world stays coherent. No suppressed exceptions.
- Retries MUST be **explicitly approved**, bounded, cancellation-aware, idempotent and version-checked; no automatic retries or implicit write priorities.
- Shutdown remains bounded and deadlock-free; original and cleanup errors are separately observable. If a worker retains a borrowed lease after the deadline, report incomplete cleanup, enter a terminal non-mutating quarantined world state and retain strong ownership of referenced buffers until release or process exit. No false successful `DisposeAsync`, no freeing buffers while still reachable. Error reporting MUST NOT block simulation hot paths.

## Input, diagnostics and acceptance

- Platform-neutral input event: type, source category, monotonically ordered sequence and timestamp; no Win32 types crossing `Core`. Focus loss produces neutral/released state; resize/minimize are host lifecycle events.
- Diagnostics include severity/code, subsystem, tick/monotonic time where relevant, optional UTC, correlation/job/entity and redacted context. Log buffers are bounded; sink failure and overflow are observable and non-blocking on hot threads. Never log secrets.
- Record benchmarks with commit, SDK/runtime, OS/CPU, configuration, scenario, sample count, latency, allocations, queue and worker utilization. Do not assume option A is faster without measurement.

### Required implementation tests (not yet executed)

- Synthetic clock: exactly 60 base/20 gameplay steps per 1 simulated second; jitter, stall, 5-step/250-ms limits, pause/resume and event responsiveness.
- IDs: cross-world, destroy/recreate, generation exhaustion/retirement, world identity reuse safety and capacity failure.
- Borrowed-view invalidation and barrier safety; owned async snapshot version mismatch/late cancellation.
- DAG cycle, queue overflow, concurrent independent reads, missing access declarations, cancellation/fault propagation and bounded shutdown, including permanently uncooperative leased readers and quarantine outcomes.
- Randomized worker completion with stable canonical admission/commit ordering; strict conflicting-batch rejection and unchanged affected state; transitive commit-gated dependencies across ticks (including deferred batch rejection), worker-forged/stale coherence proof rejection, safe continuation and forced stop on unverifiable partial commit.
- Real Windows open/resize/focus/close smoke test; non-blocking diagnostics and meaningful failure metrics.

**Review gate:** Precise public API signatures, resource-key schema, atomic batch scope, tick phase visibility, supervisor classification, lease/version tokens and test evidence require owner review before coding; documentation is not a sign-off. See [ADR-0002](../adr/ADR-0002-lifecycle-threading.md), [ADR-0003](../adr/ADR-0003-fixed-step-scheduling.md), [ADR-0004](../adr/ADR-0004-ecs-jobs.md), and [ADR-0005](../adr/ADR-0005-diagnostics-recovery.md).

**Contract detail:** See [ECS/job review contract §11](ecs-jobs-review-contract.md) for precise proposed fixes to P1-01/P1-02/P1-03 and P2-04/P2-05. Its detailed semantics supersede abbreviated examples here; independent specialist/integration reviews and owner acceptance are still pending.
