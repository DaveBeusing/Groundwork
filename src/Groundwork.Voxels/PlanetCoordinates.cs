namespace Groundwork.Voxels;

public enum PlanetFace
{
    // The addressing helpers use consecutive positive/negative pairs for X, Y and Z.
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ,
}

/// <summary>Patch edges in counterclockwise boundary order.</summary>
public enum PlanetEdge
{
    Bottom,
    Right,
    Top,
    Left,
}

public readonly record struct PlanetPatchKey(PlanetFace Face, int Level, int TileU, int TileV);

/// <summary>A canonical integer point on the shared cube grid.</summary>
public readonly record struct PlanetSampleKey(int X, int Y, int Z);

public readonly record struct PlanetVector(double X, double Y, double Z);

/// <summary>Addressing for the six cube-sphere charts in the WP08 contract.</summary>
public static class PlanetCoordinates
{
    public const int IntervalsPerPatch = 16;
    public const int MaximumLevel = 5;
    public const int FinestIntervalsPerFace = IntervalsPerPatch << MaximumLevel;

    public static PlanetSampleKey Sample(PlanetPatchKey patch, int u, int v)
    {
        ValidatePatch(patch);
        ValidateLocalIndex(u, nameof(u));
        ValidateLocalIndex(v, nameof(v));
        return SampleUnchecked(patch, u, v);
    }

    /// <summary>Samples an edge from its counterclockwise start to its end.</summary>
    public static PlanetSampleKey SampleEdge(PlanetPatchKey patch, PlanetEdge edge, int index)
    {
        ValidatePatch(patch);
        ValidateLocalIndex(index, nameof(index));
        var (u, v) = EdgeIndices(edge, index);
        return SampleUnchecked(patch, u, v);
    }

    public static PlanetVector Direction(PlanetSampleKey sample)
    {
        if (!IsSampleComponent(sample.X) || !IsSampleComponent(sample.Y) || !IsSampleComponent(sample.Z) ||
            Math.Max(Math.Abs(sample.X), Math.Max(Math.Abs(sample.Y), Math.Abs(sample.Z))) != FinestIntervalsPerFace)
            throw new ArgumentOutOfRangeException(nameof(sample), "Sample must lie on the canonical cube lattice.");

        double x = sample.X, y = sample.Y, z = sample.Z;
        var length = Math.Sqrt(x * x + y * y + z * z);
        return new PlanetVector(x / length, y / length, z / length);
    }

    /// <summary>Projects a nonzero direction to its dominant chart, breaking ties X, Y, Z.</summary>
    public static (PlanetFace Face, double U, double V) Project(PlanetVector direction)
    {
        if (!double.IsFinite(direction.X) || !double.IsFinite(direction.Y) || !double.IsFinite(direction.Z))
            throw new ArgumentOutOfRangeException(nameof(direction), "Direction must be finite.");

        var x = Math.Abs(direction.X);
        var y = Math.Abs(direction.Y);
        var z = Math.Abs(direction.Z);
        if (x == 0 && y == 0 && z == 0)
            throw new ArgumentOutOfRangeException(nameof(direction), "Direction must be nonzero.");

        var face = DominantFace(direction, x, y, z);
        var (normal, u, v) = Basis(face);
        var divisor = Component(normal, direction);
        return (face, Component(u, direction) / divisor, Component(v, direction) / divisor);
    }

