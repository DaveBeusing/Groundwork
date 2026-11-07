using Groundwork.Voxels;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetCoordinateTests
{
    internal static readonly (PlanetFace Face, PlanetEdge Edge, PlanetFace OtherFace, PlanetEdge OtherEdge)[] Seams =
    [
        (PlanetFace.PositiveX, PlanetEdge.Bottom, PlanetFace.NegativeY, PlanetEdge.Right),
        (PlanetFace.PositiveX, PlanetEdge.Right, PlanetFace.NegativeZ, PlanetEdge.Left),
        (PlanetFace.PositiveX, PlanetEdge.Top, PlanetFace.PositiveY, PlanetEdge.Right),
        (PlanetFace.PositiveX, PlanetEdge.Left, PlanetFace.PositiveZ, PlanetEdge.Right),
        (PlanetFace.NegativeX, PlanetEdge.Bottom, PlanetFace.NegativeY, PlanetEdge.Left),
        (PlanetFace.NegativeX, PlanetEdge.Right, PlanetFace.PositiveZ, PlanetEdge.Left),
        (PlanetFace.NegativeX, PlanetEdge.Top, PlanetFace.PositiveY, PlanetEdge.Left),
        (PlanetFace.NegativeX, PlanetEdge.Left, PlanetFace.NegativeZ, PlanetEdge.Right),
        (PlanetFace.PositiveY, PlanetEdge.Bottom, PlanetFace.PositiveZ, PlanetEdge.Top),
        (PlanetFace.PositiveY, PlanetEdge.Top, PlanetFace.NegativeZ, PlanetEdge.Top),
        (PlanetFace.NegativeY, PlanetEdge.Bottom, PlanetFace.NegativeZ, PlanetEdge.Bottom),
        (PlanetFace.NegativeY, PlanetEdge.Top, PlanetFace.PositiveZ, PlanetEdge.Bottom)
    ];

    private const int Intervals = 16;
    private const int MaximumLevel = 5;
    private const int Extent = 512;

    [TestMethod]
    public void AllTwelveCubeSeamsShareEveryFinestSampleInReverseOrder()
    {
        foreach (var (face, edge, otherFace, otherEdge) in Seams)
        {
            for (var level = 0; level <= MaximumLevel; level++)
            {
                var tileCount = 1 << level;
                for (var index = 0; index < tileCount; index++)
                {
                    var first = BoundaryPatch(face, edge, level, index);
                    var second = BoundaryPatch(otherFace, otherEdge, level, tileCount - 1 - index);
                    Assert.AreEqual((second, otherEdge, true), PlanetCoordinates.Neighbor(first, edge));
                    Assert.AreEqual((first, edge, true), PlanetCoordinates.Neighbor(second, otherEdge));

                    for (var k = 0; k <= Intervals; k++)
                    {
                        var sample = PlanetCoordinates.SampleEdge(first, edge, k);
                        Assert.AreEqual(sample, PlanetCoordinates.SampleEdge(second, otherEdge, Intervals - k));
                        var fineIndex = (index * Intervals + k) << (MaximumLevel - level);
                        var fineTile = Math.Min(fineIndex / Intervals, (1 << MaximumLevel) - 1);
                        var finest = BoundaryPatch(face, edge, MaximumLevel, fineTile);
                        Assert.AreEqual(sample, PlanetCoordinates.SampleEdge(finest, edge, fineIndex - fineTile * Intervals));
                    }
                }
            }
        }
    }

    [TestMethod]
    public void EightCubeCornersHaveExactlyTheirThreeSignedFaceOwners()
    {
        var owners = new Dictionary<PlanetSampleKey, HashSet<PlanetFace>>();
        foreach (var face in Enum.GetValues<PlanetFace>())
        {
            var patch = new PlanetPatchKey(face, 0, 0, 0);
            for (var u = 0; u <= Intervals; u += Intervals)
            {
                for (var v = 0; v <= Intervals; v += Intervals)
                {
                    var corner = PlanetCoordinates.Sample(patch, u, v);
                    if (!owners.TryGetValue(corner, out var faces))
                        owners.Add(corner, faces = []);
                    Assert.IsTrue(faces.Add(face));
                }
            }
        }

        Assert.HasCount(8, owners);
        foreach (var (corner, faces) in owners)
        {
            Assert.AreEqual(Extent, Math.Abs(corner.X));
            Assert.AreEqual(Extent, Math.Abs(corner.Y));
            Assert.AreEqual(Extent, Math.Abs(corner.Z));
            PlanetFace[] expected =
            [
                corner.X > 0 ? PlanetFace.PositiveX : PlanetFace.NegativeX,
                corner.Y > 0 ? PlanetFace.PositiveY : PlanetFace.NegativeY,
                corner.Z > 0 ? PlanetFace.PositiveZ : PlanetFace.NegativeZ
            ];
            Assert.IsTrue(faces.SetEquals(expected));
            Assert.AreEqual(expected[0], PlanetCoordinates.Project(PlanetCoordinates.Direction(corner)).Face);
        }
    }

    [TestMethod]
    public void InverseChartsRoundTripInteriorSamplesAndUseXThenYThenZAtTies()
    {
        foreach (var face in Enum.GetValues<PlanetFace>())
        {
            var sample = PlanetCoordinates.Sample(new PlanetPatchKey(face, 0, 0, 0), 3, 11);
            var direction = PlanetCoordinates.Direction(sample);
            var projection = PlanetCoordinates.Project(direction);
            Assert.AreEqual(face, projection.Face);
            Assert.AreEqual(-0.625, projection.U, 1e-14);
            Assert.AreEqual(0.375, projection.V, 1e-14);
            Assert.AreEqual(1.0, Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z), 1e-14);
        }

        Assert.AreEqual(PlanetFace.NegativeX, PlanetCoordinates.Project(new PlanetVector(-1, 1, 1)).Face);
        Assert.AreEqual(PlanetFace.PositiveY, PlanetCoordinates.Project(new PlanetVector(0.5, 1, -1)).Face);
        Assert.AreEqual(PlanetFace.NegativeZ, PlanetCoordinates.Project(new PlanetVector(0.5, 1, -Math.BitIncrement(1))).Face);
    }

    [TestMethod]
    public void InteriorNeighborsKeepTheirFaceAndMatchBoundaryInReverseOrder()
    {
        var patch = new PlanetPatchKey(PlanetFace.PositiveX, 3, 3, 4);
        (PlanetEdge Edge, PlanetPatchKey Patch, PlanetEdge OtherEdge)[] expected =
        [
            (PlanetEdge.Bottom, patch with { TileV = 3 }, PlanetEdge.Top),
            (PlanetEdge.Right, patch with { TileU = 4 }, PlanetEdge.Left),
            (PlanetEdge.Top, patch with { TileV = 5 }, PlanetEdge.Bottom),
            (PlanetEdge.Left, patch with { TileU = 2 }, PlanetEdge.Right)
        ];
        foreach (var (edge, neighbor, otherEdge) in expected)
        {
            Assert.AreEqual((neighbor, otherEdge, true), PlanetCoordinates.Neighbor(patch, edge));
            for (var i = 0; i <= Intervals; i++)
                Assert.AreEqual(PlanetCoordinates.SampleEdge(patch, edge, i),
                    PlanetCoordinates.SampleEdge(neighbor, otherEdge, Intervals - i));
        }
    }

    [TestMethod]
    public void InvalidKeysSamplesAndDirectionsAreRejected()
    {
        var patch = new PlanetPatchKey(PlanetFace.PositiveX, 0, 0, 0);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Sample(patch with { Face = (PlanetFace)99 }, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Sample(patch with { Level = -1 }, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Sample(patch with { Level = MaximumLevel + 1 }, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Sample(patch with { TileU = 1 }, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Sample(patch with { TileV = -1 }, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Sample(patch, -1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Sample(patch, 0, Intervals + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.SampleEdge(patch, PlanetEdge.Left, Intervals + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Neighbor(patch, (PlanetEdge)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Direction(default));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Project(default));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Project(new PlanetVector(double.NaN, 1, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Project(new PlanetVector(0, double.PositiveInfinity, 1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetCoordinates.Project(new PlanetVector(1, 0, double.NegativeInfinity)));
    }

    // The tile index increases along each chart's oriented perimeter.
    internal static PlanetPatchKey BoundaryPatch(PlanetFace face, PlanetEdge edge, int level, int index)
    {
        var last = (1 << level) - 1;
        return edge switch
        {
            PlanetEdge.Bottom => new PlanetPatchKey(face, level, index, 0),
            PlanetEdge.Right => new PlanetPatchKey(face, level, last, index),
            PlanetEdge.Top => new PlanetPatchKey(face, level, last - index, last),
            PlanetEdge.Left => new PlanetPatchKey(face, level, 0, last - index),
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
    }
}
