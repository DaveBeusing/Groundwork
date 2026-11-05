# WP01 Core ECS and Jobs declarations

This declaration candidate refines the [accepted runtime proposal](wp01-runtime-api-review.md) for API freeze review. All declarations below belong to `Groundwork.Core`. ECS implements storage, registration, capture, lease accounting and mutation staging. Jobs implements scheduler admission, execution, result buffering and retained handles. Composition connects trusted ports without either module referencing the other. This document specifies interfaces and representations for review before implementation.

## Identity and access

```csharp
public readonly record struct WorldId(ulong Value);
public readonly record struct EntityId(WorldId World, uint Slot, uint Generation);
public readonly record struct JobId(WorldId World, ulong Value);
public readonly record struct ComponentTypeId(uint Value);
public readonly record struct PartitionId(uint Value);
public readonly record struct PartitionScope(PartitionId Partition, ulong Revision);

public enum ScopeKind : byte { World = 1, Partition, Entity }
public readonly record struct ResourceScope(ScopeKind Kind, PartitionScope? Partition, EntityId? Entity);
public readonly record struct ResourceKey(WorldId World, ComponentTypeId Component, ResourceScope Scope);
public readonly record struct ComponentTarget(EntityId Entity, ComponentTypeId Component);

public enum BarrierKind : byte { BeginTick = 1, CommitResults }
public readonly record struct BarrierStamp(WorldId World, ulong Tick, BarrierKind Kind, ulong Epoch, ulong Revision);
public readonly record struct BarrierRequest(BarrierStamp Entry, ulong Tick, BarrierKind Kind);
public readonly record struct SourceKey(WorldId World, ulong Tick, TickPhase Phase, uint System, uint Partition, ulong Ordinal);
public readonly record struct BatchId(SourceKey Source, uint Sequence);
```

- A world ID is process-unique, monotonic and nonzero. Job IDs are monotonic and nonzero within their world. Neither counter wraps. Entity slot zero is usable. Generation starts at one and an exhausted slot is permanently retired. A reused slot carries a different generation. Absence of allocatable slots fails explicitly.
- The world registry freezes component identifiers before admitting jobs. `ComponentTypeId(0)` is reserved for the structural domain. `ResourceScope` has exactly one legal field combination per kind. `World` has no additional fields, `Partition` has only the nonzero partition and membership revision, and `Entity` has only the full live generational identity in the resource's world. Reject default kinds and mismatched fields. `ComponentTarget` with component zero denotes the entity-wide target used for structural identity checks.
- Grants record normalized, deduplicated keys and admitted partition membership. Actual access is limited to the intersection of admitted and currently authorized targets. Membership changes cannot add authority to an existing grant. Scope overlap is world-local, structural keys cover all component domains in their scope, and partition intersections use frozen membership. Unproven scope is rejected or conservatively excluded. Overlapping readers may execute together. Active read/write and write/write overlaps exclude competing execution. Same-barrier cross-batch conflicts reject all affected batches, including where the scheduler ran producers in dependency order.
- `BarrierRequest.Entry` describes the last completed barrier, while `Tick` and `Kind` name the attempted next boundary. Initial committed state has tick, epoch and revision zero, with `Kind` equal to zero only in that initial stamp. `BeginTick` and `CommitResults` advance epoch after quiescence, including no-op barriers. Advance world revision only when committed world data changes. Reject invalid ordering and stop before any identity or counter would wrap.
- The tick owner assigns `SourceKey` during canonical system and partition enumeration. Phase is `Gameplay` or `Base`. Source ordinal is nonzero and monotonic per world. Sort results by tick, phase, system, partition, source ordinal and batch sequence. `JobId` is a lookup key and never orders work. Reject duplicate source and batch keys. Batch and command sequences are nonzero and strictly increasing within their respective stream.

## Admission, capture and execution

