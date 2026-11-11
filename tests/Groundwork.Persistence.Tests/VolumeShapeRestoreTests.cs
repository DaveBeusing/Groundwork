using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Core;
using Groundwork.Core.Tests;
using Groundwork.Voxels;

namespace Groundwork.Persistence.Tests;

[TestClass]
public sealed class VolumeShapeRestoreTests
{
    private const int OverlayLimit = 2048;
    private const long MeshBytes = 8_000_000;

    [TestMethod]
    [DataRow("shells")]
    [DataRow("overhang")]
    [DataRow("cavity")]
    public async Task ShapeEditAndNtfsRestorePreserveMeshAndCollision(string name)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The checkpoint round trip requires Windows NTFS.");

        using var directory = new OwnedDirectory(Directory.CreateTempSubdirectory("groundwork-q02-shapes-"));
        var root = directory.Path.CreateSubdirectory("v2").FullName;
        var slotId = Guid.NewGuid();
        var lineage = Guid.NewGuid();
        var shape = Shape.For(name);
        var source = new ShapeSource(name, shape);
        var slot = VolumeCheckpointSlot.Reserve(root, slotId, lineage, source.Descriptor, Validate);
        ShapeEvidence savedEvidence;
        ulong savedTick;

        using (var run = new ShapeRun(source))
        {
            await run.Build();
            using var baseline = run.Read();
            var original = CheckGeometry(baseline, shape, edited: false);
            var input = await run.OpenMovement(baseline);
            var brush = new VolumeBrushIntent(input.Tick, 1, 1, shape.Minimum, shape.Maximum,
                shape.Operation, shape.Operation == VolumeBrushOperation.Add ? (ushort)7 : (ushort)0);
            Assert.AreEqual(VolumeAdmission.Accepted, run.Terrain.SealEditTick(input.Tick, source.Descriptor, [brush]));
            run.Hold();
            Assert.AreEqual(VolumeAdmission.Accepted, run.Terrain.TryStageEdit(input.Tick, brush.Id, run.Selection,
                MeshBytes, run.Credits, out var edit));
            Assert.IsNotNull(edit);
            using var work = edit;
            Assert.IsTrue(work.Execute());
            Assert.AreEqual(1UL, run.ReadRevision());
            await run.FinishUnchanged(input.Tick);

            var commit = await run.Begin();
            using var reservation = run.Terrain.TryReserveCheckpoint(commit.Tick);
            Assert.IsNotNull(reservation);
            await run.Publish(work, commit.Tick);
            Assert.AreEqual(2UL, run.ReadRevision());
            Assert.AreEqual(brush, ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).CommittedEdit);
            Assert.AreEqual(original.Sweep, CheckGeometry(baseline, shape, edited: false).Sweep);
            using var edited = run.Read();
            savedEvidence = CheckGeometry(edited, shape, edited: true);
            Assert.AreNotEqual(original.PlanIdentity, savedEvidence.PlanIdentity);

