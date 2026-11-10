namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetLatticeTests
{
    private const int Intervals = PlanetCoordinates.IntervalsPerPatch;
    private static readonly Guid WorldId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid PlanetId = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly PlanetDescriptor Planet = CreatePlanet();

    [TestMethod]
    public void EveryCubeEdgeSharesScopedKeysInBothDirectionsAcrossAllLodPairsAndRebases()
    {
        Assert.HasCount(12, PlanetCoordinateTests.Seams);
        foreach (var (face, edge, otherFace, otherEdge) in PlanetCoordinateTests.Seams)
        {
            VerifyEdge(face, edge, otherFace, otherEdge);
            VerifyEdge(otherFace, otherEdge, face, edge);
        }
    }

    [TestMethod]
    public void EightCubeCornersShareScopedKeysAcrossThreeFacesMixedLodsAndRebases()
    {
        var corners = new Dictionary<PlanetLatticeSample, HashSet<PlanetFace>>();
        foreach (var face in Enum.GetValues<PlanetFace>())
        {
            for (var u = 0; u <= Intervals; u += Intervals)
            for (var v = 0; v <= Intervals; v += Intervals)
            {
                var root = new PlanetPatchKey(face, 0, 0, 0);
                var expected = Planet.SampleAt(root, u, v);
                if (!corners.TryGetValue(expected, out var faces))
                    corners.Add(expected, faces = []);
                Assert.IsTrue(faces.Add(face));

                for (var level = 0; level <= PlanetCoordinates.MaximumLevel; level++)
                {
                    var last = (1 << level) - 1;
                    var patch = new PlanetPatchKey(face, level, u == 0 ? 0 : last, v == 0 ? 0 : last);
                    Assert.AreEqual(expected, Planet.SampleAt(patch, u, v));
                    VerifyRebase(PlanetCoordinates.Sample(patch, u, v), expected);
                }
            }
        }

        Assert.HasCount(8, corners);
        foreach (var (sample, faces) in corners)
        {
            var vertex = sample.Vertex;
            Assert.IsTrue(faces.SetEquals([
                vertex.X > 0 ? PlanetFace.PositiveX : PlanetFace.NegativeX,
                vertex.Y > 0 ? PlanetFace.PositiveY : PlanetFace.NegativeY,
                vertex.Z > 0 ? PlanetFace.PositiveZ : PlanetFace.NegativeZ
            ]));
        }
    }

    [TestMethod]
    public void SignedBrickDivisionRoundTripsAndSharesUpperBoundaryVertices()
    {
        long[] axes = [long.MinValue, -65, -64, -33, -32, -1, 0, 31, 32, 63, 64, long.MaxValue];
        foreach (var x in axes)
        foreach (var y in axes)
        foreach (var z in axes)
        {
            var vertex = new LatticeCoordinate(x, y, z);
            var (brick, localX, localY, localZ) = vertex.Locate();
            Assert.AreEqual(vertex, brick.Vertex(localX, localY, localZ));
            Assert.IsTrue(localX is >= 0 and < 32 && localY is >= 0 and < 32 && localZ is >= 0 and < 32);
        }

        var shared = new LatticeCoordinate(-32, 0, 32);
        for (var dx = 0; dx <= 1; dx++)
        for (var dy = 0; dy <= 1; dy++)
        for (var dz = 0; dz <= 1; dz++)
            Assert.AreEqual(shared, new LatticeBrick(-1 - dx, -dy, 1 - dz).Vertex(32 * dx, 32 * dy, 32 * dz));

        Assert.ThrowsExactly<OverflowException>(() => new LatticeBrick(long.MaxValue, 0, 0).Vertex(0, 0, 0));
        Assert.ThrowsExactly<OverflowException>(() => new LatticeBrick(long.MinValue, 0, 0).Vertex(32, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LatticeBrick().Vertex(-1, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LatticeBrick().Vertex(0, 33, 0));
    }

    [TestMethod]
    public void KeysHaveStableScopedEncodingAndRejectInvalidIdentity()
    {
        var key = new PlanetLatticeSample(WorldId, PlanetId, new LatticeCoordinate(-1, long.MinValue, long.MaxValue));
        var bytes = new byte[PlanetLatticeSample.EncodedSize];
        key.Write(bytes);
        Assert.AreEqual("00112233445566778899AABBCCDDEEFF102132435465768798A9BACBDCEDFE0F" +
            "FFFFFFFFFFFFFFFF0000000000000080FFFFFFFFFFFFFF7F", Convert.ToHexString(bytes));
        Assert.AreEqual(key, PlanetLatticeSample.Read(bytes));
        Assert.AreNotEqual(key, new PlanetLatticeSample(PlanetId, WorldId, key.Vertex));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetLatticeSample.Read(new byte[bytes.Length]));
        Assert.ThrowsExactly<ArgumentException>(() => PlanetLatticeSample.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.ThrowsExactly<InvalidOperationException>(() => default(PlanetLatticeSample).Write(bytes));
    }

    [TestMethod]
    public void DescriptorAndLatticeRejectInvalidInputsAndRoundMidpointsToEven()
    {
        Assert.AreEqual(new LatticeCoordinate(0, 2, -2), Planet.SampleAt(new PlanetVector(-0.5, 1.5, -2.5)).Vertex);
        Assert.AreEqual(long.MinValue, Planet.SampleAt(new PlanetVector((double)long.MinValue, 0, 0)).Vertex.X);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Planet.SampleAt(new PlanetVector((double)long.MaxValue, 0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Planet.SampleAt(new PlanetVector(0, double.NaN, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Planet.SampleAt(new PlanetVector(0, 0, double.PositiveInfinity)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreatePlanet(radius: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreatePlanet(radius: PlanetDescriptor.MaximumRadiusMeters + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreatePlanet(radius: double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreatePlanet(cellSize: double.Epsilon));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreatePlanet(generatorVersion: 0));
        Assert.ThrowsExactly<ArgumentException>(() => CreatePlanet(hash: new string('g', 64)));
        Assert.AreEqual(new string('a', 64), Planet.ParameterSha256);
        Assert.AreEqual(ulong.MaxValue, Planet.Seed);
        Assert.AreEqual(PlanetFace.PositiveY, PlanetCoordinates.Project(new PlanetVector(0, 1, 1)).Face);
        Assert.AreEqual(PlanetFace.NegativeX, PlanetCoordinates.Project(new PlanetVector(-1, 1, -1)).Face);
        Assert.AreEqual(PlanetFace.NegativeZ, PlanetCoordinates.Project(new PlanetVector(0, 0, -1)).Face);
    }

    [TestMethod]
    public void RandomPlanetScaleRebasesPreserveSerialAndParallelKeys()
    {
        var random = new Random(7901);
        var positions = Enumerable.Range(0, 512).Select(_ => new PlanetVector(
            Axis(), Axis(), Axis())).ToArray();
        var serial = positions.Select(Planet.SampleAt).ToArray();
        var parallel = new PlanetLatticeSample[positions.Length];
        Parallel.For(0, positions.Length, i =>
        {
            var position = positions[i];
            var frame = PlanetLocalFrame.Create(position with { X = position.X - 24 });
            Assert.IsTrue(frame.TryRebase(position, out var rebased));
            var restored = rebased.ToPlanetDouble(rebased.ToLocalDouble(position));
            var renderRestored = rebased.ToPlanet(rebased.ToLocal(position));
            AssertResidual(position, restored);
            AssertResidual(position, renderRestored);
            parallel[i] = Planet.SampleAt(restored);
        });

        CollectionAssert.AreEqual(serial, parallel);

        double Axis() => random.Next(-6_400_000, 6_400_001) + random.NextDouble() / 4;
    }

    private static void VerifyEdge(PlanetFace face, PlanetEdge edge, PlanetFace otherFace, PlanetEdge otherEdge)
    {
        for (var coarseLevel = 0; coarseLevel < PlanetCoordinates.MaximumLevel; coarseLevel++)
        for (var fineLevel = coarseLevel + 1; fineLevel <= PlanetCoordinates.MaximumLevel; fineLevel++)
        {
            var coarseTiles = 1 << coarseLevel;
            var fineTiles = 1 << fineLevel;
            for (var tile = 0; tile < coarseTiles; tile++)
            for (var index = 0; index <= Intervals; index++)
            {
                var coarse = EdgePatch(face, edge, coarseLevel, tile);
                var fineIndex = fineTiles * Intervals - ((tile * Intervals + index) << (fineLevel - coarseLevel));
                var fineTile = Math.Min(fineIndex / Intervals, fineTiles - 1);
                var fine = EdgePatch(otherFace, otherEdge, fineLevel, fineTile);
                var local = fineIndex - fineTile * Intervals;
                var expected = Planet.SampleAt(coarse, edge, index);
                Assert.AreEqual(expected, Planet.SampleAt(fine, otherEdge, local));
                VerifyRebase(PlanetCoordinates.SampleEdge(fine, otherEdge, local), expected);
            }
        }
    }

    private static PlanetPatchKey EdgePatch(PlanetFace face, PlanetEdge edge, int level, int tile)
    {
        var last = (1 << level) - 1;
        return edge switch
        {
            PlanetEdge.Bottom => new(face, level, tile, 0),
            PlanetEdge.Right => new(face, level, last, tile),
            PlanetEdge.Top => new(face, level, last - tile, last),
            PlanetEdge.Left => new(face, level, 0, last - tile),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
    }

    private static void VerifyRebase(PlanetSampleKey chart, PlanetLatticeSample expected)
    {
        var position = Planet.ChartPosition(chart);
        var frame = PlanetLocalFrame.Create(position with { X = position.X - 24 });
        Assert.IsTrue(frame.TryRebase(position, out var rebased));
        foreach (var origin in new[] { frame, rebased })
        {
            var restored = origin.ToPlanetDouble(origin.ToLocalDouble(position));
            AssertResidual(position, restored);
            AssertResidual(position, origin.ToPlanet(origin.ToLocal(position)));
            Assert.AreEqual(expected, Planet.SampleAt(restored));
        }
    }

    private static void AssertResidual(PlanetVector expected, PlanetVector actual)
    {
        var x = expected.X - actual.X;
        var y = expected.Y - actual.Y;
        var z = expected.Z - actual.Z;
        Assert.IsLessThanOrEqualTo(0.001, Math.Sqrt(x * x + y * y + z * z));
    }

    private static PlanetDescriptor CreatePlanet(double radius = PlanetDescriptor.MaximumRadiusMeters,
        double cellSize = 1, uint generatorVersion = 1, string? hash = null) =>
        new(WorldId, PlanetId, ulong.MaxValue, generatorVersion, 1, 1, hash ?? new string('A', 64), radius, cellSize);
}
