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
2. Apply previously accepted **pure structural and mixed structural/component batches atomically** at `BeginTick`.
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
- **Drafted but not approved:** exact resource-key overlap semantics, deterministic job ordering, lease/version tokens, supervisor decision interface and intra-tick visibility are proposed in section 7. Independent L2/L3A/L3B/L5 and C00 reviews remain required. Drafting is not evidence of formal approval.
- **Evidence absent:** no implementation, compile check, automated tests, independent owner approvals or cross-repository consumer validation has been established by this review. Do not mark Issue #1 accepted until its review gates are satisfied.

## 7. Concrete cross-lane interface proposal (review required)

These definitions are normative **proposals** for L2/L3A/L3B/L5 review, not accepted APIs. Numeric widths and C# signatures below are conceptual; no executable contract or approval is implied.

### 7.1 Resource keys and conflict domains

A canonical resource key contains `WorldId`, `ComponentTypeId` (or the reserved structural/world resource), `ScopeKind` and `ScopeId`. Scope kinds are `World`, `Partition` and `Entity`. A key is valid only if its entity/partition belongs to the declared world and its component type is registered. Normalize and deduplicate keys before dispatch; invalid or ambiguous partitions are rejected, never assumed disjoint.

Two accesses overlap if their world IDs match, their component domains match (including world/structural wildcard coverage), and their scopes intersect. A world scope intersects all scopes in that world. Partition scopes intersect according to a stable, versioned partition membership map; an entity intersects its containing partition. When partition membership cannot be proven for the scheduled epoch, conservatively treat the accesses as overlapping or reject the descriptor. Read/read overlap is permitted; any read/write overlap creates a scheduling dependency or exclusion. Worker writes are to private buffers only; declared command targets are validated again at commit.

`DestroyEntity(e)` conflicts with every pending component command or structural command targeting `e` in the same barrier candidate set. Borrowed reads are execution-time accesses, not candidate command batches: all such leases must be released before structural mutation; completed reads are not retroactively rejected. `CreateEntity` allocates IDs only at the world-writer barrier and cannot be referenced by an unrelated batch before publication. `AddComponent`/`RemoveComponent` conflict with other pending **commands** targeting the same entity/component and with overlapping structural operations. Borrowed reads are scheduled and drained before a mutation barrier; already completed reads are not rejected as command conflicts. A live borrowed lease blocks the barrier rather than becoming an implicit write conflict. Explicitly ordered dependencies permit scheduler execution ordering but do not authorize two overlapping command batches to mutate the same target at one barrier. Such batches must use separate named commit barriers with version validation between them; overlapping batches in the same candidate set are **all rejected** before any candidate is applied, even if their jobs have a dependency edge. No implicit last-writer-wins.

### 7.2 Stable job and batch identity

At schedule admission, the owning simulation scheduler allocates a monotonic per-world `SourceOrdinal` for the declared tick and phase. A job descriptor contains `(WorldId, TickId, PhaseOrdinal, SystemOrdinal, PartitionOrdinal, SourceOrdinal, JobId)`; these values are immutable, and duplicate full identities are invalid. `SourceOrdinal` is allocated from deterministic system/partition enumeration, **not** thread arrival order; otherwise the scheduler must reject deterministic-mode admission. Each source emits increasing `BatchSequence` and `CommandSequence` values with bounded sizes.

Sort validated batches by `(TickId, PhaseOrdinal, SystemOrdinal, PartitionOrdinal, SourceOrdinal, BatchSequence)`, then commands by `CommandSequence`. Any duplicate ordering tuple is an error, not a tie resolved by completion timing. Check dependency DAG, declared permissions, identities, versions and the **entire** barrier candidate-set conflict graph before committing. A dependency edge controls execution order, not same-barrier visibility or write priority; any same-barrier overlapping writes are rejected. Version checks use the committed state at barrier entry, with atomic staging of nonconflicting batches. Reject conflicting batches and recursively reject dependent batches; stage all accepted changes against a coherent candidate state and publish atomically. A failed stage leaves the prior state intact. If atomic publication cannot be established, classify it `UnsafePartialCommit`, require `StopWorld`, prohibit further writes, and retain protected memory until outstanding read leases are confirmed released. Never report a successful rollback without evidence.

