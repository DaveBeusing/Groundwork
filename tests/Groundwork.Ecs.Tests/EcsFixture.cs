using System.Collections.Immutable;
using Groundwork.Core;

namespace Groundwork.Ecs.Tests;

internal readonly record struct Position(int X);
internal readonly record struct Health(int Value);

internal sealed class EcsFixture : IDisposable
{
    internal static readonly ComponentTypeId Positions = new(1);
    internal static readonly ComponentTypeId Healths = new(2);
    internal static readonly ComponentRegistry Registry = new ComponentRegistryBuilder()
        .Register<Position>(Positions).Register<Health>(Healths).Build();
    private readonly RuntimeSafetyAuthority _runtime;
    private ulong _ordinal;

    internal EcsFixture(int slots = 16, IEnumerable<PartitionId>? partitions = null)
    {
        var quota = new ResourceQuota(1, 64,
            new JobQuota(32, 8, 32, 32, 16, 32, 64, 65536, 65536, 65536),
            new DiagnosticQuota(8, 65536, 24, 65536, 16, 4, 64, 1024));
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, quota));
        var reservation = budget.TryReserveRuntime(quota);
        Assert.IsNotNull(reservation);
        var duration = TimeSpan.FromSeconds(2);
        _runtime = RuntimeSafetyAuthority.Create(TimeProvider.System, new RuntimeTimingLimits(duration, duration, duration), reservation);
        World = EcsWorld.Open(_runtime, quota with
        {
            Diagnostics = quota.Diagnostics with { EmergencyRecords = 21, EmergencyBytes = 61440 },
        }, new ContinuePolicy(), Registry, new EcsLimits(slots, 2, 4, 1048576, 134217728), partitions);
    }

    internal EcsWorld World { get; }
    internal WorldPublicationPorts Ports => World.Safety.Publication;
    internal JobDispatchPhase? Phase { get; private set; }

    internal async Task<EcsBatchPlan> BeginAsync(ulong tick)
    {
        var plan = await RunBarrierAsync(tick, BarrierKind.BeginTick);
        var opened = Ports.Phases.TryOpen(Ports.Committed.Read().Stamp, TickPhase.Base);
        Assert.AreEqual(JobPhaseOpenStatus.Opened, opened.Status);
        Assert.IsNotNull(opened.Phase);
        Phase = opened.Phase;
        return plan;
    }

    internal async Task<EcsBatchPlan> EndAsync(ulong tick)
    {
        Assert.IsNotNull(Phase);
        Ports.Phases.Close(Phase);
        Phase = null;
        var plan = await RunBarrierAsync(tick, BarrierKind.CommitResults);
        var stamp = Ports.Committed.Read().Stamp;
        var observer = new ObserverPublicationDraft(stamp, World.Safety.State.Read().PendingFaultGeneration,
            World.CaptureObserver(stamp));
        Assert.AreEqual(ObserverPublication.Published, Ports.Observer.TryPublish(observer));
        return plan;
    }

    private async Task<EcsBatchPlan> RunBarrierAsync(ulong tick, BarrierKind kind)
    {
        var request = new BarrierRequest(Ports.Committed.Read().Stamp, tick, kind);
        Assert.AreEqual(BorrowDrainStatus.Drained,
            await Ports.Borrow.CloseAndDrainAsync(request, World.Safety.CreateBorrowedDrainDeadline()));
        var sealedSet = Ports.Candidates.SealCandidates(request);
        Assert.AreEqual(CandidateSealStatus.Sealed, sealedSet.Status);
        Assert.IsNotNull(sealedSet.Candidates);
        var plan = World.Stage(sealedSet.Candidates);
        foreach (var issue in plan.Issues)
            Assert.AreEqual(FaultReportStatus.Recorded, issue.Report(World.Safety.Reporter, TimeProvider.System.GetTimestamp()));
        ulong generation = World.Safety.State.Read().PendingFaultGeneration;
        if (!plan.Issues.IsEmpty)
        {
            var proof = World.Safety.Writer.TryIssue(request.Entry, generation);
            Assert.IsNotNull(proof);
            var review = World.Safety.Coordinator.TryBegin(proof);
            Assert.IsNotNull(review);
            var decision = await World.Safety.Supervisor.DecideAsync(review, CancellationToken.None);
            Assert.AreEqual(DecisionApplication.Continued, World.Safety.Coordinator.TryApply(decision));
        }
        var prepared = Ports.Preparation.TryPrepare(plan.CreateDraft(generation));
        Assert.AreEqual(BarrierPreparationStatus.Prepared, prepared.Status);
        Assert.IsNotNull(prepared.Publication);
        Assert.AreNotEqual(BarrierStatus.Stopped, Ports.Finalization.TryFinalize(prepared.Publication).Status);
        return plan;
    }

    internal IJobHandle Admit(JobInputKind input, ImmutableArray<ResourceKey> read = default,
        ImmutableArray<ResourceKey> write = default, ImmutableArray<ComponentTarget> snapshot = default, long snapshotBytes = 2048)
    {
        var phase = Phase;
        Assert.IsNotNull(phase);
        ulong ordinal = ++_ordinal;
        var result = Ports.Execution.TryAdmit(new JobId(World.Id, ordinal), new JobSubmission(
            new SourceKey(World.Id, phase.Stamp.Tick, phase.Phase, 1, 0, ordinal), new JobHandlerId(1), input,
            write.IsDefaultOrEmpty ? JobResultKind.Commandless : JobResultKind.Commands,
            [], read.IsDefault ? [] : read, write.IsDefault ? [] : write, snapshot.IsDefault ? [] : snapshot, [],
            new JobResultBudget(2, 16, 128, 0, input == JobInputKind.OwnedSnapshot ? snapshotBytes : 0)), phase);
        Assert.AreEqual(JobAdmissionStatus.Accepted, result.Status);
        Assert.IsNotNull(result.Handle);
        return result.Handle;
    }

    internal JobDispatchPermit Dispatch(IJobHandle job)
    {
        Assert.IsNotNull(Phase);
        var result = Ports.Execution.TryAuthorizeDispatch(job.Id, Phase);
        Assert.AreEqual(JobDispatchStatus.Authorized, result.Status);
        Assert.IsNotNull(result.Permit);
        return result.Permit;
    }

    internal IJobInput Capture(JobDispatchPermit permit)
    {
        var result = World.Inputs.TryCapture(new JobInputRequest(permit));
        Assert.AreEqual(JobInputCaptureStatus.Captured, result.Status);
        Assert.IsNotNull(result.Input);
        Assert.AreEqual(permit.DispatchStamp, result.Input.CapturedAt);
        return result.Input;
    }

    internal void Complete(JobDispatchPermit permit, params JobCommand[] commands)
    {
        ImmutableArray<CompletedBatch> batches = commands.Length == 0 ? [] :
            [new CompletedBatch(new BatchId(permit.Source, 1), [.. commands])];
        Assert.AreEqual(ExecutionSettlementStatus.Settled, Ports.Execution.TrySettle(new JobExecutionCompletion(
            permit, JobExecutionStatus.Succeeded, batches, [])));
    }

    internal ResourceKey WorldScope(ComponentTypeId component = default) =>
        new(World.Id, component, new ResourceScope(ScopeKind.World, null, null));

    internal static JobCommand Create(uint sequence = 1, uint provisional = 1, PartitionScope? partition = null) =>
        new(1, sequence, JobCommandKind.CreateEntity, new CommandEntity(CommandEntityKind.Provisional, null, provisional),
            default, partition, null, []);

    internal static JobCommand AddNew<T>(uint sequence, ComponentTypeId type, T value, uint provisional = 1) where T : unmanaged =>
        new(1, sequence, JobCommandKind.AddComponent, new CommandEntity(CommandEntityKind.Provisional, null, provisional),
            type, null, null, Registry.Encode(value));

    internal static JobCommand Existing(uint sequence, JobCommandKind kind, EntityId entity,
        ComponentTypeId component, TargetVersion? version, ImmutableArray<byte> payload = default) =>
        new(1, sequence, kind, new CommandEntity(CommandEntityKind.Existing, entity, null), component, null, version,
            payload.IsDefault ? [] : payload);

    public void Dispose()
    {
        _runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        _runtime.SettleStop();
    }

    private sealed class ContinuePolicy : IBarrierSupervisorPolicy
    {
        public ValueTask<SupervisorAction> DecideAsync(BarrierReviewRequest request, IFaultEvidenceReader evidence,
            CancellationToken cancellationToken) => ValueTask.FromResult(SupervisorAction.ContinueAtSafeBarrier);
    }
}
