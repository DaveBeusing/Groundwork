using Groundwork.Core;

namespace Groundwork.Diagnostics.Tests;

[TestClass]
public sealed class BarrierSupervisorTests
{
    [TestMethod]
    [DataRow(FaultClass.Cancelled, SupervisorAction.ContinueAtSafeBarrier, DecisionApplication.Continued)]
    [DataRow(FaultClass.DependencyFailure, SupervisorAction.ContinueAtSafeBarrier, DecisionApplication.Continued)]
    [DataRow(FaultClass.StaleResult, SupervisorAction.ContinueAtSafeBarrier, DecisionApplication.Continued)]
    [DataRow(FaultClass.WriteConflict, SupervisorAction.ContinueAtSafeBarrier, DecisionApplication.Continued)]
    [DataRow(FaultClass.InvalidAccess, SupervisorAction.PauseForRecovery, DecisionApplication.Paused)]
    [DataRow(FaultClass.RecoverableJobFault, SupervisorAction.PauseForRecovery, DecisionApplication.Paused)]
    [DataRow(FaultClass.UnknownCritical, SupervisorAction.StopWorld, DecisionApplication.Stopped)]
    public async Task AppliesThePolicyOnlyThroughCoreAtTheVerifiedBarrier(
        FaultClass classification, SupervisorAction action, DecisionApplication application)
    {
        using var fixture = new WorldFixture();
        var stamp = await fixture.CommitEmptyTickAsync();
        Assert.AreEqual(FaultReportStatus.Recorded, fixture.Report(stamp, classification));
        Assert.AreEqual(ObserverPublication.Blocked, fixture.PublishObserver(stamp));

        var decision = await fixture.ReviewAsync(stamp);
        Assert.AreEqual(action, decision.Action);
        Assert.AreEqual(application, fixture.World.Coordinator.TryApply(decision));
        Assert.AreEqual(DecisionApplication.Rejected, fixture.World.Coordinator.TryApply(decision));

        var safety = fixture.World.State.Read();
        Assert.AreEqual(application == DecisionApplication.Paused, safety.Conditions.HasFlag(SafetyCondition.RecoveryPaused));
        Assert.AreEqual(application == DecisionApplication.Stopped, safety.Conditions.HasFlag(SafetyCondition.TerminalStop));
        Assert.AreEqual(application == DecisionApplication.Continued ? 0 : 1, safety.PendingRequiredEvidenceRecords);
        Assert.AreEqual(application == DecisionApplication.Continued ? ObserverPublication.Published : ObserverPublication.Blocked,
            fixture.PublishObserver(stamp));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PauseTakesPrecedenceOverIsolatedFailuresRegardlessOfRegistrationOrder(bool pauseFirst)
    {
        using var fixture = new WorldFixture();
        var stamp = await fixture.CommitEmptyTickAsync();
        Assert.AreEqual(FaultReportStatus.Recorded, fixture.Report(stamp,
            pauseFirst ? FaultClass.RecoverableJobFault : FaultClass.StaleResult));
        Assert.AreEqual(FaultReportStatus.Recorded, fixture.Report(stamp,
            pauseFirst ? FaultClass.StaleResult : FaultClass.RecoverableJobFault));

        var decision = await fixture.ReviewAsync(stamp);
        Assert.AreEqual(DecisionApplication.Paused, fixture.World.Coordinator.TryApply(decision));
        Assert.AreEqual(ObserverPublication.Blocked, fixture.PublishObserver(stamp));
    }

    [TestMethod]
    public async Task NewFaultInvalidatesAnExistingContinuation()
    {
        using var fixture = new WorldFixture();
        var stamp = await fixture.CommitEmptyTickAsync();
        Assert.AreEqual(FaultReportStatus.Recorded, fixture.Report(stamp, FaultClass.Cancelled));
        var first = await fixture.ReviewAsync(stamp);
        Assert.AreEqual(SupervisorAction.ContinueAtSafeBarrier, first.Action);

        Assert.AreEqual(FaultReportStatus.Recorded, fixture.Report(stamp, FaultClass.WriteConflict));
        Assert.AreEqual(DecisionApplication.Rejected, fixture.World.Coordinator.TryApply(first));
        Assert.AreEqual(ObserverPublication.Blocked, fixture.PublishObserver(stamp));
        Assert.AreEqual(2, fixture.World.State.Read().PendingRequiredEvidenceRecords);

        var current = await fixture.ReviewAsync(stamp);
        Assert.AreEqual(DecisionApplication.Continued, fixture.World.Coordinator.TryApply(current));
        Assert.AreEqual(ObserverPublication.Published, fixture.PublishObserver(stamp));
    }

    [TestMethod]
    public async Task IncompleteEvidenceSnapshotStopsInsteadOfClassifyingAPrefix()
    {
        using var fixture = new WorldFixture(evidenceCapacity: 1);
        var stamp = await fixture.CommitEmptyTickAsync();
        Assert.AreEqual(FaultReportStatus.Recorded, fixture.Report(stamp, FaultClass.Cancelled));
        Assert.AreEqual(FaultReportStatus.Recorded, fixture.Report(stamp, FaultClass.StaleResult));

        var decision = await fixture.ReviewAsync(stamp);
        Assert.AreEqual(SupervisorAction.StopWorld, decision.Action);
        Assert.AreEqual(DecisionApplication.Stopped, fixture.World.Coordinator.TryApply(decision));
        Assert.AreEqual(ObserverPublication.Blocked, fixture.PublishObserver(stamp));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SupervisorUsesTheTrustedDeadlineWhenTheInjectedClockIsUnavailable(bool expire)
    {
        var clock = new ControlledClock();
        using var fixture = new WorldFixture(clock: clock);
        var stamp = await fixture.CommitEmptyTickAsync();
        Assert.AreEqual(FaultReportStatus.Recorded, fixture.Report(stamp, FaultClass.Cancelled));

        clock.RejectSampling = true;
        var decision = await fixture.ReviewAsync(stamp);
        if (expire)
        {
            clock.RejectSampling = false;
            clock.Timestamp = decision.Request.Deadline.Timestamp;
            Assert.IsTrue(decision.Request.Deadline.IsExpired(clock));
            clock.RejectSampling = true;

            Assert.AreEqual(DecisionApplication.Rejected, fixture.World.Coordinator.TryApply(decision));
            Assert.AreEqual(DecisionApplication.Paused, fixture.World.Coordinator.CompleteWithoutDecision(decision.Request));
            Assert.AreEqual(ObserverPublication.Blocked, fixture.PublishObserver(stamp));
        }
        else
        {
            Assert.AreEqual(DecisionApplication.Continued, fixture.World.Coordinator.TryApply(decision));
            Assert.AreEqual(ObserverPublication.Published, fixture.PublishObserver(stamp));
        }
    }

    [TestMethod]
    [DataRow(FaultClass.UnsafePartialCommit)]
    [DataRow(FaultClass.BorrowedDrainExpired)]
    [DataRow(FaultClass.IncompleteCleanup)]
    public async Task TerminalFailureCannotObtainCoherenceProof(FaultClass classification)
    {
        using var fixture = new WorldFixture();
        var stamp = await fixture.CommitEmptyTickAsync();
        Assert.AreEqual(FaultReportStatus.Recorded, fixture.Report(stamp, classification));

        var state = fixture.World.State.Read();
        Assert.IsTrue(state.Conditions.HasFlag(SafetyCondition.TerminalStop));
        Assert.IsNull(fixture.World.Writer.TryIssue(stamp, state.PendingFaultGeneration));
        Assert.AreEqual(ObserverPublication.Blocked, fixture.PublishObserver(stamp));
    }

    [TestMethod]
    public async Task EvidenceLossRemainsFencedEvenForOtherwiseRecoverableReports()
    {
        using var fixture = new WorldFixture();
        var stamp = await fixture.CommitEmptyTickAsync();
        for (var index = 0; index < WorldFixture.RequiredEvidenceCapacity; index++)
            Assert.AreEqual(FaultReportStatus.Recorded, fixture.Report(stamp, FaultClass.Cancelled));
        Assert.AreEqual(FaultReportStatus.EvidenceLost, fixture.Report(stamp, FaultClass.Cancelled));

        var state = fixture.World.State.Read();
        Assert.IsTrue(state.Conditions.HasFlag(SafetyCondition.RequiredEvidenceLost));
        Assert.IsTrue(state.Conditions.HasFlag(SafetyCondition.TerminalStop));
        Assert.IsNull(fixture.World.Writer.TryIssue(stamp, state.PendingFaultGeneration));
        Assert.AreEqual(ObserverPublication.Blocked, fixture.PublishObserver(stamp));
    }

    private sealed class WorldFixture : IDisposable
    {
        internal const int RequiredEvidenceCapacity = 4;
        private readonly RuntimeSafetyAuthority _runtime;
        internal WorldSafetyPorts World { get; }

        internal WorldFixture(int evidenceCapacity = RequiredEvidenceCapacity, TimeProvider? clock = null)
        {
            var quota = new ResourceQuota(1, 16, new JobQuota(8, 4, 16, 16, 4, 8, 16, 65536, 65536, 65536),
                new DiagnosticQuota(1, 16384, 12, 32768, RequiredEvidenceCapacity, 4, 64, 1024));
            var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, quota));
            using var reservation = budget.TryReserveRuntime(quota);
            Assert.IsNotNull(reservation);
            var timeout = TimeSpan.FromSeconds(5);
            _runtime = RuntimeSafetyAuthority.Create(clock ?? TimeProvider.System, new RuntimeTimingLimits(timeout, timeout, timeout),
                reservation);
            Assert.IsTrue(_runtime.TryAllocateWorldId(out var id));
            var worldQuota = quota with { Diagnostics = quota.Diagnostics with { EmergencyRecords = 9, EmergencyBytes = 28672 } };
            World = _runtime.OpenWorld(id, worldQuota, new BarrierSupervisorPolicy(evidenceCapacity), new Root(id),
                new NoJobScopes());
        }

        internal async Task<BarrierStamp> CommitEmptyTickAsync()
        {
            await FinalizeBarrierAsync(BarrierKind.BeginTick);
            return await FinalizeBarrierAsync(BarrierKind.CommitResults);
        }

        private async Task<BarrierStamp> FinalizeBarrierAsync(BarrierKind kind)
        {
            var publication = World.Publication;
            var entry = publication.Committed.Read();
            var request = new BarrierRequest(entry.Stamp, 1, kind);
            Assert.AreEqual(BorrowDrainStatus.Drained,
                await publication.Borrow.CloseAndDrainAsync(request, World.CreateBorrowedDrainDeadline()));
            var candidates = publication.Candidates.SealCandidates(request);
            Assert.IsNotNull(candidates.Candidates);
            var prepared = publication.Preparation.TryPrepare(new BarrierPublicationDraft(candidates.Candidates, entry.Root, 0, []));
            Assert.IsNotNull(prepared.Publication);
            var outcome = publication.Finalization.TryFinalize(prepared.Publication);
            Assert.AreEqual(BarrierStatus.Unchanged, outcome.Status);
            Assert.IsNotNull(outcome.Stamp);
            return outcome.Stamp.GetValueOrDefault();
        }

        internal FaultReportStatus Report(BarrierStamp stamp, FaultClass classification)
        {
            var context = new FaultContext(stamp.World, stamp.Tick, TickPhase.Base, null, null, null);
            var observation = new FaultObservation(classification, FaultSeverity.Error, FaultSubsystem.Jobs,
                FaultOrigin.Primary, 1, TimeProvider.System.GetTimestamp(), context, [], []);
            return World.Reporter.TryReport(observation);
        }

        internal async Task<SupervisorDecision> ReviewAsync(BarrierStamp stamp)
        {
            var proof = World.Writer.TryIssue(stamp, World.State.Read().PendingFaultGeneration);
            Assert.IsNotNull(proof);
            var request = World.Coordinator.TryBegin(proof);
            Assert.IsNotNull(request);
            return await World.Supervisor.DecideAsync(request, CancellationToken.None);
        }

        internal ObserverPublication PublishObserver(BarrierStamp stamp) => World.Publication.Observer.TryPublish(
            new ObserverPublicationDraft(stamp, World.State.Read().PendingFaultGeneration, new Observer(stamp.World)));

        public void Dispose()
        {
            _runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            Assert.AreEqual(ShutdownSettlement.Released, _runtime.SettleStop());
        }
    }

    private sealed record Root(WorldId World) : IWorldStateRoot;

    private sealed record Observer(WorldId World) : IWorldObserverState;

    private sealed class ControlledClock : TimeProvider
    {
        internal bool RejectSampling { get; set; }
        internal long Timestamp { get; set; }
        public override long GetTimestamp() => RejectSampling ? throw new AssertFailedException("The supervisor sampled the injected clock.") : Timestamp;
    }

    private sealed class NoJobScopes : IJobScopePolicy
    {
        public JobScopeAdmission ValidateAdmission(JobSubmission submission, in CommittedWorldView world) =>
            new(JobScopeStatus.InvalidAccess, null);

        public bool CanDispatch(IAdmittedJobScope scope, in CommittedWorldView world) => false;

        public JobScopeOverlap EvaluateOverlap(IAdmittedJobScope first, IAdmittedJobScope second, in CommittedWorldView world) =>
            JobScopeOverlap.Unknown;
    }
}
