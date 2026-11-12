using Groundwork.Core;
using Groundwork.Core.Tests;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class VolumeCollisionTests
{
    private static readonly PlanetDescriptor Descriptor = new(new Guid("00112233-4455-6677-8899-aabbccddeeff"),
        new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"), 42, 1, 1, 1, new string('a', 64), 128);
    private static readonly PlanetDescriptor CaveDescriptor = new(Descriptor.WorldId, Descriptor.PlanetId,
        Descriptor.Seed, PlanetGeologyField.GeneratorVersion, Descriptor.TerrainSchemaVersion,
        Descriptor.MaterialCatalogVersion, Descriptor.ParameterSha256, Descriptor.RadiusMeters);

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
    public async Task EveryCubeEdgeAndCornerHitsActualTransitionGeometryAcrossRevisionAndRebase()
    {
        static long Sample(LatticeCoordinate p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
        static PlanetVector Axis(int axis, double amount) => axis == 0 ? P(amount, 0, 0) : axis == 1 ? P(0, amount, 0) : P(0, 0, amount);
        static PlanetVector Add(PlanetVector a, PlanetVector b) => P(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        var points = new HashSet<LatticeCoordinate>();
        foreach (var (face, edge, _, _) in PlanetCoordinateTests.Seams)
        {
            var d = PlanetCoordinates.Direction(PlanetCoordinates.SampleEdge(new PlanetPatchKey(face, 0, 0, 0), edge, 8));
            var scale = 32 / Math.Max(Math.Abs(d.X), Math.Max(Math.Abs(d.Y), Math.Abs(d.Z)));
            points.Add(new LatticeCoordinate((long)Math.Round(d.X * scale), (long)Math.Round(d.Y * scale), (long)Math.Round(d.Z * scale)));
        }
        for (var x = -1; x <= 1; x += 2) for (var y = -1; y <= 1; y += 2) for (var z = -1; z <= 1; z += 2)
            points.Add(new LatticeCoordinate(x * 32, y * 32, z * 32));
        Assert.HasCount(20, points);
        foreach (var point in points)
        {
            var seam = P(point.X, point.Y, point.Z);
            var axis = point.X != 0 ? 0 : 1;
            var normalAxis = (axis + 1) % 3;
            var normal = Axis(normalAxis, 1);
            var camera = Add(seam, Axis(axis, -Sample(point, axis)));
            var forward = Axis(axis, -Math.Sign(Sample(point, axis)));
            using var run = new Run(p => Sample(p, normalAxis) - Sample(point, normalAxis), camera, forward: forward);
            await run.Publish(); using var frame = await run.Open();
            using var retained = run.Coordinator.TryRead(); Assert.IsNotNull(retained);
            Assert.IsTrue(retained.Meshes.ToArray().Any(m => m.Triangles.ToArray().Any(t => t.IsTransition)));
            var start = Add(seam, Axis(normalAxis, 4)); var end = Add(seam, Axis(normalAxis, -4));
            var hits = new VolumeRayHit[8];
            var ray = retained.Raycast(start, end, hits);
            Assert.AreEqual(VolumeQueryStatus.Contact, ray.Status); Assert.AreEqual(1, ray.Count);
            Assert.IsTrue(hits[0].IsTransition, $"Ray must cross transition at {point}");
            Assert.AreEqual(4, hits[0].Distance, 2e-4);
            Assert.AreEqual(1, retained.Raycast(end, start, hits).Count);
            Assert.IsTrue(hits[0].IsTransition);
            var sphere = new VolumeCapsule(start, start, 0.25);
            var before = retained.Sweep(sphere, Axis(normalAxis, -8));
            Assert.AreEqual(VolumeQueryStatus.Contact, before.Status);
            Assert.IsTrue(before.IsTransition, $"Sphere must hit transition at {point}");
            Assert.AreEqual(3.75 / 8, before.Fraction, 2e-4);
            var body = sphere with { A = Add(start, Axis((normalAxis + 1) % 3, -0.3)), B = Add(start, Axis((normalAxis + 1) % 3, 0.3)) };
            Assert.AreEqual(VolumeQueryStatus.Contact, retained.Sweep(body, Axis(normalAxis, -8)).Status);
            run.Coordinator.ChangeView(run.Coordinator.View.Frame, 1);
            Assert.IsTrue(run.Coordinator.ConfirmMask(run.Coordinator.View));
            Assert.IsFalse(run.Coordinator.HasAdmittedCoverage(camera));
            await run.Publish(new Source(p => 4 * (Sample(p, normalAxis) - Sample(point, normalAxis)) - 1), 1, 2);
            Assert.IsFalse(run.Coordinator.HasAdmittedCoverage(camera));
            using var edited = run.Coordinator.TryRead(); Assert.IsNotNull(edited);
            var changed = edited.Raycast(start, end, hits);
            Assert.AreEqual(VolumeQueryStatus.Contact, changed.Status);
            Assert.AreEqual(2UL, changed.Terrain.TerrainRevision);
            Assert.AreEqual(3.75, hits[0].Distance, 2e-4);
            using var newFrame = await run.Open(); Assert.IsTrue(run.Coordinator.HasAdmittedCoverage(camera));
            var origin = PlanetLocalFrame.Create(camera);
            Assert.IsTrue(origin.TryRebase(Add(camera, Axis(axis, 24)), out origin));
            run.Coordinator.ChangeView(origin, 2);
            Assert.IsFalse(run.Coordinator.HasAdmittedCoverage(camera));
            Assert.AreEqual(before, retained.Sweep(sphere, Axis(normalAxis, -8)));
            Assert.AreEqual(1UL, retained.Info.TerrainRevision);
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

    [TestMethod]
    public async Task FullSemanticJournalStopsMovementBeforeAnyPoseSwap()
    {
        using var run = new Run(p => p.Z - 16, journal: 7);
        await run.Publish(); using var frame = await run.Open();
        var draft = run.Coordinator.TryPlanMovement(new(P(0, 0, 16), P(1, 0, 0), default), 1, 0, out _);
        Assert.IsNotNull(draft);
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, run.Tick, BarrierKind.CommitResults);
        Assert.IsNull(run.Coordinator.TryPrepareMovement(draft, candidates, []));
        Assert.IsTrue(run.Coordinator.Statistics.Stopped);
        Assert.IsNotNull(run.Coordinator.TerminalEvidence);
        var fallback = CoreRuntimeFixture.Prepare(run.World, candidates, []);
        Assert.AreEqual(BarrierStatus.Unchanged, run.World.Publication.Finalization.TryFinalize(fallback).Status);
        Assert.IsNull(((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Player);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MovementEvidenceKeepsSwapOrRevocationOutcomeBeforeHandoff(bool swapFirst)
    {
        using var run = new Run(p => p.Z - 16, journal: 8);
        await run.Publish(); using var frame = await run.Open();
        var state = new PlanetWalkState(P(0, 0, 16), P(1, 0, 0), default);
        var draft = run.Coordinator.TryPlanMovement(state, 1, 0, out _); Assert.IsNotNull(draft);
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, run.Tick, BarrierKind.CommitResults);
        var prepared = run.Coordinator.TryPrepareMovement(draft, candidates, []); Assert.IsNotNull(prepared);
        for (var i = 0; i < 7; i++) Assert.IsTrue(run.Coordinator.TryReadEvent(out _));
        Assert.IsFalse(run.Coordinator.TryReadEvent(out _), "Pending movement outcome cannot be handed off.");
        BarrierOutcome outcome;
        if (swapFirst) { outcome = run.World.Publication.Finalization.TryFinalize(prepared); run.Coordinator.Stop(); }
        else { run.Coordinator.Stop(); outcome = run.World.Publication.Finalization.TryFinalize(prepared); }
        Assert.AreEqual(swapFirst ? BarrierStatus.Committed : BarrierStatus.Unchanged, outcome.Status);
        Assert.IsTrue(run.Coordinator.TryReadEvent(out var record));
        Assert.AreEqual(swapFirst ? VolumeEventKind.MovementCommit : VolumeEventKind.MovementRejected, record.Kind);
        Assert.IsNotNull(record.Movement);
        Assert.AreEqual(state, record.Movement.Value.Before);
        Assert.AreEqual(draft.Result, record.Movement.Value.Result);
        Assert.AreEqual(swapFirst ? outcome.Stamp : null, record.Stamp);
    }

    [TestMethod]
    public async Task FailedPreparationCanRetryTheSameSealedBarrier()
    {
        using var run = new Run(p => p.Z - 16); await run.Publish(); using var frame = await run.Open();
        var draft = run.Coordinator.TryPlanMovement(new(P(0, 0, 16), P(1, 0, 0), default), 1, 0, out _);
        Assert.IsNotNull(draft);
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, run.Tick, BarrierKind.CommitResults);
        Assert.IsNull(run.Coordinator.TryPrepareMovement(draft, candidates, default));
        var prepared = run.Coordinator.TryPrepareMovement(draft, candidates, []); Assert.IsNotNull(prepared);
        Assert.AreEqual(BarrierStatus.Committed, run.World.Publication.Finalization.TryFinalize(prepared).Status);
        var records = new List<VolumeStreamingEvent>();
        while (run.Coordinator.TryReadEvent(out var record)) records.Add(record);
        Assert.AreEqual(1, records.Count(r => r.Kind == VolumeEventKind.MovementRejected));
        Assert.AreEqual(1, records.Count(r => r.Kind == VolumeEventKind.MovementCommit));
    }

    [TestMethod]
    public async Task SubUlpRadiusAtUpperCoverageFacesEdgesAndCornerReturnsAStatus()
    {
        using var run = new Run(_ => 1); await run.Publish();
        using var lease = run.Coordinator.TryRead(); Assert.IsNotNull(lease);
        foreach (var point in new[] { P(64, 0, 20), P(64, 64, 20), P(64, 64, 64) })
            Assert.AreEqual(VolumeQueryStatus.Clear, lease.Sweep(new VolumeCapsule(point, point, 1e-16), default).Status);
    }

    [TestMethod]
    public async Task AirborneWallContactDoesNotSuppressGravityAcrossTicks()
    {
        using var run = new Run(p => 3 - 10 * p.X); await run.Publish(); using var frame = await run.Open();
        var state = new PlanetWalkState(P(0, 0, 20), P(1, 0, 0), default);
        for (var i = 0; i < 8; i++)
        {
            var draft = run.Coordinator.TryPlanMovement(state, 1, 0, out var status);
            Assert.IsNotNull(draft, $"Wall gravity: {status}");
            Assert.IsLessThan(state.Position.Z, draft.Result.State.Position.Z);
            Assert.IsLessThan(0, draft.Result.State.Velocity.Z);
            Assert.AreEqual(state.Position.X, draft.Result.State.Position.X, 1e-10);
            var candidates = await CoreRuntimeFixture.SealAsync(run.World, run.Tick, BarrierKind.CommitResults);
            var prepared = run.Coordinator.TryPrepareMovement(draft, candidates, []); Assert.IsNotNull(prepared);
            var outcome = run.World.Publication.Finalization.TryFinalize(prepared);
            Assert.AreEqual(BarrierStatus.Committed, outcome.Status);
            CoreRuntimeFixture.PublishObserver(run.World, outcome.Stamp!.Value, (int)run.Tick);
            state = draft.Result.State;
            if (i < 7) await run.Begin();
        }
    }

    [TestMethod]
    public async Task SteepContactIsNotGroundAndOffAxisLandingRemainsNonPenetrating()
    {
        using (var steep = new Run(p => p.Z - 16 - 4 * p.X))
        {
            await steep.Publish(); using var frame = await steep.Open();
            var foot = 16 + 0.25 * (Math.Sqrt(17) - 1);
            var draft = steep.Coordinator.TryPlanMovement(new(P(0, 0, foot), P(1, 0, 0), default), 1, 0, out var status);
            Assert.IsNotNull(draft, $"Steep contact: {status}");
            Assert.IsFalse(draft.Result.Grounded);
            Assert.AreEqual(0, draft.Result.State.Position.X, 1e-10);
        }
        using var run = new Run(p => p.Z - 16); await run.Publish(); using var visible = await run.Open();
        var falling = new PlanetWalkState(P(3, 0, 30), P(1, 0, 0), P(-120, 0, -1200));
        var result = run.Coordinator.TryPlanMovement(falling, 0, 0, out var fallStatus);
        Assert.IsNotNull(result, $"Off-axis landing: {fallStatus}");
        var position = result.Result.State.Position;
        var length = Math.Sqrt(position.X * position.X + position.Z * position.Z);
        var lowerCenter = position.Z + 0.25 * position.Z / length;
        Assert.IsGreaterThanOrEqualTo(16 - 1e-4, lowerCenter - 0.25);
    }

    [TestMethod]
    public async Task RejectedMovementEvidencePreservesDistinctAttemptTicks()
    {
        using var run = new Run(p => p.Z - 16); await run.Publish(); using var frame = await run.Open();
        var state = new PlanetWalkState(P(0, 0, 16), P(1, 0, 0), default);
        var attempts = new List<BarrierStamp>();
        for (var i = 0; i < 2; i++)
        {
            attempts.Add(run.World.Publication.Committed.Read().Stamp);
            var draft = run.Coordinator.TryPlanMovement(state, 1, 0, out _); Assert.IsNotNull(draft);
            var candidates = await CoreRuntimeFixture.SealAsync(run.World, run.Tick, BarrierKind.CommitResults);
            Assert.IsNull(run.Coordinator.TryPrepareMovement(draft, candidates, default));
            var fallback = CoreRuntimeFixture.Prepare(run.World, candidates, []);
            var outcome = run.World.Publication.Finalization.TryFinalize(fallback);
            Assert.AreEqual(BarrierStatus.Unchanged, outcome.Status);
            CoreRuntimeFixture.PublishObserver(run.World, outcome.Stamp!.Value, (int)run.Tick);
            if (i == 0) await run.Begin();
        }
        var rejected = new List<VolumeStreamingEvent>();
        while (run.Coordinator.TryReadEvent(out var record))
            if (record.Kind == VolumeEventKind.MovementRejected) rejected.Add(record);
        Assert.HasCount(2, rejected);
        for (var i = 0; i < 2; i++)
        {
            Assert.IsNull(rejected[i].Stamp);
            Assert.AreEqual(attempts[i], rejected[i].Movement!.Value.AttemptStamp);
            Assert.AreEqual(state, rejected[i].Movement!.Value.Before);
        }
        Assert.AreNotEqual(attempts[0].Tick, attempts[1].Tick);
    }

    [TestMethod]
    public async Task CaveClimbPublishesOnlyBoundedClearSweeps()
    {
        var state = new PlanetWalkState(P(0, 0, 16.25), P(1, 0, 0), default);
        using var run = new Run(p => Math.Min(p.Z - 16, 18 - p.Z), state.Position, cave: true);
        await run.Publish();
        using var frame = await run.Open();

        Assert.IsNull(run.Coordinator.TryPlanCaveClimb(state, P(0, 0, 0.126), out var status));
        Assert.AreEqual(VolumeQueryStatus.Invalid, status);
        var blocked = run.Coordinator.TryPlanCaveClimb(state, P(0, 0, 0.1), out status);
        Assert.AreEqual(VolumeQueryStatus.Contact, status);
        Assert.IsNull(blocked);

        var delta = P(0.1, 0, 0);
        var climb = run.Coordinator.TryPlanCaveClimb(state, delta, out status);
        Assert.AreEqual(VolumeQueryStatus.Clear, status);
        Assert.IsNotNull(climb);
        Assert.AreEqual(state with { Position = P(0.1, 0, 16.25) }, climb.Result.State);
        var cut = await CoreRuntimeFixture.SealAsync(run.World, run.Tick, BarrierKind.CommitResults);
        var publication = run.Coordinator.TryPrepareMovement(climb, cut, []);
        Assert.IsNotNull(publication);
        Assert.AreEqual(BarrierStatus.Committed, run.World.Publication.Finalization.TryFinalize(publication).Status);
        while (run.Coordinator.TryReadEvent(out var entry))
            if (entry.Kind == VolumeEventKind.MovementCommit)
            {
                Assert.AreEqual(delta, entry.Movement!.Value.ClimbDisplacement);
                return;
            }
        Assert.Fail("Climb movement evidence was not recorded.");
    }

    private sealed class Source(Func<LatticeCoordinate, long> density, PlanetDescriptor? descriptor = null) : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => descriptor ?? VolumeCollisionTests.Descriptor;
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
        private ulong _frame;
        public VolumeStreamingSelection Selection => _selection;
        public VolumeResources Unit => _unit;
        public Run(Func<LatticeCoordinate, long> density, PlanetVector center = default, int journal = 1024, PlanetVector? forward = null, bool cave = false)
        {
            World = Fixture.OpenWorld(); _source = new Source(density, cave ? CaveDescriptor : Descriptor);
            var heading = forward ?? P(1, 0, 0);
            _selection = cave
                ? VolumeStreamingSelection.SelectCave(_source.Descriptor, center, heading, new(center, center, 0.5), default)
                : VolumeStreamingSelection.Select(_source.Descriptor, center, heading);
            _unit = new(VolumeStreamingCoordinator.RequiredCpuBytes(_selection, 10_000_000), 100, 20, 100, 1, 1, 1, 64, 1);
            var baseline = _unit.Retained + _unit.Retained; baseline = baseline with { LeaseSlots = baseline.LeaseSlots + 16 };
            var limits = new VolumeStreamingLimits(baseline + _unit + _unit + _unit + _unit, baseline, _unit, _unit, _unit, _unit, 16, 16, journal);
            Coordinator = new(World, _source.Descriptor, limits);
            Assert.IsTrue(Coordinator.ConfirmMask(Coordinator.View));
        }
        public async Task Publish(Source? source = null, ulong sourceRevision = 0, ulong targetRevision = 1)
        {
            if (Tick != 0 && World.Publication.Committed.Read().Stamp.Kind == BarrierKind.BeginTick)
            {
                var completed = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, Tick, BarrierKind.CommitResults);
                CoreRuntimeFixture.PublishObserver(World, completed, (int)Tick);
            }
            Assert.AreEqual(VolumeAdmission.Accepted, Coordinator.TryStage(VolumeWorkClass.Movement, _selection, sourceRevision, targetRevision, 10_000_000, _unit, source ?? _source, out var work));
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
            var frame = Coordinator.TryRegisterFrame(_generation, Coordinator.View, ++_frame); Assert.IsNotNull(frame);
            Assert.IsTrue(Coordinator.AcknowledgeFrame(frame)); await Begin();
            Assert.IsTrue(Coordinator.BeginTick(World.Publication.Committed.Read().Stamp)); return frame;
        }
        public void Dispose() { Coordinator.Stop(); Fixture.Dispose(); }
    }
}