```csharp
public enum DependencyKind : byte { Execution = 1, Commit }
public enum JobInputKind : byte { None = 1, Borrowed, OwnedSnapshot }
public enum JobResultKind : byte { Commandless = 1, Commands }

public readonly record struct JobDependency(JobId Prerequisite, DependencyKind Kind);
public readonly record struct JobHandlerId(uint Value);
public readonly record struct JobResultBudget(
    int MaxBatches, int MaxCommands, long MaxCommandPayloadBytes,
    int MaxOutputBytes, long MaxSnapshotBytes);

public sealed record JobSubmission(
    SourceKey Source,
    JobHandlerId Handler,
    JobInputKind InputKind,
    JobResultKind ResultKind,
    ImmutableArray<JobDependency> Dependencies,
    ImmutableArray<ResourceKey> ReadScopes,
    ImmutableArray<ResourceKey> CommandScopes,
    ImmutableArray<ComponentTarget> SnapshotTargets,
    ImmutableArray<byte> Arguments,
    JobResultBudget Budget);

public enum JobAdmissionStatus : byte
{
    Accepted = 1, InvalidDescriptor, InvalidAccess, InvalidPrerequisite, CapacityExceeded, Closed
}

public enum JobExecutionStatus : byte
{
    Waiting = 1, Ready, Running, Succeeded, Cancelled, Faulted, DependencyFailed
}

public enum JobCommitStatus : byte { Pending = 1, NotApplicable, Committed, Rejected }
public enum JobCancellationStatus : byte { Requested = 1, AlreadyRequested, Settled, Released }
public readonly record struct JobObservation(JobExecutionStatus Execution, JobCommitStatus Commit);
public readonly record struct JobAdmissionResult(JobAdmissionStatus Status, IJobHandle? Handle);

public interface IJobScheduler
{
    JobAdmissionResult TrySubmit(JobSubmission submission, JobDispatchPhase phase);
    ValueTask DispatchReadyAsync(JobDispatchPhase phase, CancellationToken cancellationToken);
}

public interface IJobHandle : IDisposable
{
    JobId Id { get; }
    JobObservation Observe();
    JobCancellationStatus RequestCancellation();
    int CopyReceipts(Span<BatchReceipt> destination);
}

public interface IJobInputReader
{
    BarrierStamp CapturedAt { get; }
    ComponentRead<T> Read<T>(EntityId entity) where T : unmanaged;
    EntityRead ReadEntity(EntityId entity);
}

public interface IJobInput : IJobInputReader, IDisposable { }

public readonly struct TargetVersion { }

public enum ComponentReadStatus : byte
{
    Present = 1, Missing, InvalidId, ForeignWorld, DisposedWorld, Stale, ExpiredLease, UndeclaredAccess
}

public readonly record struct ComponentRead<T>(ComponentReadStatus Status, T Value, TargetVersion? Version)
    where T : unmanaged;
public readonly record struct EntityRead(ComponentReadStatus Status, TargetVersion? Version);

public enum JobInputCaptureStatus : byte
{
    Captured = 1, InvalidPermit, UnmetDependency, BusyBarrier, InvalidAccess, CapacityExceeded, Closed
}
public readonly record struct JobInputRequest(JobDispatchPermit Permit);
public readonly record struct JobInputCapture(JobInputCaptureStatus Status, IJobInput? Input);

public interface IJobInputSource
{
    JobInputCapture TryCapture(in JobInputRequest request);
}

public enum BorrowDrainStatus : byte { Drained = 1, TimedOut, InvalidBarrier, Stopped }

public interface IBorrowBarrierControl
{
    ValueTask<BorrowDrainStatus> CloseAndDrainAsync(BarrierRequest request, RuntimeDeadline deadline);
}

public enum JobOutputReadStatus : byte { Copied = 1, Undeclared, BufferTooSmall, Expired }
public readonly record struct JobOutputRead(JobOutputReadStatus Status, int BytesRequired);

public interface IJobDependencyOutputs
{
    JobOutputRead TryCopy(JobId prerequisite, Span<byte> destination);
}

public interface IJobArguments
{
    JobOutputRead TryCopy(Span<byte> destination);
}

public readonly record struct JobExecutionContext(
    JobId Job,
    BarrierStamp DispatchStamp,
    IJobArguments Arguments,
    IJobInputReader? Input,
    IJobDependencyOutputs Dependencies,
    IJobCommandWriter Results);

public delegate ValueTask JobHandler(JobExecutionContext context, CancellationToken cancellationToken);

public sealed class JobDispatchPermit
{
    private JobDispatchPermit() { }

    public JobId Job { get; }
    public SourceKey Source { get; }
    public BarrierStamp DispatchStamp { get; }
    public TickPhase DispatchPhase { get; }
    public IJobDependencyOutputs DependencyOutputs => throw new NotSupportedException();
    public JobInputKind InputKind { get; }
    public IAdmittedJobScope Scope => throw new NotSupportedException();
    public ImmutableArray<ComponentTarget> SnapshotTargets { get; }
    public JobResultBudget Budget { get; }
}

public enum JobDispatchStatus : byte { Authorized = 1, NotReady, InvalidJob, InvalidBarrier, InvalidAccess, Closed }
public readonly record struct JobDispatchResult(JobDispatchStatus Status, JobDispatchPermit? Permit);

public enum JobCapturePermitStatus : byte { Authorized = 1, InvalidPermit, Expired, AlreadyConsumed, CapacityExceeded }

public interface IJobCaptureLease : IDisposable
{
    CommittedWorldView World { get; }
}

public readonly record struct JobCapturePermitResult(JobCapturePermitStatus Status, IJobCaptureLease? Lease);

public interface IJobCaptureAuthority
{
    JobCapturePermitResult TryAcquire(JobDispatchPermit permit);
}
```