### 7.3 Lease, epoch and version tokens

`WorldId` is an opaque non-reused identity; `WorldEpoch` increases at each storage-invalidating barrier without wraparound; `ComponentVersion` is a per-target opaque monotonic token, never silently reused. A borrowed `ReadLease<T>` is an explicitly disposable, non-copyable logical capability bound to world, epoch and a declared read scope. The implementation tracks active leases and ensures no mutable storage is reclaimed or changed while any lease may still dereference it. A cancelled worker must acknowledge completion and release before the barrier advances; a timeout causes a fail-safe world pause/stop, not lease invalidation by fiat. Do not expose an escaping `ref`/`Span` whose lifetime cannot be enforced.

Cross-barrier jobs use independently owned immutable, bounded snapshots. Result batches carry source world/epoch, target entity identity, expected component versions and ownership metadata. The single writer rejects missing, stale, foreign-world or version-mismatched targets before applying any part of a batch. Token widths, allocator, lease enforcement and memory reclamation are L3A/L3B review items; overflow must fail explicitly rather than alias.

### 7.4 Tick visibility matrix

| Boundary | Input and state visible | Mutations allowed | Visibility guarantee |
| --- | --- | --- | --- |
| `AcquireInput` | Previous published world; bounded next-tick host input | Freeze immutable input snapshot | Input becomes available to this tick only |
| `BeginTick` | Input snapshot and previous committed state | Single writer applies previously accepted structural commands | All subsequent systems see completed structural changes |
| `Gameplay20` (eligible ticks) | State after `BeginTick` | Systems compute; commands/results are buffered | No buffered job result is visible to another system yet |
| `Base60` | State after `BeginTick`; no uncommitted gameplay or job changes | Systems calculate into private buffers; no owner-local ECS mutations during system execution | No computed result becomes visible before `CommitResults` |
| `CommitResults` | Whole eligible candidate set | Commit **pure nonstructural** batches atomically; prevalidate and enqueue **entire** structural or mixed batches for next `BeginTick` | Pure component results visible after commit; all changes from structural/mixed batches remain invisible until next `BeginTick` |
| `PublishState` | Final committed state of this tick | Publish immutable previous/current observer snapshots | Renderer reads only published snapshots |

There is **no implicit additional barrier** between `Gameplay20` and `Base60`, and neither phase mutates live ECS state. All systems in those phases observe the same committed tick-start state; they may exchange immutable non-ECS values only through explicitly declared dependencies. A system requiring ECS results from another system must execute in a later tick or after a separately reviewed named barrier. If same-tick gameplay-to-base visibility is required, L2 must introduce a named intermediate commit barrier and update dependency/lease rules first. Structural changes produced this tick are applied at the next `BeginTick`. **A mixed structural/component batch is never split across publication barriers:** after conflict analysis, enqueue the whole validated batch; publish neither its component changes nor its structural changes at the current `CommitResults`. At the next `BeginTick`, revalidate all identities, versions, dependencies and conflicts against the then-committed state, apply the entire batch against isolated/staged state, and publish atomically or reject the **entire** batch without partial mutation. A pure structural batch follows the same delayed rule. If a command in a deferred batch requires an entity created by that batch, use batch-local provisional IDs resolved solely by the world writer at commit; never expose those IDs to other batches before successful publication. A batch requiring immediate structural visibility is invalid under this contract. Async results are eligible only at a future `CommitResults` after version checks. The established 60/20 Hz cadence, five-step catch-up limit and 250 ms debt cap remain unchanged.

### 7.5 Supervisor protocol and diagnostics

