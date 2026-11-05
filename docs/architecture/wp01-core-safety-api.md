# WP01 Core safety API

**Status** Draft declarations for API freeze. The [accepted runtime proposal](wp01-runtime-api-review.md#fault-supervision-and-cleanup) and [ADR-0005](../adr/ADR-0005-diagnostics-recovery.md) govern this specification. All declarations are in `Groundwork.Core`. Throwing bodies stand in for runtime implementation when checking declaration compatibility. ECS and Jobs define the identities, world root and publication ports. Host and timing define `TickPhase` and `HostStopReason`.

## Fault reporting and operational outcomes

```csharp
public enum FaultClass : byte
{
    Unspecified, DiagnosticOnly, Cancelled, DependencyFailure, StaleResult,
    WriteConflict, InvalidAccess, RecoverableJobFault, UnknownCritical,
    UnsafePartialCommit, BorrowedDrainExpired, IncompleteCleanup
}

public enum FaultSeverity : byte { Unspecified, Information, Warning, Error, Critical }
public enum FaultSubsystem : byte { Unspecified, Host, Platform, Simulation, Ecs, Jobs, Diagnostics }
public enum FaultOrigin : byte { Unspecified, Primary, Cleanup, Sink }
public enum FaultReportStatus : byte { EvidenceLost, Recorded, Coalesced }

public readonly record struct FaultContext(
    WorldId? World, ulong? Tick, TickPhase? Phase, JobId? Job,
    SourceKey? Source, Guid? Correlation);

public readonly ref struct FaultObservation(
    FaultClass classification, FaultSeverity severity, FaultSubsystem subsystem,
    FaultOrigin origin, uint code, long timestamp, FaultContext context,
    ReadOnlySpan<ResourceKey> targets, ReadOnlySpan<char> redactedText)
{
    public FaultClass Classification { get; } = classification;
    public FaultSeverity Severity { get; } = severity;
    public FaultSubsystem Subsystem { get; } = subsystem;
    public FaultOrigin Origin { get; } = origin;
    public uint Code { get; } = code;
    public long Timestamp { get; } = timestamp;
    public FaultContext Context { get; } = context;
    public ReadOnlySpan<ResourceKey> Targets { get; } = targets;
    public ReadOnlySpan<char> RedactedText { get; } = redactedText;
}

public interface IFaultReporter
{
    FaultReportStatus TryReport(scoped in FaultObservation observation);
}

public sealed record OperationalFailure(
    FaultClass Classification, FaultSubsystem Subsystem, FaultContext Context,
    ExceptionDispatchInfo Exception);

public sealed record RuntimeFailure(
    OperationalFailure? Primary, ImmutableArray<OperationalFailure> Cleanup);

public sealed class IncompleteCleanupException : Exception
{
    public IncompleteCleanupException(FaultSubsystem subsystem) => Subsystem = subsystem;
    public FaultSubsystem Subsystem { get; }
}
```

The reporting capability determines the admissible world. A world-scoped observation must agree with that world, including its job, source and targets. When a source and its originating tick or phase are both present, they must agree. A runtime-scoped reporter handles host and startup errors. The monotonic timestamp aids diagnostics. The authority uses registration order for publication decisions. Severity, timestamps, correlation and user-visible fault fields confer no authority. Producers supply bounded redacted text. Ingress copies at most the configured span prefixes into reserved storage and never retains caller memory. An unknown classification or malformed required scope or target latches `EvidenceLost`. For optional details, overflow increments delivery loss. `Coalesced` means an event received no new individual record. The snapshot counters distinguish repeating, coalesced and lost delivery.

`OperationalFailure` remains on the owning operation's result or exception path and preserves the original stack through `ExceptionDispatchInfo`. `RuntimeFailure` keeps cleanup entries separate and in observation order. Construction rejects a default cleanup array. Cleanup storage is charged to the operation until handed to its caller. L5 retains only bounded copied fault summaries. The typed incomplete-cleanup exception signals that ownership transferred to custody. It exposes no retained resource.

## Safety state and authority

```csharp
[Flags]
public enum SafetyCondition : byte
{
    None = 0, RecoveryPaused = 1, RequiredEvidenceLost = 2,
    TerminalStop = 4, IncompleteCleanup = 8, CounterSaturated = 16
}

public readonly record struct RuntimeSafetySnapshot(
    ulong PendingFaultGeneration, int PendingRequiredEvidenceRecords,
    SafetyCondition Conditions, ulong RepeatCount, ulong CoalescedCount,
    ulong DeliveryLossCount, ulong EvidenceLossCount);

public interface IRuntimeSafetyState
{
    RuntimeSafetySnapshot Read();
}

public readonly record struct RequiredFaultEvidence(
    ulong Generation, FaultClass Classification, FaultSubsystem Subsystem,
    FaultOrigin Origin, uint Code, FaultContext Context, ResourceKey? Target);

public enum FaultEvidenceReadStatus : byte
{
    EvidenceLost, Copied, InsufficientCapacity, StaleGeneration
}

public interface IFaultEvidenceReader
{
    FaultEvidenceReadStatus CopyPending(
        ulong generation, Span<RequiredFaultEvidence> destination, out int count);
}

public enum SupervisorAction : byte { StopWorld, PauseForRecovery, ContinueAtSafeBarrier }
public enum DecisionApplication : byte { Rejected, Continued, Paused, Stopped }

public sealed class WorldCoherenceProof
{
    internal WorldCoherenceProof() { }
    public BarrierStamp Stamp => throw new NotSupportedException();
    public ulong FaultGeneration => throw new NotSupportedException();
}

public sealed class BarrierReviewRequest
{
    internal BarrierReviewRequest() { }
    public ulong RequestId => throw new NotSupportedException();
    public WorldCoherenceProof Proof => throw new NotSupportedException();
    public RuntimeDeadline Deadline => throw new NotSupportedException();
}

public sealed class SupervisorDecision
{
    internal SupervisorDecision() { }
    public SupervisorAction Action => throw new NotSupportedException();
    public BarrierReviewRequest Request => throw new NotSupportedException();
}

public interface IWorldCoherenceIssuer
{
    WorldCoherenceProof? TryIssue(in BarrierStamp stamp, ulong faultGeneration);
}

public interface IBarrierReviewControl
{
    BarrierReviewRequest? TryBegin(WorldCoherenceProof proof);
    DecisionApplication TryApply(SupervisorDecision decision);
    DecisionApplication CompleteWithoutDecision(BarrierReviewRequest request);
    DecisionApplication CompleteUnverified(ulong faultGeneration);
}

public interface ISafeBarrierSupervisor
{
    ValueTask<SupervisorDecision> DecideAsync(
        BarrierReviewRequest request, CancellationToken cancellationToken);
}

public interface IBarrierSupervisorPolicy
{
    ValueTask<SupervisorAction> DecideAsync(
        BarrierReviewRequest request, IFaultEvidenceReader evidence,
        CancellationToken cancellationToken);
}

public sealed class RuntimeSafetyAuthority
{
    private RuntimeSafetyAuthority() { }
    public static RuntimeSafetyAuthority Create(
        TimeProvider clock, RuntimeTimingLimits timing, ProcessRuntimeReservation reservation)
        => throw new NotSupportedException();
    public IRuntimeStopControl StopControl => throw new NotSupportedException();
    public IFaultReporter Reporter => throw new NotSupportedException();
    public IRuntimeSafetyState State => throw new NotSupportedException();
    public RuntimeWakeSignal UiWake => throw new NotSupportedException();
    public RuntimeWakeSignal SimulationWake => throw new NotSupportedException();
    public IRuntimeOwnership RuntimeOwnership => throw new NotSupportedException();
    public ShutdownSettlement SettleStop() => throw new NotSupportedException();
    public ShutdownSettlement SettleWorld(WorldId world) => throw new NotSupportedException();
    public bool TryAllocateWorldId(out WorldId world) => throw new NotSupportedException();
    public bool TryAbandonWorldId(WorldId world) => throw new NotSupportedException();
    public WorldSafetyPorts OpenWorld(
        WorldId world, ResourceQuota quota, IBarrierSupervisorPolicy policy,
        IWorldStateRoot initialRoot, IJobScopePolicy scopePolicy)
        => throw new NotSupportedException();
}

public sealed class WorldSafetyPorts
{
    internal WorldSafetyPorts() { }
    public IFaultReporter Reporter => throw new NotSupportedException();
    public IRuntimeSafetyState State => throw new NotSupportedException();
    public IFaultEvidenceReader Evidence => throw new NotSupportedException();
    public IWorldCoherenceIssuer Writer => throw new NotSupportedException();
    public IBarrierReviewControl Coordinator => throw new NotSupportedException();
    public ISafeBarrierSupervisor Supervisor => throw new NotSupportedException();
    public WorldPublicationPorts Publication => throw new NotSupportedException();
    public IRuntimeOwnership Ownership => throw new NotSupportedException();
    public RuntimeDeadline CreateBorrowedDrainDeadline() => throw new NotSupportedException();
}
```

Only the composition root holds `RuntimeSafetyAuthority` and the complete port bundle. It distributes reporter and read-only observation ports separately from writer and coordinator roles. Constructors for the proof, request, decision, authority and port bundle are private or internal to Core. A second authority with matching numeric IDs cannot mint capabilities accepted by the original authority. The writer calls `TryIssue` after checking the actual committed state and proving that outstanding dependencies and all pending evidence for the specified generation were isolated. Core checks authority and freshness again before issuance. `CopyPending` copies required records only for the current intact generation. If multiple targets are required, retain one record per target with the same event generation. Insufficient destination capacity reports the needed count without a partial copy. No borrowed reference to fault storage escapes.

`TryAllocateWorldId` takes one pending world slot from the runtime and issues a never-reused process identity. When world capacity or identity space is exhausted, it returns false and a default ID. `TryAbandonWorldId` returns the pending slot once and permanently retires the ID, including after root construction fails. It returns false for a foreign, active or previously released ID. `OpenWorld` requires a pending ID issued to the same runtime, an initial root for that ID, and available quota. It moves the pending slot to active ownership only after validation and reservation. The returned safety and publication ports share one Core-owned world state initialized from that root, including the committed stamp, job and receipt ledger, and safety generation. The world never swaps a separately owned root after a safety call. Finalization in the publication port changes the authoritative root under the safety and terminal ordering described below.

`TryBegin` issues one request for the current proof and an absolute deadline. Proof issuance and review require the issuing world's current named barrier, including the exact tick, kind, epoch and revision. The Core-owned supervisor channel validates the request before calling the configured policy outside the control gate and wraps the policy result in a decision bound to that request and authority. The policy can choose an action but cannot mint an authenticated decision. `TryApply` consumes the live request once and validates authority, world, committed barrier stamp, proof generation, current pending generation, deadline and safety conditions. `CompleteWithoutDecision` settles an unanswered live request with fail-closed pause or stop. It can run without waiting for the policy. Invalid, stale or consumed artifacts never authorize publication. Checked identity exhaustion stops the affected world.

If no valid proof or request can be established, `CompleteUnverified` terminally stops the current unresolved generation. A stale generation returns `Rejected`, leaves the newer generation fenced and does not settle it. A valid live request uses its own completion path. The Jobs authority's fault settlement registers mandatory evidence or latches its loss in the same control transition that settles the job, even when normal diagnostic capacity is exhausted.

Required evidence ingress, recovery/stop transitions and every writer or observer publication share a linearizable control order. Mandatory record capacity and the generation change are settled together. Hot-path ingress performs bounded nonblocking work and invokes no sink. If ingress cannot retain mandatory evidence, it sets `RequiredEvidenceLost` without waiting on record storage or depending on queue delivery. Sink failures disable the sink, use the reserved emergency summary, and never re-enter the sink or reporting pipeline. A continuation can settle covered evidence and clear recovery pause only while its generation is current. Finalization still validates the expected control generation and current conditions at the commit point. No success result or opaque artifact authorizes a later swap outside that gate. A fault or terminal close that wins before publication blocks the swap. Once the swap wins, subsequent faults govern later publications.

`UnsafePartialCommit` and `BorrowedDrainExpired` stop immediately. `UnknownCritical`, lost mandatory evidence, unverified proof and a failed supervisor request stop unless independently established coherence permits a fenced recovery pause. `InvalidAccess` and `RecoverableJobFault` default to fenced pause with independent coherence. Cancellation, dependency failure, stale results and write conflicts permit continuation only after the writer establishes isolation and the current decision authorizes it. A host resume clears only user pause. `TerminalStop`, `RequiredEvidenceLost` and `IncompleteCleanup` are monotonic. Required evidence loss prohibits continuation for the affected authority. A new authority has a distinct identity.

## Explicit finite limits

```csharp
public readonly record struct RuntimeTimingLimits(
    TimeSpan Shutdown, TimeSpan BorrowedDrain, TimeSpan SupervisorResponse);

public readonly record struct DiagnosticQuota(
    int NormalRecords, long NormalBytes,
    int EmergencyRecords, long EmergencyBytes,
    int RequiredEvidenceRecords, int MaxTargetsPerRecord,
    int MaxTextCharsPerRecord, int MaxPayloadBytesPerRecord);

public readonly record struct JobQuota(
    int Jobs, int Workers, int DependencyEdges, int RetentionHandles,
    int BorrowedLeases, int DeferredBatches, int ReceiptRecords,
    long DescriptorBytes, long SnapshotBytes, long ResultBytes);

public readonly record struct ResourceQuota(
    int Worlds, int OutstandingOperations, JobQuota Jobs, DiagnosticQuota Diagnostics);

public readonly record struct ProcessResourceLimits(int Runtimes, ResourceQuota Resources);

public sealed class ProcessResourceBudget
{
    public ProcessResourceBudget(ProcessResourceLimits limits) => throw new NotSupportedException();
    public ProcessRuntimeReservation? TryReserveRuntime(ResourceQuota quota)
        => throw new NotSupportedException();
}

public sealed class ProcessRuntimeReservation : IDisposable
{
    internal ProcessRuntimeReservation() { }
    public ResourceQuota Quota => throw new NotSupportedException();
    public void Dispose() => throw new NotSupportedException();
}

public readonly struct RuntimeDeadline
{
    public long Timestamp => throw new NotSupportedException();
    public bool IsExpired(TimeProvider clock) => throw new NotSupportedException();
    public TimeSpan GetRemaining(TimeProvider clock) => throw new NotSupportedException();
    public TimeSpan GetRemainingForSettlement(TimeProvider clock) => throw new NotSupportedException();
}

public interface IRuntimeStopControl
{
    RuntimeDeadline RequestStop(HostStopReason reason);
}

public sealed class RuntimeWakeSignal
{
    private RuntimeWakeSignal() { }
    public WaitHandle Handle => throw new NotSupportedException();
    public void Signal() => throw new NotSupportedException();
}

public interface IRuntimeWorkLease : IDisposable { }

public interface IRuntimeOwnership
{
    IRuntimeWorkLease? TryTrack(object retainedOwner);
}

public enum ShutdownSettlement : byte { Pending = 1, Released, Custody, InvalidScope }
```

Composition supplies every duration, count and byte ceiling. Constructors or admission reject default and invalid values before resource acquisition. Require positive values, a positive clock frequency, and a representable duration in the shared timestamp domain. Treat shape limits such as maximum text length as local validation limits. Compare them with parent caps without adding them to reservation totals. Charge aggregate counters and capacities with checked arithmetic. Require `Workers` to fit the job capacity and per-record payload capacity to fit the normal and emergency backing limits. Emergency reservation must fit its required evidence ledger plus separate first-primary, cleanup and sink summaries and the control state. Reserve the complete configured record/byte capacity before accepting producers. L5 defines the retained record layout and checks the cost of every allocated record and backing buffer, including metadata and redacted copies, against these quotas. Mandatory capacity is distinct from optional queue capacity. Sink disablement does not release records still reachable by an in-flight sink.

Create one process budget at composition and reuse it across runtime restarts. Runtime reservation atomically charges the process limits. A runtime claims its reservation once and opens worlds only from its remaining credit. World reservations and runtime-local use share that credit. Charge child use against its reserved share without double-counting the process allocation. A failed capacity reservation admits no work. Outstanding operations include pending lifecycle calls, drains, reviews, joins and cancellation dispatch. Custody retains the original reservations until all users acknowledge release. Disposing an unclaimed reservation returns capacity once. After runtime adoption, only its terminal release or custody protocol can return capacity. An abandoned reservation cannot increase admission capacity.

An owning module registers a retention bundle before starting or exposing work that can outlive its caller. A null `TryTrack` rejects the operation. The issued lease identifies exactly one charged registration. The owner acknowledges release by disposing that lease once it proves the producer and all users have quiesced. The bundle holds late acquisitions and affinity-bound cleanup until acknowledgment. Worker cancellation alone does not dispose the lease. Terminal settlement validates the adopted scope, returns `Pending` before the deadline while tracked work remains, returns `Released` when all work and child reservations have settled, or transfers ownership to process custody and returns `Custody` at expiry. A world stop starts that world's finite deadline; a runtime stop fences all worlds and caps their deadlines by the shared runtime deadline. Duplicate settlement cannot return credits twice. A lease acknowledged after custody returns its share once and releases the scope only after its last owner and child are clear.

Core owns distinct retained wake signals for the UI pump and simulation coordinator, with a single waiting consumer each. A signal persists until consumed and may coalesce repeated notifications. The first stop signals both independently of the startup or cleanup gate. Pause, resume and supervisor recovery signal the simulation endpoint. The host rechecks the control state after waking. Handles are borrowed by their trusted waiting owners and remain open until waiters have quiesced, including in custody. Only L1 bridges its handle into the native message wait.

Deadlines are Core-issued values tied to the injected clock by identity. A default deadline or a different clock is invalid. Compute the absolute timestamp using widened checked arithmetic and ceiling-round positive durations to timestamp units. Remaining time clamps to zero and floor-rounds the conversion to `TimeSpan`. Expiry compares timestamps directly. If the clock rolls back or the deadline cannot be represented, latch stop and issue an already-expired deadline. The first terminal stop latches admission and publication closed and captures the single shutdown deadline before cancellation dispatch or an affinity-bound wait. Repeated requests return that deadline and cannot weaken terminal safety state. Barrier and review deadlines are separate finite budgets and never extend an active terminal deadline. A caller cancelling its wait does not cancel shared cleanup. At expiry, transfer reachable resources and reservations to process custody and surface `IncompleteCleanupException` while release continues under the original ownership and affinity rules.

The coordinator requests a Core-issued borrowed drain deadline through `CreateBorrowedDrainDeadline` and passes it to the borrow barrier port. The deadline uses the configured borrowed-drain budget and the authority's clock domain. Core uses the last trusted injected-clock sample at issuance, then checks trusted samples and the system watchdog while waiting and when the final pin releases. The drain does not invoke the injected clock or timer. An explicit sample through `IsExpired` or `GetRemaining` outside the drain can shorten the remaining budget.

`GetRemainingForSettlement` validates the clock identity and returns the smaller of the remaining budget at the latest trusted sample and the remaining system-monotonic watchdog budget. It clamps negative results to zero and does not invoke the supplied clock or its timer. Use it when waiting for shutdown cleanup. Terminal stop issuance for a runtime or a world uses the last trusted sample for an injected clock.

Core orders world and runtime publication through one runtime gate. When a producer cannot acquire the gate for required fault evidence, it sets a monotonic evidence-loss fence without waiting. A world reporter fences its own world and wakes Simulation. A runtime reporter fences all its worlds and wakes the UI and Simulation. Optional telemetry contention uses delivery loss and leaves the publication fences unchanged.

The first runtime stop publishes its fence and deadline together without entering the authority gate. It signals both retained wakes and the worker cancellation notification. For an injected clock, the stop path uses the last trusted sample and avoids invoking caller code. This can shorten cleanup when the clock has been idle or is blocked. Settlement observes trusted clock samples and a system-monotonic watchdog that can only shorten the remaining budget. A contended settlement returns promptly. At expiry it retains the runtime in process custody, with all reservations still charged, and reconciles the ledger when the gate becomes available. An injected clock can be sampled through the deadline outside the authority gate.

## Acceptance traces

- Register a same-revision required fault before and after prepared writer and observer swaps. Verify the winning operation and ensure a stale decision cannot authorize the later swap.
- Submit evidence, proofs and decisions from another authority with matching IDs. Verify rejection, including a second response to a consumed request and a response after deadline.
- Saturate optional delivery, then mandatory evidence and emergency slots. Check that optional loss stays separate, required loss fences publication, and primary, cleanup and sink outcomes remain distinct. Include a disabled sink with an outstanding record.
- Exercise duration overflow, cross-clock deadline rejection, duplicate stop and caller wait cancellation. Verify custody retention and capacity rejection while startup, writer, lease or sink work remains reachable, then return capacity after the last release.
