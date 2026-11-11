using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetDensityFieldTests
{
    private static readonly Guid WorldId = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid PlanetId = new("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly PlanetLandformRecipe Recipe =
        new(61, -512, 1280, new(17, 1536), new(11, 1024), new(5, 192), 13, 7, 19);

    [TestMethod]
    public void ThreeSeedsMatchIndependentGoldenTileAndLandformVectorsInEveryDispatchOrder()
    {
        Assert.AreEqual("c46f414a922dbe6e83dd8b62568a3be8bda2bdf34073541f1d1b81e59cb3dd15", Recipe.ParameterSha256);
        (ulong Seed, long Density, ushort Material, PlanetLandform Landform, string Digest)[] fixtures =
        [
            (0, -1294, 7, new(53433, 949, 1306, -686, 38), "239029E1413A2229116F553B482ED839E806B92895504E4503B8A9822225C693"),
            (42, -465, 19, new(30805, 330, 1497, -994, -55), "15CD0F65D91F4F21F6688BEA27AD666BF6A3CD3F3DA38CFB54738845C755E575"),
            (ulong.MaxValue, 340, 7, new(20162, 39, 739, -832, 27), "9EF3AE1E98A7897EEA6195EAE2DD09198C47A47AEE8C2D17DB7D823B5FF89B3F")
        ];

        var canonicalOrder = Enumerable.Range(0, 12).ToArray();
        var reverseOrder = canonicalOrder.Reverse().ToArray();
        var shuffled = (int[])canonicalOrder.Clone();
        new Random(8002).Shuffle(shuffled);
        var distinct = new HashSet<string>();

        foreach (var fixture in fixtures)
        {
            var field = (PlanetDensityField)PlanetTerrainGenerator.Create(Describe(fixture.Seed), landform: Recipe);
            var point = new LatticeCoordinate(-128, -17, 5);
            Assert.AreEqual(fixture.Density, field.SampleDensity(point));
            Assert.AreEqual(fixture.Material, field.SampleMaterial(point));
            Assert.AreEqual(fixture.Landform, field.SampleLandform(point));

            var serial = TileDigest(field, canonicalOrder, parallel: false);
            Assert.AreEqual(fixture.Digest, serial);
            Assert.AreEqual(serial, TileDigest(field, reverseOrder, parallel: false));
            Assert.AreEqual(serial, TileDigest(field, shuffled, parallel: true));
            Assert.IsTrue(distinct.Add(serial));
        }
    }

    [TestMethod]
    public void DensitySignAndZeroAreIndependentOfMaterial()
    {
        var flat = new PlanetLandformRecipe(61, 0, 0, new(17, 0), new(11, 0), new(5, 0), 13, 7, 7);
        var field = new PlanetDensityField(Describe(42, flat, radius: 128.25), flat);
        Assert.AreEqual(-64L, field.SampleDensity(new LatticeCoordinate(128, 0, 0)));
        Assert.AreEqual(192L, field.SampleDensity(new LatticeCoordinate(129, 0, 0)));
        Assert.AreEqual((ushort)7, field.SampleMaterial(new LatticeCoordinate(129, 0, 0)));

        // 3-4-5 direction, exactly on the reference sphere and on the lattice.
        field = new PlanetDensityField(Describe(42, flat, radius: 125), flat);
        Assert.AreEqual(0L, field.SampleDensity(new LatticeCoordinate(-75, 100, 0)));
        Assert.AreEqual(-125L * PlanetDensityField.UnitsPerCell, field.SampleDensity(new LatticeCoordinate()));
        Assert.IsGreaterThan(0L, field.SampleDensity(new LatticeCoordinate(
            PlanetDensityField.MaximumCoordinate, -PlanetDensityField.MaximumCoordinate,
            PlanetDensityField.MaximumCoordinate)));

        field = new PlanetDensityField(Describe(42, flat, radius: 1 + 1.0 / 512), flat);
        Assert.AreEqual(0L, field.SampleDensity(new LatticeCoordinate(1, 0, 0)));
        field = new PlanetDensityField(Describe(42, flat, radius: 1 + 3.0 / 512), flat);
        Assert.AreEqual(-2L, field.SampleDensity(new LatticeCoordinate(1, 0, 0)));
    }

    [TestMethod]
    public void SharedBrickVerticesAndMixedLodChartsReturnIdenticalDensity()
    {
        var field = new PlanetDensityField(Describe(42), Recipe);
        var corner = new LatticeCoordinate(-128, 0, 0);
        var expected = field.SampleDensity(corner);
        for (var x = 0; x <= 1; x++)
        for (var y = 0; y <= 1; y++)
        for (var z = 0; z <= 1; z++)
            Assert.AreEqual(expected, field.SampleDensity(new LatticeBrick(-4 - x, -y, -z).Vertex(32 * x, 32 * y, 32 * z)));

        foreach (var (face, edge, otherFace, otherEdge) in PlanetCoordinateTests.Seams)
        {
            var a = field.Descriptor.SampleAt(new PlanetPatchKey(face, 0, 0, 0), edge, 4);
            var b = field.Descriptor.SampleAt(BoundaryPatch(otherFace, otherEdge), otherEdge, 8);
            Assert.AreEqual(a, b);
            Assert.AreEqual(field.SampleDensity(a), field.SampleDensity(b));
        }
    }

    [TestMethod]
    public void NoiseInterpolatesContinuouslyAcrossSignedGridPlanes()
    {
        var field = new PlanetDensityField(Describe(42), Recipe);
        foreach (var plane in new[] { -61, 0, 61 })
        {
            foreach (var coordinate in new[] { new LatticeCoordinate(plane, -13, 24),
                new LatticeCoordinate(12, plane, -24), new LatticeCoordinate(-12, 13, plane) })
            {
                var center = field.SampleLandform(coordinate).ContinentMask;
                var axis = coordinate.X == plane ? 0 : coordinate.Y == plane ? 1 : 2;
                for (var offset = -1; offset <= 1; offset += 2)
                {
                    var adjacent = axis switch
                    {
                        0 => coordinate with { X = coordinate.X + offset },
                        1 => coordinate with { Y = coordinate.Y + offset },
                        _ => coordinate with { Z = coordinate.Z + offset }
                    };
                    Assert.IsLessThanOrEqualTo(1076, Math.Abs(field.SampleLandform(adjacent).ContinentMask - center));
                }
            }
        }
    }

    [TestMethod]
    public void RejectsUnsupportedVersionsRecipeMismatchAndOutOfDomainSamples()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PlanetDensityField(Describe(42, generatorVersion: 2), Recipe));
        Assert.ThrowsExactly<ArgumentException>(() => new PlanetDensityField(Describe(42, schemaVersion: 2), Recipe));
        var other = new PlanetLandformRecipe(61, -512, 1281, new(17, 1536), new(11, 1024), new(5, 192), 13, 7, 19);
        Assert.AreNotEqual(Recipe.ParameterSha256, other.ParameterSha256);
        Assert.ThrowsExactly<ArgumentException>(() => new PlanetDensityField(Describe(42), other));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PlanetLandformRecipe(61, 0, 0, default, new(11, 0), new(5, 0), 13, 7, 19));

        var field = new PlanetDensityField(Describe(42), Recipe);
        Assert.ThrowsExactly<ArgumentException>(() => field.SampleDensity(new PlanetLatticeSample(PlanetId, WorldId, default)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => field.SampleDensity(new LatticeCoordinate(long.MinValue, 0, 0)));
    }

    private static PlanetDescriptor Describe(ulong seed, PlanetLandformRecipe? recipe = null, double radius = 128,
        uint generatorVersion = PlanetDensityField.GeneratorVersion, uint schemaVersion = PlanetDensityField.TerrainSchemaVersion) =>
        new(WorldId, PlanetId, seed, generatorVersion, schemaVersion, 1, (recipe ?? Recipe).ParameterSha256, radius);

    private static PlanetPatchKey BoundaryPatch(PlanetFace face, PlanetEdge edge) => edge switch
    {
        PlanetEdge.Bottom => new(face, 1, 1, 0),
        PlanetEdge.Right => new(face, 1, 1, 1),
        PlanetEdge.Top => new(face, 1, 0, 1),
        PlanetEdge.Left => new(face, 1, 0, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(edge))
    };

    private static string TileDigest(PlanetDensityField field, int[] order, bool parallel)
    {
        var hashes = new byte[order.Length * SHA256.HashSizeInBytes];
        if (parallel)
            Parallel.ForEach(order, Evaluate);
        else
            foreach (var tile in order)
                Evaluate(tile);

        return Convert.ToHexString(SHA256.HashData(hashes));

        void Evaluate(int tile)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> bytes = stackalloc byte[46];
            var origin = new LatticeCoordinate(tile % 2 == 0 ? -132 : 124, tile * 5 - 30, 13 - tile * 3);
            for (var z = 0; z < 5; z++)
            for (var y = 0; y < 5; y++)
            for (var x = 0; x < 5; x++)
            {
                var position = new LatticeCoordinate(origin.X + x, origin.Y + y, origin.Z + z);
                var landform = field.SampleLandform(position);
                BinaryPrimitives.WriteInt64LittleEndian(bytes, field.SampleDensity(position));
                BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], field.SampleMaterial(position));
                BinaryPrimitives.WriteInt32LittleEndian(bytes[10..], landform.ContinentMask);
                BinaryPrimitives.WriteInt64LittleEndian(bytes[14..], landform.BaseElevation);
                BinaryPrimitives.WriteInt64LittleEndian(bytes[22..], landform.Mountain);
                BinaryPrimitives.WriteInt64LittleEndian(bytes[30..], landform.Valley);
                BinaryPrimitives.WriteInt64LittleEndian(bytes[38..], landform.Detail);
                hash.AppendData(bytes);
            }

            hash.GetHashAndReset(hashes.AsSpan(tile * SHA256.HashSizeInBytes, SHA256.HashSizeInBytes));
        }
    }
}