`TargetVersion` is a public readonly opaque struct. Its non-public constructor binds an issuer, grant, capture, full target and mutation stamp, with presence encoded in the stamp. The default value has no authority. A writer-owned `ITargetVersionIssuer` below is the only issuance path for ECS. Versions and data come from one coherent read. `Present` and `Missing` carry a token for a live entity and known component, with `Value` meaningful only for `Present`. `ReadEntity` carries an entity-wide token only for `Present`, requires the structural read scope, and detects any committed mutation to the entity. A removal produces a fresh absence stamp. An unrelated commit preserves a component's stamp. Commands cannot use a token issued to another grant or capture. Check token exhaustion before publication.

```csharp
public readonly record struct TargetVersionStamp(
    JobDispatchPermit Permit, ComponentTarget Target, bool Present, ulong Mutation);

public interface ITargetVersionIssuer
{
    TargetVersion Issue(in TargetVersionStamp stamp);
}
```

Only approved, explicitly registered copy-out types are eligible. `unmanaged` is necessary but does not permit pointers, handles or a value that aliases mutable storage. Admission selects a registered trusted handler and copies all externally supplied buffers. Argument and dependency readers copy into caller-owned storage without exposing their backing arrays. If the destination is short, report the required size without writing. A handler never receives an authority object. The scheduler passes a non-disposable worker reader facade. Only the scheduler retains the disposable `IJobInput`. A borrowed reader tracks in-flight copies through disposal races and may not span a barrier. A snapshot copies only the declared `SnapshotTargets` and their versions, including valid absence, within its reservation. Commandless `None` uses a null reader. A structural create with no read input can also use `None` if its explicit structural grant covers the destination.

`JobDispatchPermit` is opaque and carries the normalized scope and admitted membership. `TryAcquire` consumes the permit, checks the exact active phase and dispatch stamp, pins that committed root and registers the live-resource lease in one transition. ECS reads only that root. Successful capture has `CapturedAt == DispatchStamp`. An owned snapshot releases its pin after copying. A borrowed input retains the pin through its last in-flight copy. On failure release any acquired pin and settle the failed dispatch without invoking the handler or retrying. Capture state for a commit-dependent job only in its first eligible phase after every required commit. Preserve the original source key across ticks. Link worker cancellation to owner cancellation and shutdown. Returning from the handler settles execution separately from publication.

Each prerequisite must be active, previously admitted and in the same world. Forward or self references cannot form a cycle. Execution prerequisites target declared commandless jobs and expose bounded immutable output through a copy into the caller's buffer. Commit prerequisites require all trusted commit receipts for a nonempty sealed batch set. The scheduler never rents a worker slot or borrowed lease to a job waiting on dependencies. Admission reserves bounded storage and retention before taking ownership. Aggregate limits come from the runtime budget contract. A zero per-job limit disallows that category, and no descriptor may declare capacity above its aggregate quota. The owner handle retains its record. `Dispose` releases that retention and does not request cancellation. Other dependents retain their own references. After disposal, `Observe` and `CopyReceipts` throw `ObjectDisposedException`; cancellation reports `Released`. `CopyReceipts` copies the current ordered prefix that fits and returns the number copied. It exposes copies of receipt projections, not authority.

