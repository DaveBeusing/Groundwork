using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetTriplanarMappingTests
{
    private static readonly Guid World = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Planet = new("10213243-5465-7687-98a9-bacbdcedfe0f");
    private const int Intervals = PlanetCoordinates.IntervalsPerPatch;
    private const int BrickCount = 8;

    [TestMethod]
    public void CanonicalSignedPositionsFreezePhysicalScaleAndNormalBasis()
    {
        var point = Map(new(6_399_999.9375, -6_400_000.015625, 4.125));
        Assert.AreEqual((1_599_999L, 252d), (point.X.Repeat, point.X.PhaseTexels));
        Assert.AreEqual((-1_600_001L, 255d), (point.Y.Repeat, point.Y.PhaseTexels));
        Assert.AreEqual((1L, 8d), (point.Z.Repeat, point.Z.PhaseTexels));

        var anchor = Map(new(6_400_000, -6_400_000, 4));
        var projection = PlanetTriplanarMapping.Project(point, anchor, new(-1, 2, -3));
        Assert.AreEqual(new Vector2(1, 8), projection.XProjection);
        Assert.AreEqual(new Vector2(8, -4), projection.YProjection);
        Assert.AreEqual(new Vector2(4, -1), projection.ZProjection);
        Assert.AreEqual(new Vector3(-1, 1, -1), projection.AxisSigns);
        Assert.AreEqual(new Vector3(1f / 6, 2f / 6, 3f / 6), projection.Weights);

        // Neutral tangent-space normals blend back to the geometric normal, including
        // negative signed axes on cave walls and the undersides of overhangs.
        var neutral = Vector3.Normalize(projection.AxisSigns * projection.Weights);
        Assert.AreEqual(0f, Vector3.Distance(neutral, Vector3.Normalize(new(-1, 2, -3))), 1e-6f);
        var grazing = PlanetTriplanarMapping.Project(point, anchor, new(-0f, -float.MaxValue, 0));
        Assert.AreEqual(new Vector3(1, -1, 1), grazing.AxisSigns);
        Assert.AreEqual(Vector3.UnitY, grazing.Weights);
        Assert.AreEqual(0d, Map(new(-double.Epsilon, 0, 0)).X.PhaseTexels);
    }

    [TestMethod]
    public void CommonAnchorInterpolatesAcrossARepeatAndRebaseOnlyShiftsWholeRepeats()
    {
        var left = PlanetTriplanarMapping.Project(Map(new(3.984375, 0, 0)), default, Vector3.UnitZ);
        var right = PlanetTriplanarMapping.Project(Map(new(4.015625, 0, 0)), default, Vector3.UnitZ);
        Assert.AreEqual(255f, left.ZProjection.X);
        Assert.AreEqual(257f, right.ZProjection.X);
        Assert.AreEqual(256f, Vector2.Lerp(left.ZProjection, right.ZProjection, 0.5f).X);

        var position = new PlanetVector(-6_399_990.3125, 128.25, -3.875);
        var first = PlanetLocalFrame.Create(new(-6_400_000, 128, 0));
        Assert.IsTrue(first.TryRebase(new(-6_399_976, 144, -16), out var next));
        var point = Map(position);
        var projection = PlanetTriplanarMapping.Project(point, Map(first.Origin), new(3, -2, 1));
        var rebased = PlanetTriplanarMapping.Project(point, Map(next.Origin), new(3, -2, 1));
        Assert.AreNotEqual(projection.ZProjection, rebased.ZProjection);
        Assert.AreEqual(projection.Weights, rebased.Weights);
        Assert.AreEqual(projection.AxisSigns, rebased.AxisSigns);
        Assert.AreEqual(Wrap(projection.XProjection), Wrap(rebased.XProjection));
        Assert.AreEqual(Wrap(projection.YProjection), Wrap(rebased.YProjection));
        Assert.AreEqual(Wrap(projection.ZProjection), Wrap(rebased.ZProjection));

        // Normals cross the X sign plane along this edge. Compute the signed
        // projection only after interpolating the local position and normal.
        var a = Map(new(4.125, 7.75, 3.75));
        var b = Map(new(4.375, 8.75, 4.75));
        var nearAnchor = Map(new(4, 8, 4));
        var farAnchor = Map(new(0, 0, 0));
        var normal = Vector3.Lerp(new(-1, 0, 1), new(1, 0, 1), 0.25f);
        var nearUv = Interpolate(nearAnchor);
        var farUv = Interpolate(farAnchor);
        Assert.IsGreaterThan(0f, nearUv.Weights.X);
        Assert.AreEqual(Wrap(nearUv.XProjection), Wrap(farUv.XProjection));
        Assert.AreEqual(Wrap(nearUv.ZProjection), Wrap(farUv.ZProjection));

        PlanetTriplanarProjection Interpolate(PlanetTriplanarPoint basis) => PlanetTriplanarMapping.ProjectTexels(
            Vector3.Lerp(PlanetTriplanarMapping.ToLocalTexels(a, basis), PlanetTriplanarMapping.ToLocalTexels(b, basis), 0.25f),
            normal);
    }

    [TestMethod]
    public void TriangleAnchorFitsExactWindowAndRejectsOversizedOrbitPrimitives()
    {
        var first = Map(new(-1024, 0, 0));
        var second = Map(new(1024, 0, 0));
        var third = Map(new(0, 2, -2));
        Assert.IsTrue(PlanetTriplanarMapping.TryGetTriangleAnchor(first, second, third, out var anchor));
        Assert.IsTrue(PlanetTriplanarMapping.TryGetTriangleAnchor(third, second, first, out var reordered));
        Assert.AreEqual(anchor, reordered);
        Assert.AreEqual(-(float)PlanetTriplanarMapping.MaximumLocalTexels, PlanetTriplanarMapping.ToLocalTexels(first, anchor).X);
        Assert.AreEqual((float)PlanetTriplanarMapping.MaximumLocalTexels, PlanetTriplanarMapping.ToLocalTexels(second, anchor).X);
        Assert.IsFalse(PlanetTriplanarMapping.TryGetTriangleAnchor(first, Map(new(1024.015625, 0, 0)), third, out _));

        var patch = BuildPatch(new(PlanetFace.PositiveZ, 0, 0, 0), PlanetDescriptor.MaximumRadiusMeters);
        PlanetTriplanarPoint Control(int u, int v) => Map(Find(patch, PlanetCoordinates.Sample(patch.Patch, u, v)));
        Assert.IsFalse(PlanetTriplanarMapping.TryGetTriangleAnchor(Control(8, 8), Control(9, 8), Control(8, 9), out _),
            "The coarse orbit primitive requires the documented untextured fallback.");
    }

    [TestMethod]
    public void VolumeDecompositionKeepsFractionalCellsWhenGlobalCellMagnitudeExceedsDoubleIntegerPrecision()
    {
        var size = Math.ScaleB(1, -29);
        var descriptor = Descriptor(PlanetDescriptor.MaximumRadiusMeters, size);
        var origin = (1L << 53) + 1;
        var mapped = PlanetTriplanarMapping.FromVolume(descriptor, new(origin, -origin, 0), new(0.25f));

        Assert.AreEqual(4_194_304L, mapped.X.Repeat);
        Assert.AreEqual(1.25 * size * PlanetTriplanarMapping.TexelsPerMeter, mapped.X.PhaseTexels);
        Assert.AreEqual(-4_194_305L, mapped.Y.Repeat);
        Assert.AreEqual(256 - 0.75 * size * PlanetTriplanarMapping.TexelsPerMeter, mapped.Y.PhaseTexels);
        Assert.AreEqual(0.25 * size * PlanetTriplanarMapping.TexelsPerMeter, mapped.Z.PhaseTexels);

        var decimalCell = Descriptor(PlanetDescriptor.MaximumRadiusMeters, 0.1);
        var first = PlanetTriplanarMapping.FromVolume(decimalCell, new(63_999_936, -64_000_032, 0), new(32.25f, 32.5f, 7));
        var second = PlanetTriplanarMapping.FromVolume(decimalCell, new(63_999_968, -64_000_000, 0), new(0.25f, 0.5f, 7));
        var physicalAnchor = Map(new(6_400_000, -6_400_000, 0));
        AssertProjectionClose(PlanetTriplanarMapping.Project(first, physicalAnchor, Vector3.UnitZ),
            PlanetTriplanarMapping.Project(second, physicalAnchor, Vector3.UnitZ));
    }

    [TestMethod]
    public void MatchingChartControlsAgreeOnAllTwelveEdgesAndEightCornersAcrossTwoToOneLod()
    {
        const double radius = PlanetDescriptor.MaximumRadiusMeters;
        var descriptor = Descriptor(radius);
        var corners = new HashSet<PlanetSampleKey>();
        foreach (var (face, edge, adjacentFace, adjacentEdge) in PlanetCoordinateTests.Seams)
        {
            var coarse = BuildPatch(new(face, 0, 0, 0), radius);
            var controls = new Dictionary<PlanetSampleKey, PlanetVector>();
            for (var i = 0; i <= Intervals; i++)
            {
                var key = PlanetCoordinates.SampleEdge(coarse.Patch, edge, i);
                controls.Add(key, Find(coarse, key));
                if (i is 0 or Intervals)
                    corners.Add(key);
            }

            var transition = (PlanetTransitionEdges)(1 << (int)adjacentEdge);
            var matched = new HashSet<PlanetSampleKey>();
            for (var tile = 0; tile < 2; tile++)
            {
                var patch = PlanetCoordinateTests.BoundaryPatch(adjacentFace, adjacentEdge, 1, tile);
                var fine = BuildPatch(patch, radius, transition);
                for (var i = 0; i <= Intervals; i += 2)
                {
                    var key = PlanetCoordinates.SampleEdge(patch, adjacentEdge, i);
                    var finePosition = Find(fine, key);
                    var coarsePosition = controls[key];
                    var normal = Direction(key);
                    var anchor = Map(coarsePosition);
                    Assert.AreEqual(anchor, Map(finePosition), $"Lattice address {key} changed between charts.");

                    // A mesh-local float is already quantized by the volume path.
                    // Evaluate the same control through that path at actual planet scale.
                    var volumePosition = ThroughVolume(descriptor, coarsePosition);
                    AssertProjectionClose(PlanetTriplanarMapping.Project(anchor, anchor, normal),
                        PlanetTriplanarMapping.Project(volumePosition, anchor, normal));
                    matched.Add(key);
                }
            }
            Assert.HasCount(Intervals + 1, matched);
        }
        Assert.HasCount(8, corners);
    }

    [TestMethod]
    public void OrbitControlsResolveActualSharedNearSurfaceMaterialsAtTwoLodsWithoutRequestingBricks()
    {
        var descriptor = Descriptor(8);
        var source = new Field(descriptor, Shape.Plane);
        var mesh = BuildVolume(source, new(-1, -1, -1), 0, 1);
        var coarse = BuildPatch(new(PlanetFace.PositiveZ, 0, 0, 0), 8);
        var fine = BuildPatch(new(PlanetFace.PositiveZ, 1, 1, 1), 8);
        var first = PlanetCoordinates.Sample(coarse.Patch, 12, 11);
        var second = PlanetCoordinates.Sample(coarse.Patch, 13, 11);
        Assert.AreEqual(first, PlanetCoordinates.Sample(fine.Patch, 8, 6));
        Assert.AreEqual(second, PlanetCoordinates.Sample(fine.Patch, 10, 6));

        var nearFirst = FindNear(mesh, 4, 3);
        var nearSecond = FindNear(mesh, 5, 3);
        Assert.AreEqual((ushort)7, nearFirst.Material);
        Assert.AreEqual((ushort)19, nearSecond.Material);
        PlanetSurfaceMaterialSample[] samples =
        [
            new(new(World, Planet, new(4, 3, 7)), nearFirst.Material),
            new(new(World, Planet, new(5, 3, 7)), nearSecond.Material)
        ];
        var precedingQueries = source.Queries;
        Verify(first, nearFirst);
        Verify(second, nearSecond);
        // The orbit proxy for an unshared cave or a local edit uses its declared ID.
        Assert.AreEqual((ushort)51, PlanetOrbitMaterialSelector.Select(descriptor.SampleAt(Find(coarse, first)), [], 51));
        Assert.AreEqual(precedingQueries, source.Queries);

        void Verify(PlanetSampleKey key, VolumeVertex near)
        {
            var position = Find(coarse, key);
            var finePosition = Find(fine, key);
            var orbit = Map(position);
            var volume = PlanetTriplanarMapping.FromVolume(mesh.Descriptor, mesh.Origin, near.Position);
            Assert.AreEqual(orbit, Map(finePosition));
            AssertProjectionClose(PlanetTriplanarMapping.Project(orbit, orbit, Vector3.UnitZ),
                PlanetTriplanarMapping.Project(volume, orbit, near.Normal));
            var control = descriptor.SampleAt(position);
            Assert.AreEqual(near.Material, PlanetOrbitMaterialSelector.Select(control, samples, 51));
            Assert.AreEqual(near.Material, PlanetOrbitMaterialSelector.Select(descriptor.SampleAt(finePosition),
                [samples[1], samples[0]], 51));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MixedLodCaveAndOverhangMappingSurvivesReloadDistantEditAndReorderedJobs(bool cave)
    {
        var descriptor = Descriptor(128);
        var shape = cave ? Shape.Cavity : Shape.Overhang;
        var original = new Field(descriptor, shape);
        var edited = new Field(descriptor, shape, edit: true);
        var mesh = BuildVolume(original, default, 254, 1);
        var reloaded = BuildVolume(edited, default, 254, 2);
        Assert.AreNotEqual(original.SampleDensity(new(96, 32, 32)), edited.SampleDensity(new(96, 32, 32)));
        Assert.AreEqual((byte)254, mesh.LodMask);
        Assert.IsTrue(mesh.Triangles.ToArray().Any(t => t.IsTransition));
        Assert.IsTrue(mesh.Triangles.ToArray().Any(t => t.A.Normal.Y < 0 && t.A.Normal.Z < 0));
        CollectionAssert.AreEqual(ProjectMesh(mesh, false), ProjectMesh(reloaded, true));
    }

    [TestMethod]
    public void InvalidInputsFailBeforeProducingCoordinatesOrPaletteSelections()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Map(new(double.NaN, 0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Map(new(double.MaxValue, 0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlanetTriplanarMapping.FromVolume(Descriptor(1), default, new(float.NaN, 0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlanetTriplanarMapping.FromVolume(Descriptor(1), default, new(65, 0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlanetTriplanarMapping.Project(Map(new(1024.25, 0, 0)), default, Vector3.UnitZ));
        const double nearBound = 65_535.9987;
        var bounded = PlanetTriplanarMapping.ToLocalTexels(Map(new(nearBound / 64, 0, 0)), default);
        Assert.AreEqual(nearBound, bounded.X, PlanetTriplanarMapping.PositionToleranceTexels);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlanetTriplanarMapping.ProjectTexels(new(0, float.NaN, 0), Vector3.UnitZ));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlanetTriplanarMapping.Project(default, default, Vector3.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlanetTriplanarMapping.Project(default, default, new(float.PositiveInfinity, 0, 0)));
        Assert.ThrowsExactly<ArgumentException>(() => PlanetOrbitMaterialSelector.Select(default, [], 7));
        var control = new PlanetLatticeSample(World, Planet, default);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetOrbitMaterialSelector.Select(control, [], 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetOrbitMaterialSelector.Select(control,
            new PlanetSurfaceMaterialSample[PlanetOrbitMaterialSelector.MaximumSamples + 1], 7));
        Assert.ThrowsExactly<ArgumentException>(() => PlanetOrbitMaterialSelector.Select(control, [new(control, 0)], 7));
        Assert.ThrowsExactly<ArgumentException>(() => PlanetOrbitMaterialSelector.Select(control,
            [new(new(Planet, World, default), 7)], 7));
        Assert.ThrowsExactly<ArgumentException>(() => PlanetOrbitMaterialSelector.Select(control,
            [new(control, 7), new(control, 19)], 7));
    }

    [TestMethod]
    public void NearestSelectionUsesSignedXThenYThenZTieBreakAndIgnoresDistantControls()
    {
        var control = new PlanetLatticeSample(World, Planet, new(-5, -5, -5));
        var nearestLow = new PlanetSurfaceMaterialSample(new(World, Planet, new(-6, -5, -5)), 19);
        var tiedHigher = new PlanetSurfaceMaterialSample(new(World, Planet, new(-5, -6, -5)), 7);
        var tiedLowerZ = new PlanetSurfaceMaterialSample(new(World, Planet, new(-5, -5, -6)), 23);
        var tiedHigherZ = new PlanetSurfaceMaterialSample(new(World, Planet, new(-5, -5, -4)), 31);
        var distanceTwo = new PlanetSurfaceMaterialSample(new(World, Planet, new(-7, -5, -5)), 37);
        Assert.AreEqual((ushort)19, PlanetOrbitMaterialSelector.Select(control, [tiedHigher, nearestLow, distanceTwo], 51));
        Assert.AreEqual((ushort)19, PlanetOrbitMaterialSelector.Select(control, [distanceTwo, nearestLow, tiedHigher], 51));
        Assert.AreEqual((ushort)7, PlanetOrbitMaterialSelector.Select(control, [tiedHigherZ, tiedLowerZ, tiedHigher], 51));
        Assert.AreEqual((ushort)23, PlanetOrbitMaterialSelector.Select(control, [tiedHigherZ, tiedLowerZ], 51));
        Assert.AreEqual((ushort)51, PlanetOrbitMaterialSelector.Select(control, [distanceTwo], 51));
    }

    private static PlanetTriplanarPoint Map(PlanetVector position) => PlanetTriplanarMapping.FromCanonical(position);

    private static PlanetTriplanarPoint ThroughVolume(PlanetDescriptor descriptor, PlanetVector position)
    {
        var cell = new LatticeCoordinate((long)Math.Floor(position.X), (long)Math.Floor(position.Y), (long)Math.Floor(position.Z));
        var origin = cell.Locate().Brick.Vertex(0, 0, 0);
        return PlanetTriplanarMapping.FromVolume(descriptor, origin,
            new((float)(position.X - origin.X), (float)(position.Y - origin.Y), (float)(position.Z - origin.Z)));
    }

    private static PlanetPatchMesh BuildPatch(PlanetPatchKey key, double radius,
        PlanetTransitionEdges transitions = PlanetTransitionEdges.None) =>
        // A shared flat plane fixture under +Z and a radius-only fixture elsewhere.
        PlanetPatchMesh.Build(key, radius,
            sample => radius == 8 ? 8 / PlanetCoordinates.Direction(sample).Z - radius : 0, transitions);

    private static PlanetVector Find(PlanetPatchMesh mesh, PlanetSampleKey key)
    {
        var index = mesh.Samples.IndexOf(key);
        Assert.IsGreaterThanOrEqualTo(0, index, $"Missing patch sample {key}.");
        return mesh.Positions[index];
    }

    private static VolumeVertex FindNear(PlanetVolumeMesh mesh, int x, int y)
    {
        var localX = x - mesh.Origin.X;
        var localY = y - mesh.Origin.Y;
        foreach (var triangle in mesh.Triangles)
        {
            if (Matches(triangle.A)) return triangle.A;
            if (Matches(triangle.B)) return triangle.B;
            if (Matches(triangle.C)) return triangle.C;
        }
        throw new InvalidOperationException($"Missing near control {x}, {y}.");

        bool Matches(VolumeVertex vertex) => vertex.Position.X == localX && vertex.Position.Y == localY;
    }

    private static PlanetDescriptor Descriptor(double radius, double cellSize = 1) =>
        new(World, Planet, 42, 1, 1, 1, new string('a', 64), radius, cellSize);

    private static Vector3 Direction(PlanetSampleKey sample)
    {
        var direction = PlanetCoordinates.Direction(sample);
        return new((float)direction.X, (float)direction.Y, (float)direction.Z);
    }

    private static Vector2 Wrap(Vector2 uv) => new(WrapAxis(uv.X), WrapAxis(uv.Y));

    private static float WrapAxis(float value)
    {
        const int repeat = PlanetTriplanarMapping.TexelsPerRepeat;
        return value - MathF.Floor(value / repeat) * repeat;
    }

    private static void AssertProjectionClose(PlanetTriplanarProjection expected, PlanetTriplanarProjection actual)
    {
        var tolerance = (float)PlanetTriplanarMapping.PositionToleranceTexels;
        Assert.AreEqual(0f, Vector2.Distance(expected.XProjection, actual.XProjection), tolerance);
        Assert.AreEqual(0f, Vector2.Distance(expected.YProjection, actual.YProjection), tolerance);
        Assert.AreEqual(0f, Vector2.Distance(expected.ZProjection, actual.ZProjection), tolerance);
        Assert.AreEqual(expected.Weights, actual.Weights);
        Assert.AreEqual(expected.AxisSigns, actual.AxisSigns);
    }

    private static PlanetVolumeMesh BuildVolume(Field source, LatticeBrick minimum, int mask, ulong revision)
    {
        var store = new PlanetBrickStore(source, BrickCount * PlanetBrickStore.BrickPayloadBytes, BrickCount);
        var inputs = new VolumeMeshInput[BrickCount];
        try
        {
            for (var i = 0; i < inputs.Length; i++)
            {
                var key = new LatticeBrick(minimum.X + (i & 1), minimum.Y + ((i >> 1) & 1), minimum.Z + (i >> 2));
                Assert.IsTrue(store.TryGenerate(key));
                var lease = store.TryAcquire(key);
                Assert.IsNotNull(lease);
                inputs[i] = new(lease, (mask & (1 << i)) == 0 ? 1 : 2);
            }
            var mesh = PlanetVolumeMesher.Build(inputs, revision, long.MaxValue);
            Assert.IsNotNull(mesh);
            return mesh;
        }
        finally
        {
            foreach (var input in inputs)
                input.Brick?.Dispose();
        }
    }

    private static byte[] ProjectMesh(PlanetVolumeMesh mesh, bool reordered)
    {
        var triangles = mesh.Triangles.ToArray();
        var data = new byte[triangles.Length * 50];
        var anchor = Map(new(mesh.Origin.X, mesh.Origin.Y, mesh.Origin.Z));
        if (reordered)
            Parallel.For(0, triangles.Length, i => Sample(triangles.Length - i - 1));
        else
            for (var i = 0; i < triangles.Length; i++)
                Sample(i);
        return SHA256.HashData(data);

        void Sample(int index)
        {
            var vertex = triangles[index].A;
            var point = PlanetTriplanarMapping.FromVolume(mesh.Descriptor, mesh.Origin, vertex.Position);
            var sample = PlanetTriplanarMapping.Project(point, anchor, vertex.Normal);
            var target = data.AsSpan(index * 50, 50);
            Write(target, sample.XProjection);
            Write(target[8..], sample.YProjection);
            Write(target[16..], sample.ZProjection);
            Write(target[24..], new(sample.Weights.X, sample.Weights.Y));
            Write(target[32..], new(sample.Weights.Z, sample.AxisSigns.X));
            Write(target[40..], new(sample.AxisSigns.Y, sample.AxisSigns.Z));
            BinaryPrimitives.WriteUInt16LittleEndian(target[48..], vertex.Material);
        }

        static void Write(Span<byte> target, Vector2 values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(target, values.X);
            BinaryPrimitives.WriteSingleLittleEndian(target[4..], values.Y);
        }
    }

    private enum Shape { Plane, Cavity, Overhang }

    private sealed class Field(PlanetDescriptor descriptor, Shape shape, bool edit = false) : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => descriptor;
        public int Queries { get; private set; }

        public long SampleDensity(LatticeCoordinate point)
        {
            Queries++;
            if (shape == Shape.Plane)
                return (point.Z - 8) * 256;
            if (edit && point == new LatticeCoordinate(96, 32, 32))
                return -256;

            var x = point.X - 32;
            var y = point.Y - 32;
            var z = point.Z - 32;
            if (shape == Shape.Overhang)
                return Math.Min(Box(0, -4, 0, 4, 12, 4), Box(0, 8, 0, 18, 3, 11));

            var outer = 10 * ((x + 1) * (x + 1) + (y + 2) * (y + 2) + (z + 3) * (z + 3)) - 2997;
            return Math.Max(outer, 700 - 10 * (x * x + y * y + z * z));

            long Box(long cx, long cy, long cz, long ex, long ey, long ez) =>
                Math.Max(Math.Abs(x - cx) - ex, Math.Max(Math.Abs(y - cy) - ey, Math.Abs(z - cz) - ez));
        }

        public ushort SampleMaterial(LatticeCoordinate cell)
        {
            Queries++;
            return (ushort)((cell.X & 1) == 0 ? 7 : 19);
        }
    }
}
