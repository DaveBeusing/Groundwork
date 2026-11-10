namespace Groundwork.Voxels;

/// <summary>
/// Seeded elevation components in 1/256 lattice-cell units, with a continent mask in [0, 65535].
/// </summary>
public readonly record struct PlanetLandform(int ContinentMask, long BaseElevation, long Mountain,
    long Valley, long Detail)
{
    public long Elevation => checked(BaseElevation + Mountain + Valley + Detail);
}

/// <summary>
/// Version 1 planet terrain. Density is sampled at 3D lattice vertices. Materials are sampled
/// independently at owned cell keys and never determine solidity.
/// </summary>
public sealed class PlanetDensityField : IPlanetBrickSource
{
    public const uint GeneratorVersion = 1;
    public const uint TerrainSchemaVersion = 1;
    public const int UnitsPerCell = 256;

    // Guarantees x² + y² + z², scaled by UnitsPerCell², fits in UInt128.
    public const long MaximumCoordinate = 1L << 54;

    private const int NoiseHalfRange = 1 << 15;
    private const ulong ContinentStream = 0x434F4E54494E454EUL;
    private const ulong MountainStream = 0x4D4F554E5441494EUL;
    private const ulong ValleyStream = 0x56414C4C45590000UL;
    private const ulong DetailStream = 0x44455441494C0000UL;
    private const ulong MaterialStream = 0x4D4154455249414CUL;
    private readonly PlanetLandformRecipe _recipe;
    private readonly long _radius;

    public PlanetDensityField(PlanetDescriptor descriptor, PlanetLandformRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(recipe);
        if (descriptor.GeneratorVersion != GeneratorVersion || descriptor.TerrainSchemaVersion != TerrainSchemaVersion)
            throw new ArgumentException("Unsupported generator or density schema version.", nameof(descriptor));
        if (descriptor.ParameterSha256 != recipe.ParameterSha256)
            throw new ArgumentException("Descriptor parameter digest does not match the recipe.", nameof(recipe));

        Descriptor = descriptor;
        _recipe = recipe;
        // The only floating-point step is the initial radius conversion: binary64 division,
        // exact power-of-two scaling and round-to-nearest, ties to even. All sampling is integer.
        _radius = checked((long)Math.Round(descriptor.RadiusMeters / descriptor.CellSizeMeters * UnitsPerCell,
            MidpointRounding.ToEven));
    }

    public PlanetDescriptor Descriptor { get; }

    /// <summary>
    /// Signed density in 1/256 lattice-cell units. Negative is solid, positive is empty,
    /// and zero is the surface. For sign classification, zero is on the non-solid side.
    /// </summary>
    public long SampleDensity(PlanetLatticeSample sample)
    {
        if (sample.WorldId != Descriptor.WorldId || sample.PlanetId != Descriptor.PlanetId)
            throw new ArgumentException("Sample belongs to a different world or planet.", nameof(sample));

        return SampleDensity(sample.Vertex);
    }

    /// <summary>Samples a vertex in this field's planet lattice.</summary>
    public long SampleDensity(LatticeCoordinate vertex)
    {
        ValidateCoordinate(vertex);
        var squared = Square(vertex.X) + Square(vertex.Y) + Square(vertex.Z);
        var distance = NearestSquareRoot(squared << 16);
        return checked(distance - _radius - Landform(vertex).Elevation);
    }

    public PlanetLandform SampleLandform(LatticeCoordinate vertex)
    {
        ValidateCoordinate(vertex);
        return Landform(vertex);
    }

    /// <summary>Samples the base material at the canonical lower corner of an owned cell.</summary>
    public ushort SampleMaterial(LatticeCoordinate cell)
    {
        ValidateCoordinate(cell);
        return Noise(cell, _recipe.MaterialScaleCells, MaterialStream) < 0
            ? _recipe.BaseMaterial : _recipe.AlternateMaterial;
    }