## Batches, receipts and publication

```csharp
public enum JobCommandKind : byte { CreateEntity = 1, DestroyEntity, AddComponent, ReplaceComponent, RemoveComponent }
public enum CommandEntityKind : byte { Existing = 1, Provisional }
public readonly record struct CommandEntity(CommandEntityKind Kind, EntityId? Existing, uint? Provisional);

public readonly record struct JobCommand(
    uint BatchSequence,
    uint CommandSequence,
    JobCommandKind Kind,
    CommandEntity Entity,
    ComponentTypeId Component,
    PartitionScope? Destination,
    TargetVersion? ExpectedVersion,
    ImmutableArray<byte> Payload);

public enum BatchAppendResult : byte { Accepted = 1, InvalidCommand, UndeclaredTarget, CapacityExceeded, Closed }

public interface IJobCommandWriter
{
    BatchAppendResult TryAppend(in JobCommand command);
    BatchAppendResult TrySetOutput(ReadOnlySpan<byte> payload);
}

public enum BatchReceiptStatus : byte { Committed = 1, Rejected }
public enum BatchRejection : byte
{
    None = 0, Cancelled, DependencyFailed, StaleTarget, InvalidAccess, WriteConflict, InvalidBatch, Stopped,
    BarrierInvalidated
}

public readonly record struct BatchReceipt(
    BatchId Batch, BarrierStamp Barrier, BatchReceiptStatus Status, BatchRejection Rejection);

public sealed record CompletedBatch(BatchId Batch, ImmutableArray<JobCommand> Commands);
public sealed record JobExecutionCompletion(
    JobDispatchPermit Permit, JobExecutionStatus Status, ImmutableArray<CompletedBatch> Batches,
    ImmutableArray<byte> CommandlessOutput);

public enum ExecutionSettlementStatus : byte { Settled = 1, Cancelled, InvalidCompletion, AlreadySettled, Stopped }
public enum JobPhaseOpenStatus : byte { Opened = 1, InvalidPhase, AlreadyOpen, Closed }

public sealed class JobDispatchPhase
{
    private JobDispatchPhase() { }
    public BarrierStamp Stamp { get; }
    public TickPhase Phase { get; }
}

public readonly record struct JobPhaseOpenResult(JobPhaseOpenStatus Status, JobDispatchPhase? Phase);

public interface IJobPhaseControl
{
    JobPhaseOpenResult TryOpen(BarrierStamp stamp, TickPhase phase);
    void Close(JobDispatchPhase phase);
}

public interface IJobExecutionAuthority
{
    JobAdmissionResult TryAdmit(JobId job, JobSubmission submission, JobDispatchPhase phase);
    JobDispatchResult TryAuthorizeDispatch(JobId job, JobDispatchPhase phase);
    JobCancellationStatus RequestCancellation(JobId job);
    ExecutionSettlementStatus TrySettle(JobExecutionCompletion completion);
    ExecutionSettlementStatus TrySettleFault(JobDispatchPermit permit, scoped in FaultObservation fault);
}

public sealed record CandidateBatch(
    JobDispatchPermit Permit, BatchId Batch,
    ulong CancellationGeneration, bool Deferred, ImmutableArray<JobCommand> Commands);

public sealed class CandidateSet
{
    private CandidateSet() { }

    public BarrierRequest Request { get; }
    public ImmutableArray<CandidateBatch> Batches { get; }
}

public enum CandidateSealStatus : byte { Sealed = 1, BusyBarrier, InvalidBarrier, Stopped }
public readonly record struct CandidateSealResult(CandidateSealStatus Status, CandidateSet? Candidates);

public interface IJobPublicationSource
{
    CandidateSealResult SealCandidates(in BarrierRequest request);
}

public interface IWorldStateRoot
{
    WorldId World { get; }
}

public readonly record struct CommittedWorldView(BarrierStamp Stamp, IWorldStateRoot Root);

public interface ICommittedWorldSource
{
    CommittedWorldView Read();
}

public interface IAdmittedJobScope
{
    ImmutableArray<ResourceKey> ReadScopes { get; }
    ImmutableArray<ResourceKey> CommandScopes { get; }
}

public enum JobScopeStatus : byte { Authorized = 1, InvalidAccess, CapacityExceeded }
public readonly record struct JobScopeAdmission(JobScopeStatus Status, IAdmittedJobScope? Scope);
public enum JobScopeOverlap : byte { Independent = 1, Excluded, Unknown }

public interface IJobScopePolicy
{
    JobScopeAdmission ValidateAdmission(JobSubmission submission, in CommittedWorldView world);
    bool CanDispatch(IAdmittedJobScope scope, in CommittedWorldView world);
    JobScopeOverlap EvaluateOverlap(
        IAdmittedJobScope first, IAdmittedJobScope second, in CommittedWorldView world);
}

public readonly record struct BatchDisposition(BatchId Batch, BatchDispositionKind Kind, BatchRejection Rejection);
public enum BatchDispositionKind : byte { Commit = 1, Defer, Reject }
public sealed record BarrierPublicationDraft(
    CandidateSet Candidates, IWorldStateRoot NextRoot, ulong ExpectedSafetyGeneration,
    ImmutableArray<BatchDisposition> Dispositions);

public sealed class PreparedBarrierPublication
{
    private PreparedBarrierPublication() { }
}

public enum BarrierPreparationStatus : byte { Prepared = 1, InvalidCandidate, CapacityExceeded, Stopped }
public readonly record struct BarrierPreparationResult(BarrierPreparationStatus Status, PreparedBarrierPublication? Publication);

public interface IBarrierPreparationSource
{
    BarrierPreparationResult TryPrepare(BarrierPublicationDraft draft);
}

public interface IBarrierPublicationAuthority
{
    BarrierOutcome TryFinalize(PreparedBarrierPublication publication);
    BarrierAbortStatus TryAbort(CandidateSet candidates, BarrierAbortReason reason);
}

public enum BarrierAbortReason : byte { PreparationFailed = 1, Cancelled, Faulted }
public enum BarrierAbortStatus : byte { Stopped = 1, AlreadySettled, InvalidCandidate }

public interface IWorldObserverState
{
    WorldId World { get; }
}

public readonly record struct ObserverPublicationDraft(
    BarrierStamp Source, ulong ExpectedSafetyGeneration, IWorldObserverState State);

public interface IObserverPublicationAuthority
{
    ObserverPublication TryPublish(in ObserverPublicationDraft draft);
}

public readonly record struct ObserverFrame(BarrierStamp Stamp, IWorldObserverState State);

public interface IObserverSnapshotLease : IDisposable
{
    ObserverFrame? Previous { get; }
    ObserverFrame Current { get; }
}

public enum ObserverReadStatus : byte { Acquired = 1, Empty, CapacityExceeded, Closed }
public readonly record struct ObserverReadResult(ObserverReadStatus Status, IObserverSnapshotLease? Lease);

public interface IObserverSnapshotSource
{
    ObserverReadResult TryAcquire();
}

public sealed class WorldPublicationPorts
{
    internal WorldPublicationPorts() { }

    public IJobExecutionAuthority Execution => throw new NotSupportedException();
    public IJobPhaseControl Phases => throw new NotSupportedException();
    public IJobCaptureAuthority Capture => throw new NotSupportedException();
    public ITargetVersionIssuer VersionIssuer => throw new NotSupportedException();
    public IJobPublicationSource Candidates => throw new NotSupportedException();
    public IBarrierPreparationSource Preparation => throw new NotSupportedException();
    public IBarrierPublicationAuthority Finalization => throw new NotSupportedException();
    public IObserverPublicationAuthority Observer => throw new NotSupportedException();
    public IObserverSnapshotSource ObserverSnapshots => throw new NotSupportedException();
    public ICommittedWorldSource Committed => throw new NotSupportedException();
}
```

