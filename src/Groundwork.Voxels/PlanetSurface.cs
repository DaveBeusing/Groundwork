namespace Groundwork.Voxels;

/// <summary>A radial intersection with a terrain triangle and its outward unit normal.</summary>
public readonly record struct PlanetSupport(PlanetVector Position, PlanetVector Normal);

public interface IPlanetSurface
{
    /// <summary>Queries support along a finite, nonzero planet-centered direction.</summary>
    /// <returns>Whether the direction has terrain support in the current coverage.</returns>
    bool TryGetSupport(PlanetVector direction, out PlanetSupport support);
}

/// <summary>Read-only ground support over one set of finest-level terrain meshes.</summary>
public sealed class PlanetSurface : IPlanetSurface
{
    public const int MaximumPatches = 192;

    private const int Intervals = PlanetCoordinates.IntervalsPerPatch;
    private const int TilesPerFace = 1 << PlanetCoordinates.MaximumLevel;
    private const double BoundaryTolerance = 1e-10;

    private readonly Dictionary<PlanetPatchKey, PlanetPatchMesh> _patches;

    /// <summary>Captures unique finest-level meshes without stitched edges from a shared terrain recipe.</summary>
    public PlanetSurface(ReadOnlySpan<PlanetPatchMesh> patches)
    {
        if (patches.Length > MaximumPatches)
            throw new ArgumentOutOfRangeException(nameof(patches));

        _patches = new Dictionary<PlanetPatchKey, PlanetPatchMesh>(patches.Length);
        foreach (var mesh in patches)
        {
            if (mesh is null || mesh.Patch.Level != PlanetCoordinates.MaximumLevel ||
                mesh.Transitions != PlanetTransitionEdges.None || !_patches.TryAdd(mesh.Patch, mesh))
                throw new ArgumentException("Support requires unique finest-level patches with regular boundaries.", nameof(patches));
        }
    }

    /// <summary>Intersects the radial ray with the indexed triangle under the projected direction.</summary>
    public bool TryGetSupport(PlanetVector direction, out PlanetSupport support)
    {
        var (face, u, v) = PlanetCoordinates.Project(direction);
        var (tileU, localU) = Locate(u);
        var (tileV, localV) = Locate(v);
        var primary = new PatchLocation(new PlanetPatchKey(face, PlanetCoordinates.MaximumLevel, tileU, tileV), localU, localV);
        if (_patches.TryGetValue(primary.Patch, out var mesh))
            return Intersect(mesh, primary, direction, out support);

        // At a shared edge or corner, an incident patch can supply the same boundary.
        // A chart-grid corner has four incident patches, and a cube corner has three.
        Span<PatchLocation> candidates = stackalloc PatchLocation[4];
        candidates[0] = primary;
        var count = 1;
        for (var index = 0; index < count; index++)
        {
            var candidate = candidates[index];
            if (index != 0 && _patches.TryGetValue(candidate.Patch, out mesh))
                return Intersect(mesh, candidate, direction, out support);

            if (candidate.U == 0)
                AddNeighbor(candidates, ref count, candidate, PlanetEdge.Left, Intervals - candidate.V);
            else if (candidate.U == Intervals)
                AddNeighbor(candidates, ref count, candidate, PlanetEdge.Right, candidate.V);
            if (candidate.V == 0)
                AddNeighbor(candidates, ref count, candidate, PlanetEdge.Bottom, candidate.U);
            else if (candidate.V == Intervals)
                AddNeighbor(candidates, ref count, candidate, PlanetEdge.Top, Intervals - candidate.U);
        }

        support = default;
        return false;
    }

    private static (int Tile, double Local) Locate(double chart)
    {
        var grid = (chart + 1) * (PlanetCoordinates.FinestIntervalsPerFace / 2.0);
        var boundary = Math.Round(grid / Intervals) * Intervals;
        if (Math.Abs(grid - boundary) <= BoundaryTolerance)
            grid = boundary;
        var tile = Math.Min((int)(grid / Intervals), TilesPerFace - 1);
        return (tile, grid - tile * Intervals);
    }

    private static void AddNeighbor(Span<PatchLocation> candidates, ref int count, PatchLocation source,
        PlanetEdge edge, double along)
    {
        var (patch, neighborEdge, reversed) = PlanetCoordinates.Neighbor(source.Patch, edge);
        for (var i = 0; i < count; i++)
        {
            if (candidates[i].Patch == patch)
                return;
        }

        if (reversed)
            along = Intervals - along;
        var (u, v) = neighborEdge switch
        {
            PlanetEdge.Bottom => (along, 0.0),
            PlanetEdge.Right => (Intervals, along),
            PlanetEdge.Top => (Intervals - along, Intervals),
            _ => (0.0, Intervals - along)
        };
        candidates[count++] = new PatchLocation(patch, u, v);
    }

    private static bool Intersect(PlanetPatchMesh mesh, PatchLocation location, PlanetVector direction,
        out PlanetSupport support)
    {
        var u = Math.Min((int)location.U, Intervals - 1);
        var v = Math.Min((int)location.V, Intervals - 1);
        // The unstitched mesher emits two triangles per cell, starting below its diagonal.
        var first = (v * Intervals + u) * 6;
        if (location.U - u < location.V - v)
            first += 3;
        var indices = mesh.Indices;
        var positions = mesh.Positions;
        var a = positions[(int)indices[first]];
        var b = positions[(int)indices[first + 1]];
        var c = positions[(int)indices[first + 2]];

        var scale = Math.Max(MaxComponent(a), Math.Max(MaxComponent(b), MaxComponent(c)));
        var unitDirection = Normalize(direction);
        var ab = Divide(Subtract(b, a), scale);
        var ac = Divide(Subtract(c, a), scale);
        var normal = Normalize(Cross(ab, ac));
        var incidence = Dot(normal, unitDirection);
        var radius = Dot(normal, Divide(a, scale)) / incidence * scale;
        if (incidence <= 0 || !double.IsFinite(radius) || radius <= 0)
        {
            support = default;
            return false;
        }

        support = new PlanetSupport(
            new PlanetVector(unitDirection.X * radius, unitDirection.Y * radius, unitDirection.Z * radius), normal);
        return true;
    }

    private static PlanetVector Normalize(PlanetVector vector)
    {
        var scale = MaxComponent(vector);
        if (scale == 0)
            return default;
        var scaled = Divide(vector, scale);
        return Divide(scaled, Math.Sqrt(Dot(scaled, scaled)));
    }

    private static double MaxComponent(PlanetVector vector) =>
        Math.Max(Math.Abs(vector.X), Math.Max(Math.Abs(vector.Y), Math.Abs(vector.Z)));

    private static PlanetVector Divide(PlanetVector vector, double divisor) =>
        new(vector.X / divisor, vector.Y / divisor, vector.Z / divisor);

    private static PlanetVector Subtract(PlanetVector first, PlanetVector second) =>
        new(first.X - second.X, first.Y - second.Y, first.Z - second.Z);

    private static PlanetVector Cross(PlanetVector first, PlanetVector second) =>
        new(first.Y * second.Z - first.Z * second.Y, first.Z * second.X - first.X * second.Z,
            first.X * second.Y - first.Y * second.X);

    private static double Dot(PlanetVector first, PlanetVector second) =>
        first.X * second.X + first.Y * second.Y + first.Z * second.Z;

    private readonly record struct PatchLocation(PlanetPatchKey Patch, double U, double V);
}
