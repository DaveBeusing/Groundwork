using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetBrickReadSessionTests
{
    private const int Bytes = PlanetBrickStore.BrickPayloadBytes;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly PlanetDescriptor Descriptor = new(
        new Guid("00112233-4455-6677-8899-aabbccddeeff"),
        new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"),
        42, 1, 1, 1, new string('a', 64), 128);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SessionReadsEveryScalarValueAfterParentDisposalAndEviction(bool profiled)
    {
        var source = new Source();
        var store = new PlanetBrickStore(source, Bytes, 1);
        var key = new LatticeBrick(-1, 0, -1);
        Assert.IsTrue(Generate(store, key, profiled));
        using var lease = Acquire(store, key);
        var access = new SessionAccess(lease);
        access.Enter();
        try
        {
            lease.Dispose();
            Assert.IsTrue(store.Evict(key));
            Assert.AreEqual((long)Bytes, store.ChargedPayloadBytes);
            Assert.ThrowsExactly<ObjectDisposedException>(() => lease.ReadDensity(0, 0, 0));
            Assert.ThrowsExactly<ObjectDisposedException>(() => lease.ReadMaterial(0, 0, 0));

            for (var z = 0; z < PlanetBrickStore.VertexEdge; z++)
            for (var y = 0; y < PlanetBrickStore.VertexEdge; y++)
            for (var x = 0; x < PlanetBrickStore.VertexEdge; x++)
                Assert.AreEqual(source.SampleDensity(key.Vertex(x, y, z)), access.Density(x, y, z));
            for (var z = 0; z < PlanetBrickStore.CellEdge; z++)
            for (var y = 0; y < PlanetBrickStore.CellEdge; y++)
            for (var x = 0; x < PlanetBrickStore.CellEdge; x++)
                Assert.AreEqual(source.SampleMaterial(key.Vertex(x, y, z)), access.Material(x, y, z));

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => access.Density(33, 0, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => access.Density(0, -1, 0));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => access.Material(0, 32, 0));
        }
        finally
        {
            access.Exit();
        }
        Assert.AreEqual(0L, store.ChargedPayloadBytes);
    }

    [TestMethod]
    public void LastSessionReturnsExactlyTheExistingLeaseSlotAndPayloadCharge()
    {
        var source = new Source();
        var store = new PlanetBrickStore(source, Bytes, 1);
        var key = new LatticeBrick();
        var next = new LatticeBrick(2, 0, 0);
        Assert.IsTrue(store.TryGenerate(key));
        using var lease = Acquire(store, key);
        var access = new SessionAccess(lease);
        var sessions = 0;
        access.Enter();
        sessions++;
        access.Enter();
        sessions++;
        try
        {
            lease.Dispose();
            lease.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(access.Enter);
            // Sessions need no second slot, but must not make this slot available early.
            Assert.IsNull(store.TryAcquire(key));
            Assert.IsTrue(store.Evict(key));
            Assert.IsFalse(store.TryGenerate(next));
            Assert.AreEqual((long)Bytes, store.ChargedPayloadBytes);
            access.Exit();
            sessions--;
            Assert.AreEqual((long)Bytes, store.ChargedPayloadBytes);
            Assert.IsFalse(store.TryGenerate(next));
            Assert.AreEqual(source.SampleDensity(key.Vertex(32, 32, 32)), access.Density(32, 32, 32));
        }
        finally
        {
            while (sessions > 0)
            {
                access.Exit();
                sessions--;
            }
        }

        Assert.AreEqual(0L, store.ChargedPayloadBytes);
        Assert.ThrowsExactly<InvalidOperationException>(access.Exit);
        Assert.ThrowsExactly<InvalidOperationException>(() => access.Density(0, 0, 0));
        Assert.IsTrue(store.TryGenerate(next));
        using (var replacement = Acquire(store, next))
            Assert.AreEqual(source.SampleMaterial(next.Vertex(0, 0, 0)), replacement.ReadMaterial(0, 0, 0));
        Assert.IsTrue(store.Evict(next));
        Assert.AreEqual(0L, store.ChargedPayloadBytes);
    }

    [TestMethod]
    public void UnpairedSessionReadsAndExitsFailWithoutChangingPublicLeaseLifetime()
    {
        var source = new Source();
        var store = new PlanetBrickStore(source, Bytes, 1);
        var key = new LatticeBrick();
        Assert.IsTrue(store.TryGenerate(key));
        using var lease = Acquire(store, key);
        var access = new SessionAccess(lease);
        Assert.ThrowsExactly<InvalidOperationException>(() => access.Density(0, 0, 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => access.Material(0, 0, 0));
        Assert.ThrowsExactly<InvalidOperationException>(access.Exit);
        access.Enter();
        access.Exit();
        Assert.ThrowsExactly<InvalidOperationException>(access.Exit);
        Assert.AreEqual(source.SampleDensity(default), lease.ReadDensity(0, 0, 0));
        Assert.IsTrue(store.Evict(key));
        Assert.AreEqual((long)Bytes, store.ChargedPayloadBytes);
        lease.Dispose();
        Assert.AreEqual(0L, store.ChargedPayloadBytes);
    }

    [TestMethod]
    public void DisposedLeaseRejectsNewSessionWithoutResurrectingItsReservation()
    {
        var store = new PlanetBrickStore(new Source(), Bytes, 1);
        var key = new LatticeBrick();
        Assert.IsTrue(store.TryGenerate(key));
        using var lease = Acquire(store, key);
        var access = new SessionAccess(lease);
        lease.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(access.Enter);
        Assert.IsTrue(store.Evict(key));
        Assert.AreEqual(0L, store.ChargedPayloadBytes);
        Assert.ThrowsExactly<ObjectDisposedException>(access.Enter);
        Assert.AreEqual(0L, store.ChargedPayloadBytes);
    }

    [TestMethod]
    public async Task SessionAdmissionRacingParentDisposalHasOnlyPinnedOrRejectedOutcomes()
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var source = new Source();
            var store = new PlanetBrickStore(source, Bytes, 1);
            var key = new LatticeBrick();
            Assert.IsTrue(store.TryGenerate(key));
            using var lease = Acquire(store, key);
            var access = new SessionAccess(lease);
            using var start = new Barrier(2);
            using var admitted = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var wonAdmission = false;
            var reader = Task.Run(() =>
            {
                try
                {
                    Assert.IsTrue(start.SignalAndWait(Timeout));
                    try
                    {
                        access.Enter();
                        wonAdmission = true;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Parent disposal won the same gate; no reader was admitted.
                    }
                }
                finally
                {
                    admitted.Set();
                }
                if (!wonAdmission)
                    return;
                try
                {
                    Assert.IsTrue(resume.Wait(Timeout));
                    Assert.AreEqual(source.SampleDensity(key.Vertex(32, 2, 3)), access.Density(32, 2, 3));
                }
                finally
                {
                    access.Exit();
                }
            });
            try
            {
                Assert.IsTrue(start.SignalAndWait(Timeout));
                lease.Dispose();
                Assert.IsTrue(admitted.Wait(Timeout));
                Assert.IsTrue(store.Evict(key));
                Assert.AreEqual(wonAdmission ? (long)Bytes : 0L, store.ChargedPayloadBytes);
            }
            finally
            {
                resume.Set();
                await reader;
            }
            Assert.AreEqual(0L, store.ChargedPayloadBytes);
            Assert.AreEqual(BrickAvailability.Ungenerated, store.GetAvailability(key));
        }
    }

    [TestMethod]
    public void FailedPlanSessionAcquisitionRollsBackEarlierPins()
    {
        var (store, inputs) = Window();
        inputs[^1].Brick.Dispose();
        try
        {
            // Models disposal after Borrow's validation but before its constructor pins
            // the final input. A failed constructor must unwind every earlier session.
            var constructor = typeof(VolumeMeshPlan).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(VolumeMeshInput[]), typeof(LatticeBrick), typeof(int), typeof(int), typeof(int),
                    typeof(ulong), typeof(bool), typeof(int[]), typeof(byte[])], modifiers: null);
            Assert.IsNotNull(constructor);
            Assert.ThrowsExactly<ObjectDisposedException>(() => Invoke(() => constructor.Invoke(
                [inputs, new LatticeBrick(), 2, 2, 2, 1UL, false, null, null])));
        }
        finally
        {
            RetireAndDispose(store, inputs);
        }
        Assert.AreEqual(0L, store.ChargedPayloadBytes);
        foreach (var input in inputs)
            Assert.AreEqual(BrickAvailability.Ungenerated, store.GetAvailability(input.Brick.Key));
    }

    [TestMethod]
    public void BorrowedPlanBuildsAfterParentsAreDisposedAndRetiredThenSettlesOnce()
    {
        var (store, inputs) = Window();
        var plan = Borrow(inputs, 1);
        try
        {
            RetireAndDispose(store, inputs);
            Assert.AreEqual(inputs.Length * (long)Bytes, store.ChargedPayloadBytes);
            var mesh = PlanetVolumeMesher.Build(plan, plan.Minimum, long.MaxValue);
            Assert.IsNotNull(mesh);
            Assert.IsGreaterThan(0, mesh.Triangles.Length);
            Assert.AreEqual(plan.Identity, mesh.PlanIdentity);
        }
        finally
        {
            plan.Dispose();
            RetireAndDispose(store, inputs);
        }
        plan.Dispose();
        Assert.AreEqual(0L, store.ChargedPayloadBytes);
        Assert.ThrowsExactly<ObjectDisposedException>(() => PlanetVolumeMesher.Build(plan, plan.Minimum, long.MaxValue));
    }

    [TestMethod]
    public void DisposedPlanRetainsSessionsUntilItsAdmittedReaderExits()
    {
        var (store, inputs) = Window();
        var plan = Borrow(inputs, 1);
        var enter = Bind<Action>(plan, "Enter");
        var exit = Bind<Action>(plan, "Exit");
        var read = Bind<Func<Vector3, long>>(plan, "Read");
        enter();
        try
        {
            RetireAndDispose(store, inputs);
            plan.Dispose();
            plan.Dispose();
            Assert.AreEqual(inputs.Length * (long)Bytes, store.ChargedPayloadBytes);
            Assert.AreEqual(new Source().SampleDensity(new(1, 2, 3)), read(new(1, 2, 3)));
            Assert.ThrowsExactly<ObjectDisposedException>(() => PlanetVolumeMesher.Build(plan, plan.Minimum, long.MaxValue));
        }
        finally
        {
            exit();
            plan.Dispose();
            RetireAndDispose(store, inputs);
        }
        Assert.AreEqual(0L, store.ChargedPayloadBytes);
        foreach (var input in inputs)
            Assert.ThrowsExactly<InvalidOperationException>(() => new SessionAccess(input.Brick).Density(0, 0, 0));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SessionPinsTheExactImmutableEditSnapshotInsteadOfAnotherRevision(bool profiled)
    {
        var baseline = new Source();
        var original = Overlay(baseline);
        var added = Apply(original, new VolumeBrushIntent(1, 1, 0, new(1, 1, 1), new(2, 2, 2),
            VolumeBrushOperation.Add, 23));
        var removed = Apply(added, new VolumeBrushIntent(2, 2, 1, new(1, 1, 1), new(2, 2, 2),
            VolumeBrushOperation.Remove));
        var sources = new[] { original, added, removed };
        var stores = sources.Select(source => new PlanetBrickStore(source, Bytes, 1)).ToArray();
        var key = new LatticeBrick();
        var leases = new List<PlanetBrickStore.BrickLease>();
        var sessions = new List<SessionAccess>();
        try
        {
            for (var i = 0; i < stores.Length; i++)
            {
                Assert.IsTrue(Generate(stores[i], key, profiled));
                var lease = Acquire(stores[i], key);
                leases.Add(lease);
                var access = new SessionAccess(lease);
                access.Enter();
                sessions.Add(access);
                lease.Dispose();
                Assert.IsTrue(stores[i].Evict(key));
            }

            for (var i = 0; i < sessions.Count; i++)
            {
                Assert.AreEqual(sources[i].SampleDensity(new(1, 1, 1)), sessions[i].Density(1, 1, 1));
                Assert.AreEqual(sources[i].SampleMaterial(new(1, 1, 1)), sessions[i].Material(1, 1, 1));
                Assert.AreEqual((long)Bytes, stores[i].ChargedPayloadBytes);
            }
            Assert.AreNotEqual(sessions[0].Density(1, 1, 1), sessions[1].Density(1, 1, 1));
            Assert.AreEqual(128L, sessions[2].Density(1, 1, 1));
            Assert.AreEqual((ushort)23, sessions[1].Material(1, 1, 1));
            Assert.AreEqual(baseline.SampleMaterial(new(1, 1, 1)), sessions[2].Material(1, 1, 1));
        }
        finally
        {
            foreach (var access in sessions)
                access.Exit();
            foreach (var lease in leases)
                lease.Dispose();
            foreach (var store in stores)
                store.Evict(key);
        }
        foreach (var store in stores)
            Assert.AreEqual(0L, store.ChargedPayloadBytes);
    }

    private static (PlanetBrickStore Store, VolumeMeshInput[] Inputs) Window()
    {
        var store = new PlanetBrickStore(new Source(), 8L * Bytes, 8);
        var inputs = new VolumeMeshInput[8];
        for (var i = 0; i < inputs.Length; i++)
        {
            var key = new LatticeBrick(i & 1, (i >> 1) & 1, i >> 2);
            Assert.IsTrue(store.TryGenerate(key));
            inputs[i] = new(Acquire(store, key), 2);
        }
        return (store, inputs);
    }

    private static void RetireAndDispose(PlanetBrickStore store, VolumeMeshInput[] inputs)
    {
        foreach (var input in inputs)
        {
            store.Evict(input.Brick.Key);
            input.Brick.Dispose();
        }
    }

    private static PlanetBrickStore.BrickLease Acquire(PlanetBrickStore store, LatticeBrick key)
    {
        var lease = store.TryAcquire(key);
        Assert.IsNotNull(lease);
        return lease;
    }

    private static bool Generate(PlanetBrickStore store, LatticeBrick key, bool profiled)
    {
        if (!profiled)
            return store.TryGenerate(key);
        var method = typeof(PlanetBrickStore).GetMethod("TryGenerateProfiled", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return (bool)Invoke(() => method.Invoke(store, [key, null, CancellationToken.None]))!;
    }

    private delegate VolumeMeshPlan BorrowPlan(ReadOnlySpan<VolumeMeshInput> inputs, ulong revision);

    private static VolumeMeshPlan Borrow(VolumeMeshInput[] inputs, ulong revision)
    {
        var method = typeof(VolumeMeshPlan).GetMethod("Borrow", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return method.CreateDelegate<BorrowPlan>()(inputs, revision);
    }

    private static IPlanetBrickSource Overlay(IPlanetBrickSource source)
    {
        var type = typeof(PlanetBrickStore).Assembly.GetType("Groundwork.Voxels.VolumeEditOverlay");
        Assert.IsNotNull(type);
        var result = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [source], culture: null);
        Assert.IsNotNull(result);
        return (IPlanetBrickSource)result;
    }

    private static IPlanetBrickSource Apply(IPlanetBrickSource overlay, VolumeBrushIntent intent)
    {
        var method = overlay.GetType().GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return (IPlanetBrickSource)Invoke(() => method.Invoke(overlay, [intent, 2048, (Action)(() => { }), null]))!;
    }

    private static object? Invoke(Func<object?> action)
    {
        try
        {
            return action();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static T Bind<T>(object target, string name) where T : Delegate
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return method.CreateDelegate<T>(target);
    }

    // Reflection follows the existing tests' approach to internal APIs without adding
    // an InternalsVisibleTo contract. Bound delegates keep the full-payload oracle fast.
    private sealed class SessionAccess(PlanetBrickStore.BrickLease lease)
    {
        public Action Enter { get; } = Bind<Action>(lease, "EnterReadSession");
        public Action Exit { get; } = Bind<Action>(lease, "ExitReadSession");
        public Func<int, int, int, long> Density { get; } = Bind<Func<int, int, int, long>>(lease, "ReadDensityInSession");
        public Func<int, int, int, ushort> Material { get; } = Bind<Func<int, int, int, ushort>>(lease, "ReadMaterialInSession");
    }

    private sealed class Source : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => PlanetBrickReadSessionTests.Descriptor;
        public long SampleDensity(LatticeCoordinate point) => point.X * 1010 + point.Y * 170 - point.Z * 290 - 371;
        public ushort SampleMaterial(LatticeCoordinate point) =>
            (ushort)(7 + (point.X & 1) * 2 + (point.Y & 1) * 4 + (point.Z & 1) * 8);
    }
}
