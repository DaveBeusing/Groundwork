using Groundwork.Core;
using Groundwork.Core.Tests;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class VolumeCollisionTests
{
    private static readonly PlanetDescriptor Descriptor = new(new Guid("00112233-4455-6677-8899-aabbccddeeff"),
        new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"), 42, 1, 1, 1, new string('a', 64), 128);
    private static PlanetVector P(double x, double y, double z) => new(x, y, z);

    [TestMethod]
    public async Task MultiHitRaySeparatesCavityFloorAndCeilingAndRejectsPartialBuffers()
    {
        using var run = new Run(p => Math.Min(p.Z + 4, 4 - p.Z)); // Empty cavity between two solids.
        await run.Publish();
        using var lease = run.Coordinator.TryRead(); Assert.IsNotNull(lease);
        var hits = new VolumeRayHit[8];
        var result = lease.Raycast(P(0, 0, -10), P(0, 0, 10), hits);
        Assert.AreEqual(VolumeQueryStatus.Contact, result.Status);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(6, hits[0].Distance, 2e-4); Assert.AreEqual(14, hits[1].Distance, 2e-4);
        Assert.IsGreaterThan(0, hits[0].Normal.Z); Assert.IsLessThan(0, hits[1].Normal.Z);
        Assert.AreEqual(VolumeQueryStatus.BudgetExceeded, lease.Raycast(P(0, 0, -10), P(0, 0, 10), new VolumeRayHit[1]).Status);
        Assert.AreEqual(VolumeQueryStatus.BudgetExceeded, lease.Raycast(P(0, 0, -10), P(0, 0, 10), hits, new(1, 1)).Status);
        Assert.AreEqual(VolumeQueryStatus.Invalid, lease.Raycast(default, default, hits).Status);
        Assert.AreEqual(VolumeQueryStatus.CoverageUnavailable, lease.Raycast(P(-1000, 0, 0), default, hits).Status);
    }

    [TestMethod]
    public async Task SweptCapsuleCannotTunnelThroughWallOrCeilingAndHandlesTangency()
    {
        using var run = new Run(p => Math.Min(4 - p.X, 4 - p.Z)); await run.Publish();
        using var lease = run.Coordinator.TryRead(); Assert.IsNotNull(lease);
        var body = new VolumeCapsule(P(0, 0, 0), P(0, 0, 1), 0.25);
        var wall = lease.Sweep(body, P(40, 0, 0));
        Assert.AreEqual(VolumeQueryStatus.Contact, wall.Status);
        Assert.AreEqual(3.75 / 40, wall.Fraction, 2e-4); Assert.IsLessThan(0, wall.Normal.X);
        var ceiling = lease.Sweep(body, P(0, 0, 20));
        Assert.AreEqual(VolumeQueryStatus.Contact, ceiling.Status);
        Assert.AreEqual(2.75 / 20, ceiling.Fraction, 2e-4);
        Assert.AreEqual(VolumeQueryStatus.InitialOverlap, lease.Sweep(body with { A = P(4, 0, 0), B = P(4, 0, 1) }, default).Status);
        Assert.AreEqual(VolumeQueryStatus.Clear, lease.Sweep(body with { A = P(3.75, 0, 0), B = P(3.75, 0, 1) }, P(0, 1, 0)).Status);
        Assert.AreEqual(VolumeQueryStatus.CoverageUnavailable, lease.Sweep(body, P(1000, 0, 0)).Status);
        Assert.AreEqual(VolumeQueryStatus.Invalid, lease.Sweep(body with { Radius = double.NaN }, default).Status);
        Assert.AreEqual(VolumeQueryStatus.BudgetExceeded, lease.Sweep(body, P(1, 0, 0), new(1, 1)).Status);
    }

    [TestMethod]
    public async Task CapsuleInteriorAndSmallSolidCannotBeMissedByEndpointSampling()
    {
        using var run = new Run(p => (p.X - 2) * (p.X - 2) + p.Y * p.Y + (p.Z - 3) * (p.Z - 3) - 1); await run.Publish();
        using var lease = run.Coordinator.TryRead(); Assert.IsNotNull(lease);
        var result = lease.Sweep(new VolumeCapsule(P(0, 0, 0), P(0, 0, 6), 0.25), P(10, 0, 0));
        Assert.AreEqual(VolumeQueryStatus.Contact, result.Status);
        Assert.IsTrue(result.Fraction > 0 && result.Fraction < 0.2);
        Assert.AreEqual(VolumeQueryStatus.InitialOverlap,
            lease.Sweep(new VolumeCapsule(P(2, 0, 0), P(2, 0, 6), 0.25), default).Status);
    }

    [TestMethod]
    public async Task PosePublishesOnlyAtCoreCommitAndStopOrViewChangeRevokesIt()
    {
        foreach (var stop in new[] { false, true })
        {
            using var run = new Run(p => p.Z - 16); await run.Publish();
            var state = new PlanetWalkState(P(0, 0, 16), P(1, 0, 0), default);
            Assert.IsNull(run.Coordinator.TryPlanMovement(state, 1, 0, out _));
            using var frame = await run.Open();
            var draft = run.Coordinator.TryPlanMovement(state, 1, 0, out var status);
            Assert.IsNotNull(draft, $"Movement status: {status}"); Assert.AreEqual(VolumeQueryStatus.Clear, status);
            Assert.IsNull(((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Player);
            var candidates = await CoreRuntimeFixture.SealAsync(run.World, run.Tick, BarrierKind.CommitResults);
            var prepared = run.Coordinator.TryPrepareMovement(draft, candidates, []);
            Assert.IsNotNull(prepared);
            if (stop) run.Coordinator.Stop();
            else run.Coordinator.ChangeView(PlanetLocalFrame.Create(default), 2);
            Assert.AreEqual(BarrierStatus.Unchanged, run.World.Publication.Finalization.TryFinalize(prepared).Status);
            Assert.IsNull(((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Player);
        }
    }

    [TestMethod]
    public async Task CommittedPosePreventsTeleportAndOldTickDraftReuseAndKeepsRadialGravity()
    {
        using var run = new Run(p => p.Z - 16); await run.Publish(); using var frame = await run.Open();
        var state = new PlanetWalkState(P(0, 0, 16), P(1, 0, 0), default);
        var draft = run.Coordinator.TryPlanMovement(state, 1, 0, out var status);
        Assert.IsNotNull(draft, $"Movement status: {status}"); Assert.AreEqual(VolumeQueryStatus.Clear, status);
        Assert.IsTrue(draft.Result.Grounded);
        Assert.AreEqual(PlanetSurfaceWalker.WalkSpeed / 60, draft.Result.State.Position.X, 1e-9);
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, run.Tick, BarrierKind.CommitResults);
        var prepared = run.Coordinator.TryPrepareMovement(draft, candidates, []); Assert.IsNotNull(prepared);
        Assert.AreEqual(BarrierStatus.Committed, run.World.Publication.Finalization.TryFinalize(prepared).Status);
        Assert.AreEqual(draft.Result.State, ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Player);
        CoreRuntimeFixture.PublishObserver(run.World, run.World.Publication.Committed.Read().Stamp, (int)run.Tick);
        await run.Begin();
        Assert.IsNull(run.Coordinator.TryPlanMovement(state, 1, 0, out status)); Assert.AreEqual(VolumeQueryStatus.Invalid, status);
        var next = run.Coordinator.TryPlanMovement(draft.Result.State, 0, 0, out _); Assert.IsNotNull(next);
        candidates = await CoreRuntimeFixture.SealAsync(run.World, run.Tick, BarrierKind.CommitResults);
        Assert.IsNull(run.Coordinator.TryPrepareMovement(draft, candidates, []));
        Assert.IsNotNull(run.Coordinator.TryPrepareMovement(next, candidates, []));
    }

    [TestMethod]
    public async Task MissingCoverageInvalidInputAndExhaustionHoldWithoutAccumulatingMotion()
    {
        using var run = new Run(p => p.Z - 16); await run.Publish(); using var frame = await run.Open();
        var state = new PlanetWalkState(P(0, 0, 20), P(1, 0, 0), default);
        Assert.IsNull(run.Coordinator.TryPlanMovement(state, 1, 0, out var status, new(1, 1)));
        Assert.AreEqual(VolumeQueryStatus.BudgetExceeded, status);
        Assert.IsNull(run.Coordinator.TryPlanMovement(state, 2, 0, out status)); Assert.AreEqual(VolumeQueryStatus.Invalid, status);
        Assert.IsNull(run.Coordinator.TryPlanMovement(state with { Position = P(1000, 0, 20) }, 1, 0, out status));
        Assert.AreEqual(VolumeQueryStatus.CoverageUnavailable, status);
        var draft = run.Coordinator.TryPlanMovement(state, 1, 0, out status); Assert.IsNotNull(draft);
        Assert.AreEqual(4d / 60, draft.Result.State.Position.X, 1e-9);
        Assert.AreEqual(20 - 9.81 / 3600, draft.Result.State.Position.Z, 1e-9);
        Assert.IsFalse(draft.Result.Grounded);
    }

    [TestMethod]
    public async Task EveryCubeEdgeAndCornerUsesCanonicalMixedLodCollisionAcrossRebase()
    {
        var points = new HashSet<LatticeCoordinate>();
        foreach (var (face, edge, _, _) in PlanetCoordinateTests.Seams)
        {
            var direction = PlanetCoordinates.Direction(PlanetCoordinates.SampleEdge(new PlanetPatchKey(face, 0, 0, 0), edge, 8));
            points.Add(new LatticeCoordinate((long)Math.Round(direction.X * 32), (long)Math.Round(direction.Y * 32), (long)Math.Round(direction.Z * 32)));
        }
        for (var x = -1; x <= 1; x += 2) for (var y = -1; y <= 1; y += 2) for (var z = -1; z <= 1; z += 2)
            points.Add(new LatticeCoordinate(x * 18, y * 18, z * 18));
        Assert.HasCount(20, points);
        foreach (var point in points)
        {
            var center = P(point.X, point.Y, point.Z);
            using var run = new Run(p => p.Z - point.Z, center); await run.Publish(); using var frame = await run.Open();
            using var retained = run.Coordinator.TryRead(); Assert.IsNotNull(retained);
            Assert.IsTrue(retained.Meshes.ToArray().Any(m => m.LodMask != 0));
            var start = P(point.X, point.Y, point.Z + 4); var end = P(point.X, point.Y, point.Z - 4);
            var ray = retained.Raycast(start, end, new VolumeRayHit[4]);
            Assert.AreEqual(VolumeQueryStatus.Contact, ray.Status); Assert.AreEqual(1, ray.Count);
            var sphere = new VolumeCapsule(start, start, 0.25);
            var before = retained.Sweep(sphere, P(0, 0, -8));
            Assert.AreEqual(VolumeQueryStatus.Contact, before.Status);
            Assert.AreEqual(3.75 / 8, before.Fraction, 2e-4);
            var origin = PlanetLocalFrame.Create(center); Assert.IsTrue(origin.TryRebase(P(point.X + 24, point.Y, point.Z), out origin));
            run.Coordinator.ChangeView(origin, 2);
            Assert.IsNull(run.Coordinator.TryPlanMovement(new(start, P(1, 0, 0), default), 0, 0, out _));
            Assert.AreEqual(before, retained.Sweep(sphere, P(0, 0, -8)));
        }
    }

    [TestMethod]
    public async Task WalkableSlopeAndFastFallRespectTheWholeCapsule()
    {
        using (var slope = new Run(p => 4 * (p.Z - 16) - p.X))
        {
            await slope.Publish(); using var frame = await slope.Open();
            var foot = 16 + 0.25 * (Math.Sqrt(1.0625) - 1);
            var state = new PlanetWalkState(P(0, 0, foot), P(1, 0, 0), default);
            var step = slope.Coordinator.TryPlanMovement(state, 1, 0, out var status);
            Assert.IsNotNull(step, $"Slope: {status}");
            Assert.IsGreaterThan(0, step.Result.State.Position.X);
            Assert.IsGreaterThan(foot, step.Result.State.Position.Z);
            Assert.IsTrue(step.Result.Grounded);
        }
        using var run = new Run(p => p.Z - 16); await run.Publish(); using var visible = await run.Open();
        var fall = new PlanetWalkState(P(0, 0, 40), P(1, 0, 0), P(0, 0, -1800));
        var result = run.Coordinator.TryPlanMovement(fall, 0, 0, out var fallStatus);
        Assert.IsNotNull(result, $"Fall: {fallStatus}");
        Assert.IsTrue(result.Result.Grounded);
        Assert.AreEqual(16, result.Result.State.Position.Z, 2e-4);
    }

    [TestMethod]
    public async Task UpwardMotionHitsCavityRoofInsteadOfChoosingAnotherRadialGround()
    {
        using var run = new Run(p => Math.Min(p.Z - 16, 20 - p.Z));
        await run.Publish(); using var visible = await run.Open();
        var state = new PlanetWalkState(P(0, 0, 16.1), P(1, 0, 0), P(0, 0, 240));
        var result = run.Coordinator.TryPlanMovement(state, 0, 0, out var status);
        Assert.IsNotNull(result, $"Roof: {status}");
        Assert.AreEqual(18.3, result.Result.State.Position.Z, 2e-4);
        Assert.IsFalse(result.Result.Grounded);
        Assert.AreEqual(0, result.Result.State.Velocity.Z);
    }

    [TestMethod]
    public async Task ReplacementThatWouldEmbedThePlayerCannotChangeTheCommittedRoot()
    {
        using var run = new Run(p => p.Z - 16); await run.Publish(); using var frame = await run.Open();
        var state = new PlanetWalkState(P(0, 0, 16), P(1, 0, 0), default);
        var movement = run.Coordinator.TryPlanMovement(state, 0, 0, out _); Assert.IsNotNull(movement);
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, run.Tick, BarrierKind.CommitResults);
        var pose = run.Coordinator.TryPrepareMovement(movement, candidates, []); Assert.IsNotNull(pose);
        var outcome = run.World.Publication.Finalization.TryFinalize(pose);
        Assert.AreEqual(BarrierStatus.Committed, outcome.Status);
        CoreRuntimeFixture.PublishObserver(run.World, outcome.Stamp!.Value, (int)run.Tick);
        var original = run.World.Publication.Committed.Read().Root;
        using var old = run.Coordinator.TryRead(); Assert.IsNotNull(old);
        run.Coordinator.ChangeView(run.Coordinator.View.Frame, 1);
        Assert.IsTrue(run.Coordinator.ConfirmMask(run.Coordinator.View));
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.TryStage(VolumeWorkClass.FirstEdit,
            run.Selection, 1, 2, 10_000_000, run.Unit, new Source(p => p.Z - 18), out var work));
        Assert.IsNotNull(work);
        using (work)
        {
            Assert.IsTrue(work.Execute()); await run.Begin();
            candidates = await CoreRuntimeFixture.SealAsync(run.World, run.Tick, BarrierKind.CommitResults);
            Assert.IsNull(run.Coordinator.TryPreparePublication(work.Generation, candidates, []));
            var unchanged = CoreRuntimeFixture.Prepare(run.World, candidates, []);
            Assert.AreEqual(BarrierStatus.Unchanged, run.World.Publication.Finalization.TryFinalize(unchanged).Status);
            Assert.AreSame(original, run.World.Publication.Committed.Read().Root);
            Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);
            Assert.AreEqual(1UL, old.Info.TerrainRevision);
        }
        old.Dispose();
        Assert.Throws<ObjectDisposedException>(() => old.Sweep(new VolumeCapsule(default, default, 1), default));
    }

    [TestMethod]
    public async Task QuerySmokeReportsLatencyAndManagedAllocations()
    {
        using var run = new Run(p => p.Z - 16); await run.Publish();
        using var lease = run.Coordinator.TryRead(); Assert.IsNotNull(lease);
        var sphere = new VolumeCapsule(P(0, 0, 20), P(0, 0, 20), 0.25);
        for (var i = 0; i < 16; i++) _ = lease.Sweep(sphere, P(0, 0, -8));
        var samples = new double[64];
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < samples.Length; i++)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = lease.Sweep(sphere, P(0, 0, -8));
            samples[i] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (result.Status != VolumeQueryStatus.Contact) Assert.Fail("Benchmark query failed.");
        }
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Array.Sort(samples);
        Console.WriteLine($"C02 smoke: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; " +
            $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; cores={Environment.ProcessorCount}; " +
            $"triangles={lease.Meshes.ToArray().Sum(m => m.Triangles.Length)}; n=64; " +
            $"p50={samples[31]:F4}ms p95={samples[60]:F4}ms p99={samples[63]:F4}ms; managedBytes={bytes}. " +
            "Single-thread portable query smoke, not Q01 production/hardware qualification.");
        Assert.AreEqual(0L, bytes, "Warmed query must not allocate managed memory.");
    }

    private sealed class Source(Func<LatticeCoordinate, long> density) : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => VolumeCollisionTests.Descriptor;
        public long SampleDensity(LatticeCoordinate p) => density(p);
        public ushort SampleMaterial(LatticeCoordinate p) => 1;
    }
    private sealed class Clock : CoreRuntimeFixture.TestClock { }
    private sealed class Run : IDisposable
    {
        public CoreRuntimeFixture Fixture { get; } = new(new Clock());
        public WorldSafetyPorts World { get; }
        public VolumeStreamingCoordinator Coordinator { get; }
        public ulong Tick { get; private set; }
        private readonly Source _source;
        private readonly VolumeStreamingSelection _selection;
        private readonly VolumeResources _unit;
        private ulong _generation;
        public VolumeStreamingSelection Selection => _selection;
        public VolumeResources Unit => _unit;
        public Run(Func<LatticeCoordinate, long> density, PlanetVector center = default)
        {
            World = Fixture.OpenWorld(); _source = new Source(density);
            _selection = VolumeStreamingSelection.Select(Descriptor, center, P(1, 0, 0));
            _unit = new(VolumeStreamingCoordinator.RequiredCpuBytes(_selection, 10_000_000), 100, 20, 100, 1, 1, 1, 64, 1);
            var baseline = _unit.Retained + _unit.Retained; baseline = baseline with { LeaseSlots = baseline.LeaseSlots + 16 };
            var limits = new VolumeStreamingLimits(baseline + _unit + _unit + _unit + _unit, baseline, _unit, _unit, _unit, _unit, 16, 16, 1024);
            Coordinator = new(World, Descriptor, limits);
            Assert.IsTrue(Coordinator.ConfirmMask(Coordinator.View));
        }
        public async Task Publish()
        {
            Assert.AreEqual(VolumeAdmission.Accepted, Coordinator.TryStage(VolumeWorkClass.Movement, _selection, 0, 1, 10_000_000, _unit, _source, out var work));
            Assert.IsNotNull(work);
            using (work)
            {
                Assert.IsTrue(work.Execute()); _generation = work.Generation;
                await Begin();
                var candidates = await CoreRuntimeFixture.SealAsync(World, Tick, BarrierKind.CommitResults);
                var prepared = Coordinator.TryPreparePublication(work.Generation, candidates, []); Assert.IsNotNull(prepared);
                var outcome = World.Publication.Finalization.TryFinalize(prepared); Assert.AreEqual(BarrierStatus.Committed, outcome.Status);
                CoreRuntimeFixture.PublishObserver(World, outcome.Stamp!.Value, (int)Tick);
            }
        }
        public async Task Begin() => await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, ++Tick, BarrierKind.BeginTick);
        public async Task<VolumeFrameRegistration> Open()
        {
            var frame = Coordinator.TryRegisterFrame(_generation, Coordinator.View, 1); Assert.IsNotNull(frame);
            Assert.IsTrue(Coordinator.AcknowledgeFrame(frame)); await Begin();
            Assert.IsTrue(Coordinator.BeginTick(World.Publication.Committed.Read().Stamp)); return frame;
        }
        public void Dispose() { Coordinator.Stop(); Fixture.Dispose(); }
    }
}