Proposed request: `FaultReport(FaultClass, Severity, WorldId, TickId, PhaseOrdinal, JobId?, AffectedTargets, CorrelationId, PrimaryError, CleanupErrors, WorldCoherenceEvidence)`. Proposed response: `SupervisorDecision(ContinueAtSafeBarrier | PauseForRecovery | StopWorld, CorrelationId, Reason)`. L5 owns bounded, nonblocking diagnostic publication; critical fault evidence must have a guaranteed fallback channel if the regular queue is full. Workers report faults but never decide world continuation.

`Cancelled`, `DependencyFailed`, `StaleResult` and `WriteConflict` may continue only after rejected buffers/dependents are isolated and the single writer proves world coherence. `InvalidAccess` and `RecoverableJobFault` default to pause pending explicit supervisor classification. `UnsafePartialCommit` requires `StopWorld` and prohibits any further mutation; `UnknownCritical` also defaults to `StopWorld` unless coherence has been independently established and a reviewed recovery policy explicitly permits a safe pause. A supervisor timeout, diagnostic failure that loses required safety evidence, or incomplete lease drain fails closed. Shutdown cancels admission, drains/releases bounded work, reports unreleased leases and never waits indefinitely while claiming successful cleanup.

### 7.6 Required review evidence

L3A: approve identity, lease, version and structural conflict semantics. L3B: approve key overlap, DAG, deterministic admission and batch validation. L2: approve the phase visibility matrix, barriers and bounded stop behavior. L5: approve fault payload, backpressure and supervisor decision path. C00/integration: verify public boundary consistency and record the approved revision and owner sign-offs in Issue #1. These approvals are **pending**, not represented by this proposal. Implementation tests remain required in their delivery lanes.

## 8. Consistency resolution and review status (2026-11-03)

The six issues from the cross-document consistency review are addressed as **proposed semantics**, not independently approved implementation contracts: (1) Gameplay20/Base60 have no implicit same-tick ECS writes; (2) mixed structural/component batches are deferred **as a whole** until the next BeginTick, preserving atomic publication and version validation; (3) borrowed reads are drained, not treated as command conflicts; (4) job dependency ordering never overrides same-barrier conflicting-write rejection; (5) unverifiable partial commit mandates StopWorld; (6) all API details remain proposed until affected owners sign off. These clarifications preserve the approved single-writer, deterministic rejection and supervised fault isolation policies. No compiled implementation, tests or independent reviews are asserted.


## 9. Review evidence and handover state (2026-11-04)

- **Product-owner architecture consent:** The project owner confirmed the chosen architectural direction in the project discussion. This is not an independent GitHub code review or an approval of as-yet-uncompiled C# signatures.
- **Design-level checks:** The six inconsistencies noted in the 2026-11-03 review are addressed as proposed rules. In particular, deferred mixed batches now remain *wholly* invisible until their atomic BeginTick publication; no cross-tick partial commit is allowed. Borrowed-reader drain and canonical command conflict detection are separate responsibilities; `UnsafePartialCommit` always forces `StopWorld`.
- **Required sign-offs:** C00/integration and L2/L3A/L3B/L5 reviewers must review these semantics, declare acceptance or actionable changes, and record reviewer identity and approved commit SHA in Issue #1. Other affected module and WYU package-contract owners must acknowledge their own interface boundaries. A self-authored analysis/comment does not satisfy an independent review gate.
- **Acceptance evidence still outstanding:** No implementation, compiled interface example, test run, CI/package integrity demonstration or independent review is claimed by this PR. WP01-C00 is a *contract gate*; implementation tests and clean consumer integration belong to later WP01 work lanes and must not be falsely marked complete today.
- **Next action:** Keep PR #11 open until documented contract-owner reviews are collected; merge to `master` only after the approval gate is satisfied. Then record the merged commit SHA and sign-off evidence in Issue #1, close it with `completed`, and begin B00/Issue #2. Do not close Issue #1 merely because the drafting tasks are finished.
