using System.Reflection;

namespace Groundwork.Core.Tests;

[TestClass]
public sealed class SafetyAuthorityTests
{
    [TestMethod]
    public void PendingWorldReleaseDoesNotRecycleItsIdentity()
    {
        using var fixture = new CoreRuntimeFixture();

        Assert.IsTrue(fixture.Runtime.TryAllocateWorldId(out var first));
        Assert.IsFalse(fixture.Runtime.TryAllocateWorldId(out _));
        Assert.IsTrue(fixture.Runtime.TryAbandonWorldId(first));
        Assert.IsFalse(fixture.Runtime.TryAbandonWorldId(first));
        Assert.IsTrue(fixture.Runtime.TryAllocateWorldId(out var next));
        Assert.IsGreaterThan(first.Value, next.Value);
    }

    [TestMethod]
    public void StopIsDurableAndDuplicateRequestsKeepTheOriginalDeadline()
    {
        using var fixture = new CoreRuntimeFixture();
        var runtime = fixture.Runtime;
        var first = runtime.StopControl.RequestStop(HostStopReason.WindowClose);

        Assert.IsTrue(runtime.UiWake.Handle.WaitOne(TimeSpan.Zero));
        Assert.IsTrue(runtime.SimulationWake.Handle.WaitOne(TimeSpan.Zero));
        Assert.IsTrue(runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
        Assert.IsFalse(first.IsExpired(fixture.Clock));
        Assert.IsNull(runtime.RuntimeOwnership.TryTrack(new object()));

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var duplicate = runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        Assert.AreEqual(first.Timestamp, duplicate.Timestamp);
        Assert.AreEqual(TimeSpan.FromSeconds(1), duplicate.GetRemaining(fixture.Clock));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(first.IsExpired(fixture.Clock));
        Assert.AreEqual(TimeSpan.Zero, duplicate.GetRemaining(fixture.Clock));
    }

    [TestMethod]
    public async Task StopFencesAndSignalsBothWakesWhileTheRuntimeGateIsHeld()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var runtime = fixture.Runtime;

        var (gateOwner, entered, release) = HoldGate(runtime);
        RuntimeDeadline deadline;
        try
        {
            await entered.WaitAsync(TimeSpan.FromSeconds(5));
            deadline = await Task.Run(() => runtime.StopControl.RequestStop(HostStopReason.WindowClose))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(runtime.UiWake.Handle.WaitOne(TimeSpan.Zero));
            Assert.IsTrue(runtime.SimulationWake.Handle.WaitOne(TimeSpan.Zero));
        }
        finally
        {
            release.TrySetResult();
            await gateOwner.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsTrue(world.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
        Assert.AreEqual(deadline.Timestamp, runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation).Timestamp);
    }

    [TestMethod]
    public async Task ShutdownTransfersToCustodyWhileTheRuntimeGateIsHeld()
    {
        var shutdown = TimeSpan.FromMilliseconds(100);
        var otherDeadline = TimeSpan.FromSeconds(2);
        using var fixture = new CoreRuntimeFixture(timing: new RuntimeTimingLimits(shutdown, otherDeadline, otherDeadline));
        var world = fixture.OpenWorld();
        var runtime = fixture.Runtime;
        using var owner = runtime.RuntimeOwnership.TryTrack(new object());
        Assert.IsNotNull(owner);

        var (gateOwner, entered, release) = HoldGate(runtime);
        try
        {
            await entered.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => runtime.StopControl.RequestStop(HostStopReason.WindowClose)).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(shutdown * 2);

            var outcome = await Task.Run(runtime.SettleStop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(ShutdownSettlement.Custody, outcome);
            Assert.IsFalse(gateOwner.IsCompleted);
            Assert.IsNull(fixture.Budget.TryReserveRuntime(CoreRuntimeFixture.Quota));
        }
        finally
        {
            release.TrySetResult();
            await gateOwner.WaitAsync(TimeSpan.FromSeconds(5));
        }

        owner.Dispose();
        Assert.IsTrue(SpinWait.SpinUntil(() => runtime.SettleStop() == ShutdownSettlement.Released, TimeSpan.FromSeconds(5)));
        using var next = fixture.Budget.TryReserveRuntime(CoreRuntimeFixture.Quota);
        Assert.IsNotNull(next);
    }

    [TestMethod]
    public void DeadlineRejectsAnotherClockAndExpiresOnClockRollback()
    {
        using var fixture = new CoreRuntimeFixture();
        var deadline = fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        var otherClock = new CoreRuntimeFixture.TestClock();

        Assert.ThrowsExactly<ArgumentException>(() => deadline.GetRemaining(otherClock));
        fixture.Clock.Advance(TimeSpan.FromSeconds(-1));
        Assert.IsTrue(deadline.IsExpired(fixture.Clock));
        Assert.AreEqual(TimeSpan.Zero, deadline.GetRemaining(fixture.Clock));
        Assert.AreEqual(TimeSpan.Zero, deadline.GetRemainingForSettlement(fixture.Clock));
    }

    [TestMethod]
    public void SettlementDeadlineUsesTrustedWatchdogWithoutSamplingClock()
    {
        var clock = new ThrowingClock();
        var budget = TimeSpan.FromMilliseconds(200);
        using var fixture = new CoreRuntimeFixture(clock, timing: new RuntimeTimingLimits(budget, budget, budget));
        var deadline = fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        clock.RejectSamples = true;

        try
        {
            Assert.IsLessThanOrEqualTo(budget, deadline.GetRemainingForSettlement(clock));
            Assert.ThrowsExactly<ArgumentException>(() => deadline.GetRemainingForSettlement(TimeProvider.System));
            Assert.ThrowsExactly<ArgumentException>(() => default(RuntimeDeadline).GetRemainingForSettlement(clock));
            Assert.IsTrue(SpinWait.SpinUntil(() => deadline.GetRemainingForSettlement(clock) == TimeSpan.Zero,
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            clock.RejectSamples = false;
        }
    }

    [TestMethod]
    public void WorldFaultStopsWithoutSamplingAnUnavailableClock()
    {
        var clock = new ThrowingClock();
        using var fixture = new CoreRuntimeFixture(clock);
        var world = fixture.OpenWorld();
        clock.RejectSamples = true;

        try
        {
            var context = new FaultContext(world.Publication.Committed.Read().Stamp.World, null, null, null, null, null);
            var observation = new FaultObservation(FaultClass.IncompleteCleanup, FaultSeverity.Error, FaultSubsystem.Jobs,
                FaultOrigin.Cleanup, 1, 0, context, [], []);
            world.Reporter.TryReport(observation);

            Assert.IsTrue(world.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
            Assert.IsFalse(fixture.Runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
        }
        finally
        {
            clock.RejectSamples = false;
        }
    }

    [TestMethod]
    public void DeadlineCustodyKeepsCapacityChargedUntilTheLastOwnerReleases()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        using var hostLease = fixture.Runtime.RuntimeOwnership.TryTrack(new object());
        using var worldLease = world.Ownership.TryTrack(new object());
        Assert.IsNotNull(hostLease);
        Assert.IsNotNull(worldLease);

        var deadline = fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        Assert.AreEqual(ShutdownSettlement.Pending, fixture.Runtime.SettleStop());
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.IsTrue(deadline.IsExpired(fixture.Clock));
        Assert.AreEqual(ShutdownSettlement.Custody, fixture.Runtime.SettleStop());
        Assert.IsNull(fixture.Budget.TryReserveRuntime(CoreRuntimeFixture.Quota));

        hostLease.Dispose();
        hostLease.Dispose();
        Assert.IsNull(fixture.Budget.TryReserveRuntime(CoreRuntimeFixture.Quota));
        worldLease.Dispose();

        using var next = fixture.Budget.TryReserveRuntime(CoreRuntimeFixture.Quota);
        Assert.IsNotNull(next);
    }

    [TestMethod]
    public async Task FaultRegisteredWhilePolicyRunsInvalidatesThePendingDecision()
    {
        using var fixture = new CoreRuntimeFixture();
        var policy = new ControlledPolicy();
        var world = fixture.OpenWorld(policy);
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        Assert.AreEqual(FaultReportStatus.Recorded, ReportRequired(world, stamp));
        var firstGeneration = world.State.Read().PendingFaultGeneration;
        var proof = world.Writer.TryIssue(stamp, firstGeneration);
        Assert.IsNotNull(proof);
        var request = world.Coordinator.TryBegin(proof);
        Assert.IsNotNull(request);
        var decisionTask = world.Supervisor.DecideAsync(request, CancellationToken.None).AsTask();

        try
        {
            await policy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(FaultReportStatus.Recorded, ReportRequired(world, stamp, 2));
        }
        finally
        {
            policy.Release.TrySetResult();
        }

        var decision = await decisionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(DecisionApplication.Rejected, world.Coordinator.TryApply(decision));
        var current = world.State.Read();
        Assert.IsGreaterThan(firstGeneration, current.PendingFaultGeneration);
        Assert.AreEqual(FaultEvidenceReadStatus.StaleGeneration, world.Evidence.CopyPending(firstGeneration, [], out _));
        Assert.AreEqual(DecisionApplication.Rejected, world.Coordinator.CompleteUnverified(firstGeneration));
        Assert.AreEqual(DecisionApplication.Stopped, world.Coordinator.CompleteUnverified(current.PendingFaultGeneration));
        Assert.IsTrue(world.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
    }

    [TestMethod]
    public async Task RequiredEvidenceOverflowIsDistinctFromOptionalDeliveryLoss()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);

        Report(world, stamp, FaultClass.DiagnosticOnly, 1);
        Report(world, stamp, FaultClass.DiagnosticOnly, 2);
        var optional = world.State.Read();
        Assert.IsGreaterThan(0UL, optional.DeliveryLossCount);
        Assert.IsFalse(optional.Conditions.HasFlag(SafetyCondition.RequiredEvidenceLost));

        for (var i = 0; i < CoreRuntimeFixture.Quota.Diagnostics.RequiredEvidenceRecords; i++)
        {
            Assert.AreEqual(FaultReportStatus.Recorded, ReportRequired(world, stamp, checked((uint)i + 1)));
        }

        Assert.AreEqual(FaultReportStatus.EvidenceLost, ReportRequired(world, stamp, 10));
        var required = world.State.Read();
        Assert.IsTrue(required.Conditions.HasFlag(SafetyCondition.RequiredEvidenceLost));
        Assert.IsGreaterThan(0UL, required.EvidenceLossCount);
        Assert.IsNull(world.Writer.TryIssue(stamp, required.PendingFaultGeneration));
    }

    [TestMethod]
    public async Task TerminalCleanupRetainsSeparatePrimaryCleanupAndSinkEvidence()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        Assert.AreEqual(FaultReportStatus.Recorded, ReportRequired(world, stamp));
        fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);

        var context = new FaultContext(stamp.World, stamp.Tick, TickPhase.Base, null, null, null);
        var cleanup = new FaultObservation(FaultClass.IncompleteCleanup, FaultSeverity.Error, FaultSubsystem.Ecs,
            FaultOrigin.Cleanup, 2, 0, context, [], []);
        var sink = new FaultObservation(FaultClass.RecoverableJobFault, FaultSeverity.Error, FaultSubsystem.Diagnostics,
            FaultOrigin.Sink, 3, 0, context, [], []);
        Assert.AreEqual(FaultReportStatus.Recorded, world.Reporter.TryReport(cleanup));
        Assert.AreEqual(FaultReportStatus.Recorded, world.Reporter.TryReport(sink));

        var state = world.State.Read();
        var evidence = new RequiredFaultEvidence[3];
        Assert.AreEqual(FaultEvidenceReadStatus.Copied, world.Evidence.CopyPending(state.PendingFaultGeneration, evidence, out var count));
        Assert.AreEqual(3, count);
        CollectionAssert.AreEqual(new[] { FaultOrigin.Primary, FaultOrigin.Cleanup, FaultOrigin.Sink },
            evidence.Select(item => item.Origin).ToArray());
        Assert.IsTrue(state.Conditions.HasFlag(SafetyCondition.IncompleteCleanup));
        Assert.IsTrue(state.Conditions.HasFlag(SafetyCondition.TerminalStop));

        var runtimeCleanup = new FaultObservation(FaultClass.IncompleteCleanup, FaultSeverity.Error, FaultSubsystem.Host,
            FaultOrigin.Cleanup, 4, 0, default, [], []);
        Assert.AreEqual(FaultReportStatus.Recorded, fixture.Runtime.Reporter.TryReport(runtimeCleanup));
        Assert.IsTrue(fixture.Runtime.State.Read().Conditions.HasFlag(SafetyCondition.IncompleteCleanup));
    }

    [TestMethod]
    public async Task ContendedRequiredIngressFencesOnlyTheReportingWorldWithoutWaiting()
    {
        var quota = new ResourceQuota(2, 32,
            new JobQuota(16, 8, 32, 32, 8, 16, 32, 131072, 131072, 131072),
            new DiagnosticQuota(2, 32768, 24, 65536, 8, 4, 64, 1024));
        using var fixture = new CoreRuntimeFixture(runtimeQuota: quota);
        var world = fixture.OpenWorld();
        var otherWorld = fixture.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var ports = world.Publication;
        var phase = ports.Phases.TryOpen(stamp, TickPhase.Base).Phase;
        Assert.IsNotNull(phase);
        var job = new JobId(stamp.World, 1);
        var source = new SourceKey(stamp.World, stamp.Tick, TickPhase.Base, 1, 0, 1);
        var commandScope = new ResourceKey(stamp.World, default, new ResourceScope(ScopeKind.World, null, null));
        var submission = new JobSubmission(source, new JobHandlerId(1), JobInputKind.None, JobResultKind.Commands,
            [], [], [commandScope], [], [], new JobResultBudget(1, 1, 0, 0, 0));
        using var handle = ports.Execution.TryAdmit(job, submission, phase).Handle;
        Assert.IsNotNull(handle);
        var permit = ports.Execution.TryAuthorizeDispatch(job, phase).Permit;
        Assert.IsNotNull(permit);
        var batch = new BatchId(source, 1);
        var command = new JobCommand(1, 1, JobCommandKind.CreateEntity, new CommandEntity(CommandEntityKind.Provisional, null, 1),
            default, null, null, []);
        Assert.AreEqual(ExecutionSettlementStatus.Settled, ports.Execution.TrySettle(
            new JobExecutionCompletion(permit, JobExecutionStatus.Succeeded, [new CompletedBatch(batch, [command])], [])));
        ports.Phases.Close(phase);
        var candidates = await CoreRuntimeFixture.SealAsync(world, 1, BarrierKind.CommitResults);
        var prepared = CoreRuntimeFixture.Prepare(world, candidates, [new BatchDisposition(batch, BatchDispositionKind.Defer, BatchRejection.None)]);

        var (gateOwner, entered, release) = HoldGate(fixture.Runtime);
        try
        {
            await entered.WaitAsync(TimeSpan.FromSeconds(5));
            var report = Task.Run(() => ReportRequired(world, stamp));
            Assert.AreEqual(FaultReportStatus.EvidenceLost, await report.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.TrySetResult();
            await gateOwner.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsTrue(world.State.Read().Conditions.HasFlag(SafetyCondition.RequiredEvidenceLost));
        Assert.AreEqual(BarrierStatus.Stopped, world.Publication.Finalization.TryFinalize(prepared).Status);
        Assert.AreEqual(stamp, world.Publication.Committed.Read().Stamp);
        var receipts = new BatchReceipt[1];
        Assert.AreEqual(1, handle.CopyReceipts(receipts));
        Assert.AreEqual(BatchRejection.Stopped, receipts[0].Rejection);
        Assert.AreEqual(BarrierAbortStatus.AlreadySettled, ports.Finalization.TryAbort(candidates, BarrierAbortReason.Faulted));
        Assert.IsFalse(fixture.Runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
        Assert.AreEqual(1UL, (await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(otherWorld, 1, BarrierKind.BeginTick)).Epoch);
    }

    private static FaultReportStatus ReportRequired(WorldSafetyPorts world, BarrierStamp stamp, uint code = 1) =>
        Report(world, stamp, FaultClass.Cancelled, code);

    private static FaultReportStatus Report(WorldSafetyPorts world, BarrierStamp stamp, FaultClass classification, uint code)
    {
        var context = new FaultContext(stamp.World, stamp.Tick, TickPhase.Base, null, null, null);
        var fault = new FaultObservation(classification, FaultSeverity.Warning, FaultSubsystem.Jobs, FaultOrigin.Primary, code, 0, context, [], []);
        return world.Reporter.TryReport(fault);
    }

    private sealed class ControlledPolicy : IBarrierSupervisorPolicy
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SupervisorAction> DecideAsync(
            BarrierReviewRequest request, IFaultEvidenceReader evidence, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return SupervisorAction.ContinueAtSafeBarrier;
        }
    }

    private sealed class ThrowingClock : CoreRuntimeFixture.TestClock
    {
        internal bool RejectSamples { get; set; }

        public override long GetTimestamp() => RejectSamples
            ? throw new InvalidOperationException("The injected clock is unavailable.")
            : base.GetTimestamp();
    }

    private static (Task Owner, Task Entered, TaskCompletionSource Release) HoldGate(RuntimeSafetyAuthority runtime)
    {
        // Deliberately contend the actual gate without relying on caller code running under it.
        var gate = typeof(RuntimeSafetyAuthority).GetProperty("Gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = Task.Run(() =>
        {
            lock (gate)
            {
                entered.SetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
        });
        return (owner, entered.Task, release);
    }
}