- A `CreateEntity` command defines a nonzero batch-local provisional target, with an explicit world or destination partition structural grant. No existing entity or expected version is supplied. `DestroyEntity` targets an existing entity, requires an entity-wide token and conflicts with any other batch's operation on that entity. Both use component zero. `AddComponent`, `ReplaceComponent` and `RemoveComponent` require a nonzero registered component. On an existing entity, they require the captured target token. Add requires absence at the point of staged application. Replace and remove require presence. A provisional component target must refer to an earlier create in the same batch and carries no prior version. Add and replace carry bytes of the registered bounded encoding. Create, destroy and remove have no payload. Only create specifies a destination. Reject invalid combinations before staging and never expose a provisional ID outside its batch.
- Accepting an append or output copies data into scheduler-owned reservations. Rejection does not retain caller data. `TrySetOutput` is available once for a commandless handler. Appending commands is available only to command-producing handlers. Result append closes on cancellation or successful worker completion. Successful completion seals the batch-ID set and validates commandless output before releasing an execution prerequisite. Commit dependencies remain pending while any sealed batch is deferred. A rejected unpublished batch makes the aggregate fail and blocks downstream uncommitted work. A zero-batch result has `NotApplicable` and cannot satisfy a commit dependency. A batch committed earlier remains settled even if a sibling later fails. Owner cancellation competes with the final publication and cannot reclaim a live capability.
- Jobs receives the execution authority. The coordinator opens one phase under the last successful `BeginTick` stamp and passes the capability to Jobs. It opens Gameplay only when eligible and closes it before opening Base. Close fences new dispatch and capture without revoking existing leases. `TryAdmit` validates the descriptor, live references, reservations and scope, then returns a Core-backed retained handle whose observation, receipt reads and disposal operate on the authoritative ledger. The ECS-owned `IJobScopePolicy` validates the effective scope and exclusion outside the gate. The authority rechecks the world stamp, active phase and active-grant generation at admission or dispatch. If any has changed, reject the prepared decision. Dispatch requires matching declared phase and settled prerequisites. Cancellation records intent before invoking callbacks. `TrySettle` accepts one successful or cancelled completion from the exact permit. Only success can transfer batch or output ownership. `TrySettleFault` atomically settles a fault and registers its required evidence or latches required evidence loss. Faulted jobs publish no outputs, and the original exception stays on the operational path. All transitions share the world authority used for finalization.
- `CandidateSet` and `PreparedBarrierPublication` are opaque Core objects. `WorldSafetyPorts.Publication` comes from the same world authority as safety, and composition holds the complete port bundle. Jobs receives `Execution`. The ECS owner receives `Capture`, `VersionIssuer`, `Candidates`, `Preparation` and `Committed`. The coordinator receives `Finalization` and `Observer`. The shared authority verifies that objects and permits originated from its configured channels and are current. It checks per-world identity, exact candidate-set identity and consume-once state. Field equality, a separate authority for the same world ID, and user-created implementations of a port confer no grant. Cross-assembly collaborators use the injected ports rather than constructors or friend assembly access. Private constructors and throwing member bodies in the declarations illustrate compile-checkable surfaces without implementation.
- The coordinator closes borrowed admission through `IBorrowBarrierControl` and proves release before sealing. A coherent finalized barrier reopens borrowing only for the next eligible phase and current epoch. An ordinary borrow timeout terminally quarantines the world and retains reachable resources without changing the epoch, committed root or pending queue. `BeginTick` seals the whole due deferred set. `CommitResults` seals eligible completions, stages pure replacement batches and defers whole structural or mixed batches. Revalidate all grants, dependencies, versions and full candidate conflicts at both boundaries. A `Defer` records queue ownership but emits no terminal receipt. `BeginTick` cannot defer the same batch again. Reservations survive until a terminal outcome. Candidates that arrive after sealing wait for a later barrier.
- Every candidate has one staged disposition. ECS prepares a non-mutating replacement root and records dispositions for one sealed set. The authority prepares matching queue and receipt changes before the final transition. It rechecks the entry revision, candidate, cancellation generations, dependency authority and current safety generation. Finalization publishes the world root, epoch, revision, deferred queue and terminal receipts in one transition, serialized with cancellation, stop and safety invalidation. `IWorldStateRoot` is an opaque ECS-owned object across the boundary. The authority validates the root before the final transition and installs its reference without calling arbitrary module code at the transition. `ICommittedWorldSource` is given only to the ECS owner and returns a coherent stamp and root. Borrowed reads may access that root only inside the lease acquisition protocol. Unrelated validated batches may commit only when safe policy and their prerequisites allow. A failed preflight leaves the old world visible. A stopped finalization does not expose staged mutation. Report an unverifiable partial commit as a terminal fault. Release rejected or quarantined staging only after producers quiesce.
- The coordinator prepares immutable observer state outside the gate and calls `Observer.TryPublish` with the current completed `CommitResults` stamp. The authority rechecks that stamp, the expected safety generation and pause/stop fences in the same control transition as writer finalization. The winning transition swaps the observer state once for that tick. It runs no renderer or listener code. Publication failure preserves the previous observer state and follows the tick settlement contract.
- L3A supplies explicit finite limits for live and staged ECS roots, entity/component allocation, partition count and membership bytes, and retained observer state. Validate the initial root and reserve the worst-case concurrently retained world data before opening admission. Each barrier charges the candidate list, staged root and maps, staged receipts and defer bookkeeping against validated staging and runtime quotas while the old root and ledger remain live. Reject work before allocation if the reservation cannot cover both views. Retained state remains charged until the last holder releases it, including deadline custody. These are lane-owned settings with no implicit defaults.
- Receipt authority comes only from that terminal ledger, never from `BatchReceipt` values passed by workers or callbacks. Identical deliveries can be replayed without duplicate settlement. Foreign or mismatched identities cannot satisfy a prerequisite. The coordinator's logical barrier guard remains active through finalization, rejects reentry and defers notifications until exit. Listener failure cannot turn a committed receipt into a retry.
- The coordinator owns settlement after sealing. Reserve settlement bookkeeping at seal. If preparation fails or control exits before finalization, `TryAbort` consumes the set exactly once and terminally fences the world. If a job-level cancellation invalidates a live preparation while the stamp and safety authorization remain current, finalize a reserved rejection-only outcome. Preserve world data and revision, consume the selected candidates with rejection receipts and advance the epoch. Mark other staged candidates invalidated by that barrier and report the outcome to supervision. If terminal close or a failed safety check wins, apply the terminal abort. Terminal settlement rejects unpublished batches and dependents through the reserved ledger, preserves the committed root and stamp and leaves the deferred queue inaccessible to dispatch until cleanup. Borrow admission stays closed. Foreign or already settled candidates have no effect.
- Seal the replacement root and its reachable backing data before preparation. A rejected preparation leaves staging with ECS. Successful preparation transfers ownership to the authority for finalization or abort. Published and retained roots stay immutable. Release only after authorized holders quiesce, transferring still-reachable resources to custody if needed.
- The observer reader gets a bounded lease over the coherent previous/current pair and their stamps, independently of the ECS writer view. Before first publication return `Empty`. Reserve retention before granting a lease. A successful swap rotates the old current to previous and installs the new current atomically. A blocked swap leaves both unchanged. Drop frame references before releasing the lease. Terminal close fences new leases, and outstanding buffers stay charged through custody.
- The trusted pipeline passes the active phase to scheduler submissions and calls `DispatchReadyAsync` with the same capability before closing the phase. Jobs visits ready candidates in stable source order and schedules only within available worker and capture reservations. It may rescan on readiness while the phase remains open. The phase handoff does not wait for all workers or extend borrow lifetime. A job that cannot dispatch during that phase remains pending for its next eligible phase unless it reaches a terminal outcome.
- Core binds the dispatch permit's `DependencyOutputs` reader to that permit and its retained commandless execution prerequisites. Jobs passes only the reader to the handler. Copy from the authoritative retained output under an in-flight read reservation. The reader remains valid across phases while that worker owns its invocation, rejects undeclared prerequisites, and expires on settlement. Keep the buffer charged until the worker and all in-flight copies have quiesced. This avoids a second retained copy and does not expose a backing buffer.

## Freeze checks

Review the declaration choices with L2, L3A, L3B and L5. Require executable contract tests during implementation for foreign and exhausted IDs, admitted partition narrowing, stale and absent-version ABA, borrowed copy/dispose races, deferred mixed batches, post-commit input capture, whole-set conflicts, cancellation at the publish boundary and receipt retention after owner release. The concrete state root, storage layout, worker pool, registry and retention data structures remain lane implementation choices.
