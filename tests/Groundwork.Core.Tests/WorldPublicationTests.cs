using System.Collections.Immutable;

namespace Groundwork.Core.Tests;

[TestClass]
public sealed class WorldPublicationTests
{
    [TestMethod]
    public async Task CommandlessOutputRemainsAvailableToAnAdmittedDependentAfterOwnerRelease()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var ports = world.Publication;
        var phase = OpenBasePhase(ports, stamp);
        var first = new JobId(stamp.World, 1);
        var second = new JobId(stamp.World, 2);
        using var firstHandle = Admit(ports, first, Commandless(phase, 1), phase);
        using var secondHandle = Admit(ports, second, Commandless(phase, 2) with
        {
            Dependencies = [new JobDependency(first, DependencyKind.Execution)],
        }, phase);
        Assert.AreEqual(JobDispatchStatus.NotReady, ports.Execution.TryAuthorizeDispatch(second, phase).Status);

        var producer = Dispatch(ports, first, phase);
        Assert.AreEqual(ExecutionSettlementStatus.Settled, ports.Execution.TrySettle(
            new JobExecutionCompletion(producer, JobExecutionStatus.Succeeded, [], [7, 11, 19])));
        firstHandle.Dispose();

        var dependent = Dispatch(ports, second, phase);
        byte[] shortBuffer = [99, 99];
        Assert.AreEqual(new JobOutputRead(JobOutputReadStatus.BufferTooSmall, 3), dependent.DependencyOutputs.TryCopy(first, shortBuffer));
        CollectionAssert.AreEqual(new byte[] { 99, 99 }, shortBuffer);
        var output = new byte[3];
        Assert.AreEqual(new JobOutputRead(JobOutputReadStatus.Copied, 3), dependent.DependencyOutputs.TryCopy(first, output));
        CollectionAssert.AreEqual(new byte[] { 7, 11, 19 }, output);
        Assert.AreEqual(JobOutputReadStatus.Undeclared, dependent.DependencyOutputs.TryCopy(new JobId(stamp.World, 3), output).Status);

