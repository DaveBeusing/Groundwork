using System.Collections.Immutable;

namespace Groundwork.Core;

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

public readonly struct TargetVersion
{
    internal TargetVersion(WorldPublicationAuthority owner, TargetVersionStamp stamp)
    {
        Owner = owner;
        Stamp = stamp;
    }

    internal WorldPublicationAuthority? Owner { get; }
    internal TargetVersionStamp Stamp { get; }
}

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
    private readonly TaskCompletionSource _cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // This private task only detaches the internal runtime-stop observer. External
    // continuations can attach to the asynchronous cancellation signal above.
    private readonly TaskCompletionSource _settlement = new();
    private Task? _runtimeStop;

    internal JobDispatchPermit(JobId job, JobSubmission submission, JobDispatchPhase phase,
        IAdmittedJobScope scope, IJobArguments arguments, IJobDependencyOutputs dependencyOutputs, Task runtimeStop)
    {
        Job = job;
        Source = submission.Source;
        DispatchStamp = phase.Stamp;
        DispatchPhase = phase.Phase;
        Scope = scope;
        Arguments = arguments;
        DependencyOutputs = dependencyOutputs;
        InputKind = submission.InputKind;
        SnapshotTargets = submission.SnapshotTargets;
        Budget = submission.Budget;
        _runtimeStop = runtimeStop;
        _ = ObserveRuntimeStopAsync(runtimeStop);
    }

    public JobId Job { get; }
    public SourceKey Source { get; }
    public BarrierStamp DispatchStamp { get; }
    public TickPhase DispatchPhase { get; }
    public IJobArguments Arguments { get; }
    public IJobDependencyOutputs DependencyOutputs { get; }
    public Task CancellationRequested
    {
        get
        {
            // A coordinator returning from capture may poll before the stop observer resumes.
            var runtimeStop = Volatile.Read(ref _runtimeStop);
            if (runtimeStop is { IsCompletedSuccessfully: true } &&
                ReferenceEquals(runtimeStop, Volatile.Read(ref _runtimeStop)))
                SignalCancellation();
            return _cancellation.Task;
        }
    }
    public JobInputKind InputKind { get; }
    public IAdmittedJobScope Scope { get; }
    public ImmutableArray<ComponentTarget> SnapshotTargets { get; }
    public JobResultBudget Budget { get; }

    internal void SignalCancellation() => _cancellation.TrySetResult();

    internal void SignalSettlement()
    {
        Volatile.Write(ref _runtimeStop, null);
        _settlement.TrySetResult();
    }

    private async Task ObserveRuntimeStopAsync(Task runtimeStop)
    {
        // Settlement unregisters the invocation's wait even if the runtime remains alive.
        if (ReferenceEquals(await Task.WhenAny(runtimeStop, _settlement.Task).ConfigureAwait(false), runtimeStop))
            SignalCancellation();
    }
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

public readonly record struct TargetVersionStamp(
    JobDispatchPermit Permit, ComponentTarget Target, bool Present, ulong Mutation);

public interface ITargetVersionIssuer
{
    TargetVersion Issue(in TargetVersionStamp stamp);
    bool TryRead(TargetVersion version, JobDispatchPermit permit, ComponentTarget target, out TargetVersionStamp stamp);
}

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
    internal JobDispatchPhase(BarrierStamp stamp, TickPhase phase)
    {
        Stamp = stamp;
        Phase = phase;
    }
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
    internal CandidateSet(WorldPublicationAuthority owner, BarrierRequest request, ImmutableArray<CandidateBatch> batches)
    {
        Owner = owner;
        Request = request;
        Batches = batches;
    }

    internal WorldPublicationAuthority Owner { get; }
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

    /// <summary>Whether an admitted partition could contain this entity. Unknown membership is conservative.</summary>
    bool MayContain(PartitionScope partition, EntityId entity) => true;
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
    ImmutableArray<BatchDisposition> Dispositions, RootChangePermit? RootChange)
{
    public BarrierPublicationDraft(CandidateSet candidates, IWorldStateRoot nextRoot, ulong expectedSafetyGeneration,
        ImmutableArray<BatchDisposition> dispositions)
        : this(candidates, nextRoot, expectedSafetyGeneration, dispositions, null) { }

    public void Deconstruct(out CandidateSet candidates, out IWorldStateRoot nextRoot,
        out ulong expectedSafetyGeneration, out ImmutableArray<BatchDisposition> dispositions)
    {
        candidates = Candidates;
        nextRoot = NextRoot;
        expectedSafetyGeneration = ExpectedSafetyGeneration;
        dispositions = Dispositions;
    }
}

public sealed class PreparedBarrierPublication
{
    internal PreparedBarrierPublication(BarrierPublicationDraft draft, bool changesWorld)
    {
        Draft = draft;
        ChangesWorld = changesWorld;
    }

    internal BarrierPublicationDraft Draft { get; }
    internal bool ChangesWorld { get; }
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
    private readonly WorldPublicationAuthority _authority;
    internal WorldPublicationPorts(WorldPublicationAuthority authority)
    {
        _authority = authority;
        RootChanges = authority.RootChanges;
        Execution = new JobExecutionPort(authority);
        Phases = new JobPhasePort(authority);
        Capture = new JobCapturePort(authority);
        VersionIssuer = new TargetVersionPort(authority);
        Candidates = new CandidatePort(authority);
        Preparation = new BarrierPreparationPort(authority);
        Finalization = new BarrierFinalizationPort(authority);
        Observer = new ObserverPublicationPort(authority);
        ObserverSnapshots = new ObserverSnapshotPort(authority);
        Committed = new CommittedWorldPort(authority);
        Borrow = new BorrowBarrierPort(authority);
    }

    public CommittedCut? TryReserveCommitResultsCut(ulong tick) => _authority.ReserveCut(tick);

    public IRootChangeAuthority RootChanges { get; }
    public IJobExecutionAuthority Execution { get; }
    public IJobPhaseControl Phases { get; }
    public IJobCaptureAuthority Capture { get; }
    public ITargetVersionIssuer VersionIssuer { get; }
    public IJobPublicationSource Candidates { get; }
    public IBarrierPreparationSource Preparation { get; }
    public IBarrierPublicationAuthority Finalization { get; }
    public IObserverPublicationAuthority Observer { get; }
    public IObserverSnapshotSource ObserverSnapshots { get; }
    public ICommittedWorldSource Committed { get; }
    public IBorrowBarrierControl Borrow { get; }
}
