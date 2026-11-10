using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetBrickStoreTests
{
    private const int Bytes = PlanetBrickStore.BrickPayloadBytes;
    private static readonly PlanetLandformRecipe Recipe =
        new(61, -512, 1280, new(17, 1536), new(11, 1024), new(5, 192), 13, 7, 19);
    private static readonly PlanetDescriptor Planet = new(
        new Guid("00112233-4455-6677-8899-aabbccddeeff"),
        new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"),
        42, 1, 1, 3, Recipe.ParameterSha256, 128, cellSizeMeters: 0.5);

    [TestMethod]
    public void GeneratedBricksShareFacesEdgesAndOneEightBrickCornerAtNegativeCoordinates()
    {
        var field = new PlanetDensityField(Planet, Recipe);
        var store = new PlanetBrickStore(field, 8 * Bytes, 8);
        var keys = Enumerable.Range(0, 8).Select(i =>
            new LatticeBrick(-2 + (i & 1), -1 + ((i >> 1) & 1), -1 + (i >> 2))).ToArray();
        Parallel.ForEach(keys, key => Assert.IsTrue(store.TryGenerate(key)));

        var shared = new LatticeCoordinate(-32, 0, 0);
        var expected = field.SampleDensity(shared);
        foreach (var key in keys)
        {
            using var lease = Acquire(store, key);
            var x = key.X == -2 ? 32 : 0;
            var y = key.Y == -1 ? 32 : 0;
            var z = key.Z == -1 ? 32 : 0;
            Assert.AreEqual(shared, key.Vertex(x, y, z));
            Assert.AreEqual(expected, lease.ReadDensity(x, y, z));
        }

        using var first = Acquire(store, keys[0]);
        using var acrossX = Acquire(store, keys[1]);
        using var acrossXY = Acquire(store, keys[3]);
        for (var z = 0; z <= 32; z++)
        {
            for (var y = 0; y <= 32; y++)
                Assert.AreEqual(first.ReadDensity(32, y, z), acrossX.ReadDensity(0, y, z));
            Assert.AreEqual(first.ReadDensity(32, 32, z), acrossXY.ReadDensity(0, 0, z));
        }

        Assert.AreEqual(field.SampleMaterial(keys[0].Vertex(31, 2, 3)), first.ReadMaterial(31, 2, 3));
        Assert.AreEqual(field.SampleMaterial(keys[1].Vertex(0, 2, 3)), acrossX.ReadMaterial(0, 2, 3));
        Assert.AreEqual(3U, first.Descriptor.MaterialCatalogVersion);
        Assert.AreEqual(0.5, first.Descriptor.CellSizeMeters);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => first.ReadMaterial(32, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => first.ReadDensity(33, 0, 0));
    }

    [TestMethod]
    public void PayloadAndLeaseLimitsKeepEvictedReadersChargedUntilTheLastRelease()
    {
        var calls = 0;
        var source = new Source(_ => { calls++; return -1; });
        var key = new LatticeBrick(-1, 0, 0);
        var next = new LatticeBrick(0, 0, 0);
        var unsupported = new PlanetDescriptor(Planet.WorldId, Planet.PlanetId, Planet.Seed, Planet.GeneratorVersion,
            terrainSchemaVersion: 2, Planet.MaterialCatalogVersion, Planet.ParameterSha256, Planet.RadiusMeters);
        Assert.ThrowsExactly<ArgumentException>(() => new PlanetBrickStore(
            new Source(_ => { calls++; return -1; }, descriptor: unsupported), Bytes, 2));
        var tooSmall = new PlanetBrickStore(source, Bytes - 1, 2);
        Assert.IsFalse(tooSmall.TryGenerate(key));
        Assert.AreEqual(0, calls);
        Assert.AreEqual(0L, tooSmall.ChargedPayloadBytes);
        Assert.ThrowsExactly<OverflowException>(() => tooSmall.TryGenerate(new LatticeBrick(long.MaxValue, 0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            tooSmall.TryGenerate(new LatticeBrick(PlanetDensityField.MaximumCoordinate / 32, 0, 0)));
        Assert.AreEqual(0, calls);

        var store = new PlanetBrickStore(source, Bytes, 2);
        Assert.AreEqual(BrickAvailability.Ungenerated, store.GetAvailability(key));
        Assert.IsTrue(store.TryGenerate(key));
        Assert.IsTrue(store.TryGenerate(key));
        Assert.AreEqual(PlanetBrickStore.VertexCount, calls);
        Assert.AreEqual(BrickAvailability.Known, store.GetAvailability(key));
        using var first = Acquire(store, key);
        using var second = Acquire(store, key);
        Assert.IsNull(store.TryAcquire(key));

        Assert.IsTrue(store.Evict(key));
        Assert.AreEqual(BrickAvailability.Unavailable, store.GetAvailability(key));
        Assert.IsNull(store.TryAcquire(key));
        Assert.IsFalse(store.TryGenerate(next));
        Assert.IsFalse(store.TryGenerate(key));
        Assert.AreEqual((long)Bytes, store.ChargedPayloadBytes);
        Assert.AreEqual(-1L, first.ReadDensity(32, 0, 0));
        first.Dispose();
        first.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => first.ReadDensity(0, 0, 0));
        Assert.AreEqual((long)Bytes, store.ChargedPayloadBytes);
        Assert.AreEqual((ushort)7, second.ReadMaterial(0, 0, 0));
        second.Dispose();
        Assert.AreEqual(0L, store.ChargedPayloadBytes);
        Assert.AreEqual(BrickAvailability.Ungenerated, store.GetAvailability(key));
        Assert.IsTrue(store.TryGenerate(next));
        Assert.IsTrue(store.Evict(next));
        Assert.AreEqual(0L, store.ChargedPayloadBytes);
    }

    [TestMethod]
    public async Task EvictionDuringGenerationPreventsPublicationAndRetainsQuotaUntilSettlement()
    {
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var source = new Source(coordinate =>
        {
            if (coordinate == default)
            {
                entered.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException();
            }
            return 1;
        });
        var store = new PlanetBrickStore(source, Bytes, 1);
        var key = new LatticeBrick();
        var next = new LatticeBrick(1, 0, 0);
        var generation = Task.Run(() => store.TryGenerate(key));
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(BrickAvailability.Generating, store.GetAvailability(key));
            Assert.IsNull(store.TryAcquire(key));
            Assert.IsFalse(store.TryGenerate(key));
            Assert.IsTrue(store.Evict(key));
            Assert.AreEqual(BrickAvailability.Unavailable, store.GetAvailability(key));
            Assert.IsFalse(store.TryGenerate(next));
            Assert.AreEqual((long)Bytes, store.ChargedPayloadBytes);
        }
        finally
        {
            resume.Set();
            Assert.IsFalse(await generation);
        }

        Assert.AreEqual(0L, store.ChargedPayloadBytes);
        Assert.AreEqual(BrickAvailability.Ungenerated, store.GetAvailability(key));
        Assert.IsTrue(store.TryGenerate(next));
    }

    [TestMethod]
    public void FailedOrConflictingGenerationDoesNotPublishAndReturnsQuota()
    {
        var old = new LatticeBrick(-1, -1, -1);
        var next = new LatticeBrick();
        var value = -1;
        var store = new PlanetBrickStore(new Source(_ => value), 2 * Bytes, 1);
        Assert.IsTrue(store.TryGenerate(old));
        using var lease = Acquire(store, old);
        Assert.IsTrue(store.Evict(old));

        value = 1;
        // These bricks meet only at one corner. The old sample is still visible through its lease.
        Assert.ThrowsExactly<InvalidOperationException>(() => store.TryGenerate(next));
        Assert.AreEqual((long)Bytes, store.ChargedPayloadBytes);
        Assert.AreEqual(BrickAvailability.Ungenerated, store.GetAvailability(next));
        Assert.AreEqual(-1L, lease.ReadDensity(32, 32, 32));
        value = -1;
        Assert.IsTrue(store.TryGenerate(next));
        Assert.AreEqual(2L * Bytes, store.ChargedPayloadBytes);

        using var cancellation = new CancellationTokenSource();
        var canceled = new PlanetBrickStore(new Source(_ => { cancellation.Cancel(); return 1; }), Bytes, 1);
        Assert.ThrowsExactly<OperationCanceledException>(() => canceled.TryGenerate(next, cancellation.Token));
        Assert.AreEqual(0L, canceled.ChargedPayloadBytes);
        Assert.AreEqual(BrickAvailability.Ungenerated, canceled.GetAvailability(next));

        var invalid = new PlanetBrickStore(new Source(_ => 1, _ => 0), Bytes, 1);
        Assert.ThrowsExactly<InvalidOperationException>(() => invalid.TryGenerate(next));
        Assert.AreEqual(0L, invalid.ChargedPayloadBytes);
        Assert.IsNull(invalid.TryAcquire(next));
    }

    [TestMethod]
    public void SyntheticCavityOverhangAndDetachedSolidHaveStableVolumeBytes()
    {
        var store = new PlanetBrickStore(new Source(SyntheticDensity,
            cell => (ushort)(7 + (cell.X + cell.Y + cell.Z) % 2)), Bytes, 1);
        var key = new LatticeBrick();
        Assert.IsTrue(store.TryGenerate(key));
        using var lease = Acquire(store, key);
        Assert.IsGreaterThan(0L, lease.ReadDensity(16, 10, 16)); // Enclosed cavity.
        Assert.IsLessThan(0L, lease.ReadDensity(16, 10, 21));    // Cavity wall.
        Assert.IsLessThan(0L, lease.ReadDensity(29, 15, 16));    // Overhang.
        Assert.IsGreaterThan(0L, lease.ReadDensity(29, 10, 16));
        Assert.IsLessThan(0L, lease.ReadDensity(2, 26, 2));      // Detached solid.
        Assert.IsGreaterThan(0L, lease.ReadDensity(2, 23, 2));
        Assert.AreEqual((ushort)7, lease.ReadMaterial(16, 10, 16)); // Material is not occupancy.

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[8];
        for (var z = 0; z < PlanetBrickStore.VertexEdge; z++)
        for (var y = 0; y < PlanetBrickStore.VertexEdge; y++)
        for (var x = 0; x < PlanetBrickStore.VertexEdge; x++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes, lease.ReadDensity(x, y, z));
            hash.AppendData(bytes);
        }
        for (var z = 0; z < PlanetBrickStore.CellEdge; z++)
        for (var y = 0; y < PlanetBrickStore.CellEdge; y++)
        for (var x = 0; x < PlanetBrickStore.CellEdge; x++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, lease.ReadMaterial(x, y, z));
            hash.AppendData(bytes[..2]);
        }

        Assert.AreEqual("3A510900D241BABDA1E85297FFBB2E5132DD60EC87D0C53715C213041D34B773",
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static long SyntheticDensity(LatticeCoordinate position)
    {
        var body = Math.Max(Box(16, 10, 16, 10, 6, 10), -Box(16, 10, 16, 3, 3, 3));
        return Math.Min(body, Math.Min(Box(16, 15, 16, 14, 1, 4), Box(2, 26, 2, 1, 1, 1)));

        long Box(int x, int y, int z, int ex, int ey, int ez) =>
            Math.Max(Math.Abs(position.X - x) - ex,
                Math.Max(Math.Abs(position.Y - y) - ey, Math.Abs(position.Z - z) - ez));
    }

    private static PlanetBrickStore.BrickLease Acquire(PlanetBrickStore store, LatticeBrick key)
    {
        var lease = store.TryAcquire(key);
        Assert.IsNotNull(lease);
        return lease;
    }

    private sealed class Source(Func<LatticeCoordinate, long> density, Func<LatticeCoordinate, ushort>? material = null,
        PlanetDescriptor? descriptor = null)
        : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => descriptor ?? Planet;
        public long SampleDensity(LatticeCoordinate vertex) => density(vertex);
        public ushort SampleMaterial(LatticeCoordinate cell) => material?.Invoke(cell) ?? 7;
    }
}
