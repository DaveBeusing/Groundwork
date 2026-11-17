using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetRoundedDivisionTests
{
    [TestMethod]
    public void CachedDivisionMatchesSignedScalarTiesToEvenAcrossDomainAndBoundaries()
    {
        var divisorType = typeof(PlanetDensityField).GetNestedType("RoundedDivisor", BindingFlags.NonPublic);
        Assert.IsNotNull(divisorType);
        var constructor = divisorType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: [typeof(int)], modifiers: null);
        var round = divisorType.GetMethod("Round", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: [typeof(long)], modifiers: null);
        Assert.IsNotNull(constructor);
        Assert.IsNotNull(round);

        var random = new Random(0x431925);
        var denominators = new HashSet<int>
        {
            1, 2, 3, 24, 48, 96, 192, 1024, 32768, 65535, int.MaxValue,
            4, 7, 8, 16, 31, 32, 33, 63, 64, 65, 127, 128, 129,
        };
        for (var i = 0; i < 24; i++)
            denominators.Add(random.Next(1, int.MaxValue));

        var compared = 0;
        foreach (var denominator in denominators.Order())
        {
            // Box the private readonly value type once per denominator. The oracle below
            // uses signed division/remainder and never the cached reciprocal algorithm.
            var divisor = constructor.Invoke([denominator]);
            foreach (var numerator in Numerators(denominator, random))
            {
                var expected = ScalarRound(numerator, denominator);
                var actual = (long)round.Invoke(divisor, [numerator])!;
                if (actual != expected)
                    Assert.Fail($"Round({numerator}, {denominator}) returned {actual}; scalar ties-to-even gives {expected}.");
                compared++;
            }
        }
        Assert.IsGreaterThan(10_000, compared, $"Expected broad signed coverage, compared {compared} values.");
    }

    private static long ScalarRound(long numerator, int denominator)
    {
        var quotient = numerator / denominator;
        var remainder = numerator % denominator;
        // |remainder| < denominator <= Int32.MaxValue, so doubling is safe in Int64.
        var twice = 2L * Math.Abs(remainder);
        if (twice > denominator || twice == denominator && (quotient & 1) != 0)
            quotient += Math.Sign(remainder);
        return quotient;
    }

    private static HashSet<long> Numerators(int denominator, Random random)
    {
        var values = new HashSet<long>
        {
            long.MinValue, long.MinValue + 1, long.MinValue + 2,
            long.MaxValue, long.MaxValue - 1, long.MaxValue - 2,
            int.MinValue, int.MaxValue, -65536, -32768, -1024, -3, -2, -1, 0, 1, 2, 3, 1024, 32768, 65536,
        };
        long[] quotients =
        [
            long.MinValue / denominator, long.MinValue / denominator + 1,
            -65537, -32768, -1025, -97, -4, -3, -2, -1, 0, 1, 2, 3, 4, 97, 1025, 32768, 65537,
            long.MaxValue / denominator - 1, long.MaxValue / denominator,
        ];
        long[] remainders =
        [
            -((long)denominator) - 1, -((long)denominator), -((long)denominator) + 1,
            -(denominator / 2L) - 1, -(denominator / 2L), -(denominator / 2L) + 1,
            -1, 0, 1, denominator / 2L - 1, denominator / 2L, denominator / 2L + 1,
            (long)denominator - 1, denominator, (long)denominator + 1,
        ];
        // BigInteger generates exact +/- quotient, remainder and half-way boundaries
        // without overflowing before the range check near Int64.MinValue/MaxValue.
        foreach (var quotient in quotients)
        foreach (var remainder in remainders)
        {
            var value = (BigInteger)quotient * denominator + remainder;
            if (value >= long.MinValue && value <= long.MaxValue)
                values.Add((long)value);
        }
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        for (var i = 0; i < 256; i++)
        {
            random.NextBytes(bytes);
            values.Add(BinaryPrimitives.ReadInt64LittleEndian(bytes));
        }
        return values;
    }
}
