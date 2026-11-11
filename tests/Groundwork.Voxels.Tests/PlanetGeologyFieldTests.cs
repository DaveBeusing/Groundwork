using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetGeologyFieldTests
{
    private static readonly Guid World = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Planet = new("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly PlanetLandformRecipe Flat =
        new(61, 0, 0, new(17, 0), new(11, 0), new(5, 0), 13, 7, 19);

    [TestMethod]
    public void RecipeAndThreeSeedBricksMatchGoldenHashesInEveryDispatchOrder()
    {
        var recipe = Recipe(shaftChance: 32768);
        (ulong Seed, double Radius, LatticeBrick Brick, string Hash)[] fixtures =
        [
            (0, 96, new(-2, 0, 0), "588854EB35C4E44A729D34DBEB21227AE3D03E2FF1CD5EFCED249045CFF1D66A"),
            (42, 170, new(2, -2, 0), "C790D73556F922CAC1ACA28C36B407362F6F755A57ECCD5DC4DAF0866E840362"),
            (ulong.MaxValue, PlanetDescriptor.MaximumRadiusMeters, new(199996, -2, 0), "35D7CB770F87D91E0EACD93B525C2797DEF4AEC0FC2F0DC489D7D773B94DC45D"),
        ];
        var actual = new List<string>();
        foreach (var fixture in fixtures)
        {
            var field = PlanetTerrainGenerator.Create(Describe(recipe, fixture.Seed, fixture.Radius), geology: recipe);
            LatticeBrick[] bricks = [fixture.Brick, fixture.Brick with { X = fixture.Brick.X + 1 }];
            var serial = BrickDigest(field, bricks, reverse: false, parallel: false);
            Assert.AreEqual(serial, BrickDigest(field, bricks, reverse: true, parallel: false));
            Assert.AreEqual(serial, BrickDigest(field, bricks, reverse: true, parallel: true));
            actual.Add(serial);
        }
        CollectionAssert.AreEqual(fixtures.Select(fixture => fixture.Hash).ToArray(), actual.ToArray(),
            $"Recipe {recipe.ParameterSha256}, bricks {string.Join(", ", actual)}");
        Assert.AreEqual("3c092efc98ccdc1704da570b8e4789f0291d50b72590d06f864e763d8bfb4fca", recipe.ParameterSha256);
        Assert.HasCount(3, actual.Distinct());
    }

    [TestMethod]
    public void PortalsKeepClearanceFromBothBricksOnAllThreeAxesAndSignedBoundaries()
    {
        var recipe = Recipe();
        var field = new PlanetGeologyField(Describe(recipe, 42, 128), recipe);
        for (var axis = 0; axis < 3; axis++)
        foreach (var plane in new[] { -64, 64 })
        {
            var face = Enumerable.Range(24, 17)
                .SelectMany(a => Enumerable.Range(24, 17).Select(b => Point(axis, plane, a, b)))
                .MaxBy(point => field.SampleDensity(point));
            Assert.AreEqual(5L * PlanetDensityField.UnitsPerCell, field.SampleDensity(face), $"Axis {axis}, plane {plane}");

            var before = Offset(face, axis, -1).Locate().Brick;
            var after = face.Locate().Brick;
            var left = new PlanetBrickStore(field, PlanetBrickStore.BrickPayloadBytes, 1);
            var right = new PlanetBrickStore(field, PlanetBrickStore.BrickPayloadBytes, 1);
            Assert.IsTrue(left.TryGenerate(before));
            Assert.IsTrue(right.TryGenerate(after));
            using var leftLease = left.TryAcquire(before)!;
            using var rightLease = right.TryAcquire(after)!;
            for (var a = 0; a <= PlanetBrickStore.CellEdge; a++)
            for (var b = 0; b <= PlanetBrickStore.CellEdge; b++)
            {
                var upper = Point(axis, PlanetBrickStore.CellEdge, a, b);
                var lower = Point(axis, 0, a, b);
                Assert.AreEqual(leftLease.ReadDensity((int)upper.X, (int)upper.Y, (int)upper.Z),
                    rightLease.ReadDensity((int)lower.X, (int)lower.Y, (int)lower.Z));
            }

            for (var step = -4; step <= 4; step++)
            for (var a = -2; a <= 2; a++)
            for (var b = -2; b <= 2; b++)
            {
                var point = Offset(face, axis, step);
                point = Offset(Offset(point, (axis + 1) % 3, a), (axis + 2) % 3, b);
                Assert.IsGreaterThan(0L, field.SampleDensity(point), $"Blocked aperture at {point}.");
            }
        }
    }

    [TestMethod]
    public void SealedChamberAndDesignatedRampRespectTheDepthEnvelope()
    {
        var sealedRecipe = Recipe(portalChance: 0);
        var sealedField = new PlanetGeologyField(Describe(sealedRecipe, 42, 128), sealedRecipe);
        Assert.IsGreaterThan(0L, sealedField.SampleDensity(new(32, 32, 32)));
        Assert.IsLessThan(0L, sealedField.SampleDensity(new(32, 32, 44)));
        Assert.IsLessThan(0L, sealedField.SampleDensity(new(64, 32, 32)));
        Assert.AreEqual(-128L * PlanetDensityField.UnitsPerCell, sealedField.SampleDensity(default));
        Assert.AreEqual(0L, sealedField.SampleDensity(new(128, 0, 0)));

        var rampRecipe = Recipe(portalChance: 0, shaftChance: 65536);
        var ramp = new PlanetGeologyField(Describe(rampRecipe, 42, 116), rampRecipe);
        var closed = new PlanetGeologyField(Describe(sealedRecipe, 42, 116), sealedRecipe);
        for (var t = 0; t <= 4; t++)
            Assert.IsGreaterThan(0L, ramp.SampleDensity(new(96 + 4 * t, 32 + 5 * t, 32)));
        var mouth = new LatticeCoordinate(103, 41, 32);
        Assert.IsLessThan(0L, closed.SampleDensity(mouth));
        Assert.IsGreaterThan(0L, ramp.SampleDensity(mouth));
        Assert.AreEqual(closed.SampleDensity(new(104, 42, 15)), ramp.SampleDensity(new(104, 42, 15)));
    }

    [TestMethod]
    public void StrataAndOreAreSeededAndCavitiesDoNotHaveOre()
    {
        var recipe = Recipe(portalChance: 0);
        var field = new PlanetGeologyField(Describe(recipe, 42, 128), recipe);
        Assert.AreEqual(recipe.ShallowRock, field.SampleMaterial(new(127, 0, 0)));
        Assert.AreEqual(recipe.DeepRock, field.SampleMaterial(new(15, 0, 0)));

        var middle = Enumerable.Range(0, 32)
            .SelectMany(y => Enumerable.Range(0, 32).Select(z => new LatticeCoordinate(75, y, z)))
            .Select(field.SampleMaterial).ToArray();
        CollectionAssert.Contains(middle, recipe.MiddleRock);
        CollectionAssert.Contains(middle, recipe.Ore);

        var cavityRecipe = Recipe(portalChance: 0, oreThreshold: 0, deepBandCells: 100);
        var cavity = new PlanetGeologyField(Describe(cavityRecipe, 42, 128), cavityRecipe);
        var air = Enumerable.Range(27, 11).SelectMany(y =>
            Enumerable.Range(27, 11).Select(z => new LatticeCoordinate(32, y, z)));
        foreach (var point in air)
        {
            Assert.IsGreaterThan(0L, cavity.SampleDensity(point));
            Assert.AreEqual(cavityRecipe.MiddleRock, cavity.SampleMaterial(point));
        }
    }

    [TestMethod]
    public void DispatchRejectsWrongRecipeAndOutOfDomainCoordinates()
    {
        var recipe = Recipe();
        var descriptor = Describe(recipe, 42, 1);
        Assert.ThrowsExactly<ArgumentException>(() => PlanetTerrainGenerator.Create(descriptor, landform: Flat));
        Assert.ThrowsExactly<ArgumentException>(() => PlanetTerrainGenerator.Create(descriptor, geology: Recipe(portalChance: 1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Recipe(maximumDepth: int.MinValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Recipe(shaftChance: 65537));

        var field = PlanetTerrainGenerator.Create(descriptor, geology: recipe);
        Assert.AreEqual(-256L, field.SampleDensity(default));
        Assert.AreEqual(0L, field.SampleDensity(new(1, 0, 0)));
        Assert.IsGreaterThan(0L, field.SampleDensity(new(-PlanetDensityField.MaximumCoordinate, 0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => field.SampleDensity(new(long.MinValue, 0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => field.SampleMaterial(new(0, 0, long.MaxValue)));
    }

    private static PlanetGeologyRecipe Recipe(int portalChance = 65536, int shaftChance = 0,
        int oreThreshold = 2500, int deepBandCells = 65, int maximumDepth = 112) =>
        new(Flat, 12, deepBandCells, 19, 3, 7, oreThreshold, 8, maximumDepth,
            portalChance, shaftChance, 21, 22, 23, 24);

    private static PlanetDescriptor Describe(PlanetGeologyRecipe recipe, ulong seed, double radius) =>
        new(World, Planet, seed, PlanetGeologyField.GeneratorVersion, PlanetDensityField.TerrainSchemaVersion,
            2, recipe.ParameterSha256, radius);

    private static LatticeCoordinate Point(int axis, int along, int a, int b) => axis switch
    {
        0 => new(along, a, b),
        1 => new(b, along, a),
        _ => new(a, b, along),
    };

    private static LatticeCoordinate Offset(LatticeCoordinate point, int axis, int amount) => axis switch
    {
        0 => point with { X = point.X + amount },
        1 => point with { Y = point.Y + amount },
        _ => point with { Z = point.Z + amount },
    };

    private static string BrickDigest(IPlanetBrickSource field, LatticeBrick[] bricks, bool reverse, bool parallel)
    {
        var store = new PlanetBrickStore(field, bricks.Length * PlanetBrickStore.BrickPayloadBytes, bricks.Length);
        var order = reverse ? bricks.Reverse() : bricks;
        if (parallel)
            Parallel.ForEach(order, brick => Assert.IsTrue(store.TryGenerate(brick)));
        else
            foreach (var brick in order)
                Assert.IsTrue(store.TryGenerate(brick));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        foreach (var brick in bricks)
        {
            using var lease = store.TryAcquire(brick)!;
            for (var z = 0; z <= PlanetBrickStore.CellEdge; z++)
            for (var y = 0; y <= PlanetBrickStore.CellEdge; y++)
            for (var x = 0; x <= PlanetBrickStore.CellEdge; x++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes, lease.ReadDensity(x, y, z));
                hash.AppendData(bytes);
                if (x < PlanetBrickStore.CellEdge && y < PlanetBrickStore.CellEdge && z < PlanetBrickStore.CellEdge)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes, lease.ReadMaterial(x, y, z));
                    hash.AppendData(bytes[..sizeof(ushort)]);
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