    /// <summary>Returns the neighboring patch at the same level. A reversed edge maps index i to 16-i.</summary>
    public static (PlanetPatchKey Patch, PlanetEdge Edge, bool Reversed) Neighbor(PlanetPatchKey patch, PlanetEdge edge)
    {
        ValidatePatch(patch);
        var limit = (1 << patch.Level) - 1;
        var (tileU, tileV, opposite) = edge switch
        {
            PlanetEdge.Bottom => (patch.TileU, patch.TileV - 1, PlanetEdge.Top),
            PlanetEdge.Right => (patch.TileU + 1, patch.TileV, PlanetEdge.Left),
            PlanetEdge.Top => (patch.TileU, patch.TileV + 1, PlanetEdge.Bottom),
            PlanetEdge.Left => (patch.TileU - 1, patch.TileV, PlanetEdge.Right),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
        if ((uint)tileU <= (uint)limit && (uint)tileV <= (uint)limit)
            return (patch with { TileU = tileU, TileV = tileV }, opposite, true);

        var (normal, u, v) = Basis(patch.Face);
        var face = edge switch
        {
            PlanetEdge.Bottom => Opposite(v),
            PlanetEdge.Right => u,
            PlanetEdge.Top => v,
            _ => Opposite(u),
        };
        var (_, neighborU, neighborV) = Basis(face);
        var (firstU, firstV) = EdgeIndices(edge, 0);
        var (lastU, lastV) = EdgeIndices(edge, IntervalsPerPatch);
        var first = SampleUnchecked(patch, firstU, firstV);
        var last = SampleUnchecked(patch, lastU, lastV);
        var width = 2 * (FinestIntervalsPerFace >> patch.Level);
        tileU = TileAtEdge(first, last, neighborU, width, limit);
        tileV = TileAtEdge(first, last, neighborV, width, limit);
        var neighborEdge = normal == neighborU ? PlanetEdge.Right :
            normal == Opposite(neighborU) ? PlanetEdge.Left :
            normal == neighborV ? PlanetEdge.Top : PlanetEdge.Bottom;
        var neighbor = new PlanetPatchKey(face, patch.Level, tileU, tileV);
        var (startU, startV) = EdgeIndices(neighborEdge, 0);
        return (neighbor, neighborEdge, first != SampleUnchecked(neighbor, startU, startV));
    }

    internal static void ValidatePatch(PlanetPatchKey patch)
    {
        if ((uint)patch.Face > (uint)PlanetFace.NegativeZ || (uint)patch.Level > MaximumLevel)
            throw new ArgumentOutOfRangeException(nameof(patch), "Invalid face or level.");

        var tiles = 1u << patch.Level;
        if ((uint)patch.TileU >= tiles || (uint)patch.TileV >= tiles)
            throw new ArgumentOutOfRangeException(nameof(patch), "Tile indices must be on the face at this level.");
    }

    private static PlanetSampleKey SampleUnchecked(PlanetPatchKey patch, int localU, int localV)
    {
        var (normal, u, v) = Basis(patch.Face);
        var stride = 1 << (MaximumLevel - patch.Level);
        var faceU = 2 * (IntervalsPerPatch * patch.TileU + localU) * stride - FinestIntervalsPerFace;
        var faceV = 2 * (IntervalsPerPatch * patch.TileV + localV) * stride - FinestIntervalsPerFace;
        return new PlanetSampleKey(
            AxisContribution(normal, u, v, PlanetFace.PositiveX, faceU, faceV),
            AxisContribution(normal, u, v, PlanetFace.PositiveY, faceU, faceV),
            AxisContribution(normal, u, v, PlanetFace.PositiveZ, faceU, faceV));
    }

    private static int AxisContribution(PlanetFace normal, PlanetFace u, PlanetFace v, PlanetFace axis, int faceU, int faceV)
    {
        var amount = (uint)axis / 2 == (uint)normal / 2 ? FinestIntervalsPerFace :
            (uint)axis / 2 == (uint)u / 2 ? faceU : faceV;
        return normal == Opposite(axis) || u == Opposite(axis) || v == Opposite(axis) ? -amount : amount;
    }

    private static int TileAtEdge(PlanetSampleKey first, PlanetSampleKey last, PlanetFace axis, int width, int limit)
    {
        var low = Math.Min((int)Component(axis, first), (int)Component(axis, last));
        return Math.Min((low + FinestIntervalsPerFace) / width, limit);
    }

    private static (int U, int V) EdgeIndices(PlanetEdge edge, int index) => edge switch
    {
        PlanetEdge.Bottom => (index, 0),
        PlanetEdge.Right => (IntervalsPerPatch, index),
        PlanetEdge.Top => (IntervalsPerPatch - index, IntervalsPerPatch),
        PlanetEdge.Left => (0, IntervalsPerPatch - index),
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    private static (PlanetFace Normal, PlanetFace U, PlanetFace V) Basis(PlanetFace face) => face switch
    {
        PlanetFace.PositiveX => (face, PlanetFace.NegativeZ, PlanetFace.PositiveY),
        PlanetFace.NegativeX => (face, PlanetFace.PositiveZ, PlanetFace.PositiveY),
        PlanetFace.PositiveY => (face, PlanetFace.PositiveX, PlanetFace.NegativeZ),
        PlanetFace.NegativeY => (face, PlanetFace.PositiveX, PlanetFace.PositiveZ),
        PlanetFace.PositiveZ => (face, PlanetFace.PositiveX, PlanetFace.PositiveY),
        PlanetFace.NegativeZ => (face, PlanetFace.NegativeX, PlanetFace.PositiveY),
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    private static PlanetFace Opposite(PlanetFace face) => (PlanetFace)((int)face ^ 1);

    private static PlanetFace DominantFace(PlanetVector direction, double x, double y, double z)
    {
        if (x >= y && x >= z)
            return direction.X < 0 ? PlanetFace.NegativeX : PlanetFace.PositiveX;
        if (y >= z)
            return direction.Y < 0 ? PlanetFace.NegativeY : PlanetFace.PositiveY;
        return direction.Z < 0 ? PlanetFace.NegativeZ : PlanetFace.PositiveZ;
    }

    private static double Component(PlanetFace axis, PlanetVector vector) => axis switch
    {
        PlanetFace.PositiveX => vector.X,
        PlanetFace.NegativeX => -vector.X,
        PlanetFace.PositiveY => vector.Y,
        PlanetFace.NegativeY => -vector.Y,
        PlanetFace.PositiveZ => vector.Z,
        _ => -vector.Z,
    };

    private static double Component(PlanetFace axis, PlanetSampleKey sample) =>
        Component(axis, new PlanetVector(sample.X, sample.Y, sample.Z));

    private static bool IsSampleComponent(int value) =>
        value >= -FinestIntervalsPerFace && value <= FinestIntervalsPerFace && value % 2 == 0;

    private static void ValidateLocalIndex(int value, string parameter)
    {
        if ((uint)value > IntervalsPerPatch)
            throw new ArgumentOutOfRangeException(parameter, "Sample index is outside the patch.");
    }
}
