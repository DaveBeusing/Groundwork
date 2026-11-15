using System.Numerics;

namespace Groundwork.Voxels;

/// <summary>One canonical axis as an integer texture repeat and a phase in [0, 256) texels.</summary>
public readonly record struct PlanetTexelAxis
{
    internal PlanetTexelAxis(long repeat, double phaseTexels)
    {
        Repeat = repeat;
        PhaseTexels = phaseTexels;
    }

    public long Repeat { get; }
    public double PhaseTexels { get; }
}

/// <summary>A texture position in the planet-centred Cartesian axes.</summary>
public readonly record struct PlanetTriplanarPoint(PlanetTexelAxis X, PlanetTexelAxis Y, PlanetTexelAxis Z);

/// <summary>
/// The three projection planes, expressed in unwrapped texels, and their normal-map basis.
/// Cyclic tangent/bitangent axes are Y/Z for X, Z/X for Y and X/Y for Z. A negative
/// projection flips its tangent and normal together. The bitangent stays positive,
/// so tangent cross bitangent equals the signed projection axis on every plane.
/// </summary>
public readonly record struct PlanetTriplanarProjection(
    Vector2 XProjection, Vector2 YProjection, Vector2 ZProjection, Vector3 Weights, Vector3 AxisSigns);

/// <summary>
/// Portable, physical triplanar mapping. World/chart ownership, render origins, and material
/// generation remain independent of these coordinates.
/// </summary>
public static class PlanetTriplanarMapping
{
    public const int TexelsPerMeter = 64;
    public const int TexelsPerRepeat = 256;
    public const int MaximumLocalTexels = 65_536;

    /// <summary>
    /// Comparison tolerance at the frozen scale for equivalent double/mesh position inputs
    /// at the supported planet radius and for bounded local float conversion. Geometry
    /// differences and prior input quantization have their own error budget.
    /// </summary>
    public const double PositionToleranceTexels = 1.0 / 128;

    private const int MetersPerRepeat = TexelsPerRepeat / TexelsPerMeter;

    /// <summary>Splits an authoritative double position, such as an orbit patch sample.</summary>
    public static PlanetTriplanarPoint FromCanonical(PlanetVector position) =>
        new(Split(position.X), Split(position.Y), Split(position.Z));

    /// <summary>
    /// Uses the volume mesh's canonical lattice origin and its neighborhood-local vertex in cells.
    /// Supply the original mesh-local geometry before camera translation.
    /// </summary>
    public static PlanetTriplanarPoint FromVolume(PlanetDescriptor descriptor, LatticeCoordinate meshOrigin,
        Vector3 localCells)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        PlanetDensityField.ValidateCoordinate(meshOrigin);
        if (!InNeighborhood(localCells.X) || !InNeighborhood(localCells.Y) || !InNeighborhood(localCells.Z))
            throw new ArgumentOutOfRangeException(nameof(localCells), "Position must lie in the two-brick mesh neighborhood.");

