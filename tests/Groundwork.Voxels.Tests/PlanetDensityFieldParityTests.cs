using System.Numerics;
using System.Reflection;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetDensityFieldParityTests
{
    private delegate void DensityRowSampler(LatticeCoordinate origin, Span<long> values);
    private delegate void MaterialRowSampler(LatticeCoordinate origin, Span<ushort> values);

    [TestMethod]
    public void FreshRowsMatchScalarSamplesAcrossGridCrossingsDisabledLayersAndDomainEdges()
    {
        PlanetLandformRecipe[] recipes =
        [
            new(1024, -6144, 8192, new(192, 6144), new(96, 3072), new(24, 512), 48, 1, 2),
            new(61, -512, 1280, new(17, 1536), new(11, 1024), new(5, 192), 13, 7, 19),
            new(1, 0, 0, new(3, 0), new(5, 0), new(7, 0), 1, 7, 19),
            new(13, -256, 512, new(17, 0), new(11, 1024), new(5, 0), 3, 7, 19),
            new(32768, -6144, 8192, new(24576, 6144), new(49152, 3072), new(2, 512), 24576, 1, 2)
        ];
        var maximum = PlanetDensityField.MaximumCoordinate;
        long[] starts = [-maximum, -32769, -24577, -193, -97, -25, -1, 0, 23, 95, 191, 24573, 32765, maximum - 64];
        foreach (var seed in new ulong[] { 0, 42, ulong.MaxValue })
        foreach (var recipe in recipes)
        {
            var field = new PlanetDensityField(new(new("00112233-4455-6677-8899-aabbccddeeff"),
                new("10213243-5465-7687-98a9-bacbdcedfe0f"), seed, 1, 1, 1, recipe.ParameterSha256, 4096), recipe);
            var densityRow = typeof(PlanetDensityField).GetMethod("SampleDensityRow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<DensityRowSampler>(field);
            var materialRow = typeof(PlanetDensityField).GetMethod("SampleMaterialRow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<MaterialRowSampler>(field);
            foreach (var start in starts)
            foreach (var length in new[] { 0, 1, 32, 33, 65 })
            {
                var origin = new LatticeCoordinate(start, start < -1000 ? -maximum : 4096, start > 1000 ? maximum : -17);
                var densities = new long[length];
                var materials = new ushort[length];
                densityRow(origin, densities);
                materialRow(origin, materials);
                for (var index = 0; index < length; index++)
                {
                    var vertex = origin with { X = start + index };
                    Assert.AreEqual(field.SampleDensity(vertex), densities[index], $"Density seed={seed}, vertex={vertex}");
                    Assert.AreEqual(field.SampleMaterial(vertex), materials[index], $"Material seed={seed}, vertex={vertex}");
                }
            }
        }
    }

    [TestMethod]
    public void RowsRejectOutOfDomainEndBeforeWritingAnySample()
    {
        var recipe = new PlanetLandformRecipe(61, 0, 0, new(17, 0), new(11, 0), new(5, 0), 13, 7, 19);
        var field = new PlanetDensityField(new(new("00112233-4455-6677-8899-aabbccddeeff"),
            new("10213243-5465-7687-98a9-bacbdcedfe0f"), 42, 1, 1, 1, recipe.ParameterSha256, 4096), recipe);
        var densityRow = typeof(PlanetDensityField).GetMethod("SampleDensityRow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<DensityRowSampler>(field);
        var materialRow = typeof(PlanetDensityField).GetMethod("SampleMaterialRow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<MaterialRowSampler>(field);
        long[] densities = [-7, -7];
        ushort[] materials = [23, 23];
        var origin = new LatticeCoordinate(PlanetDensityField.MaximumCoordinate, 0, 0);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => densityRow(origin, densities));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => materialRow(origin, materials));
        CollectionAssert.AreEqual(new long[] { -7, -7 }, densities);
        CollectionAssert.AreEqual(new ushort[] { 23, 23 }, materials);
    }

    [TestMethod]
    public void NoiseMatchesOriginalCornerEvaluationAcrossStreamsScalesAndSignedBoundaries()
    {
        var recipe = new PlanetLandformRecipe(61, -512, 1280,
            new(17, 1536), new(11, 1024), new(5, 192), 13, 7, 19);
        int[] scales = [1, 24, 48, 61, 96, 192, 1024, int.MaxValue];
        ulong[] seeds = [0, 42, 0x8000000000000001, ulong.MaxValue];
        // Include all V1 streams and an unrelated stream used by geology.
        ulong[] streams = [0x434F4E54494E454E, 0x4D4F554E5441494E, 0x56414C4C45590000,
            0x44455441494C0000, 0x4D4154455249414C, 0x5354524154410002];
        var method = typeof(PlanetDensityField).GetMethod("Noise", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var seed in seeds)
        {
            var field = new PlanetDensityField(new(new("00112233-4455-6677-8899-aabbccddeeff"),
                new("10213243-5465-7687-98a9-bacbdcedfe0f"), seed, 1, 1, 1, recipe.ParameterSha256, 4096), recipe);
            var sample = method.CreateDelegate<Func<LatticeCoordinate, int, ulong, int>>(field);
            foreach (var scale in scales)
            foreach (var coordinate in BoundaryCoordinates(scale))
            foreach (var stream in streams)
                Assert.AreEqual(OriginalNoise(seed, coordinate, scale, stream), sample(coordinate, scale, stream),
                    $"seed={seed}, scale={scale}, stream={stream}, coordinate={coordinate}");
        }
    }

    [TestMethod]
    public void NearestSquareRootMatchesIndependentIntegerOracleAcrossFastPathAndDomainBoundaries()
    {
        var method = typeof(PlanetDensityField).GetMethod("NearestSquareRoot", BindingFlags.Static | BindingFlags.NonPublic)!;
        var nearest = method.CreateDelegate<Func<UInt128, long>>();
        var values = new HashSet<UInt128> { 0, 1, 2, 3, 4, 5, ulong.MaxValue - 1, ulong.MaxValue,
            (UInt128)ulong.MaxValue + 1, (UInt128)ulong.MaxValue + 2 };
        ulong[] roots = [1, 2, 3, 255, 256, 65535, 65536, uint.MaxValue,
            (ulong)uint.MaxValue + 1, 1UL << 40, 1UL << 54, 1UL << 62];
        foreach (var root in roots)
        {
            var square = (UInt128)root * root;
            // Adjacent squares and both sides of the exact nearest-integer decision.
            values.UnionWith([square - 1, square, square + 1, square + root, square + root + 1]);
        }
        // Largest supported three-axis density coordinate, including the 256-unit scaling.
        var maximum = (UInt128)PlanetDensityField.MaximumCoordinate;
        var domainMaximum = 3 * maximum * maximum << 16;
        values.UnionWith([domainMaximum - 1, domainMaximum, domainMaximum + 1]);
        // Exercise both sides of many powers of two, not merely perfect-square inputs.
        for (var bit = 1; bit <= 125; bit++)
        {
            var power = UInt128.One << bit;
            values.UnionWith([power - 1, power, power + 1]);
        }
        foreach (var squared in values)
            Assert.AreEqual(OracleNearestSquareRoot(squared), nearest(squared), $"squared={squared}");
    }

    private static IEnumerable<LatticeCoordinate> BoundaryCoordinates(int scale)
    {
        foreach (var grid in new[] { -2, -1, 0, 1, 2 })
        foreach (var offset in new[] { -1, 0, 1 })
        {
            var axis = (long)grid * scale + offset;
            yield return new(axis, -13, 27);
            yield return new(31, axis, -17);
            yield return new(-19, 23, axis);
        }
        var maximum = PlanetDensityField.MaximumCoordinate;
        yield return new(maximum, maximum, maximum);
        yield return new(-maximum, -maximum, -maximum);
        yield return new(maximum, -maximum, maximum - 1);
        yield return new(-maximum + 1, maximum - 1, -maximum);
    }

    // Preserve the original eight independent X/Y/Z hash chains and X-then-Y-then-Z
    // rounding. Do not call production Mix, Grid, or RoundDivide from this oracle.
    private static int OriginalNoise(ulong worldSeed, LatticeCoordinate vertex, int scale, ulong stream)
    {
        var (x, dx) = OriginalGrid(vertex.X, scale);
        var (y, dy) = OriginalGrid(vertex.Y, scale);
        var (z, dz) = OriginalGrid(vertex.Z, scale);
        var seed = OriginalMix(worldSeed ^ stream);
        return (int)Interpolate(Plane(z), Plane(z + 1), dz, scale);

        long Plane(long plane) => Interpolate(
            Interpolate(Corner(x, y, plane), Corner(x + 1, y, plane), dx, scale),
            Interpolate(Corner(x, y + 1, plane), Corner(x + 1, y + 1, plane), dx, scale), dy, scale);

        int Corner(long i, long j, long k)
        {
            var hash = OriginalMix(seed ^ unchecked((ulong)i));
            hash = OriginalMix(hash ^ unchecked((ulong)j));
            hash = OriginalMix(hash ^ unchecked((ulong)k));
            return (int)(hash >> 48) - 32768;
        }
    }

    private static (long Index, long Remainder) OriginalGrid(long axis, int scale)
    {
        var quotient = axis / scale;
        var remainder = axis % scale;
        return remainder < 0 ? (quotient - 1, remainder + scale) : (quotient, remainder);
    }

    private static long Interpolate(long a, long b, long weight, int scale)
    {
        var numerator = a * (scale - weight) + b * weight;
        var quotient = numerator / scale;
        var remainder = numerator % scale;
        if (2 * Math.Abs(remainder) > scale || (2 * Math.Abs(remainder) == scale && (quotient & 1) != 0))
            quotient += Math.Sign(remainder);
        return quotient;
    }

    private static ulong OriginalMix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EB;
            return value ^ (value >> 31);
        }
    }

    private static long OracleNearestSquareRoot(UInt128 squared)
    {
        var value = BigInteger.CreateChecked(squared);
        BigInteger lower = 0, upper = long.MaxValue;
        while (lower < upper)
        {
            var midpoint = (lower + upper + 1) / 2;
            if (midpoint * midpoint <= value)
                lower = midpoint;
            else
                upper = midpoint - 1;
        }
        // Compare distances to the neighboring integer squares. No floating point.
        var next = lower + 1;
        return (long)(value - lower * lower < next * next - value ? lower : next);
    }
}