    private PlanetLandform Landform(LatticeCoordinate vertex)
    {
        var mask = Noise(vertex, _recipe.ContinentScaleCells, ContinentStream) + NoiseHalfRange;
        var elevation = Interpolate(_recipe.LowElevation, _recipe.HighElevation, mask, 2 * NoiseHalfRange - 1);
        var mountain = Ridge(vertex, _recipe.Mountains, MountainStream);
        var valley = -Ridge(vertex, _recipe.Valleys, ValleyStream);
        var detail = _recipe.Detail.Amplitude == 0 ? 0 :
            RoundDivide((long)_recipe.Detail.Amplitude * Noise(vertex, _recipe.Detail.ScaleCells, DetailStream), NoiseHalfRange);
        return new PlanetLandform(mask, elevation, mountain, valley, detail);
    }

    private long Ridge(LatticeCoordinate vertex, TerrainReliefLayer layer, ulong stream)
    {
        if (layer.Amplitude == 0)
            return 0;
        var ridge = NoiseHalfRange - Math.Abs(Noise(vertex, layer.ScaleCells, stream));
        return RoundDivide((long)layer.Amplitude * ridge, NoiseHalfRange);
    }

    // Version 1 value noise interpolates X, then Y, then Z with Euclidean grid coordinates.
    private int Noise(LatticeCoordinate vertex, int scale, ulong stream)
    {
        var (x, dx) = Grid(vertex.X, scale);
        var (y, dy) = Grid(vertex.Y, scale);
        var (z, dz) = Grid(vertex.Z, scale);
        var seed = Mix(Descriptor.Seed ^ stream);
        var low = Plane(z);
        var high = Plane(z + 1);
        return (int)Interpolate(low, high, dz, scale);

        long Plane(long plane)
        {
            var a = Interpolate(Corner(x, y, plane), Corner(x + 1, y, plane), dx, scale);
            var b = Interpolate(Corner(x, y + 1, plane), Corner(x + 1, y + 1, plane), dx, scale);
            return Interpolate(a, b, dy, scale);
        }

        int Corner(long i, long j, long k)
        {
            var h = Mix(seed ^ unchecked((ulong)i));
            h = Mix(h ^ unchecked((ulong)j));
            h = Mix(h ^ unchecked((ulong)k));
            return (int)(h >> 48) - NoiseHalfRange;
        }
    }

    // SplitMix64 increment and finalizer, with intentional modulo-2^64 arithmetic.
    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    private static (long Index, long Remainder) Grid(long axis, int scale)
    {
        var (index, remainder) = Math.DivRem(axis, scale);
        return remainder < 0 ? (index - 1, remainder + scale) : (index, remainder);
    }

    private static long Interpolate(long a, long b, long weight, int scale) =>
        RoundDivide(a * (scale - weight) + b * weight, scale);

    private static long RoundDivide(long numerator, int denominator)
    {
        var (quotient, remainder) = Math.DivRem(numerator, denominator);
        var twice = 2 * Math.Abs(remainder);
        if (twice > denominator || (twice == denominator && (quotient & 1) != 0))
            quotient += Math.Sign(remainder);
        return quotient;
    }

    private static UInt128 Square(long axis) => (UInt128)((Int128)axis * axis);

    private static long NearestSquareRoot(UInt128 squared)
    {
        if (squared == 0)
            return 0;

        var root = UInt128.One << (((int)UInt128.Log2(squared) + 2) / 2);
        while (true)
        {
            var next = (root + squared / root) >> 1;
            if (next >= root)
                return checked((long)(squared - root * root > root ? root + 1 : root));
            root = next;
        }
    }

    private static void ValidateCoordinate(LatticeCoordinate coordinate)
    {
        if (Outside(coordinate.X) || Outside(coordinate.Y) || Outside(coordinate.Z))
            throw new ArgumentOutOfRangeException(nameof(coordinate), "Coordinate is outside the version 1 density domain.");

        static bool Outside(long value) => value < -MaximumCoordinate || value > MaximumCoordinate;
    }
}
