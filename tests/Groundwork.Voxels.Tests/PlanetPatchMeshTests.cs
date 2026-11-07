using Groundwork.Voxels;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetPatchMeshTests
{
    private const int Intervals = 16;
    private const double Radius = 64;
    private static readonly PlanetEdge[] Edges = Enum.GetValues<PlanetEdge>();

    [TestMethod]
    public void AllTwelveSeamsMatchAtEqualAndMixedLevelsFromEitherSide()
    {
        foreach (var (face, edge, otherFace, otherEdge) in PlanetCoordinateTests.Seams)
        {
            var first = PlanetPatchMesh.Build(new PlanetPatchKey(face, 0, 0, 0), Radius, Height);
            var second = PlanetPatchMesh.Build(new PlanetPatchKey(otherFace, 0, 0, 0), Radius, Height);
            var firstEdge = BoundarySegments(first, edge);
            var secondEdge = BoundarySegments(second, otherEdge);
            Assert.HasCount(Intervals, firstEdge);
            Assert.HasCount(Intervals, secondEdge);
            Assert.IsTrue(firstEdge.SetEquals(Reverse(secondEdge)), $"Equal levels at {face}/{edge}.");
            AssertMixedSeam(firstEdge, otherFace, otherEdge);
            AssertMixedSeam(secondEdge, face, edge);
        }
    }

    [TestMethod]
    public void EveryTransitionMaskHasOutwardNondegenerateTrianglesAndAnUnbrokenBoundary()
    {
        foreach (var face in Enum.GetValues<PlanetFace>())
        {
            for (var mask = 0; mask < 16; mask++)
            {
                var patch = new PlanetPatchKey(face, 1, 0, 0);
                var transitions = (PlanetTransitionEdges)mask;
                var mesh = PlanetPatchMesh.Build(patch, Radius, Height, transitions);
                var label = $"{face}, {transitions}";
                Assert.AreEqual(patch, mesh.Patch, label);
                Assert.AreEqual(transitions, mesh.Transitions, label);
                var expected = ExpectedBoundary(patch, transitions);
                var actual = ActualBoundary(mesh);
                Assert.IsTrue(expected.SetEquals(actual), $"Boundary mismatch or T-junction for {label}.");

                var positions = mesh.Positions;
                var indices = mesh.Indices;
                var samples = mesh.Samples;
                Assert.AreEqual(samples.Length, positions.Length, label);
                Assert.AreEqual(289 - 8 * System.Numerics.BitOperations.PopCount((uint)mask), samples.Length, label);
                Assert.AreEqual(0, indices.Length % 3, label);
                Assert.AreEqual(samples.Length, samples.ToArray().Distinct().Count(), label);
                var area = 0.0;
                for (var i = 0; i < indices.Length; i += 3)
                {
                    var a = (int)indices[i];
                    var b = (int)indices[i + 1];
                    var c = (int)indices[i + 2];
                    var normal = Cross(Subtract(positions[b], positions[a]), Subtract(positions[c], positions[a]));
                    Assert.IsGreaterThan(0.0, Dot(normal, positions[a]), $"Invalid winding or degenerate triangle for {label}.");

                    var planarNormal = Cross(Subtract(samples[b], samples[a]), Subtract(samples[c], samples[a]));
                    var doubleArea = Dot(planarNormal, FaceNormal(face));
                    Assert.IsGreaterThan(0.0, doubleArea, $"Folded triangle in cube coordinates for {label}.");
                    area += doubleArea;
                }

                Assert.AreEqual(2.0 * 512 * 512, area, label);
            }
        }
    }

    [TestMethod]
    public void MeshIsDeterministicAndAppliesRadiusAndHeightToCanonicalDirection()
    {
        var patch = new PlanetPatchKey(PlanetFace.PositiveZ, 5, 31, 31);
        const PlanetTransitionEdges transitions = PlanetTransitionEdges.Right | PlanetTransitionEdges.Top;
        var first = PlanetPatchMesh.Build(patch, Radius, Height, transitions);
        var repeat = PlanetPatchMesh.Build(patch, Radius, Height, transitions);
        Assert.IsTrue(first.Samples.SequenceEqual(repeat.Samples));
        Assert.IsTrue(first.Positions.SequenceEqual(repeat.Positions));
        Assert.IsTrue(first.Indices.SequenceEqual(repeat.Indices));

        for (var i = 0; i < first.Samples.Length; i++)
        {
            var sample = first.Samples[i];
            var position = first.Positions[i];
            var direction = PlanetCoordinates.Direction(sample);
            var distance = Radius + Height(sample);
            Assert.AreEqual(direction.X * distance, position.X, 1e-13);
            Assert.AreEqual(direction.Y * distance, position.Y, 1e-13);
            Assert.AreEqual(direction.Z * distance, position.Z, 1e-13);
        }
    }

    [TestMethod]
    public void InvalidMeshInputsAreRejected()
    {
        var patch = new PlanetPatchKey(PlanetFace.PositiveX, 1, 0, 0);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetPatchMesh.Build(patch, 0, Height));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetPatchMesh.Build(patch, double.NaN, Height));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetPatchMesh.Build(patch, double.PositiveInfinity, Height));
        Assert.ThrowsExactly<ArgumentNullException>(() => PlanetPatchMesh.Build(patch, Radius, null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetPatchMesh.Build(patch, Radius, Height, (PlanetTransitionEdges)16));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            PlanetPatchMesh.Build(patch with { Level = 0 }, Radius, Height, PlanetTransitionEdges.Bottom));
        Assert.ThrowsExactly<ArgumentException>(() => PlanetPatchMesh.Build(patch, Radius, _ => -Radius));
        Assert.ThrowsExactly<ArgumentException>(() => PlanetPatchMesh.Build(patch, Radius, _ => double.NaN));
    }

    private static void AssertMixedSeam(HashSet<PositionedSegment> coarse, PlanetFace fineFace, PlanetEdge fineEdge)
    {
        var fine = new HashSet<PositionedSegment>();
        for (var tile = 0; tile < 2; tile++)
        {
            var patch = PlanetCoordinateTests.BoundaryPatch(fineFace, fineEdge, 1, tile);
            var mesh = PlanetPatchMesh.Build(patch, Radius, Height, TransitionFor(fineEdge));
            var segments = BoundarySegments(mesh, fineEdge);
            Assert.HasCount(Intervals / 2, segments);
            fine.UnionWith(segments);
        }

        Assert.IsTrue(coarse.SetEquals(Reverse(fine)), $"2:1 seam at {fineFace}/{fineEdge}.");
    }

    private static HashSet<PositionedSegment> BoundarySegments(PlanetPatchMesh mesh, PlanetEdge edge)
    {
        var edgeSamples = new HashSet<PlanetSampleKey>();
        for (var i = 0; i <= Intervals; i++)
            edgeSamples.Add(PlanetCoordinates.SampleEdge(mesh.Patch, edge, i));

        var positions = new Dictionary<PlanetSampleKey, PlanetVector>();
        for (var i = 0; i < mesh.Samples.Length; i++)
        {
            var sample = mesh.Samples[i];
            if (edgeSamples.Contains(sample))
                positions.Add(sample, mesh.Positions[i]);
        }

        return ActualBoundary(mesh)
            .Where(segment => edgeSamples.Contains(segment.From) && edgeSamples.Contains(segment.To))
            .Select(segment => new PositionedSegment(segment, positions[segment.From], positions[segment.To]))
            .ToHashSet();
    }

    private static HashSet<Segment> ActualBoundary(PlanetPatchMesh mesh)
    {
        var samples = mesh.Samples;
        var indices = mesh.Indices;
        var edges = new HashSet<Segment>();
        var used = new HashSet<Segment>();
        for (var i = 0; i < indices.Length; i += 3)
        {
            Assert.IsLessThan((uint)samples.Length, indices[i]);
            Assert.IsLessThan((uint)samples.Length, indices[i + 1]);
            Assert.IsLessThan((uint)samples.Length, indices[i + 2]);
            var a = samples[(int)indices[i]];
            var b = samples[(int)indices[i + 1]];
            var c = samples[(int)indices[i + 2]];
            AddEdge(a, b);
            AddEdge(b, c);
            AddEdge(c, a);
        }

        return edges;

        void AddEdge(PlanetSampleKey from, PlanetSampleKey to)
        {
            var segment = new Segment(from, to);
            Assert.IsTrue(used.Add(segment), "An oriented edge occurs more than once.");
            if (!edges.Remove(segment.Reverse()))
                edges.Add(segment);
        }
    }

    private static HashSet<Segment> ExpectedBoundary(PlanetPatchKey patch, PlanetTransitionEdges transitions)
    {
        var result = new HashSet<Segment>();
        foreach (var edge in Edges)
        {
            var stride = (transitions & TransitionFor(edge)) != 0 ? 2 : 1;
            for (var i = 0; i < Intervals; i += stride)
                result.Add(new Segment(PlanetCoordinates.SampleEdge(patch, edge, i),
                    PlanetCoordinates.SampleEdge(patch, edge, i + stride)));
        }
        return result;
    }

    private static IEnumerable<PositionedSegment> Reverse(IEnumerable<PositionedSegment> segments) =>
        segments.Select(segment => segment.Reverse());

    private static PlanetTransitionEdges TransitionFor(PlanetEdge edge) => edge switch
    {
        PlanetEdge.Bottom => PlanetTransitionEdges.Bottom,
        PlanetEdge.Right => PlanetTransitionEdges.Right,
        PlanetEdge.Top => PlanetTransitionEdges.Top,
        PlanetEdge.Left => PlanetTransitionEdges.Left,
        _ => throw new ArgumentOutOfRangeException(nameof(edge))
    };

    private static PlanetVector FaceNormal(PlanetFace face) => face switch
    {
        PlanetFace.PositiveX => new(1, 0, 0),
        PlanetFace.NegativeX => new(-1, 0, 0),
        PlanetFace.PositiveY => new(0, 1, 0),
        PlanetFace.NegativeY => new(0, -1, 0),
        PlanetFace.PositiveZ => new(0, 0, 1),
        PlanetFace.NegativeZ => new(0, 0, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(face))
    };

    private static double Height(PlanetSampleKey sample) => (sample.X - sample.Y + sample.Z) / 2048.0;
    private static PlanetVector Subtract(PlanetVector a, PlanetVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static PlanetVector Subtract(PlanetSampleKey a, PlanetSampleKey b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static PlanetVector Cross(PlanetVector a, PlanetVector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static double Dot(PlanetVector a, PlanetVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private readonly record struct Segment(PlanetSampleKey From, PlanetSampleKey To)
    {
        internal Segment Reverse() => new(To, From);
    }

    private readonly record struct PositionedSegment(Segment Segment, PlanetVector From, PlanetVector To)
    {
        internal PositionedSegment Reverse() => new(Segment.Reverse(), To, From);
    }
}