        Assert.AreEqual(ExecutionSettlementStatus.Settled, ports.Execution.TrySettle(
            new JobExecutionCompletion(dependent, JobExecutionStatus.Succeeded, [], [])));
        Assert.AreEqual(JobOutputReadStatus.Expired, dependent.DependencyOutputs.TryCopy(first, output).Status);
        ports.Phases.Close(phase);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BorrowPinsItsExactCommittedRootUntilReleaseOrDeadline(bool lateRelease)
    {
        using var fixture = new CoreRuntimeFixture(new ManualDrainClock());
        var world = fixture.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var ports = world.Publication;
        var phase = OpenBasePhase(ports, stamp);
        var job = new JobId(stamp.World, 1);
        var submission = Commandless(phase, 1) with
        {
            InputKind = JobInputKind.Borrowed,
            ReadScopes = [new ResourceKey(stamp.World, new ComponentTypeId(1), new ResourceScope(ScopeKind.World, null, null))],
        };
        using var handle = Admit(ports, job, submission, phase);
        var permit = Dispatch(ports, job, phase);
        var capture = ports.Capture.TryAcquire(permit);
        Assert.AreEqual(JobCapturePermitStatus.Authorized, capture.Status);
        using var lease = capture.Lease;
        Assert.IsNotNull(lease);
        Assert.AreEqual(stamp, lease.World.Stamp);
        Assert.AreSame(ports.Committed.Read().Root, lease.World.Root);
        Assert.AreEqual(JobCapturePermitStatus.AlreadyConsumed, ports.Capture.TryAcquire(permit).Status);

        ports.Phases.Close(phase);
        var request = new BarrierRequest(stamp, 1, BarrierKind.CommitResults);
        var deadline = world.CreateBorrowedDrainDeadline();
        var drain = ports.Borrow.CloseAndDrainAsync(request, deadline).AsTask();
        Assert.IsFalse(drain.IsCompleted);
        if (lateRelease)
            fixture.Clock.Advance(deadline.GetRemaining(fixture.Clock));
        lease.Dispose();
        Assert.AreEqual(lateRelease ? BorrowDrainStatus.TimedOut : BorrowDrainStatus.Drained,
            await drain.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(stamp, ports.Committed.Read().Stamp);
        if (lateRelease)
            Assert.AreEqual(CandidateSealStatus.Stopped, ports.Candidates.SealCandidates(request).Status);
        ports.Execution.TrySettle(new JobExecutionCompletion(permit, JobExecutionStatus.Cancelled, [], []));
    }

    [TestMethod]
    public async Task BorrowDrainTimeoutFencesWorldWithoutAdvancingTheBarrier()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var ports = world.Publication;
        var before = ports.Committed.Read();
        var phase = OpenBasePhase(ports, stamp);
        var job = new JobId(stamp.World, 1);
        using var handle = Admit(ports, job, Commandless(phase, 1) with
        {
            InputKind = JobInputKind.Borrowed,
            ReadScopes = [new ResourceKey(stamp.World, new ComponentTypeId(1), new ResourceScope(ScopeKind.World, null, null))],
        }, phase);
        var permit = Dispatch(ports, job, phase);
        var result = ports.Capture.TryAcquire(permit);
        Assert.AreEqual(JobCapturePermitStatus.Authorized, result.Status);
        using var lease = result.Lease;
        Assert.IsNotNull(lease);
        ports.Phases.Close(phase);
        var request = new BarrierRequest(stamp, 1, BarrierKind.CommitResults);
        var deadline = world.CreateBorrowedDrainDeadline();
        fixture.Clock.Advance(deadline.GetRemaining(fixture.Clock));

        Assert.AreEqual(BorrowDrainStatus.TimedOut, await ports.Borrow.CloseAndDrainAsync(request, deadline));
        Assert.IsTrue(world.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
        Assert.AreEqual(before, ports.Committed.Read());
        Assert.AreEqual(before, lease.World);
        Assert.AreEqual(CandidateSealStatus.Stopped, ports.Candidates.SealCandidates(request).Status);
        lease.Dispose();
        ports.Execution.TrySettle(new JobExecutionCompletion(permit, JobExecutionStatus.Cancelled, [], []));
    }

    [TestMethod]
    public async Task DeferredCapacityIsReservedBeforeAdmittingACommandJob()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld(jobs: CoreRuntimeFixture.Quota.Jobs with { DeferredBatches = 1 });
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var ports = world.Publication;
        var phase = OpenBasePhase(ports, stamp);
        var first = new JobId(stamp.World, 1);
        var second = new JobId(stamp.World, 2);
        using var firstHandle = Admit(ports, first, Structural(phase, 1), phase);

        var rejected = ports.Execution.TryAdmit(second, Structural(phase, 2), phase);
        Assert.AreEqual(JobAdmissionStatus.CapacityExceeded, rejected.Status);
        Assert.IsNull(rejected.Handle);

        Assert.AreEqual(JobCancellationStatus.Requested, firstHandle.RequestCancellation());
        firstHandle.Dispose();
        using var admitted = Admit(ports, second, Structural(phase, 2), phase);
        var batch = CompleteCreate(ports, Dispatch(ports, second, phase));
        ports.Phases.Close(phase);
        var candidates = await CoreRuntimeFixture.SealAsync(world, 1, BarrierKind.CommitResults);
        var prepared = CoreRuntimeFixture.Prepare(world, candidates, [new BatchDisposition(batch, BatchDispositionKind.Defer, BatchRejection.None)]);
        Assert.AreEqual(BarrierStatus.Unchanged, ports.Finalization.TryFinalize(prepared).Status);
    }

    [TestMethod]
    public async Task DeferredBatchDoesNotReleaseItsCommitDependentUntilTheNextBeginTick()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var ports = world.Publication;
        var phase = OpenBasePhase(ports, stamp);
        var first = new JobId(stamp.World, 1);
        var second = new JobId(stamp.World, 2);
        using var producer = Admit(ports, first, Structural(phase, 1), phase);
        using var dependent = Admit(ports, second, Commandless(phase, 2) with
        {
            Dependencies = [new JobDependency(first, DependencyKind.Commit)],
        }, phase);
        var permit = Dispatch(ports, first, phase);
        var batch = CompleteCreate(ports, permit);
        Assert.AreEqual(JobDispatchStatus.NotReady, ports.Execution.TryAuthorizeDispatch(second, phase).Status);
        ports.Phases.Close(phase);