            using var cut = reservation.TryTake();
            Assert.IsNotNull(cut);
            savedTick = cut.Value.Publication.Stamp.Tick;
            Assert.AreEqual(commit.Tick, savedTick);
            using var writer = slot.OpenWriter(out var opened);
            Assert.AreEqual(VolumeSlotStatus.Empty, opened.Status);
            Assert.IsNotNull(writer);
            Assert.AreEqual(VolumeCaptureAdmission.Accepted, writer.TryAdmit(cut.Value, 1, static (_, game) =>
            {
                game.Span[0] = 42;
                return 1;
            }));
            Assert.AreEqual(VolumeSlotStatus.Committed, writer.ExecutePending().Status);
        }

        var reopened = new VolumeCheckpointSlot(root, slotId, lineage, source.Descriptor, Validate);
        var loaded = reopened.Load();
        Assert.AreEqual(VolumeSlotStatus.Loaded, loaded.Status, loaded.Error?.ToString());
        var checkpoint = loaded.Checkpoint;
        Assert.IsNotNull(checkpoint);
        Assert.AreEqual(savedTick, checkpoint.Tick);
        Assert.AreEqual(2UL, checkpoint.TerrainRevision);
        Assert.IsGreaterThan(8, checkpoint.Overlay.Length);
        CollectionAssert.AreEqual(new byte[] { 42 }, checkpoint.Game.ToArray());

        using var restarted = new ShapeRun(source, checkpoint);
        Assert.IsFalse(restarted.Terrain.HasAdmittedCoverage(default));
        await restarted.Build();
        Assert.AreEqual(savedTick + 1, restarted.World.Publication.Committed.Read().Stamp.Tick);
        Assert.AreEqual(checkpoint.TerrainRevision, restarted.ReadRevision());
        Assert.IsFalse(restarted.Terrain.HasAdmittedCoverage(default));
        using var restored = restarted.Read();
        var bytes = new byte[checkpoint.Overlay.Length];
        Assert.IsTrue(restored.TryWriteEditOverlay(bytes, out var written));
        Assert.AreEqual(bytes.Length, written);
        CollectionAssert.AreEqual(checkpoint.Overlay.ToArray(), bytes);

        var restoredEvidence = CheckGeometry(restored, shape, edited: true);
        Assert.AreEqual(savedEvidence.PlanIdentity, restoredEvidence.PlanIdentity);
        CollectionAssert.AreEqual(savedEvidence.MeshOrigins, restoredEvidence.MeshOrigins);
        CollectionAssert.AreEqual(savedEvidence.Triangles, restoredEvidence.Triangles);
        CollectionAssert.AreEqual(savedEvidence.Hits, restoredEvidence.Hits);
        Assert.AreEqual(savedEvidence.Sweep.Status, restoredEvidence.Sweep.Status);
        Assert.AreEqual(savedEvidence.Sweep.Fraction, restoredEvidence.Sweep.Fraction);
        Assert.AreEqual(savedEvidence.Sweep.Normal, restoredEvidence.Sweep.Normal);
        var resumed = await restarted.OpenMovement(restored);
        Assert.IsTrue(restarted.Terrain.HasAdmittedCoverage(default));
        await restarted.FinishUnchanged(resumed.Tick);
    }

    [TestMethod]
    public async Task CapturedV2CaveCheckpointRestoresPoseGeometryAndDepletedOreFromNtfs()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The checkpoint round trip requires Windows NTFS.");

        using var directory = new OwnedDirectory(Directory.CreateTempSubdirectory("groundwork-wp12-c01-"));
        var root = directory.Path.CreateSubdirectory("v2").FullName;
        var landform = new PlanetLandformRecipe(61, 0, 0, new(17, 0), new(11, 0), new(5, 0), 13, 7, 19);
        var recipe = new PlanetGeologyRecipe(landform, 12, 100, 19, 0, 7, 0, 8, 112, 65536, 0, 21, 22, 23, 24);
        var descriptor = new PlanetDescriptor(new Guid("00112233-4455-6677-8899-aabbccddeeff"),
            new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"), 42, 2, 1, 2, recipe.ParameterSha256, 128);
        var source = new PlanetGeologyField(descriptor, recipe);
        var deposit = Enumerable.Range(45, 8).SelectMany(y => Enumerable.Range(18, 8)
            .Select(z => new LatticeCoordinate(63, y, z)))
            .First(cell => source.SampleMaterial(cell) == recipe.Ore && Enumerable.Range(-1, 4).All(x =>
                Enumerable.Range(-1, 4).All(y => Enumerable.Range(-1, 4).All(z =>
                    source.SampleDensity(new(cell.X + x, cell.Y + y, cell.Z + z)) < 0))));
        var upper = new LatticeCoordinate(deposit.X + 1, deposit.Y + 1, deposit.Z + 1);
        var slotId = Guid.NewGuid();
        var lineage = Guid.NewGuid();
        var slot = VolumeCheckpointSlot.Reserve(root, slotId, lineage, descriptor,
            static cut => cut.Game.Length == 1 ? SaveGameValidation.Valid : SaveGameValidation.Malformed);
        PlanetWalkState savedPose;
        string plan;
        VolumeRayHit[] savedHits;
        ulong savedTick;

        using (var run = new ShapeRun(source))
        {
            await run.Build();
            using var original = run.Read();
            var begin = await run.OpenMovement(original);
            var pose = new PlanetWalkState(new(64, 33, 26), new(0, 0, 1), default);
            var movement = run.Terrain.TryPlanMovement(pose, 0, 0, out _);
            Assert.IsNotNull(movement);
            var movementCut = await CoreRuntimeFixture.SealAsync(run.World, begin.Tick, BarrierKind.CommitResults);
            var planned = run.Terrain.TryPrepareMovement(movement, movementCut, []);
            Assert.IsNotNull(planned);
            var moved = run.World.Publication.Finalization.TryFinalize(planned);
            Assert.AreEqual(BarrierStatus.Committed, moved.Status);
            CoreRuntimeFixture.PublishObserver(run.World, moved.Stamp!.Value, (int)begin.Tick);
            savedPose = movement.Result.State;

            using var capture = await Edit(run, 1, VolumeBrushOperation.Remove, save: true);
            Assert.IsNotNull(capture);
            Assert.IsNotNull(capture.Value.Terrain);
            savedTick = capture.Value.Publication.Stamp.Tick;
            var committed = (VolumeWorldRoot)capture.Value.Publication.Root;
            Assert.AreEqual(savedPose, committed.Player);
            Assert.AreEqual(1, committed.CommittedOreDepletion?.Deposits);
            Assert.AreEqual((byte)1, ((CaveInventory)committed.State).Deposits);
            plan = capture.Value.Terrain.Info.PlanIdentity;
            savedHits = ReadMine(capture.Value.Terrain);

            using var writer = slot.OpenWriter(out var opened);
            Assert.AreEqual(VolumeSlotStatus.Empty, opened.Status);
            Assert.IsNotNull(writer);
            Assert.AreEqual(VolumeCaptureAdmission.Accepted, writer.TryAdmit(capture.Value, 1, static (state, game) =>
            {
                game.Span[0] = ((CaveInventory)state.State).Deposits;
                return 1;
            }));
            var saved = writer.ExecutePending();
            Assert.AreEqual(VolumeSlotStatus.Committed, saved.Status, saved.Error?.ToString());
        }

        var loaded = slot.Load();
        Assert.AreEqual(VolumeSlotStatus.Loaded, loaded.Status, loaded.Error?.ToString());
        var checkpoint = loaded.Checkpoint;
        Assert.IsNotNull(checkpoint);
        Assert.AreEqual(descriptor, checkpoint.Planet);
        Assert.AreEqual(savedTick, checkpoint.Tick);
        Assert.AreEqual(savedPose, checkpoint.Player);
        Assert.AreEqual(2UL, checkpoint.TerrainRevision);
        CollectionAssert.AreEqual(new byte[] { 1 }, checkpoint.Game.ToArray());

        using var restored = new ShapeRun(source, checkpoint);
        Assert.IsFalse(restored.Terrain.HasAdmittedCoverage(savedPose.Position));
        await restored.Build();
        using (var terrain = restored.Read())
        {
            Assert.AreEqual(plan, terrain.Info.PlanIdentity);
            CollectionAssert.AreEqual(savedHits, ReadMine(terrain));
            var overlay = new byte[terrain.EditOverlayBytes];
            Assert.IsTrue(terrain.TryWriteEditOverlay(overlay, out var written));
            Assert.AreEqual(overlay.Length, written);
            CollectionAssert.AreEqual(checkpoint.Overlay.ToArray(), overlay);
        }
        Assert.AreEqual(savedPose, ((VolumeWorldRoot)restored.World.Publication.Committed.Read().Root).Player);

        await Edit(restored, 2, VolumeBrushOperation.Add, save: false);
        await Edit(restored, 3, VolumeBrushOperation.Remove, save: false);
        var after = (VolumeWorldRoot)restored.World.Publication.Committed.Read().Root;
        Assert.AreEqual(4UL, after.Terrain.TerrainRevision);
        Assert.IsNull(after.CommittedOreDepletion);
        Assert.AreEqual((byte)1, ((CaveInventory)after.State).Deposits);

        async Task<VolumeCheckpointCut?> Edit(ShapeRun run, ulong revision, VolumeBrushOperation operation, bool save)
        {
            using var lease = run.Read();
            var begin = await run.OpenMovement(lease);
            var brush = new VolumeBrushIntent(begin.Tick, 1, revision, deposit, upper, operation,
                operation == VolumeBrushOperation.Add ? recipe.Ore : (ushort)0);
            Assert.AreEqual(VolumeAdmission.Accepted, run.Terrain.SealEditTick(begin.Tick, descriptor, [brush]));
            run.Hold();
            Assert.IsTrue(VolumeStreamingCoordinator.TryGetEditDependencies(brush, out var footprint));
            Assert.AreEqual(VolumeAdmission.Accepted,
                run.Terrain.TryStageEdit(begin.Tick, brush.Id, run.Select(footprint), run.MeshLimit, run.Credits, out var work));
            Assert.IsNotNull(work);
            using var owned = work;
            Assert.IsTrue(owned.Execute());
            Assert.AreEqual(revision, run.ReadRevision());
            await run.FinishUnchanged(begin.Tick);
            var commit = await run.Begin();
            using var reservation = save ? run.Terrain.TryReserveCheckpoint(commit.Tick) : null;
            if (save)
                Assert.IsNotNull(reservation);
            await run.Publish(owned, commit.Tick);
            return reservation?.TryTake();
        }

        VolumeRayHit[] ReadMine(VolumeStreamingLease terrain)
        {
            Assert.IsTrue(terrain.TryReadDensity(new(descriptor.WorldId, descriptor.PlanetId, deposit), out var density));
            Assert.IsGreaterThan(0L, density);
            var hits = new VolumeRayHit[8];
            var ray = terrain.Raycast(new(deposit.X - 1, deposit.Y + 0.5, deposit.Z + 0.5),
                new(upper.X + 1, deposit.Y + 0.5, deposit.Z + 0.5), hits);
            Assert.AreEqual(VolumeQueryStatus.Contact, ray.Status);
            Assert.AreEqual(2, ray.Count);
            return hits[..ray.Count];
        }
    }

    private sealed record CaveInventory(WorldId World, ulong OreInventoryRevision, byte Deposits) : IVolumeOreInventoryState
    {
        public ComponentTarget OreInventoryTarget => new(new(World, 1, 1), new(7));
        public IWorldStateRoot StageOreDepletion(in VolumeOreDepletion depletion) =>
            this with { OreInventoryRevision = OreInventoryRevision + 1, Deposits = checked((byte)(Deposits + depletion.Deposits)) };
    }

    private static SaveGameValidation Validate(VolumeCheckpoint checkpoint) =>
        checkpoint.Game.SequenceEqual(new byte[] { 42 }) ? SaveGameValidation.Valid : SaveGameValidation.Malformed;

    private static ShapeEvidence CheckGeometry(VolumeStreamingLease lease, Shape shape, bool edited)
    {
        var meshes = lease.Meshes.ToArray();
        var triangles = meshes.SelectMany(mesh => mesh.Triangles.ToArray()).ToArray();
        Assert.IsGreaterThan(0, triangles.Length);
        Assert.AreEqual(edited ? shape.AfterComponents : shape.BeforeComponents, CountClosedComponents(meshes));
        if (edited && shape.Operation == VolumeBrushOperation.Add)
            Assert.IsTrue(triangles.Any(triangle => triangle.A.Material == 7 || triangle.B.Material == 7 || triangle.C.Material == 7));

        var hits = new VolumeRayHit[16];
        var ray = lease.Raycast(shape.RayStart, shape.RayEnd, hits);
        var count = edited ? shape.AfterHits : shape.BeforeHits;
        Assert.AreEqual(count == 0 ? VolumeQueryStatus.Clear : VolumeQueryStatus.Contact, ray.Status);
        Assert.AreEqual(count, ray.Count);
        for (var i = 1; i < ray.Count; i++)
            Assert.IsGreaterThan(hits[i - 1].Distance, hits[i].Distance);

        var body = new VolumeCapsule(shape.SweepStart, shape.SweepStart, 0.25);
        var sweep = lease.Sweep(body, shape.SweepTravel);
        Assert.AreEqual(edited ? shape.AfterSweep : shape.BeforeSweep, sweep.Status);
        if (sweep.Status == VolumeQueryStatus.Contact)
        {
            Assert.IsGreaterThan(0.0, sweep.Fraction);
            Assert.IsLessThan(1.0, sweep.Fraction);
        }
        return new(lease.Info.PlanIdentity, meshes.Select(mesh => mesh.Origin).ToArray(), triangles, hits[..ray.Count], sweep);
    }

    private static int CountClosedComponents(PlanetVolumeMesh[] meshes)
    {
        var edges = new Dictionary<(Vector3, Vector3), (int Count, int Orientation)>();
        foreach (var mesh in meshes)
        {
            var origin = new Vector3(mesh.Origin.X, mesh.Origin.Y, mesh.Origin.Z);
            foreach (var triangle in mesh.Triangles)
            {
                var a = origin + triangle.A.Position;
                var b = origin + triangle.B.Position;
                var c = origin + triangle.C.Position;
                AddEdge(a, b);
                AddEdge(b, c);
                AddEdge(c, a);
            }
        }
        foreach (var edge in edges)
            Assert.AreEqual((2, 0), edge.Value, $"Open or inconsistently wound edge {edge.Key}.");

        var neighbors = new Dictionary<Vector3, List<Vector3>>();
        foreach (var (a, b) in edges.Keys)
        {
            Connect(a, b);
            Connect(b, a);
        }
        var visited = new HashSet<Vector3>();
        var pending = new Stack<Vector3>();
        var components = 0;
        foreach (var vertex in neighbors.Keys)
        {
            if (!visited.Add(vertex))
                continue;
            components++;
            pending.Push(vertex);
            while (pending.TryPop(out var current))
                foreach (var neighbor in neighbors[current])
                    if (visited.Add(neighbor))
                        pending.Push(neighbor);
        }
        return components;

        void Connect(Vector3 from, Vector3 to)
        {
            if (!neighbors.TryGetValue(from, out var adjacent))
                neighbors.Add(from, adjacent = []);
            adjacent.Add(to);
        }
        void AddEdge(Vector3 a, Vector3 b)
        {
            var order = (a.X, a.Y, a.Z).CompareTo((b.X, b.Y, b.Z));
            Assert.AreNotEqual(0, order);
            var key = order < 0 ? (a, b) : (b, a);
            var edge = edges.GetValueOrDefault(key);
            edges[key] = (edge.Count + 1, edge.Orientation + Math.Sign(order));
        }
    }

    private sealed record ShapeEvidence(string PlanIdentity, LatticeCoordinate[] MeshOrigins,
        VolumeTriangle[] Triangles, VolumeRayHit[] Hits, VolumeSweepResult Sweep);

    private sealed record Shape(Func<LatticeCoordinate, long> Field, VolumeBrushOperation Operation,
        LatticeCoordinate Minimum, LatticeCoordinate Maximum, PlanetVector RayStart, PlanetVector RayEnd,
        int BeforeHits, int AfterHits, int BeforeComponents, int AfterComponents,
        PlanetVector SweepStart, PlanetVector SweepTravel,
        VolumeQueryStatus BeforeSweep, VolumeQueryStatus AfterSweep)
    {
        internal static Shape For(string name) => name switch
        {
            "shells" => new(p => Math.Min(Shell(p, -10, 5, 2), Shell(p, 10, 5, 2)), VolumeBrushOperation.Add,
                new(-5, -2, -2), new(5, 2, 2), new(-20, 0, 0), new(20, 0, 0), 8, 6, 4, 3,
                new(0, 0, 5), new(0, 0, -10), VolumeQueryStatus.Clear, VolumeQueryStatus.Contact),
            "overhang" => new(p => Math.Min(Box(p, 0, 5, 6, 2, 1), Box(p, -5, 1, 1, 2, 4)), VolumeBrushOperation.Remove,
                new(1, -2, 2), new(7, 2, 8), new(3, 0, -4), new(3, 0, 12), 2, 0, 1, 1,
                new(3, 0, -4), new(0, 0, 16), VolumeQueryStatus.Contact, VolumeQueryStatus.Clear),
            "cavity" => new(p => Shell(p, 0, 7, 3), VolumeBrushOperation.Remove,
                new(-1, -1, 2), new(1, 1, 8), new(0, 0, -12), new(0, 0, 12), 4, 2, 2, 1,
                default, new(0, 0, 12), VolumeQueryStatus.Contact, VolumeQueryStatus.Clear),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        private static long Shell(LatticeCoordinate p, long x, long outer, long inner) =>
            Math.Max(Box(p, x, 0, outer, outer, outer), -Box(p, x, 0, inner, inner, inner));

        private static long Box(LatticeCoordinate p, long x, long z, long width, long depth, long height) =>
            256 * Math.Max(Math.Max(Math.Abs(p.X - x) - width, Math.Abs(p.Y) - depth), Math.Abs(p.Z - z) - height) - 128;
    }

    private sealed class ShapeSource(string name, Shape shape) : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor { get; } = new(new Guid("19708eb7-24e8-41dc-abda-e118bc082b04"),
            new Guid("26a26c9d-0f3b-45c9-8afe-69fa4a75fb38"), 42, 1, 1, 1,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wp11-q02-shape-v1/" + name))), 128);
        public long SampleDensity(LatticeCoordinate p) => shape.Field(p);
        public ushort SampleMaterial(LatticeCoordinate cell) => 3;
    }

    private sealed class ShapeClock : CoreRuntimeFixture.TestClock { }

    private sealed class ShapeRun : IDisposable
    {
        private readonly CoreRuntimeFixture _runtime = new(new ShapeClock());
        private readonly IPlanetBrickSource _source;
        private readonly CaveInventory? _initialInventory;
        private ulong _frame;
        private readonly ulong _revision;
        private ulong _tick;

        internal ShapeRun(IPlanetBrickSource source, VolumeCheckpoint? checkpoint = null)
        {
            _source = source;
            _tick = checkpoint?.Tick ?? 0;
            _revision = checkpoint?.TerrainRevision ?? 1;
            World = _runtime.OpenWorld(completedTick: _tick);
            MeshLimit = source is PlanetGeologyField ? 32_000_000 : MeshBytes;
            _initialInventory = source is PlanetGeologyField
                ? new(World.Publication.Committed.Read().Root.World, 1, checkpoint is null ? (byte)0 : checkpoint.Game[0]) : null;
            Selection = Select();
            Credits = new(VolumeStreamingCoordinator.RequiredCpuBytes(Selection, MeshLimit) + OverlayLimit * 64 + 4096,
                0, 0, 0, 1, 1, 1, 64, 1);
            var baseline = Credits.Retained + Credits.Retained;
            baseline = baseline with { LeaseSlots = baseline.LeaseSlots + 8 };
            var limits = new VolumeStreamingLimits(baseline + Credits + Credits + Credits + Credits,
                baseline, Credits, Credits, Credits, Credits, 12, 8, 256);
            Terrain = checkpoint is null ? new(World, source, limits, OverlayLimit) :
                checkpoint.RestoreTerrain(World, source, limits, OverlayLimit, source is PlanetGeologyField
                    ? static material => material is >= 21 and <= 24 : static material => material is 3 or 7);
        }

        internal WorldSafetyPorts World { get; }
        internal VolumeStreamingCoordinator Terrain { get; }
        internal VolumeStreamingSelection Selection { get; }
        internal VolumeResources Credits { get; }
        internal long MeshLimit { get; }

        internal VolumeStreamingSelection Select(VolumeEditDependencies? edit = null)
        {
            if (_source is not PlanetGeologyField)
                return VolumeStreamingSelection.Select(_source.Descriptor, default, new(-1, 0, 0));
            var center = new PlanetVector(64, 33, 26);
            return VolumeStreamingSelection.SelectCave(_source.Descriptor, center, new(-1, 0, 0),
                new(center, center with { Z = 27 }, 0.5), default, edit);
        }

        internal async Task Build()
        {
            Assert.IsTrue(Terrain.ConfirmMask(Terrain.View));
            Assert.AreEqual(VolumeAdmission.Accepted,
                Terrain.TryStage(VolumeWorkClass.Movement, Selection, 0, _revision, MeshLimit, Credits, _source, out var work));
            Assert.IsNotNull(work);
            using var owned = work;
            Assert.IsTrue(owned.Execute());
            var begin = await Begin();
            await Publish(owned, begin.Tick);
        }

        internal VolumeStreamingLease Read() => Terrain.TryRead() ?? throw new AssertFailedException("Missing committed terrain.");
        internal ulong ReadRevision() => ((VolumeWorldRoot)World.Publication.Committed.Read().Root).Terrain.TerrainRevision;

        internal void Hold()
        {
            Terrain.ChangeView(Terrain.View.Frame, Terrain.View.RendererEpoch);
            Assert.IsTrue(Terrain.ConfirmMask(Terrain.View));
        }

        internal Task<BarrierStamp> Begin() =>
            CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, ++_tick, BarrierKind.BeginTick);

        internal async Task<BarrierStamp> OpenMovement(VolumeStreamingLease lease)
        {
            using var frame = Terrain.TryRegisterFrame(lease.Info.Generation, lease.Info.View, ++_frame);
            Assert.IsNotNull(frame);
            Assert.IsTrue(Terrain.AcknowledgeFrame(frame));
            var begin = await Begin();
            Assert.IsTrue(Terrain.BeginTick(begin));
            return begin;
        }

        internal async Task FinishUnchanged(ulong tick)
        {
            var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, tick, BarrierKind.CommitResults);
            CoreRuntimeFixture.PublishObserver(World, stamp, (int)tick);
        }

        internal async Task Publish(VolumeStreamingWork work, ulong tick)
        {
            var candidates = await CoreRuntimeFixture.SealAsync(World, tick, BarrierKind.CommitResults);
            var initial = World.Publication.Committed.Read().Root is VolumeWorldRoot ? null : _initialInventory;
            var prepared = Terrain.TryPreparePublication(work.Generation, candidates, [], initial);
            Assert.IsNotNull(prepared);
            var result = World.Publication.Finalization.TryFinalize(prepared);
            Assert.AreEqual(BarrierStatus.Committed, result.Status);
            CoreRuntimeFixture.PublishObserver(World, result.Stamp!.Value, (int)tick);
        }

        public void Dispose()
        {
            Terrain.Stop();
            _runtime.Dispose();
        }
    }

    private sealed class OwnedDirectory(DirectoryInfo path) : IDisposable
    {
        internal DirectoryInfo Path { get; } = path;
        public void Dispose() => Path.Delete(recursive: true);
    }
}
