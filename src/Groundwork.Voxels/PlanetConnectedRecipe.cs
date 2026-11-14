using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Groundwork.Voxels;

/// <summary>Immutable V3 geology and connected relief settings. Amplitudes are in 1/256 cell units.</summary>
public sealed class PlanetConnectedRecipe
{
    public PlanetConnectedRecipe(PlanetGeologyRecipe geology, int scaleCells,
        int ridgeAmplitude, int mainValleyAmplitude, int tributaryAmplitude, int cutAmplitude)
    {
        ArgumentNullException.ThrowIfNull(geology);
        ArgumentOutOfRangeException.ThrowIfLessThan(scaleCells, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scaleCells, 4096);
        ValidateAmplitude(ridgeAmplitude);
        ValidateAmplitude(mainValleyAmplitude);
        ValidateAmplitude(tributaryAmplitude);
        ValidateAmplitude(cutAmplitude);

        Geology = geology;
        ScaleCells = scaleCells;
        RidgeAmplitude = ridgeAmplitude;
        MainValleyAmplitude = mainValleyAmplitude;
        TributaryAmplitude = tributaryAmplitude;
        CutAmplitude = cutAmplitude;
        Span<byte> canonical = stackalloc byte[56];
        "PLF3"u8.CopyTo(canonical);
        Convert.FromHexString(geology.ParameterSha256).CopyTo(canonical[4..36]);
        BinaryPrimitives.WriteInt32LittleEndian(canonical[36..], scaleCells);
        BinaryPrimitives.WriteInt32LittleEndian(canonical[40..], ridgeAmplitude);
        BinaryPrimitives.WriteInt32LittleEndian(canonical[44..], mainValleyAmplitude);
        BinaryPrimitives.WriteInt32LittleEndian(canonical[48..], tributaryAmplitude);
        BinaryPrimitives.WriteInt32LittleEndian(canonical[52..], cutAmplitude);
        ParameterSha256 = Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    public PlanetGeologyRecipe Geology { get; }
    public int ScaleCells { get; }
    public int RidgeAmplitude { get; }
    public int MainValleyAmplitude { get; }
    public int TributaryAmplitude { get; }
    public int CutAmplitude { get; }
    public string ParameterSha256 { get; }

    // Portal support is within 12 lattice steps in Manhattan distance.
    internal long MaximumReliefVariation =>
        Change(RidgeAmplitude, 26, 12) +
        Math.Max(Change(MainValleyAmplitude, 19, 12),
            Math.Min(TributaryAmplitude, Change(TributaryAmplitude, 12, 12) + Change(TributaryAmplitude, 32, 12))) +
        Change(CutAmplitude, 12, 18);

    private long Change(int amplitude, int width, int steps)
    {
        if (amplitude == 0)
            return 0;
        // Allow two quantized profile units for endpoint and centre rounding.
        var denominator = width * 256L * ScaleCells;
        var numerator = amplitude * (steps * 256L + 2L * ScaleCells);
        return Math.Min(amplitude, (numerator + denominator - 1) / denominator + 2);
    }

    private static void ValidateAmplitude(int amplitude)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amplitude);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(amplitude, 256 * PlanetDensityField.UnitsPerCell);
    }
}

/// <summary>Nonnegative profile contributions in density units. Overlapping valleys use the deeper floor.</summary>
public readonly record struct PlanetConnectedSample(long Ridge, long MainValley, long Tributary, long Cut)
{
    public long Valley => -Math.Max(MainValley, Tributary);
    public long Elevation => Ridge + Valley - Cut;
}

internal sealed class PlanetConnectedRelief(ulong seed, PlanetConnectedRecipe recipe)
{
    private const ulong PhaseStream = 0x4c414e44464d0003UL;
    private readonly ulong _phase = PlanetDensityField.Mix(seed ^ PhaseStream);

    internal PlanetConnectedSample Sample(LatticeCoordinate vertex)
    {
        // Periodic bands use world-space X/Z, without face selection or a patch-edge jump.
        // The bounded remainders are quantized to 1/256 profile units before band evaluation.
        const int q = 256;
        var scale = recipe.ScaleCells;
        var period = 256L * scale;
        var z = Wrap(vertex.Z + (long)((_phase >> 8) & 255) * scale, period) * q / scale;
        var x = Wrap(vertex.X + (long)(_phase & 255) * scale, period) * q / scale;
        var main = Wave(z, 128 * q, 12 * q);
        var ridge = main - 58 * q + Wave(z, 256 * q, 8 * q);
        var tributaryOffset = Math.Max(0, 128 * q - z);
        var branchWeight = Math.Clamp(Math.Min(z, tributaryOffset), 0, 32 * q);
        var tributary = main + tributaryOffset / 3;

        return new(
            Band(WrappedDistance(x, ridge), 26 * q, recipe.RidgeAmplitude),
            Band(WrappedDistance(x, main), 19 * q, recipe.MainValleyAmplitude),
            Band(WrappedDistance(x, tributary), 12 * q, recipe.TributaryAmplitude) * branchWeight / (32 * q),
            Band(WrappedDistance(x, ridge - 9 * q) + Math.Abs(z - 168 * q), 12 * q, recipe.CutAmplitude));

        static long Band(long distance, int width, int amplitude) =>
            PlanetDensityField.RoundDivide(Math.Max(0, width - distance) * amplitude, width);

        static long Wave(long value, int wavelength, int amplitude)
        {
            var part = value % wavelength;
            return (wavelength / 2 - Math.Abs(part - wavelength / 2)) * (4L * amplitude) / wavelength - amplitude;
        }

        static long WrappedDistance(long value, long centre)
        {
            const int width = 256 * q;
            var distance = Math.Abs(value - Wrap(centre, width));
            return Math.Min(distance, width - distance);
        }

        static long Wrap(long value, long width)
        {
            var part = value % width;
            return part < 0 ? part + width : part;
        }
    }
}