        var results = await CoreRuntimeFixture.SealAsync(world, 1, BarrierKind.CommitResults);
        Assert.AreEqual(batch, results.Batches.Single().Batch);
        var defer = CoreRuntimeFixture.Prepare(world, results, [new BatchDisposition(batch, BatchDispositionKind.Defer, BatchRejection.None)]);
        var result = ports.Finalization.TryFinalize(defer);
        Assert.AreEqual(BarrierStatus.Unchanged, result.Status);
        Assert.AreEqual(JobCommitStatus.Pending, producer.Observe().Commit);
        Assert.AreEqual(0, producer.CopyReceipts(new BatchReceipt[1]));
        Assert.IsTrue(result.Stamp.HasValue);
        CoreRuntimeFixture.PublishObserver(world, result.Stamp.GetValueOrDefault(), 0);

        var next = await CoreRuntimeFixture.SealAsync(world, 2, BarrierKind.BeginTick);
        Assert.IsTrue(next.Batches.Single().Deferred);
        var changed = new CoreRuntimeFixture.Root(stamp.World, 1);
        var prepared = CoreRuntimeFixture.Prepare(world, next, [new BatchDisposition(batch, BatchDispositionKind.Commit, BatchRejection.None)], changed);
        var committed = ports.Finalization.TryFinalize(prepared);
        Assert.AreEqual(BarrierStatus.Committed, committed.Status);
        Assert.AreSame(changed, ports.Committed.Read().Root);
        Assert.AreEqual(JobCommitStatus.Committed, producer.Observe().Commit);
        var receipts = new BatchReceipt[1];
        Assert.AreEqual(1, producer.CopyReceipts(receipts));
        Assert.AreEqual(BatchReceiptStatus.Committed, receipts[0].Status);
        Assert.AreEqual(batch, receipts[0].Batch);
        Assert.AreEqual(2UL, receipts[0].Barrier.Tick);
        Assert.IsTrue(committed.Stamp.HasValue);
        var nextPhase = OpenBasePhase(ports, committed.Stamp.GetValueOrDefault());
        var allowed = Dispatch(ports, second, nextPhase);
        Assert.AreEqual(2UL, allowed.DispatchStamp.Tick);
        Assert.AreEqual(1UL, allowed.Source.Tick);
        ports.Execution.TrySettle(new JobExecutionCompletion(allowed, JobExecutionStatus.Cancelled, [], []));
        ports.Phases.Close(nextPhase);
    }

    [TestMethod]
    public async Task CancellationAfterPreparationRejectsTheBatchWithoutChangingTheRoot()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var ports = world.Publication;
        var root = ports.Committed.Read().Root;
        var phase = OpenBasePhase(ports, stamp);
        var job = new JobId(stamp.World, 1);
        using var handle = Admit(ports, job, Structural(phase, 1), phase);
        var batch = CompleteCreate(ports, Dispatch(ports, job, phase));
        ports.Phases.Close(phase);
        var candidates = await CoreRuntimeFixture.SealAsync(world, 1, BarrierKind.CommitResults);
        var prepared = CoreRuntimeFixture.Prepare(world, candidates, [new BatchDisposition(batch, BatchDispositionKind.Defer, BatchRejection.None)]);

        Assert.AreEqual(JobCancellationStatus.Requested, handle.RequestCancellation());
        var finalized = ports.Finalization.TryFinalize(prepared);
        Assert.AreEqual(BarrierStatus.Unchanged, finalized.Status);
        Assert.AreSame(root, ports.Committed.Read().Root);
        Assert.AreEqual(JobCommitStatus.Rejected, handle.Observe().Commit);
        var receipts = new BatchReceipt[1];
        Assert.AreEqual(1, handle.CopyReceipts(receipts));
        Assert.AreEqual(BatchReceiptStatus.Rejected, receipts[0].Status);
        Assert.AreEqual(BatchRejection.Cancelled, receipts[0].Rejection);
    }

    [TestMethod]
    public async Task UnpreparedCandidateSetCanBeAbortedOnlyOnce()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var ports = world.Publication;
        var before = ports.Committed.Read();
        var phase = OpenBasePhase(ports, stamp);
        var job = new JobId(stamp.World, 1);
        using var handle = Admit(ports, job, Structural(phase, 1), phase);
        var batch = CompleteCreate(ports, Dispatch(ports, job, phase));
        ports.Phases.Close(phase);
        var candidates = await CoreRuntimeFixture.SealAsync(world, 1, BarrierKind.CommitResults);

        Assert.AreEqual(BarrierAbortStatus.Stopped, ports.Finalization.TryAbort(candidates, BarrierAbortReason.PreparationFailed));
        Assert.AreEqual(BarrierAbortStatus.AlreadySettled, ports.Finalization.TryAbort(candidates, BarrierAbortReason.PreparationFailed));
        Assert.AreEqual(before, ports.Committed.Read());
        Assert.IsTrue(world.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
        Assert.AreEqual(JobCommitStatus.Rejected, handle.Observe().Commit);
        var receipts = new BatchReceipt[1];
        Assert.AreEqual(1, handle.CopyReceipts(receipts));
        Assert.AreEqual(batch, receipts[0].Batch);
        Assert.AreEqual(BatchReceiptStatus.Rejected, receipts[0].Status);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationNotifiesTheDispatchedWorker(bool stopRuntime)
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var ports = world.Publication;
        var phase = OpenBasePhase(ports, stamp);
        var job = new JobId(stamp.World, 1);
        using var handle = Admit(ports, job, Commandless(phase, 1), phase);
        var permit = Dispatch(ports, job, phase);
        Assert.IsFalse(permit.CancellationRequested.IsCompleted);

        if (stopRuntime)
            fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        else
            Assert.AreEqual(JobCancellationStatus.Requested, handle.RequestCancellation());

        await permit.CancellationRequested.WaitAsync(TimeSpan.FromSeconds(5));
        var status = ports.Execution.TrySettle(new JobExecutionCompletion(permit, JobExecutionStatus.Cancelled, [], []));
        Assert.AreEqual(stopRuntime ? ExecutionSettlementStatus.Stopped : ExecutionSettlementStatus.Cancelled, status);
        Assert.AreEqual(JobExecutionStatus.Cancelled, handle.Observe().Execution);
    }

    [TestMethod]
    public async Task SettlingAJobFaultFencesAnAlreadyPreparedBarrier()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var ports = world.Publication;
        var phase = OpenBasePhase(ports, stamp);
        var job = new JobId(stamp.World, 1);
        using var handle = Admit(ports, job, Commandless(phase, 1), phase);
        var permit = Dispatch(ports, job, phase);
        ports.Phases.Close(phase);
        var candidates = await CoreRuntimeFixture.SealAsync(world, 1, BarrierKind.CommitResults);
        var prepared = CoreRuntimeFixture.Prepare(world, candidates, []);

        var context = new FaultContext(stamp.World, stamp.Tick, phase.Phase, job, permit.Source, null);
        var fault = new FaultObservation(FaultClass.RecoverableJobFault, FaultSeverity.Error, FaultSubsystem.Jobs,
            FaultOrigin.Primary, 1, fixture.Clock.GetTimestamp(), context, [], []);
        Assert.AreEqual(ExecutionSettlementStatus.Settled, ports.Execution.TrySettleFault(permit, fault));
        Assert.AreEqual(JobExecutionStatus.Faulted, handle.Observe().Execution);
        Assert.AreEqual(1, world.State.Read().PendingRequiredEvidenceRecords);

        Assert.AreEqual(BarrierStatus.Stopped, ports.Finalization.TryFinalize(prepared).Status);
        Assert.AreEqual(stamp, ports.Committed.Read().Stamp);
    }

    [TestMethod]
    public async Task TerminalStopFencesAPreparedObserverSwap()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var ports = world.Publication;
        await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var first = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.CommitResults);
        CoreRuntimeFixture.PublishObserver(world, first, 1);
        await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 2, BarrierKind.BeginTick);
        var second = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 2, BarrierKind.CommitResults);
        CoreRuntimeFixture.PublishObserver(world, second, 2);
        var read = ports.ObserverSnapshots.TryAcquire();
        Assert.AreEqual(ObserverReadStatus.Acquired, read.Status);
        using var frames = read.Lease;
        Assert.IsNotNull(frames);
        Assert.AreEqual(first, frames.Previous?.Stamp);
        Assert.AreEqual(second, frames.Current.Stamp);
        await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 3, BarrierKind.BeginTick);
        var third = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 3, BarrierKind.CommitResults);
        var draft = new ObserverPublicationDraft(third, world.State.Read().PendingFaultGeneration, new CoreRuntimeFixture.ObserverState(third.World, 3));

        fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        Assert.AreEqual(ObserverPublication.Blocked, ports.Observer.TryPublish(draft));
        Assert.AreEqual(ObserverReadStatus.Closed, ports.ObserverSnapshots.TryAcquire().Status);
        Assert.AreEqual(first, frames.Previous?.Stamp);
        Assert.AreEqual(second, frames.Current.Stamp);
    }

    private static JobDispatchPhase OpenBasePhase(WorldPublicationPorts ports, BarrierStamp stamp)
    {
        var opened = ports.Phases.TryOpen(stamp, TickPhase.Base);
        Assert.AreEqual(JobPhaseOpenStatus.Opened, opened.Status);
        Assert.IsNotNull(opened.Phase);
        return opened.Phase;
    }

    private static IJobHandle Admit(WorldPublicationPorts ports, JobId job, JobSubmission submission, JobDispatchPhase phase)
    {
        var result = ports.Execution.TryAdmit(job, submission, phase);
        Assert.AreEqual(JobAdmissionStatus.Accepted, result.Status);
        Assert.IsNotNull(result.Handle);
        return result.Handle;
    }

    private static JobDispatchPermit Dispatch(WorldPublicationPorts ports, JobId job, JobDispatchPhase phase)
    {
        var result = ports.Execution.TryAuthorizeDispatch(job, phase);
        Assert.AreEqual(JobDispatchStatus.Authorized, result.Status);
        Assert.IsNotNull(result.Permit);
        return result.Permit;
    }

    private static JobSubmission Commandless(JobDispatchPhase phase, ulong ordinal) => new(
        new SourceKey(phase.Stamp.World, phase.Stamp.Tick, phase.Phase, 1, 0, ordinal),
        new JobHandlerId(1),
        JobInputKind.None,
        JobResultKind.Commandless,
        [], [], [], [], [],
        new JobResultBudget(0, 0, 0, 16, 0));

    private static JobSubmission Structural(JobDispatchPhase phase, ulong ordinal) => Commandless(phase, ordinal) with
    {
        ResultKind = JobResultKind.Commands,
        CommandScopes = [new ResourceKey(phase.Stamp.World, default, new ResourceScope(ScopeKind.World, null, null))],
        Budget = new JobResultBudget(1, 1, 0, 0, 0),
    };

    private static BatchId CompleteCreate(WorldPublicationPorts ports, JobDispatchPermit permit)
    {
        var batch = new BatchId(permit.Source, 1);
        var create = new JobCommand(1, 1, JobCommandKind.CreateEntity,
            new CommandEntity(CommandEntityKind.Provisional, null, 1), default, null, null, ImmutableArray<byte>.Empty);
        Assert.AreEqual(ExecutionSettlementStatus.Settled, ports.Execution.TrySettle(new JobExecutionCompletion(
            permit, JobExecutionStatus.Succeeded, [new CompletedBatch(batch, [create])], [])));
        return batch;
    }

    private sealed class ManualDrainClock : CoreRuntimeFixture.TestClock
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new InertTimer();
    }

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
