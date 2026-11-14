using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetConnectedFieldTests
{
    private static readonly PlanetLandformRecipe Flat =
        new(61, 0, 0, new(17, 0), new(11, 0), new(5, 0), 13, 7, 19);
    private static readonly PlanetGeologyRecipe Geology =
        new(Flat, 4, 92, 19, 1, 7, 0, 8, 104, 65536, 65536, 21, 22, 24, 26);
    private static readonly PlanetConnectedRecipe Recipe = new(Geology, 2, 5632, 4096, 2560, 1024);

    [TestMethod]
    public void CanonicalSamplesAndBricksMatchThreeSeedGoldens()
    {
        (ulong Seed, double Radius)[] fixtures = [(42, 4096), (20261111, 96), (ulong.MaxValue, PlanetDescriptor.MaximumRadiusMeters)];
        var hashes = new List<string>();
        foreach (var (seed, radius) in fixtures)
        {
            var field = Open(seed, radius);
            var x = (long)radius;
            LatticeBrick[] bricks = [new LatticeCoordinate(x - 20, -17, 23).Locate().Brick,
                new LatticeCoordinate(x + 12, -17, 23).Locate().Brick];
            var serial = PlanetGeologyFieldTests.BrickDigest(field, bricks, reverse: false, parallel: false);
            Assert.AreEqual(serial, PlanetGeologyFieldTests.BrickDigest(field, bricks, reverse: true, parallel: true));
            hashes.Add(serial);
            hashes.Add(SampleDigest(field));
        }
        string[] expected = [
            "568A12401E5E0ECC5428C0570476018860B8D82C67DD0DE954C76E971B5FD407",
            "D60DBD0D714C4D5F3B2BA68D19C9678F63197BC51E15378656546EF89B9D2A5D",
            "7B2F4FDB5E948456868D9A6059AFB8AE75590D09C2705B9FC42DC05DD355F72E",
            "A1998061DCD4BB942DBD6763F26B58A7BEF78B5E603A8DA617950E1ACDBDE006",
            "8F31B3EF939DD93B8665F9451B72107C5251F3F3D7FC3C3CB42A32B14A558E4A",
            "835112C85CD6F857810C846C51D2E7AF7A37C1B09E9F62A9455B53A2AD5A8F70",
        ];
        CollectionAssert.AreEqual(expected, hashes.ToArray(), string.Join(",", hashes));
    }

    [TestMethod]
    public void AllEdgesAndCornersShareReliefDensityAndMaterialAtMixedLods()
    {
        const int intervals = PlanetCoordinates.IntervalsPerPatch;
        foreach (var radius in new[] { 96d, 4096, PlanetDescriptor.MaximumRadiusMeters })
        {
            var field = Open(42, radius);
            var descriptor = field.Descriptor;
            foreach (var (face, edge, other, otherEdge) in PlanetCoordinateTests.Seams)
            {
                var coarse = new PlanetPatchKey(face, 0, 0, 0);
                var reference = Enumerable.Range(0, intervals + 1)
                    .Select(index => descriptor.SampleAt(coarse, edge, index))
                    .Distinct().ToDictionary(sample => sample, sample => Values(field, sample.Vertex));
                for (var half = 0; half < 2; half++)
                {
                    var patch = otherEdge switch
                    {
                        PlanetEdge.Left => new PlanetPatchKey(other, 1, 0, half),
                        PlanetEdge.Right => new PlanetPatchKey(other, 1, 1, half),
                        PlanetEdge.Bottom => new PlanetPatchKey(other, 1, half, 0),
                        _ => new PlanetPatchKey(other, 1, half, 1),
                    };
                    for (var i = 0; i <= intervals; i += 2)
                    {
                        var sample = descriptor.SampleAt(patch, otherEdge, i);
                        Assert.AreEqual(reference[sample], Values(field, sample.Vertex));
                    }
                }
            }

            var corners = new Dictionary<PlanetLatticeSample, int>();
            foreach (var face in Enum.GetValues<PlanetFace>())
            foreach (var u in new[] { 0, intervals })
            foreach (var v in new[] { 0, intervals })
            {
                var root = new PlanetPatchKey(face, 0, 0, 0);
                var fine = new PlanetPatchKey(face, 3, u == 0 ? 0 : 7, v == 0 ? 0 : 7);
                var sample = descriptor.SampleAt(root, u, v);
                var chart = PlanetCoordinates.Sample(fine, u, v);
                Assert.AreEqual(sample, descriptor.SampleAt(fine, u, v));
                var position = descriptor.ChartPosition(chart);
                var frame = PlanetLocalFrame.Create(default);
                Assert.IsTrue(frame.TryRebase(position, out var rebased));
                var local = rebased.ToLocalDouble(position);
                var reconstructed = new PlanetVector(rebased.Origin.X + local.X, rebased.Origin.Y + local.Y, rebased.Origin.Z + local.Z);
                Assert.AreEqual(sample, descriptor.SampleAt(reconstructed));
                Assert.AreEqual(Values(field, sample.Vertex), Values(field, descriptor.SampleAt(reconstructed).Vertex));
                corners[sample] = corners.GetValueOrDefault(sample) + 1;
            }
            Assert.HasCount(8, corners);
            Assert.IsTrue(corners.Values.All(count => count == 3));
        }
    }

    [TestMethod]
    public void SharedReliefDrivesUncarvedDensityAndWrongRecipesAreRejected()
    {
        var field = Open(42, 4096);
        var reference = new PlanetDensityField(new(field.Descriptor.WorldId, field.Descriptor.PlanetId, 42, 1, 1, 2,
            Flat.ParameterSha256, 4096), Flat);
        var kinds = default(PlanetConnectedSample);
        for (var z = -256; z < 256; z += 4)
        for (var x = -256; x < 256; x += 4)
        {
            var vertex = new LatticeCoordinate(x, 4096, z);
            var relief = field.SampleConnectedRelief(vertex);
            Assert.AreEqual(reference.SampleDensity(vertex) - field.SampleLandform(vertex).Elevation,
                field.SampleUncarvedDensity(vertex));
            kinds = new(Math.Max(kinds.Ridge, relief.Ridge), Math.Max(kinds.MainValley, relief.MainValley),
                Math.Max(kinds.Tributary, relief.Tributary), Math.Max(kinds.Cut, relief.Cut));
        }
        Assert.IsTrue(kinds is { Ridge: > 0, MainValley: > 0, Tributary: > 0, Cut: > 0 });
        Assert.ThrowsExactly<ArgumentException>(() => PlanetTerrainGenerator.Create(field.Descriptor, geology: Geology));
        Assert.ThrowsExactly<ArgumentException>(() => PlanetTerrainGenerator.CreateConnected(field.Descriptor,
            new PlanetConnectedRecipe(Geology, 3, 5632, 4096, 2560, 1024)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => field.SampleLandform(new(1L << 55, 0, 0)));
    }

    private static PlanetGeologyField Open(ulong seed, double radius) =>
        PlanetTerrainGenerator.CreateConnected(new(new("00112233-4455-6677-8899-aabbccddeeff"),
            new("10213243-5465-7687-98a9-bacbdcedfe0f"), seed, 3, 1, 2, Recipe.ParameterSha256, radius), Recipe);

    private static (PlanetLandform Landform, long Density, ushort Material) Values(PlanetGeologyField field, LatticeCoordinate vertex) =>
        (field.SampleLandform(vertex), field.SampleDensity(vertex), field.SampleMaterial(vertex));

    private static string SampleDigest(PlanetGeologyField field)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        for (var z = -256; z <= 256; z += 16)
        for (var x = -256; x <= 256; x += 16)
        {
            var sample = field.SampleConnectedRelief(new(x, (long)field.Descriptor.RadiusMeters, z));
            foreach (var contribution in new[] { sample.Ridge, sample.MainValley, sample.Tributary, sample.Cut })
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes, contribution);
                hash.AppendData(bytes);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
