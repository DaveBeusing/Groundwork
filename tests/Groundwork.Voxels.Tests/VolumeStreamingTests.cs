using Groundwork.Core;
using Groundwork.Core.Tests;
using Groundwork.Jobs;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class VolumeStreamingTests
{
    [TestMethod]
    public async Task ProtectedPathsProgressUnderFullSpeculationWhileOldGeometryIsRetained()
    {
        var measure = Environment.GetEnvironmentVariable("GROUNDWORK_Q01_MEASURE") == "1";
        var phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using var run = new Run();
        using var initial = run.Stage(VolumeWorkClass.Movement, 0, 1);
        Assert.IsTrue(initial.Execute());
        await run.Publish(initial);
        using var old = run.Coordinator.TryRead();
        Assert.IsNotNull(old);
        Assert.IsGreaterThan(0, old.Meshes.ToArray().Sum(m => m.Triangles.Length));
        using var oldFrame = await run.Open(initial.Generation);

        Sample("baseline");
        using var speculation = run.Stage(VolumeWorkClass.Speculative, 1, 1);
        Assert.AreEqual(VolumeAdmission.Backpressure, run.TryStage(VolumeWorkClass.Speculative, 1, 1, out _));
        Sample("speculative-full");
        using var edit = run.Stage(VolumeWorkClass.FirstEdit, 1, 2);
        Assert.IsTrue(edit.Execute());
        await run.Publish(edit);
        using var editFrame = run.Coordinator.TryRegisterFrame(edit.Generation, run.Coordinator.View, 2);
        Assert.IsNotNull(editFrame);
        Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);
        Assert.IsFalse(run.Coordinator.HasAdmittedCoverage(default));

        Sample("first-edit-retained");
        using var recovery = run.Stage(VolumeWorkClass.Recovery, 2, 2);
        Assert.IsTrue(recovery.Execute());
        await run.Publish(recovery);
        using var currentFrame = await run.Open(recovery.Generation, frame: 3);
        using var current = run.Coordinator.TryRead();
        Assert.IsNotNull(current);
        Assert.AreEqual(2UL, current.Info.TerrainRevision);
        Assert.AreEqual(1UL, old.Info.TerrainRevision);
        Assert.IsTrue(run.Coordinator.HasAdmittedCoverage(default));
        Assert.IsFalse(run.Coordinator.HasAdmittedCoverage(new PlanetVector(10000, 0, 0)));
        Assert.IsTrue(speculation.IsCancellationRequested);
        Assert.AreEqual(1, run.Coordinator.Statistics.Used.DensityJobs);
        Assert.IsTrue(run.Coordinator.Statistics.Peak.Fits(run.Limits.Total));

        Sample("recovery-retained");
        var retained = run.Coordinator.Statistics.Used.CpuBytes;
        oldFrame.Dispose();
        Assert.AreEqual(retained, run.Coordinator.Statistics.Used.CpuBytes);
        old.Dispose();
        Assert.IsLessThan(retained, run.Coordinator.Statistics.Used.CpuBytes);
        editFrame.Dispose();
        speculation.Dispose();
        Assert.AreEqual(0, run.Coordinator.Statistics.Used.DensityJobs);
        Sample("settled");

        static object ResourceVector(VolumeResources resources) => new
        {
            resources.CpuBytes, resources.NativeBytes, resources.UploadBytes, resources.RetirementBytes,
            resources.DensityJobs, resources.MeshJobs, resources.QueueSlots, resources.LeaseSlots, resources.RetirementSlots,
        };
        void Sample(string phase)
        {
            if (!measure)
                return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var stats = run.Coordinator.Statistics;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Console.WriteLine("Q01 " + System.Text.Json.JsonSerializer.Serialize(new
            {
                route = "coordinator",
                phase,
                elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart, now).TotalMilliseconds,
                used = ResourceVector(stats.Used),
                peak = ResourceVector(stats.Peak),
                stats.Readers,
                stats.Generations,
                stats.MovementHeld,
                process.WorkingSet64,
                process.PrivateMemorySize64,
            }));
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    [TestMethod]
    public async Task JobsDispatchPreparesPrivatelyAndCoreCommitsOneCompleteRoot()
    {
        using var run = new Run();
        using var work = run.Stage(VolumeWorkClass.Movement, 0, 1);
        var begin = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(run.World, 1, BarrierKind.BeginTick);
        var opened = run.World.Publication.Phases.TryOpen(begin, TickPhase.Base);
        Assert.IsNotNull(opened.Phase);
        var handler = new JobHandlerId(83);
        var jobs = new JobScheduler(begin.World, new JobSchedulerLimits(2, 1),
            run.World.Publication.Execution, new NoInput(), run.World.Ownership, run.World.Reporter, run.Fixture.Clock,
            new Dictionary<JobHandlerId, JobHandler>
            {
                [handler] = (_, token) =>
                {
                    Assert.IsTrue(work.Execute(token));
                    return ValueTask.CompletedTask;
                }
            });
        var admission = jobs.TrySubmit(new JobSubmission(
            new SourceKey(begin.World, 1, TickPhase.Base, 83, 0, 1), handler, JobInputKind.None,
            JobResultKind.Commandless, [], [], [], [], [], default), opened.Phase);
        var handle = admission.Handle as IJobExecutionHandle;
        Assert.IsNotNull(handle);
        using (handle)
        {
            await jobs.DispatchReadyAsync(opened.Phase, CancellationToken.None);
            Assert.AreEqual(JobExecutionStatus.Succeeded,
                (await handle.ExecutionCompletion.WaitAsync(TimeSpan.FromSeconds(15))).Status);
        }
        Assert.IsFalse(run.World.Publication.Committed.Read().Root is VolumeWorldRoot);
        run.World.Publication.Phases.Close(opened.Phase);
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, 1, BarrierKind.CommitResults);
        var prepared = run.Coordinator.TryPreparePublication(work.Generation, candidates, []);
        Assert.IsNotNull(prepared);
        Assert.IsNull(run.Coordinator.TryRead());
        Assert.AreEqual(BarrierStatus.Committed, run.World.Publication.Finalization.TryFinalize(prepared).Status);
        using var reader = run.Coordinator.TryRead();
        Assert.IsNotNull(reader);
        Assert.IsTrue(reader.Meshes.ToArray().All(m => m.PlanIdentity == reader.Info.PlanIdentity));
        Assert.IsTrue(reader.Meshes.ToArray().All(m => m.TerrainRevision == reader.Info.TerrainRevision));
        Assert.IsTrue(reader.TryReadDensity(new PlanetLatticeSample(Descriptor.WorldId, Descriptor.PlanetId, default), out var density));
        Assert.IsLessThan(0L, density);
        Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);
    }

    [TestMethod]
    public async Task SupersededGenerationSettlesWithoutPublishingAndRebaseRejectsOldAck()
    {
        using var run = new Run();
        using var oldWork = run.Stage(VolumeWorkClass.Movement, 0, 1);
        Assert.IsTrue(oldWork.Execute());
        await run.Publish(oldWork);
        var registration = run.Coordinator.TryRegisterFrame(oldWork.Generation, run.Coordinator.View, 1);
        Assert.IsNotNull(registration);
        using (registration)
        {
            using var superseded = run.Stage(VolumeWorkClass.Recovery, 1, 1);
            var nextFrame = PlanetLocalFrame.Create(new PlanetVector(24, 0, 0));
            Assert.IsTrue(nextFrame.TryRebase(new PlanetVector(48, 0, 0), out nextFrame));
            run.Coordinator.ChangeView(nextFrame, 2);
            Assert.IsFalse(run.Coordinator.AcknowledgeFrame(registration));
            Assert.IsTrue(superseded.IsCancellationRequested);
            Assert.IsFalse(superseded.Execute());
            Assert.AreEqual(0, run.Coordinator.Statistics.Used.DensityJobs);
            Assert.AreEqual(oldWork.Generation, ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Terrain.Generation);
            Assert.IsFalse(run.Coordinator.HasAdmittedCoverage(default));
            Assert.IsTrue(run.Coordinator.ConfirmMask(run.Coordinator.View));
            using var replacement = run.Stage(VolumeWorkClass.Recovery, 1, 1);
            Assert.IsTrue(replacement.Execute());
            await run.Publish(replacement);
            using var frame = await run.Open(replacement.Generation, frame: 2);
            Assert.IsTrue(run.Coordinator.HasAdmittedCoverage(default));
        }
    }

    [TestMethod]
    public async Task CancellationDuringGenerationKeepsCreditsUntilWorkerSettles()
    {
        using var run = new Run();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var source = new Source(() =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException();
        });
        using var work = run.Stage(VolumeWorkClass.Movement, 0, 1, source);
        var task = Task.Run(() => work.Execute());
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(15)));
            run.Coordinator.ChangeView(PlanetLocalFrame.Create(default), 2);
            Assert.AreEqual(run.Unit, run.Coordinator.Statistics.Used);
        }
        finally
        {
            resume.Set();
        }
        Assert.IsFalse(await task);
        Assert.AreEqual(default, run.Coordinator.Statistics.Used);
        Assert.IsNull(run.Coordinator.TryRead());
    }

    [TestMethod]
    public void EveryResourceDimensionRejectsMinusOneAndAdmitsExactAndPlusOne()
    {
        foreach (var dimension in Enumerable.Range(0, 9))
        foreach (var delta in new[] { -1, 0, 1 })
        {
            using var run = new Run(adjust: (dimension, delta));
            var status = run.TryStage(VolumeWorkClass.Movement, 0, 1, out var work);
            Assert.AreEqual(delta < 0 ? VolumeAdmission.Backpressure : VolumeAdmission.Accepted, status);
            work?.Dispose();
            Assert.AreEqual(default, run.Coordinator.Statistics.Used);
            Assert.IsTrue(run.Coordinator.Statistics.Peak.Fits(run.Limits.Total));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FullJournalRetainsTerminalEvidenceAndCoreSwapOutcome(bool swapFirst)
    {
        using var run = new Run(journal: 5);
        using var work = run.Stage(VolumeWorkClass.Movement, 0, 1);
        Assert.IsTrue(work.Execute());
        await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(run.World, 1, BarrierKind.BeginTick);
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, 1, BarrierKind.CommitResults);
        var prepared = run.Coordinator.TryPreparePublication(work.Generation, candidates, []);
        Assert.IsNotNull(prepared);
        if (swapFirst)
            Assert.AreEqual(BarrierStatus.Committed, run.World.Publication.Finalization.TryFinalize(prepared).Status);
        run.Coordinator.ChangeView(PlanetLocalFrame.Create(default), 2);
        if (!swapFirst)
            Assert.AreEqual(BarrierStatus.Unchanged, run.World.Publication.Finalization.TryFinalize(prepared).Status);

        Assert.IsTrue(run.Coordinator.Statistics.Stopped);
        Assert.IsNotNull(run.Coordinator.TerminalEvidence);
        var records = new List<VolumeStreamingEvent>();
        while (run.Coordinator.TryReadEvent(out var record))
            records.Add(record);
        Assert.HasCount(5, records);
        Assert.AreEqual(1UL, records[^1].TerrainRevision);
        Assert.AreEqual(swapFirst ? VolumeEventKind.Commit : VolumeEventKind.Rejected, records[^1].Kind);
        if (swapFirst)
        {
            Assert.AreEqual(run.World.Publication.Committed.Read().Stamp, records[^1].Stamp);
            var root = run.World.Publication.Committed.Read().Root as VolumeWorldRoot;
            Assert.IsNotNull(root);
            Assert.AreEqual(work.Generation, root.Terrain.Generation);
            Assert.IsFalse(string.IsNullOrEmpty(root.Terrain.PlanIdentity));
        }
        else
            Assert.IsFalse(run.World.Publication.Committed.Read().Root is VolumeWorldRoot);
        Assert.AreEqual(default, run.Coordinator.Statistics.Used);
    }

    [TestMethod]
    public void SelectionRetainsSupportHaloAcrossNegativeBrickBoundariesAndTurns()
    {
        var first = VolumeStreamingSelection.Select(Descriptor, new PlanetVector(-1, -1, -1), new PlanetVector(1, 0, 0));
        var second = VolumeStreamingSelection.Select(Descriptor, new PlanetVector(0, 0, 0), new PlanetVector(-1, 0, 0), previous: first);
        Assert.AreEqual(first.Minimum, second.Minimum);
        Assert.IsTrue(second.Requests.ToArray().Any(r => r.Stride == 2));
        foreach (var request in second.Requests)
        {
            var p = request.Brick.Vertex(0, 0, 0);
            if (p.X <= 8 && p.X + 32 >= -8 && p.Y <= 8 && p.Y + 32 >= -8 && p.Z <= 8 && p.Z + 32 >= -8)
                Assert.AreEqual(1, request.Stride);
        }
        var moved = VolumeStreamingSelection.Select(Descriptor, new PlanetVector(100, 0, 0), new PlanetVector(1, 0, 0), previous: second);
        Assert.AreNotEqual(second.Minimum, moved.Minimum);
    }

    [TestMethod]
    public async Task ProtectedDispatcherRunsWhileSpeculativeWorkerAndQueueRemainBlocked()
    {
        using var run = new Run();
        using var work = run.Stage(VolumeWorkClass.Movement, 0, 1);
        var begin = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(run.World, 1, BarrierKind.BeginTick);
        var phase = run.World.Publication.Phases.TryOpen(begin, TickPhase.Base).Phase;
        Assert.IsNotNull(phase);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speculative = new JobHandlerId(1);
        var protectedHandler = new JobHandlerId(2);
        var scheduler = new JobScheduler(begin.World, new JobSchedulerLimits(2, 2),
            run.World.Publication.Execution, new NoInput(), run.World.Ownership, run.World.Reporter, run.Fixture.Clock,
            new Dictionary<JobHandlerId, JobHandler>
            {
                [speculative] = async (_, token) =>
                {
                    entered.TrySetResult();
                    await blocked.Task.WaitAsync(token);
                },
                [protectedHandler] = (_, token) =>
                {
                    Assert.IsTrue(work.Execute(token));
                    return ValueTask.CompletedTask;
                }
            }, new JobDispatchReservation(1, 1, [protectedHandler]));
        try
        {
            using var first = Submit(speculative, 1);
            await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var queued = Submit(speculative, 2);
            Assert.AreEqual(JobAdmissionStatus.CapacityExceeded, scheduler.TrySubmit(Submission(speculative, 3), phase).Status);
            var protectedStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using var progress = Submit(protectedHandler, 4);
            await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
            Assert.AreEqual(JobExecutionStatus.Succeeded,
                (await progress.ExecutionCompletion.WaitAsync(TimeSpan.FromSeconds(15))).Status);
            if (Environment.GetEnvironmentVariable("GROUNDWORK_Q01_MEASURE") == "1")
                Console.WriteLine("Q01 " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    route = "scheduler",
                    phase = "protected-while-speculation-full",
                    elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(protectedStart).TotalMilliseconds,
                    blockedSpeculative = 1,
                    queuedSpeculative = 1,
                }));
            Assert.IsFalse(first.ExecutionCompletion.IsCompleted);
            Assert.IsFalse(queued.ExecutionCompletion.IsCompleted);
            Assert.IsNull(run.Coordinator.TryRead());
            blocked.TrySetResult();
            await first.ExecutionCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
            await queued.ExecutionCompletion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            blocked.TrySetResult();
            run.World.Publication.Phases.Close(phase);
            await scheduler.StopAsync(run.World.CreateBorrowedDrainDeadline());
        }

        JobSubmission Submission(JobHandlerId handler, ulong ordinal) => new(
            new SourceKey(begin.World, 1, TickPhase.Base, 83, 0, ordinal), handler, JobInputKind.None,
            JobResultKind.Commandless, [], [], [], [], [], default);

        IJobExecutionHandle Submit(JobHandlerId handler, ulong ordinal)
        {
            var admission = scheduler.TrySubmit(Submission(handler, ordinal), phase);
            Assert.AreEqual(JobAdmissionStatus.Accepted, admission.Status);
            var handle = admission.Handle as IJobExecutionHandle;
            Assert.IsNotNull(handle);
            return handle;
        }
    }
    [TestMethod]
    public async Task RandomCompletionRejectsOlderArtifactsAndUnrelatedTicksPreserveTerrain()
    {
        using var run = new Run();
        using var movement = run.Stage(VolumeWorkClass.Movement, 0, 1);
        using var newer = run.Stage(VolumeWorkClass.Recovery, 0, 2);
        var order = new[] { movement, newer };
        new Random(83).Shuffle(order);
        foreach (var work in order)
            Assert.IsTrue(work.Execute());
        await run.Publish(newer);
        Assert.IsTrue(movement.IsCancellationRequested);
        Assert.IsFalse(movement.Execute());
        using var rootReader = run.Coordinator.TryRead();
        Assert.IsNotNull(rootReader);
        Assert.AreEqual(2UL, rootReader.Info.TerrainRevision);
        var root = run.World.Publication.Committed.Read().Root as VolumeWorldRoot;
        Assert.IsNotNull(root);
        var begin = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(run.World, 2, BarrierKind.BeginTick);
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, 2, BarrierKind.CommitResults);
        Assert.IsNull(run.Coordinator.TryPreparePublication(movement.Generation, candidates, []));
        var unchanged = CoreRuntimeFixture.Prepare(run.World, candidates, []);
        Assert.AreEqual(BarrierStatus.Unchanged, run.World.Publication.Finalization.TryFinalize(unchanged).Status);
        Assert.AreSame(root, run.World.Publication.Committed.Read().Root);
        Assert.AreEqual(begin.Tick, run.World.Publication.Committed.Read().Stamp.Tick);
    }

    [TestMethod]
    public async Task CoreStopRetainsPublicationCustodyUntilLastReaderReleases()
    {
        using var run = new Run();
        using var work = run.Stage(VolumeWorkClass.Movement, 0, 1);
        Assert.IsTrue(work.Execute());
        await run.Publish(work);
        using var reader = run.Coordinator.TryRead();
        Assert.IsNotNull(reader);
        run.Fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        Assert.IsTrue(run.Coordinator.Statistics.Stopped);
        Assert.IsFalse(run.Coordinator.HasAdmittedCoverage(default));
        Assert.IsGreaterThan(0L, run.Coordinator.Statistics.Used.CpuBytes);
        Assert.IsGreaterThan(0, reader.Meshes.Length);
        Assert.AreEqual(ShutdownSettlement.Pending, run.Fixture.Runtime.SettleStop());
        reader.Dispose();
        Assert.AreEqual(default, run.Coordinator.Statistics.Used);
        Assert.AreEqual(ShutdownSettlement.Released, run.Fixture.Runtime.SettleStop());
    }
    [TestMethod]
    public async Task FreshRuntimeRebuildsTheSameRevisionAndRejectsPriorFrameEvidence()
    {
        using var first = new Run();
        using var work = first.Stage(VolumeWorkClass.Movement, 0, 5);
        Assert.IsTrue(work.Execute());
        await first.Publish(work);
        using var reader = first.Coordinator.TryRead();
        Assert.IsNotNull(reader);
        using var oldFrame = first.Coordinator.TryRegisterFrame(work.Generation, first.Coordinator.View, 1);
        Assert.IsNotNull(oldFrame);
        first.Coordinator.Stop();
        Assert.IsGreaterThan(0, reader.Meshes.Length);

        using var restored = new Run();
        using var reload = restored.Stage(VolumeWorkClass.Recovery, 0, 5);
        Assert.IsTrue(reload.Execute());
        await restored.Publish(reload);
        using var rebuilt = restored.Coordinator.TryRead();
        Assert.IsNotNull(rebuilt);
        Assert.AreEqual(reader.Info.PlanIdentity, rebuilt.Info.PlanIdentity);
        Assert.IsFalse(restored.Coordinator.AcknowledgeFrame(oldFrame));
        Assert.IsTrue(restored.Coordinator.Statistics.MovementHeld);
    }

    [TestMethod]
    public void OversizedCreditRequestsReturnBackpressureWithoutArithmeticWrap()
    {
        using var run = new Run();
        using var queued = run.Stage(VolumeWorkClass.Speculative, 0, 1);
        var request = run.Unit with { NativeBytes = long.MaxValue };
        Assert.AreEqual(VolumeAdmission.Backpressure, run.Coordinator.TryStage(VolumeWorkClass.Speculative,
            run.Selection, 0, 1, 2_000_000, request, new Source(), out var rejected));
        Assert.IsNull(rejected);
        Assert.AreEqual(run.Unit, run.Coordinator.Statistics.Used);
    }
    [TestMethod]
    public async Task AReplacementCannotReuseTheActiveViewAndMask()
    {
        using var run = new Run();
        using var initial = run.Stage(VolumeWorkClass.Movement, 0, 1);
        Assert.IsTrue(initial.Execute());
        await run.Publish(initial);
        using var frame = await run.Open(initial.Generation);
        Assert.AreEqual(VolumeAdmission.Accepted, run.TryStage(VolumeWorkClass.Recovery, 1, 1, out var replacement));
        Assert.IsNotNull(replacement);
        using (replacement)
        {
            Assert.IsTrue(replacement.Execute());
            Assert.IsFalse(run.Coordinator.ConfirmMask(run.Coordinator.View));
            await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(run.World, 3, BarrierKind.BeginTick);
            var candidates = await CoreRuntimeFixture.SealAsync(run.World, 3, BarrierKind.CommitResults);
            Assert.IsNull(run.Coordinator.TryPreparePublication(replacement.Generation, candidates, []));
            var unchanged = CoreRuntimeFixture.Prepare(run.World, candidates, []);
            Assert.AreEqual(BarrierStatus.Unchanged, run.World.Publication.Finalization.TryFinalize(unchanged).Status);
            Assert.AreEqual(initial.Generation, ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Terrain.Generation);
        }
    }
    private static readonly PlanetDescriptor Descriptor = new(
        new Guid("00112233-4455-6677-8899-aabbccddeeff"), new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"),
        42, 1, 1, 1, new string('a', 64), 128);

    private sealed class Source(Action? first = null) : IPlanetBrickSource
    {
        private int _started;
        public PlanetDescriptor Descriptor => VolumeStreamingTests.Descriptor;
        public long SampleDensity(LatticeCoordinate p)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
                first?.Invoke();
            return 10 * (p.X * p.X + p.Y * p.Y + p.Z * p.Z) - 297;
        }
        public ushort SampleMaterial(LatticeCoordinate cell) => 7;
    }

    private sealed class NoInput : IJobInputSource
    {
        public JobInputCapture TryCapture(in JobInputRequest request) => throw new AssertFailedException("No capture requested.");
    }

    private sealed class StreamingClock : CoreRuntimeFixture.TestClock { }

    private sealed class Run : IDisposable
    {
        private ulong _tick;
        public Run(int journal = 1024, (int Dimension, int Delta)? adjust = null)
        {
            World = Fixture.OpenWorld();
            Selection = VolumeStreamingSelection.Select(Descriptor, default, new PlanetVector(1, 0, 0));
            Unit = new VolumeResources(VolumeStreamingCoordinator.RequiredCpuBytes(Selection, 2_000_000), 100, 20, 100, 1, 1, 1, 64, 1);
            var baseline = Unit.Retained + Unit.Retained + Unit.Retained;
            baseline = baseline with { LeaseSlots = baseline.LeaseSlots + 16 };
            var movement = adjust is { } change ? Adjust(Unit, change.Dimension, change.Delta) : Unit;
            Limits = new VolumeStreamingLimits(baseline + movement + Unit + Unit + Unit, baseline,
                movement, Unit, Unit, Unit, 16, 16, journal);
            Coordinator = new VolumeStreamingCoordinator(World, Descriptor, Limits);
            Coordinator.ChangeView(PlanetLocalFrame.Create(default), 1);
            Assert.IsTrue(Coordinator.ConfirmMask(Coordinator.View));
        }
        public CoreRuntimeFixture Fixture { get; } = new(new StreamingClock());
        public WorldSafetyPorts World { get; }
        public VolumeStreamingSelection Selection { get; }
        public VolumeResources Unit { get; }
        public VolumeStreamingLimits Limits { get; }
        public VolumeStreamingCoordinator Coordinator { get; }

        public VolumeAdmission TryStage(VolumeWorkClass kind, ulong source, ulong target, out VolumeStreamingWork? work, IPlanetBrickSource? sampler = null) =>
            Coordinator.TryStage(kind, Selection, source, target, 2_000_000, Unit, sampler ?? new Source(), out work);

        public VolumeStreamingWork Stage(VolumeWorkClass kind, ulong source, ulong target, IPlanetBrickSource? sampler = null)
        {
            if (kind != VolumeWorkClass.Speculative &&
                World.Publication.Committed.Read().Root is VolumeWorldRoot root &&
                root.Terrain.View == Coordinator.View)
            {
                Coordinator.ChangeView(Coordinator.View.Frame, Coordinator.View.RendererEpoch);
                Assert.IsTrue(Coordinator.ConfirmMask(Coordinator.View));
            }
            Assert.AreEqual(VolumeAdmission.Accepted, TryStage(kind, source, target, out var work, sampler));
            Assert.IsNotNull(work);
            return work;
        }

        public async Task Publish(VolumeStreamingWork work)
        {
            await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, ++_tick, BarrierKind.BeginTick);
            var candidates = await CoreRuntimeFixture.SealAsync(World, _tick, BarrierKind.CommitResults);
            var prepared = Coordinator.TryPreparePublication(work.Generation, candidates, []);
            Assert.IsNotNull(prepared);
            var outcome = World.Publication.Finalization.TryFinalize(prepared);
            Assert.AreEqual(BarrierStatus.Committed, outcome.Status);
            CoreRuntimeFixture.PublishObserver(World, outcome.Stamp!.Value, (int)_tick);
        }

        public async Task<VolumeFrameRegistration> Open(ulong generation, ulong frame = 1)
        {
            var registration = Coordinator.TryRegisterFrame(generation, Coordinator.View, frame);
            Assert.IsNotNull(registration);
            Assert.IsTrue(Coordinator.AcknowledgeFrame(registration));
            var begin = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, ++_tick, BarrierKind.BeginTick);
            Assert.IsTrue(Coordinator.BeginTick(begin));
            var committed = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, _tick, BarrierKind.CommitResults);
            CoreRuntimeFixture.PublishObserver(World, committed, (int)_tick);
            return registration;
        }

        public void Dispose()
        {
            Coordinator.Stop();
            Fixture.Dispose();
        }

        private static VolumeResources Adjust(VolumeResources value, int dimension, int delta) => dimension switch
        {
            0 => value with { CpuBytes = value.CpuBytes + delta },
            1 => value with { NativeBytes = value.NativeBytes + delta },
            2 => value with { UploadBytes = value.UploadBytes + delta },
            3 => value with { RetirementBytes = value.RetirementBytes + delta },
            4 => value with { DensityJobs = value.DensityJobs + delta },
            5 => value with { MeshJobs = value.MeshJobs + delta },
            6 => value with { QueueSlots = value.QueueSlots + delta },
            7 => value with { LeaseSlots = value.LeaseSlots + delta },
            _ => value with { RetirementSlots = value.RetirementSlots + delta }
        };
    }
}
