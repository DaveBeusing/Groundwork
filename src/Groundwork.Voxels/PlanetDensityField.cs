using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

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
        // Convert the radius once using binary64 division, exact power-of-two scaling and ties to even.
        // Sample rounding is integer-exact, including correction of the small square-root estimate.
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
    public long SampleDensity(LatticeCoordinate vertex) => SampleDensity(vertex, 0);

    internal long SampleDensity(LatticeCoordinate vertex, long relief)
    {
        ValidateCoordinate(vertex);
        var squared = Square(vertex.X) + Square(vertex.Y) + Square(vertex.Z);
        var distance = NearestSquareRoot(squared << 16);
        return checked(distance - _radius - Landform(vertex).Elevation - relief);
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

    // Every row starts from the immutable recipe, without a previous brick or persistent cache.
    internal void SampleDensityRow(LatticeCoordinate origin, Span<long> values)
    {
        if (values.IsEmpty)
            return;
        ValidateRow(origin, values.Length);
        var continent = new NoiseRow(Descriptor.Seed, origin, _recipe.ContinentScaleCells, ContinentStream);
        var mountains = _recipe.Mountains.Amplitude == 0 ? default :
            new NoiseRow(Descriptor.Seed, origin, _recipe.Mountains.ScaleCells, MountainStream);
        var valleys = _recipe.Valleys.Amplitude == 0 ? default :
            new NoiseRow(Descriptor.Seed, origin, _recipe.Valleys.ScaleCells, ValleyStream);
        var detail = _recipe.Detail.Amplitude == 0 ? default :
            new NoiseRow(Descriptor.Seed, origin, _recipe.Detail.ScaleCells, DetailStream);
        const int blockSize = 32;
        Span<int> continental = stackalloc int[blockSize];
        Span<int> ridges = stackalloc int[blockSize];
        Span<int> cuts = stackalloc int[blockSize];
        Span<int> details = stackalloc int[blockSize];
        var mountainAmplitude = _recipe.Mountains.Amplitude;
        var valleyAmplitude = _recipe.Valleys.Amplitude;
        var detailAmplitude = _recipe.Detail.Amplitude;
        var squaredYZ = Square(origin.Y) + Square(origin.Z);
        for (var offset = 0; offset < values.Length;)
        {
            var count = Math.Min(blockSize, values.Length - offset);
            continent.Fill(continental[..count]);
            if (mountainAmplitude != 0)
                mountains.Fill(ridges[..count]);
            if (valleyAmplitude != 0)
                valleys.Fill(cuts[..count]);
            if (detailAmplitude != 0)
                detail.Fill(details[..count]);
            for (var index = 0; index < count; index++)
            {
                var x = origin.X + offset + index;
                var elevation = Interpolate(_recipe.LowElevation, _recipe.HighElevation,
                    continental[index] + NoiseHalfRange, 2 * NoiseHalfRange - 1);
                var mountain = mountainAmplitude == 0 ? 0 :
                    RoundDivide((long)mountainAmplitude * (NoiseHalfRange - Math.Abs(ridges[index])), NoiseHalfRange);
                var valley = valleyAmplitude == 0 ? 0 :
                    -RoundDivide((long)valleyAmplitude * (NoiseHalfRange - Math.Abs(cuts[index])), NoiseHalfRange);
                var fine = detailAmplitude == 0 ? 0 :
                    RoundDivide((long)detailAmplitude * details[index], NoiseHalfRange);
                values[offset + index] = checked(NearestSquareRoot((Square(x) + squaredYZ) << 16) -
                    _radius - checked(elevation + mountain + valley + fine));
            }
            offset += count;
        }
    }

    internal void SampleMaterialRow(LatticeCoordinate origin, Span<ushort> values)
    {
        if (values.IsEmpty)
            return;
        ValidateRow(origin, values.Length);
        var material = new NoiseRow(Descriptor.Seed, origin, _recipe.MaterialScaleCells, MaterialStream);
        Span<int> samples = stackalloc int[32];
        for (var offset = 0; offset < values.Length;)
        {
            var count = Math.Min(samples.Length, values.Length - offset);
            material.Fill(samples[..count]);
            for (var index = 0; index < count; index++)
                values[offset + index] = samples[index] < 0 ? _recipe.BaseMaterial : _recipe.AlternateMaterial;
            offset += count;
        }
    }

    private static void ValidateRow(LatticeCoordinate origin, int length)
    {
        ValidateCoordinate(origin);
        ValidateCoordinate(origin with { X = checked(origin.X + length - 1L) });
    }

    // Noise corners are generated afresh for this row whenever X crosses a grid plane.
    // Interpolation and rounding retain the scalar X, then Y, then Z order exactly.
    private struct NoiseRow
    {
        private readonly int _scale;
        private readonly RoundedDivisor _divisor;
        private readonly int _shift, _odd;
        private readonly ulong _seed;
        private readonly long _y, _z, _dy, _dz;
        private long _gridX, _dx;
        private bool _refresh;
        private LinearRowStepper _low0, _low1, _high0, _high1;

        internal NoiseRow(ulong seed, LatticeCoordinate origin, int scale, ulong stream)
        {
            _scale = scale;
            _divisor = new RoundedDivisor(scale);
            _shift = (int)uint.TrailingZeroCount((uint)scale);
            _odd = scale >> _shift;
            _seed = Mix(seed ^ stream);
            (_y, _dy) = Grid(origin.Y, scale);
            (_z, _dz) = Grid(origin.Z, scale);
            (_gridX, _dx) = Grid(origin.X, scale);
            _refresh = true;
            _low0 = _low1 = _high0 = _high1 = default;
        }

        internal void Fill(Span<int> output)
        {
            if (!Vector128.IsHardwareAccelerated || _scale > 32768 || _odd is not (1 or 3))
            {
                for (var index = 0; index < output.Length; index++)
                    output[index] = Next();
                return;
            }

            // Work within one noise cell at a time. In the last group only the lanes within
            // that cell are emitted; the next group starts with the next cell's own corners.
            while (!output.IsEmpty)
            {
                var count = Math.Min(output.Length, _scale - (int)_dx);
                var (left, right) = GetCorners();
                for (var index = 0; index < count; index += Vector128<int>.Count)
                {
                    var x = Vector128.Create((int)_dx + index) + Vector128.Create(0, 1, 2, 3);
                    if (count - index < Vector128<int>.Count)
                        x = Vector128.Min(x, Vector128.Create((int)_dx + count - 1));
                    var lo0 = InterpolateBlock(left.GetElement(0), right.GetElement(0), x);
                    var lo1 = InterpolateBlock(left.GetElement(1), right.GetElement(1), x);
                    var hi0 = InterpolateBlock(left.GetElement(2), right.GetElement(2), x);
                    var hi1 = InterpolateBlock(left.GetElement(3), right.GetElement(3), x);
                    var lo = InterpolateBlock(lo0, lo1, Vector128.Create((int)_dy));
                    var hi = InterpolateBlock(hi0, hi1, Vector128.Create((int)_dy));
                    var result = InterpolateBlock(lo, hi, Vector128.Create((int)_dz));
                    if (count - index >= Vector128<int>.Count)
                        result.CopyTo(output[index..]);
                    else
                        for (var lane = 0; lane < count - index; lane++)
                            output[index + lane] = result.GetElement(lane);
                }
                output = output[count..];
                _dx += count;
                if (_dx == _scale)
                {
                    _dx = 0;
                    _gridX++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly Vector128<int> InterpolateBlock(Vector128<int> a, Vector128<int> b, Vector128<int> weight)
        {
            // Noise corners and rounded intermediate values fit Int16. Bounded scale and
            // in-cell weights keep the weighted numerators within Int32.
            var numerator = a * _scale + (b - a) * weight;
            var sign = numerator >> 31;
            var magnitude = ((numerator ^ sign) - sign).AsUInt32();
            var quotient = magnitude >> _shift;
            if (_odd == 3)
            {
                // The shifted magnitude is below 2^17. The fixed reciprocal is at most
                // one quotient high for the emitted lanes (whose shifted magnitude is <= 98304).
                var divided = (quotient * 21846u) >> 16;
                quotient = divided + Vector128.GreaterThan(divided * 3u, quotient);
            }
            var remainder = magnitude - quotient * (uint)_scale;
            var twice = remainder + remainder;
            var divisor = Vector128.Create((uint)_scale);
            var increment = Vector128.GreaterThan(twice, divisor) |
                (Vector128.Equals(twice, divisor) & (Vector128<uint>.Zero - (quotient & Vector128<uint>.One)));
            return (((quotient - increment).AsInt32()) ^ sign) - sign;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly Vector128<int> InterpolateBlock(int a, int b, Vector128<int> weight) =>
            InterpolateBlock(Vector128.Create(a), Vector128.Create(b), weight);

        private readonly (Vector128<int> Left, Vector128<int> Right) GetCorners()
        {
            var left = Mix(_seed ^ unchecked((ulong)_gridX));
            var right = Mix(_seed ^ unchecked((ulong)(_gridX + 1)));
            return (CornersAtX(left), CornersAtX(right));
        }

        private readonly Vector128<int> CornersAtX(ulong x)
        {
            var lower = Mix(x ^ unchecked((ulong)_y));
            var upper = Mix(x ^ unchecked((ulong)(_y + 1)));
            return Vector128.Create(Corner(lower, _z), Corner(upper, _z),
                Corner(lower, _z + 1), Corner(upper, _z + 1));
        }

        private int Next()
        {
            if (_refresh)
            {
                var (left, right) = GetCorners();
                _low0 = new LinearRowStepper(left.GetElement(0), right.GetElement(0), _dx, _scale);
                _low1 = new LinearRowStepper(left.GetElement(1), right.GetElement(1), _dx, _scale);
                _high0 = new LinearRowStepper(left.GetElement(2), right.GetElement(2), _dx, _scale);
                _high1 = new LinearRowStepper(left.GetElement(3), right.GetElement(3), _dx, _scale);
                _refresh = false;
            }
            var low = Interpolate(_low0.Next(), _low1.Next(), _dy);
            var high = Interpolate(_high0.Next(), _high1.Next(), _dy);
            var result = (int)Interpolate(low, high, _dz);
            // Callers consume consecutive X samples. Advance only after evaluating this one.
            if (++_dx == _scale)
            {
                _dx = 0;
                _gridX++;
                _refresh = true;
            }
            return result;
        }

        private readonly long Interpolate(long a, long b, long weight) =>
            _divisor.Round(a * (_scale - weight) + b * weight);

        private static int Corner(ulong prefix, long axis) =>
            (int)(Mix(prefix ^ unchecked((ulong)axis)) >> 48) - NoiseHalfRange;
    }

    private struct LinearRowStepper
    {
        private readonly int _scale;
        private readonly long _stepQuotient, _stepRemainder;
        private long _quotient, _remainder;

        internal LinearRowStepper(int left, int right, long dx, int scale)
        {
            _scale = scale;
            var slope = (long)right - left;
            (_quotient, _remainder) = Grid((long)left * scale + slope * dx, scale);
            (_stepQuotient, _stepRemainder) = Grid(slope, scale);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal long Next()
        {
            // Euclidean q/r represents the exact weighted numerator, also when it is negative.
            // Adding the slope's q/r preserves it; round only the returned value, ties to even.
            var twice = 2 * _remainder;
            var result = _quotient + (twice > _scale || (twice == _scale && (_quotient & 1) != 0) ? 1 : 0);
            _quotient += _stepQuotient;
            _remainder += _stepRemainder;
            if (_remainder >= _scale)
            {
                _remainder -= _scale;
                _quotient++;
            }
            return result;
        }
    }

    private readonly struct RoundedDivisor
    {
        private readonly uint _denominator;
        private readonly ulong _reciprocal;
        private readonly int _shift;
        private readonly uint _odd;

        internal RoundedDivisor(int denominator)
        {
            _denominator = (uint)denominator;
            _reciprocal = denominator == 1 ? 0 : ulong.MaxValue / _denominator + 1;
            _shift = (int)uint.TrailingZeroCount(_denominator);
            _odd = _denominator >> _shift;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal long Round(long numerator)
        {
            if (_denominator == 1)
                return numerator;
            var magnitude = numerator < 0 ? unchecked((ulong)-numerator) : (ulong)numerator;
            ulong quotient;
            // floor(floor(n / odd) / 2^shift) equals floor(n / (odd * 2^shift)).
            if (_odd == 1)
                quotient = magnitude >> _shift;
            else if (_odd == 3)
                quotient = (magnitude / 3) >> _shift;
            else
            {
                // ceil(2^64 / d) overestimates its exact reciprocal by less than one.
                // For magnitude <= 2^63, the high product is at most one quotient too high.
                quotient = Math.BigMul(magnitude, _reciprocal, out _);
                if (quotient * _denominator > magnitude)
                    quotient--;
            }
            var remainder = magnitude - quotient * _denominator;
            var twice = remainder * 2;
            if (twice > _denominator || (twice == _denominator && (quotient & 1) != 0))
                quotient++;
            return numerator < 0 ? -(long)quotient : (long)quotient;
        }
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
    internal int Noise(LatticeCoordinate vertex, int scale, ulong stream)
    {
        var (x, dx) = Grid(vertex.X, scale);
        var (y, dy) = Grid(vertex.Y, scale);
        var (z, dz) = Grid(vertex.Z, scale);
        var seed = Mix(Descriptor.Seed ^ stream);
        // Share hash prefixes within this sample, preserving the version 1 corner values.
        var left = Mix(seed ^ unchecked((ulong)x));
        var right = Mix(seed ^ unchecked((ulong)(x + 1)));
        var lowerLeft = Mix(left ^ unchecked((ulong)y));
        var lowerRight = Mix(right ^ unchecked((ulong)y));
        var upperLeft = Mix(left ^ unchecked((ulong)(y + 1)));
        var upperRight = Mix(right ^ unchecked((ulong)(y + 1)));
        var low = Plane(z);
        var high = Plane(z + 1);
        return (int)Interpolate(low, high, dz, scale);

        long Plane(long plane)
        {
            var a = Interpolate(Corner(lowerLeft, plane), Corner(lowerRight, plane), dx, scale);
            var b = Interpolate(Corner(upperLeft, plane), Corner(upperRight, plane), dx, scale);
            return Interpolate(a, b, dy, scale);
        }

        static int Corner(ulong prefix, long k)
        {
            var h = Mix(prefix ^ unchecked((ulong)k));
            return (int)(h >> 48) - NoiseHalfRange;
        }
    }

    // SplitMix64 increment and finalizer, with intentional modulo-2^64 arithmetic.
    internal static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    internal static (long Index, long Remainder) Grid(long axis, int scale)
    {
        var (index, remainder) = Math.DivRem(axis, scale);
        return remainder < 0 ? (index - 1, remainder + scale) : (index, remainder);
    }

    private static long Interpolate(long a, long b, long weight, int scale) =>
        RoundDivide(a * (scale - weight) + b * weight, scale);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long RoundDivide(long numerator, int denominator)
    {
        var (quotient, remainder) = Math.DivRem(numerator, denominator);
        var twice = 2 * Math.Abs(remainder);
        if (twice > denominator || (twice == denominator && (quotient & 1) != 0))
            quotient += Math.Sign(remainder);
        return quotient;
    }

    private static UInt128 Square(long axis) => (UInt128)((Int128)axis * axis);

    internal static long NearestSquareRoot(UInt128 squared)
    {
        if (squared == 0)
            return 0;

        // Ordinary terrain coordinates fit here; retain the full-width path for the domain extremes.
        if (squared <= ulong.MaxValue)
            return NearestSquareRoot64((ulong)squared);

        var root = UInt128.One << (((int)UInt128.Log2(squared) + 2) / 2);
        while (true)
        {
            var next = (root + squared / root) >> 1;
            if (next >= root)
                return checked((long)(squared - root * root > root ? root + 1 : root));
            root = next;
        }
    }

    private static long NearestSquareRoot64(ulong squared)
    {
        var root = Math.Min((ulong)Math.Sqrt(squared), uint.MaxValue);
        while (root * root > squared)
            root--;
        while (root < uint.MaxValue && (root + 1) * (root + 1) <= squared)
            root++;
        return (long)(squared - root * root > root ? root + 1 : root);
    }

    internal static void ValidateCoordinate(LatticeCoordinate coordinate)
    {
        if (Outside(coordinate.X) || Outside(coordinate.Y) || Outside(coordinate.Z))
            throw new ArgumentOutOfRangeException(nameof(coordinate), "Coordinate is outside the version 1 density domain.");

        static bool Outside(long value) => value < -MaximumCoordinate || value > MaximumCoordinate;
    }
}
