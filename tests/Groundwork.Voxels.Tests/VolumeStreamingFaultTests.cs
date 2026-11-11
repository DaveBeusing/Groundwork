using Groundwork.Core;
using Groundwork.Core.Tests;
using Groundwork.Jobs;

namespace Groundwork.Voxels.Tests;

public sealed partial class VolumeStreamingTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Q02GeneratorDelayAndQuotaPressurePreservePublishedTerrain(bool fail)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var run = new Run();
        using var initial = run.Stage(VolumeWorkClass.Movement, 0, 1);
        Assert.IsTrue(initial.Execute(token));
        await run.Publish(initial);
        using var visible = await run.Open(initial.Generation);
        using var old = run.Coordinator.TryRead();
        Assert.IsNotNull(old);
        var oldRoot = run.World.Publication.Committed.Read().Root;
        var oldHits = Probe(old);
        var oldCredits = run.Coordinator.Statistics.Used;

        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FaultSampler(() =>
        {
            entered.SetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("Q02 sampler was not released.");
        }, fail);
        using var delayed = run.Stage(VolumeWorkClass.Movement, 1, 2, source);
        var begin = await run.BeginTick();
        var phase = run.World.Publication.Phases.TryOpen(begin, TickPhase.Base).Phase;
        Assert.IsNotNull(phase);
        var handler = new JobHandlerId(89);
        bool? accepted = null;
        var scheduler = new JobScheduler(begin.World, new JobSchedulerLimits(1, 1),
            run.World.Publication.Execution, new NoInput(), run.World.Ownership, run.World.Reporter, run.Fixture.Clock,
            new Dictionary<JobHandlerId, JobHandler>
            {
                [handler] = (_, cancellation) =>
                {
                    accepted = delayed.Execute(cancellation);
                    return ValueTask.CompletedTask;
                },
            });
        IJobExecutionHandle? job = null;
        try
        {
            var admission = scheduler.TrySubmit(new JobSubmission(
                new SourceKey(begin.World, begin.Tick, TickPhase.Base, 89, 0, 1), handler, JobInputKind.None,
                JobResultKind.Commandless, [], [], [], [], [], default), phase);
            Assert.AreEqual(JobAdmissionStatus.Accepted, admission.Status);
            job = admission.Handle as IJobExecutionHandle;
            Assert.IsNotNull(job);
            await scheduler.DispatchReadyAsync(phase, token);
            await entered.Task.WaitAsync(token);
            Assert.IsFalse(job.ExecutionCompletion.IsCompleted);
            var pressure = run.Coordinator.Statistics;
            Assert.AreEqual(oldCredits + run.Unit, pressure.Used);
            Assert.AreEqual(VolumeAdmission.Backpressure, run.TryStage(VolumeWorkClass.Movement, 1, 3, out var denied));
            using (denied)
                Assert.IsNull(denied);
            Assert.AreEqual(pressure, run.Coordinator.Statistics);
            AssertPublished(run, oldRoot, old.Info, oldHits);
            Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);
            Assert.IsFalse(run.Coordinator.HasAdmittedCoverage(default));

            run.World.Publication.Phases.Close(phase);
            await FinishUnchanged(run, begin.Tick);
            if (fail)
            {
                resume.Set();
                var report = await job.ExecutionCompletion.WaitAsync(token);
                Assert.AreEqual(JobExecutionStatus.Faulted, report.Status);
                Assert.AreEqual(FaultClass.RecoverableJobFault, report.Failure?.Primary?.Classification);
                Assert.IsNull(accepted);
                Assert.AreEqual(PlanetBrickStore.VertexCount + 1, source.SampleCount);
                Assert.AreEqual(oldCredits, run.Coordinator.Statistics.Used);
                AssertPublished(run, oldRoot, old.Info, oldHits);
                Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);
                return;
            }

            // The invalidated sampler still owns its credits while Recovery publishes.
            run.Coordinator.ChangeView(run.Coordinator.View.Frame, 1);
            Assert.IsTrue(delayed.IsCancellationRequested);
            Assert.AreEqual(oldCredits + run.Unit, run.Coordinator.Statistics.Used);
            Assert.IsTrue(run.Coordinator.ConfirmMask(run.Coordinator.View));
            using var recovery = run.Stage(VolumeWorkClass.Recovery, 1, 3, new Source(threshold: 1097));
            Assert.IsTrue(recovery.Execute(token));
            await run.Publish(recovery);
            using var recovered = await run.Open(recovery.Generation, frame: 2);
            using var latest = run.Coordinator.TryRead();
            Assert.IsNotNull(latest);
            var latestRoot = run.World.Publication.Committed.Read().Root;
            var latestHits = Probe(latest);
            Assert.AreEqual(3UL, latest.Info.TerrainRevision);
            Assert.AreNotEqual(oldHits[0].Distance, latestHits[0].Distance);
            var beforeRelease = run.Coordinator.Statistics.Used;
            Assert.IsFalse(job.ExecutionCompletion.IsCompleted);
            resume.Set();
            Assert.AreEqual(JobExecutionStatus.Succeeded, (await job.ExecutionCompletion.WaitAsync(token)).Status);
            Assert.IsFalse(accepted);
            Assert.AreEqual(beforeRelease - run.Unit, run.Coordinator.Statistics.Used);
            Assert.AreEqual(VolumeAdmission.Stale, run.TryStage(VolumeWorkClass.Movement, 1, 4, out var stale));
            using (stale)
                Assert.IsNull(stale);
            await FinishUnchanged(run, (await run.BeginTick()).Tick, delayed.Generation);
            AssertPublished(run, latestRoot, latest.Info, latestHits);
            Assert.IsTrue(run.Coordinator.HasAdmittedCoverage(default));
            CollectionAssert.AreEqual(oldHits, Probe(old));
        }
        finally
        {
            resume.Set();
            run.World.Publication.Phases.Close(phase);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await scheduler.StopAsync(run.World.CreateBorrowedDrainDeadline(), cleanup.Token);
            }
            finally
            {
                job?.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task Q02CompletedOldMeshCannotReplaceNewerPublishedTerrain()
    {
        using var run = new Run();
        using var initial = run.Stage(VolumeWorkClass.Movement, 0, 1);
        Assert.IsTrue(initial.Execute());
        await run.Publish(initial);
        using var visible = await run.Open(initial.Generation);
        using var original = run.Coordinator.TryRead();
        Assert.IsNotNull(original);
        var oldHits = Probe(original);

        using var stale = run.Stage(VolumeWorkClass.Movement, 1, 2, new Source(threshold: 697));
        Assert.IsTrue(stale.Execute());
        Assert.IsFalse(stale.IsCancellationRequested);
        using var latest = run.Stage(VolumeWorkClass.Recovery, 1, 3, new Source(threshold: 1097));
        Assert.IsTrue(latest.Execute());
        await run.Publish(latest);
        using var presented = await run.Open(latest.Generation, frame: 2);
        using var committed = run.Coordinator.TryRead();
        Assert.IsNotNull(committed);
        var current = run.World.Publication.Committed.Read().Root;
        var hits = Probe(committed);
        Assert.AreEqual(3UL, committed.Info.TerrainRevision);
        Assert.AreNotEqual(oldHits[0].Distance, hits[0].Distance);
        Assert.IsTrue(stale.IsCancellationRequested);

        var credits = run.Coordinator.Statistics.Used;
        await FinishUnchanged(run, (await run.BeginTick()).Tick, stale.Generation);
        Assert.AreEqual(credits, run.Coordinator.Statistics.Used);
        Assert.AreEqual(0, credits.DensityJobs);
        Assert.AreEqual(0, credits.MeshJobs);
        AssertPublished(run, current, committed.Info, hits);
        Assert.IsTrue(run.Coordinator.HasAdmittedCoverage(default));
        CollectionAssert.AreEqual(oldHits, Probe(original));
    }

    private static VolumeRayHit[] Probe(VolumeStreamingLease lease)
    {
        var hits = new VolumeRayHit[8];
        var ray = lease.Raycast(new(-16, 0, 0), new(16, 0, 0), hits);
        Assert.AreEqual(VolumeQueryStatus.Contact, ray.Status);
        Assert.AreEqual(lease.Info, ray.Terrain);
        Assert.AreEqual(2, ray.Count);
        return hits[..ray.Count];
    }

    private static void AssertPublished(Run run, IWorldStateRoot expected, VolumePublicationInfo info, VolumeRayHit[] hits)
    {
        var root = run.World.Publication.Committed.Read().Root;
        Assert.AreSame(expected, root);
        Assert.AreEqual(info, ((VolumeWorldRoot)root).Terrain);
        using var lease = run.Coordinator.TryRead();
        Assert.IsNotNull(lease);
        Assert.AreEqual(info, lease.Info);
        Assert.IsTrue(lease.Meshes.ToArray().All(mesh =>
            mesh.PlanIdentity == info.PlanIdentity && mesh.TerrainRevision == info.TerrainRevision));
        CollectionAssert.AreEqual(hits, Probe(lease));
    }

    private static async Task FinishUnchanged(Run run, ulong tick, ulong? rejected = null)
    {
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, tick, BarrierKind.CommitResults);
        if (rejected is { } generation)
            Assert.IsNull(run.Coordinator.TryPreparePublication(generation, candidates, []));
        var unchanged = CoreRuntimeFixture.Prepare(run.World, candidates, []);
        var result = run.World.Publication.Finalization.TryFinalize(unchanged);
        Assert.AreEqual(BarrierStatus.Unchanged, result.Status);
        CoreRuntimeFixture.PublishObserver(run.World, result.Stamp!.Value, (int)tick);
    }

    private sealed class FaultSampler(Action waitAtFirstSample, bool fail) : IPlanetBrickSource
    {
        private readonly Source _source = new(waitAtFirstSample, threshold: 697);
        internal int SampleCount { get; private set; }
        public PlanetDescriptor Descriptor => _source.Descriptor;

        public long SampleDensity(LatticeCoordinate point)
        {
            if (++SampleCount == PlanetBrickStore.VertexCount + 1 && fail)
                throw new InvalidOperationException("Q02 sampling fault after the first brick.");
            return _source.SampleDensity(point);
        }

        public ushort SampleMaterial(LatticeCoordinate cell) => _source.SampleMaterial(cell);
    }
}
