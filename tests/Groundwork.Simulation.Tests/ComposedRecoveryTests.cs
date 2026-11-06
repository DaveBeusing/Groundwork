using Groundwork.Core;
using Groundwork.Diagnostics;
using Groundwork.Ecs;
using Groundwork.Jobs;

namespace Groundwork.Simulation.Tests;

[TestClass]
public sealed class ComposedRecoveryTests
{
    [TestMethod]
    public async Task BetweenIterationsRecoveryRequiresFreshDecisionAndResumesWithoutReplay()
    {
        await using var run = new RecoveryRun();
        await run.StartAsync();
        run.Clock.Timestamp = 3000;
        var beforePause = await run.AdvanceAsync();
        var committed = run.Committed;
        Assert.AreEqual(2UL, beforePause.CompletedTick);
        Assert.AreEqual(new BarrierStamp(run.World.Id, 2, BarrierKind.CommitResults, 4, 1), committed);
        Assert.AreEqual(committed, run.Driver.LastConfirmedBarrier);
        using var priorObserver = run.AcquireObserver();
        Assert.AreEqual(committed, priorObserver.Current.Stamp);
        Assert.AreEqual(new Marker(12), ReadMarker(priorObserver.Current));

        var pause = await run.PauseAfterWorkerFaultAsync();
        Assert.AreEqual(SupervisorAction.PauseForRecovery, pause.Action);
        var paused = await run.AdvanceAsync();
        Assert.AreEqual(SimulationState.Paused, paused.State);
        Assert.IsNull(paused.NextWakeTimestamp);
        Assert.AreEqual(0, paused.AppliedSteps);
        Assert.AreEqual(beforePause.RemainingScaledTime, paused.RemainingScaledTime);

        run.Driver.RequestPause();
        run.Driver.RequestResume();
        Assert.AreEqual(DecisionApplication.Rejected, run.Safety.Coordinator.TryApply(pause));
        run.Clock.Timestamp += 60000;
        var stillPaused = await run.AdvanceAsync();
        Assert.AreEqual(SimulationState.Paused, stillPaused.State);
        Assert.AreEqual(Int128.Zero, stillPaused.ElapsedScaledTime);
        Assert.AreEqual(2UL, stillPaused.CompletedTick);
        Assert.AreEqual(2UL, run.InputCount);
        Assert.AreEqual(committed, run.Committed);
        using (var held = run.AcquireObserver())
            Assert.AreEqual(priorObserver.Current, held.Current);

        var recovery = await run.AuthorizeRecoveryAsync(pause);
        Assert.AreEqual(SupervisorAction.ContinueAtSafeBarrier, recovery.Action);
        Assert.AreNotEqual(pause.Request.RequestId, recovery.Request.RequestId);
        Assert.AreEqual(DecisionApplication.Continued, run.Safety.Coordinator.TryApply(recovery));
        Assert.AreEqual(0, run.Safety.State.Read().PendingRequiredEvidenceRecords);

        var resumed = await run.AdvanceAsync();
        Assert.AreEqual(SimulationState.Running, resumed.State);
        Assert.AreEqual(3UL, resumed.CompletedTick);
        Assert.AreEqual(1, resumed.AppliedSteps);
        Assert.AreEqual(Int128.Zero, resumed.ElapsedScaledTime);
        Assert.AreEqual(Int128.Zero, resumed.RemainingScaledTime);
        Assert.AreEqual(3UL, run.InputCount);
        Assert.AreEqual(committed with { Tick = 3, Epoch = 6 }, run.Driver.LastConfirmedBarrier);
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, run.PublishedTicks);
        using var observer = run.AcquireObserver();
        Assert.AreEqual(run.Driver.LastConfirmedBarrier, observer.Current.Stamp);
        Assert.AreEqual(priorObserver.Current, observer.Previous);
        Assert.AreEqual(new Marker(12), ReadMarker(observer.Current));
    }

    [TestMethod]
    public async Task PauseEscapingActiveCommitFencesNewObserverAndStopsRuntime()
    {
        await using var run = new RecoveryRun(pauseInsideCommit: true);
        await run.StartAsync();
        using var priorObserver = run.AcquireObserver();
        Assert.AreEqual(1UL, priorObserver.Current.Stamp.Tick);

        run.Clock.Timestamp = 2000;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.AdvanceAsync().AsTask());
        var committed = run.Committed;
        Assert.AreEqual(new BarrierStamp(run.World.Id, 2, BarrierKind.CommitResults, 4, 1), committed);
        Assert.AreEqual(committed, run.Driver.LastConfirmedBarrier);
        Assert.IsTrue(run.Safety.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
        Assert.AreEqual(ObserverReadStatus.Closed, run.Safety.Publication.ObserverSnapshots.TryAcquire().Status);
        Assert.AreEqual(1UL, priorObserver.Current.Stamp.Tick);
        Assert.IsEmpty(((EcsObserverState)priorObserver.Current.State).Query<Marker>());

        run.Driver.RequestResume();
        var stopped = await run.AdvanceAsync();
        Assert.AreEqual(SimulationState.Stopped, stopped.State);
        Assert.AreEqual(1UL, stopped.CompletedTick);
        Assert.AreEqual(0, stopped.AppliedSteps);
        Assert.AreEqual(committed, run.Committed);
        CollectionAssert.AreEqual(new ulong[] { 1 }, run.PublishedTicks);
    }

    private static Marker ReadMarker(ObserverFrame observer) =>
        ((EcsObserverState)observer.State).Query<Marker>().Single().Value;

    private readonly record struct Marker(int Value);

    private sealed class RecoveryRun : ITickPipeline, IAsyncDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private static readonly ComponentTypeId MarkerType = new(1);
        private static readonly JobHandlerId SeedHandler = new(1);
        private static readonly JobHandlerId FaultHandler = new(2);
        private static readonly ResourceQuota Quota = new(1, 32,
            new JobQuota(8, 2, 8, 8, 4, 8, 16, 65536, 65536, 65536),
            new DiagnosticQuota(8, 65536, 24, 65536, 16, 4, 64, 1024));

        private readonly RuntimeSafetyAuthority _runtime;
        private readonly EcsStorageBudget _storage;
        private readonly JobScheduler _jobs;
        private readonly RecoveryPolicy _policy = new();
        private readonly TaskCompletionSource _failWorker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly InvalidOperationException _injectedFault = new("Injected worker fault.");
        private readonly SimulationFixture.CountingInput _input = new();
        private readonly bool _pauseInsideCommit;
        private IJobExecutionHandle? _faultingJob;

        internal RecoveryRun(bool pauseInsideCommit = false)
        {
            _pauseInsideCommit = pauseInsideCommit;
            var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, Quota));
            using var reservation = budget.TryReserveRuntime(Quota);
            Assert.IsNotNull(reservation);
            var timing = new RuntimeTimingLimits(Timeout, Timeout, Timeout);
            _runtime = RuntimeSafetyAuthority.Create(Clock, timing, reservation);
            const long retainedBytes = 32 * 1048576;
            _storage = new EcsStorageBudget(retainedBytes);
            var registry = new ComponentRegistryBuilder().Register<Marker>(MarkerType).Build();
            World = EcsWorld.Open(_runtime,
                Quota with { Diagnostics = Quota.Diagnostics with { EmergencyRecords = 21, EmergencyBytes = 61440 } },
                _policy, registry, new EcsLimits(16, 1, 1, 1048576, retainedBytes), _storage);
            _jobs = new JobScheduler(World.Id, new(4, 2), Ports.Execution, World.Inputs, Safety.Ownership,
                Safety.Reporter, Clock, new Dictionary<JobHandlerId, JobHandler>
                {
                    [SeedHandler] = (context, _) =>
                    {
                        var entity = new CommandEntity(CommandEntityKind.Provisional, null, 1);
                        Assert.AreEqual(BatchAppendResult.Accepted, context.Results.TryAppend(
                            new JobCommand(1, 1, JobCommandKind.CreateEntity, entity, default, null, null, [])));
                        Assert.AreEqual(BatchAppendResult.Accepted, context.Results.TryAppend(
                            new JobCommand(1, 2, JobCommandKind.AddComponent, entity, MarkerType, null, null, registry.Encode(new Marker(12)))));
                        return ValueTask.CompletedTask;
                    },
                    [FaultHandler] = async (_, cancellation) =>
                    {
                        await _failWorker.Task.WaitAsync(Timeout, cancellation);
                        throw _injectedFault;
                    },
                });
            Driver = new FixedStepSimulation(new EngineModuleContext(Clock, timing, _runtime.Reporter, _runtime.StopControl),
                _input, this, Safety.State, _runtime.SimulationWake, SimulationOptions.Default with { MaxStepsPerIteration = 1 });
        }

        internal SimulationFixture.SyntheticClock Clock { get; } = new(60000);
        internal FixedStepSimulation Driver { get; }
        internal EcsWorld World { get; }
        internal WorldSafetyPorts Safety => World.Safety;
        internal BarrierStamp Committed => Ports.Committed.Read().Stamp;
        internal List<ulong> PublishedTicks { get; } = [];
        internal ulong InputCount => _input.Count;
        private WorldPublicationPorts Ports => Safety.Publication;

        internal ValueTask<SimulationIteration> AdvanceAsync() => Driver.AdvanceAsync(CancellationToken.None);

        internal async Task StartAsync()
        {
            await AdvanceAsync();
            Clock.Timestamp = 1000;
            Assert.AreEqual(1UL, (await AdvanceAsync()).CompletedTick);
        }

        internal IObserverSnapshotLease AcquireObserver()
        {
            var observer = Ports.ObserverSnapshots.TryAcquire();
            Assert.AreEqual(ObserverReadStatus.Acquired, observer.Status);
            Assert.IsNotNull(observer.Lease);
            return observer.Lease;
        }

        internal async Task<SupervisorDecision> PauseAfterWorkerFaultAsync()
        {
            var worker = _faultingJob;
            Assert.IsNotNull(worker);
            Assert.IsFalse(worker.ExecutionCompletion.IsCompleted);
            _failWorker.SetResult();
            var execution = await worker.ExecutionCompletion.WaitAsync(Timeout);
            Assert.AreEqual(JobExecutionStatus.Faulted, execution.Status);
            var primary = execution.Failure?.Primary;
            Assert.IsNotNull(primary);
            Assert.AreEqual(FaultClass.RecoverableJobFault, primary.Classification);
            Assert.AreSame(_injectedFault, primary.Exception.SourceException);

            var decision = await DecideAsync();
            Assert.AreEqual(DecisionApplication.Paused, Safety.Coordinator.TryApply(decision));
            return decision;
        }

        internal async Task<SupervisorDecision> AuthorizeRecoveryAsync(SupervisorDecision pause)
        {
            var worker = _faultingJob;
            Assert.IsNotNull(worker);
            Assert.AreEqual(JobCommitStatus.NotApplicable, worker.Observe().Commit);
            Assert.AreEqual(pause.Request.Proof.Stamp, Committed);
            _policy.Authorization = (Committed, pause.Request.Proof.FaultGeneration, worker.Id);
            return await DecideAsync();
        }

        private async Task<SupervisorDecision> DecideAsync()
        {
            var generation = Safety.State.Read().PendingFaultGeneration;
            var proof = Safety.Writer.TryIssue(Committed, generation);
            Assert.IsNotNull(proof);
            var review = Safety.Coordinator.TryBegin(proof);
            Assert.IsNotNull(review);
            return await Safety.Supervisor.DecideAsync(review, CancellationToken.None);
        }

        public ValueTask<BarrierOutcome> BeginTickAsync(TickContext tick, CancellationToken cancellationToken) =>
            CommitBarrierAsync(tick.Tick, BarrierKind.BeginTick, cancellationToken);

        public ValueTask RunGameplayAsync(TickContext tick, FixedStep gameplayStep, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask RunBaseAsync(TickContext tick, CancellationToken cancellationToken)
        {
            if (tick.Tick > 2)
                return;

            var opened = Ports.Phases.TryOpen(Committed, TickPhase.Base);
            Assert.AreEqual(JobPhaseOpenStatus.Opened, opened.Status);
            Assert.IsNotNull(opened.Phase);
            try
            {
                bool seed = tick.Tick == 1;
                var key = new ResourceKey(World.Id, default, new ResourceScope(ScopeKind.World, null, null));
                var submission = new JobSubmission(new SourceKey(World.Id, tick.Tick, TickPhase.Base, 1, 0, tick.Tick),
                    seed ? SeedHandler : FaultHandler, JobInputKind.None, seed ? JobResultKind.Commands : JobResultKind.Commandless,
                    [], [], seed ? [key] : [], [], [], seed ? new JobResultBudget(1, 2, 16, 0, 0) : default);
                var admission = _jobs.TrySubmit(submission, opened.Phase);
                Assert.AreEqual(JobAdmissionStatus.Accepted, admission.Status);
                var worker = (IJobExecutionHandle?)admission.Handle;
                Assert.IsNotNull(worker);
                using var seedHandle = seed ? worker : null;
                if (!seed)
                    _faultingJob = worker;
                await _jobs.DispatchReadyAsync(opened.Phase, cancellationToken);
                if (seed)
                {
                    var execution = await worker.ExecutionCompletion.WaitAsync(Timeout, cancellationToken);
                    execution.Failure?.Primary?.Exception.Throw();
                    Assert.AreEqual(JobExecutionStatus.Succeeded, execution.Status);
                }
            }
            finally
            {
                Ports.Phases.Close(opened.Phase);
            }
        }

        public async ValueTask<BarrierOutcome> CommitResultsAsync(TickContext tick, CancellationToken cancellationToken)
        {
            var outcome = await CommitBarrierAsync(tick.Tick, BarrierKind.CommitResults, cancellationToken);
            if (_pauseInsideCommit && tick.Tick == 2)
            {
                await PauseAfterWorkerFaultAsync();
                Assert.AreEqual(ObserverPublication.Blocked, World.PublishObserver(Committed, Safety.State.Read().PendingFaultGeneration));
                using var held = AcquireObserver();
                Assert.AreEqual(1UL, held.Current.Stamp.Tick);
            }
            return outcome;
        }

        public ObserverPublication TryPublishState(TickContext tick)
        {
            var result = World.PublishObserver(Committed, Safety.State.Read().PendingFaultGeneration);
            if (result == ObserverPublication.Published)
                PublishedTicks.Add(tick.Tick);
            return result;
        }

        private async ValueTask<BarrierOutcome> CommitBarrierAsync(ulong tick, BarrierKind kind, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new BarrierRequest(Committed, tick, kind);
            Assert.AreEqual(BorrowDrainStatus.Drained,
                await Ports.Borrow.CloseAndDrainAsync(request, Safety.CreateBorrowedDrainDeadline()));
            var sealedSet = Ports.Candidates.SealCandidates(request);
            Assert.AreEqual(CandidateSealStatus.Sealed, sealedSet.Status);
            Assert.IsNotNull(sealedSet.Candidates);
            using var plan = World.Stage(sealedSet.Candidates);
            Assert.IsEmpty(plan.Issues);
            Assert.AreEqual(BarrierPreparationStatus.Prepared, plan.Prepare(Safety.State.Read().PendingFaultGeneration));
            return plan.FinalizePublication();
        }

        public async ValueTask DisposeAsync()
        {
            _faultingJob?.Dispose();
            var deadline = _runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            await _jobs.StopAsync(deadline);
            Assert.AreEqual(ShutdownSettlement.Released, _runtime.SettleStop());
            Assert.IsTrue(World.TryReleaseStorage());
            Assert.AreEqual(0L, _storage.ReservedBytes);
        }
    }

    private sealed class RecoveryPolicy : IBarrierSupervisorPolicy
    {
        private readonly BarrierSupervisorPolicy _default = new(16);

        internal (BarrierStamp Stamp, ulong Generation, JobId Job)? Authorization { get; set; }

        public ValueTask<SupervisorAction> DecideAsync(BarrierReviewRequest request, IFaultEvidenceReader evidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Authorization is { } approved && request.Proof.Stamp == approved.Stamp && request.Proof.FaultGeneration == approved.Generation &&
                evidence.ReadPending(approved.Generation, 0, out var record, out var count) == FaultEvidenceReadStatus.Copied &&
                count == 1 && record.Classification == FaultClass.RecoverableJobFault && record.Subsystem == FaultSubsystem.Jobs &&
                record.Context.Job == approved.Job)
            {
                Authorization = null;
                return ValueTask.FromResult(SupervisorAction.ContinueAtSafeBarrier);
            }
            return _default.DecideAsync(request, evidence, cancellationToken);
        }
    }
}
