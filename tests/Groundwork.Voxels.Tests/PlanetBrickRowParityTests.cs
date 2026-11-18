using System.Reflection;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetBrickRowParityTests
{
    // A wrapper deliberately hides the concrete field/overlay type from brick-store dispatch.
    // Its public calls remain the independent scalar oracle, including Euclidean negative grids.
    [TestMethod]
    [DataRow(0L, 1, false)]
    [DataRow(42L, 2, true)]
    [DataRow(-1L, 17, false)]
    [DataRow(long.MinValue, 31, true)]
    [DataRow(937L, 97, false)]
    [DataRow(123456789L, int.MaxValue, true)]
    public void ColdBrickRowsMatchEveryScalarDensityAndMaterial(long seed, int scale, bool profiled)
    {
        var field = Field(unchecked((ulong)seed), scale);
        AssertPayloadParity(field, new LatticeBrick(-2, -1, 0), profiled);
        AssertPayloadParity(field, new LatticeBrick(0, 0, -1), !profiled);
    }

    [TestMethod]
    public void RowsAtBothDensityDomainEdgesMatchScalarSampling()
    {
        var field = Field(ulong.MaxValue, 17);
        var edge = PlanetDensityField.MaximumCoordinate / PlanetDescriptor.BrickEdge;
        AssertPayloadParity(field, new LatticeBrick(-edge, edge - 1, -edge), profiled: true);
        AssertPayloadParity(field, new LatticeBrick(edge - 1, -edge, edge - 1), profiled: false);
    }

    [TestMethod]
    public void ParallelAdjacentBricksUseIndependentRowsAndKeepScalarSharedFaces()
    {
        var field = Field(0xDEADBEEF12345678UL, 11);
        var keys = Enumerable.Range(0, 8).Select(i =>
            new LatticeBrick(-1 + (i & 1), -1 + ((i >> 1) & 1), -1 + (i >> 2))).ToArray();
        var rowStore = new PlanetBrickStore(field, keys.Length * PlanetBrickStore.BrickPayloadBytes, keys.Length);
        var scalar = new ScalarSource(field);
        var scalarStore = new PlanetBrickStore(scalar, keys.Length * PlanetBrickStore.BrickPayloadBytes, keys.Length);
        Parallel.ForEach(keys, key => Assert.IsTrue(Generate(rowStore, key, profiled: true)));
        foreach (var key in keys)
        {
            Assert.IsTrue(scalarStore.TryGenerate(key));
            ComparePayloads(rowStore, scalarStore, key);
        }
        Assert.AreEqual(keys.Length * PlanetBrickStore.VertexCount, scalar.DensityCalls);
        Assert.AreEqual(keys.Length * PlanetBrickStore.CellCount, scalar.MaterialCalls);
    }

    [TestMethod]
    public void AddedThenRemovedOverlayRowsMatchScalarAndDoNotLeakEarlierPayloads()
    {
        var field = Field(42, 13);
        var initial = Overlay(field);
        var added = Apply(initial, new VolumeBrushIntent(1, 1, 0,
            new LatticeCoordinate(-2, -1, -1), new LatticeCoordinate(1, 1, 1),
            VolumeBrushOperation.Add, 23));
        var removed = Apply(added, new VolumeBrushIntent(2, 2, 1,
            new LatticeCoordinate(-1, 0, 0), new LatticeCoordinate(0, 1, 1),
            VolumeBrushOperation.Remove));
        var key = new LatticeBrick(-1, 0, 0);
        AssertPayloadParity(added, key, profiled: true);
        AssertPayloadParity(removed, key, profiled: true);
        AssertPayloadParity(initial, key, profiled: false);

        // Explicit edited values distinguish the snapshots even where the natural surface is solid.
        Assert.AreEqual(Math.Min(field.SampleDensity(new LatticeCoordinate(-1, 0, 0)), -128L),
            added.SampleDensity(new LatticeCoordinate(-1, 0, 0)));
        Assert.AreEqual(128L, removed.SampleDensity(new LatticeCoordinate(-1, 0, 0)));
        Assert.AreEqual((ushort)23, added.SampleMaterial(new LatticeCoordinate(-2, 0, 0)));
        Assert.AreEqual(field.SampleMaterial(new LatticeCoordinate(-2, 0, 0)),
            removed.SampleMaterial(new LatticeCoordinate(-2, 0, 0)));

        // Also exercise an overlay whose baseline is an unsupported concrete source. It must
        // retain the generic scalar fallback rather than assuming a PlanetDensityField.
        var genericOverlay = Apply(Overlay(new ScalarSource(field)),
            new VolumeBrushIntent(1, 1, 0, new LatticeCoordinate(-2, -1, -1),
                new LatticeCoordinate(1, 1, 1), VolumeBrushOperation.Add, 23));
        AssertPayloadParity(genericOverlay, key, profiled: true);
    }

    private static PlanetDensityField Field(ulong seed, int scale)
    {
        var recipe = new PlanetLandformRecipe(scale, -513, 1281,
            new TerrainReliefLayer(Math.Max(1, scale / 2), 1537),
            new TerrainReliefLayer(Math.Max(1, scale / 3), 1025),
            new TerrainReliefLayer(Math.Max(1, scale / 5), 193), scale, 7, 19);
        var descriptor = new PlanetDescriptor(new Guid("00112233-4455-6677-8899-aabbccddeeff"),
            new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"), seed, 1, 1, 3,
            recipe.ParameterSha256, 128, cellSizeMeters: 0.5);
        return new PlanetDensityField(descriptor, recipe);
    }

    private static void AssertPayloadParity(IPlanetBrickSource source, LatticeBrick key, bool profiled)
    {
        var rows = new PlanetBrickStore(source, PlanetBrickStore.BrickPayloadBytes, 1);
        var scalar = new ScalarSource(source);
        var baseline = new PlanetBrickStore(scalar, PlanetBrickStore.BrickPayloadBytes, 1);
        Assert.IsTrue(Generate(rows, key, profiled));
        Assert.IsTrue(baseline.TryGenerate(key));
        ComparePayloads(rows, baseline, key);
        Assert.AreEqual(PlanetBrickStore.VertexCount, scalar.DensityCalls);
        Assert.AreEqual(PlanetBrickStore.CellCount, scalar.MaterialCalls);
        Assert.IsTrue(rows.Evict(key));
        Assert.IsTrue(baseline.Evict(key));
        Assert.AreEqual(0L, rows.ChargedPayloadBytes);
        Assert.AreEqual(0L, baseline.ChargedPayloadBytes);
    }

    // Reflection keeps these integration tests outside the production assembly's internals.
    private static bool Generate(PlanetBrickStore store, LatticeBrick key, bool profiled)
    {
        if (!profiled)
            return store.TryGenerate(key);
        var method = typeof(PlanetBrickStore).GetMethod("TryGenerateProfiled", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return (bool)method.Invoke(store, [key, null, CancellationToken.None])!;
    }

    private static IPlanetBrickSource Overlay(IPlanetBrickSource source)
    {
        var type = typeof(PlanetBrickStore).Assembly.GetType("Groundwork.Voxels.VolumeEditOverlay");
        Assert.IsNotNull(type);
        var overlay = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [source], culture: null);
        Assert.IsNotNull(overlay);
        return (IPlanetBrickSource)overlay;
    }

    private static IPlanetBrickSource Apply(IPlanetBrickSource overlay, VolumeBrushIntent brush)
    {
        var method = overlay.GetType().GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        var result = method.Invoke(overlay, [brush, 2048, (Action)(() => { }), null]);
        Assert.IsNotNull(result);
        return (IPlanetBrickSource)result;
    }

    private static void ComparePayloads(PlanetBrickStore rows, PlanetBrickStore scalar, LatticeBrick key)
    {
        using var actual = rows.TryAcquire(key);
        using var expected = scalar.TryAcquire(key);
        Assert.IsNotNull(actual);
        Assert.IsNotNull(expected);
        for (var z = 0; z < PlanetBrickStore.VertexEdge; z++)
        for (var y = 0; y < PlanetBrickStore.VertexEdge; y++)
        for (var x = 0; x < PlanetBrickStore.VertexEdge; x++)
        {
            var reference = expected.ReadDensity(x, y, z);
            var value = actual.ReadDensity(x, y, z);
            if (reference != value)
                Assert.Fail($"Density mismatch at {key.Vertex(x, y, z)}: scalar={reference}, row={value}.");
        }
        for (var z = 0; z < PlanetBrickStore.CellEdge; z++)
        for (var y = 0; y < PlanetBrickStore.CellEdge; y++)
        for (var x = 0; x < PlanetBrickStore.CellEdge; x++)
        {
            var reference = expected.ReadMaterial(x, y, z);
            var value = actual.ReadMaterial(x, y, z);
            if (reference != value)
                Assert.Fail($"Material mismatch at {key.Vertex(x, y, z)}: scalar={reference}, row={value}.");
        }
    }

    private sealed class ScalarSource(IPlanetBrickSource source) : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => source.Descriptor;
        public int DensityCalls { get; private set; }
        public int MaterialCalls { get; private set; }

        public long SampleDensity(LatticeCoordinate vertex)
        {
            DensityCalls++;
            return source.SampleDensity(vertex);
        }

        public ushort SampleMaterial(LatticeCoordinate cell)
        {
            MaterialCalls++;
            return source.SampleMaterial(cell);
        }
    }
}