        var scale = descriptor.CellSizeMeters;
        return new(FromLattice(meshOrigin.X, localCells.X, scale), FromLattice(meshOrigin.Y, localCells.Y, scale),
            FromLattice(meshOrigin.Z, localCells.Z, scale));
    }

    /// <summary>
    /// Converts a point to unwrapped local texels using the anchor's whole repeat on each axis.
    /// Interpolate these Cartesian coordinates and the outward normal with one common anchor
    /// for the primitive. Project and wrap afterward so a normal sign change within the
    /// primitive cannot introduce a texture seam.
    /// </summary>
    public static Vector3 ToLocalTexels(PlanetTriplanarPoint point, PlanetTriplanarPoint anchor) =>
        new(Relative(point.X, anchor.X), Relative(point.Y, anchor.Y), Relative(point.Z, anchor.Z));

    /// <summary>Projects a single sample directly from its split axes and whole-repeat anchor.</summary>
    public static PlanetTriplanarProjection Project(PlanetTriplanarPoint point, PlanetTriplanarPoint anchor,
        Vector3 outwardNormal) => ProjectBounded(ToLocalTexels(point, anchor), outwardNormal);

    /// <summary>Projects interpolated local texels using the geometric normal at the same sample.</summary>
    public static PlanetTriplanarProjection ProjectTexels(Vector3 texels, Vector3 outwardNormal)
    {
        if (!InLocalWindow(texels.X) || !InLocalWindow(texels.Y) || !InLocalWindow(texels.Z))
            throw new ArgumentOutOfRangeException(nameof(texels), "Position is outside the bounded interpolation window.");
        return ProjectBounded(texels, outwardNormal);
    }

    private static PlanetTriplanarProjection ProjectBounded(Vector3 texels, Vector3 outwardNormal)
    {
        double nx = Math.Abs((double)outwardNormal.X), ny = Math.Abs((double)outwardNormal.Y),
            nz = Math.Abs((double)outwardNormal.Z);
        var length = nx + ny + nz;
        if (!double.IsFinite(length) || length == 0)
            throw new ArgumentOutOfRangeException(nameof(outwardNormal), "Normal must be finite and nonzero.");

        // Zero weight makes the orientation at a zero component immaterial. Use +1
        // for either sign of zero to keep the representation deterministic.
        var signs = new Vector3(outwardNormal.X < 0 ? -1 : 1, outwardNormal.Y < 0 ? -1 : 1,
            outwardNormal.Z < 0 ? -1 : 1);
        var weights = new Vector3((float)(nx / length), (float)(ny / length), (float)(nz / length));
        return new(new(signs.X * texels.Y, texels.Z), new(signs.Y * texels.Z, texels.X),
            new(signs.Z * texels.X, texels.Y), weights, signs);
    }

    private static bool InNeighborhood(float value) => value is >= 0 and <= 2 * PlanetDescriptor.BrickEdge;

    private static bool InLocalWindow(float value) => value is >= -MaximumLocalTexels and <= MaximumLocalTexels;

    private static PlanetTexelAxis FromLattice(long origin, float local, double cellSize)
    {
        var originDouble = (double)origin;
        var meters = originDouble * cellSize;
        // Recover both roundings separately. An admitted signed Int64 lattice value can
        // be one cell away from its nearest double above 2^53.
        var productError = Math.FusedMultiplyAdd(originDouble, cellSize, -meters);
        var remainingCells = (origin - (long)originDouble) + (double)local;
        return Split(meters, Math.FusedMultiplyAdd(remainingCells, cellSize, productError));
    }

    private static PlanetTexelAxis Split(double meters, double correction = 0)
    {
        const double longUpperExclusive = 9_223_372_036_854_775_808d;
        var repeat = Math.Floor(meters / MetersPerRepeat);
        if (!double.IsFinite(repeat) || repeat < -longUpperExclusive || repeat >= longUpperExclusive)
            throw new ArgumentOutOfRangeException(nameof(meters), "Position is outside the signed texture repeat domain.");

        var remainder = (meters - repeat * MetersPerRepeat) + correction;
        var carried = Math.Floor(remainder / MetersPerRepeat);
        var phase = (remainder - carried * MetersPerRepeat) * TexelsPerMeter;
        var index = (Int128)(long)repeat + (long)carried;
        // A remainder less than half a double ULP below a repeat can round to it.
        if (phase >= TexelsPerRepeat)
        {
            index++;
            phase = 0;
        }
        if (index < long.MinValue || index > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(meters), "Position is outside the signed texture repeat domain.");

        return new((long)index, phase <= 0 ? 0 : phase);
    }

    private static float Relative(PlanetTexelAxis axis, PlanetTexelAxis anchor)
    {
        var repeatDelta = (Int128)axis.Repeat - anchor.Repeat;
        var texels = (double)repeatDelta * TexelsPerRepeat + axis.PhaseTexels;
        if (Math.Abs(texels) > MaximumLocalTexels)
            throw new ArgumentOutOfRangeException(nameof(anchor), "The interpolation window exceeds the float precision bound.");
        return (float)texels;
    }
}
