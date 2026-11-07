namespace Groundwork.Voxels;

/// <summary>A visible leaf and the edges stitched to coarser neighbors.</summary>
public readonly record struct PlanetTerrainTile(PlanetPatchKey Patch, PlanetTransitionEdges Transitions, bool SupportsCollision);

/// <summary>An immutable, balanced cover of the six cube faces.</summary>
public sealed class PlanetTerrainPlan
{
    private readonly PlanetTerrainTile[] _tiles;

    internal PlanetTerrainPlan(PlanetTerrainTile[] tiles) => _tiles = tiles;

    public ReadOnlySpan<PlanetTerrainTile> Tiles => _tiles;
}

/// <summary>Selects a bounded six-face terrain cover around a planet-centered observer.</summary>
public static class PlanetTerrainSelection
{
    public const int MaximumLeaves = PlanetSurface.MaximumPatches;
    public const double SupportBandMeters = 6 + PlanetSurfaceWalker.BodyRadius +
        PlanetSurfaceWalker.WalkSpeed * PlanetSurfaceWalker.StepSeconds;

    private const double RefineRatio = 0.8;
    private const double RetainRatio = 1.1;
    private const int LevelLimit = PlanetCoordinates.MaximumLevel;
    private const int Intervals = PlanetCoordinates.IntervalsPerPatch;
    private static readonly PlanetFace[] Faces = Enum.GetValues<PlanetFace>();
    private static readonly PlanetEdge[] Edges = Enum.GetValues<PlanetEdge>();

    /// <summary>
    /// Selects a plan using the radius of the reference sphere in meters. Refinement compares
    /// the angular gap to a patch's enclosing cap with the cap radius. Previously split patches
    /// use a larger threshold so a stationary observer near a boundary retains its plan.
    /// Returns false when the balanced cover would exceed the leaf budget.
    /// </summary>
    public static bool TrySelect(PlanetVector position, double radius, PlanetTerrainPlan? previous, out PlanetTerrainPlan? plan)
    {
        if (!double.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));

        // Project validates the input without taking the length of a potentially huge vector.
        _ = PlanetCoordinates.Project(position);
        var scale = Math.Max(Math.Abs(position.X), Math.Max(Math.Abs(position.Y), Math.Abs(position.Z)));
        var x = position.X / scale;
        var y = position.Y / scale;
        var z = position.Z / scale;
        var length = Math.Sqrt(x * x + y * y + z * z);
        return new Selection(new PlanetVector(x / length, y / length, z / length), radius, previous).TryBuild(out plan);
    }

    private static PlanetPatchKey Parent(PlanetPatchKey patch) =>
        new(patch.Face, patch.Level - 1, patch.TileU / 2, patch.TileV / 2);

    private static PlanetPatchKey Child(PlanetPatchKey patch, int index) =>
        new(patch.Face, patch.Level + 1, patch.TileU * 2 + index % 2, patch.TileV * 2 + index / 2);

    private static double Angle(PlanetVector first, PlanetVector second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        var z = first.Z - second.Z;
        return 2 * Math.Asin(Math.Min(1, Math.Sqrt(x * x + y * y + z * z) / 2));
    }

    private static PlanetVector Direction(PlanetPatchKey patch, int u, int v) =>
        PlanetCoordinates.Direction(PlanetCoordinates.Sample(patch, u, v));

    private static PlanetTransitionEdges Transition(PlanetEdge edge) => edge switch
    {
        PlanetEdge.Bottom => PlanetTransitionEdges.Bottom,
        PlanetEdge.Right => PlanetTransitionEdges.Right,
        PlanetEdge.Top => PlanetTransitionEdges.Top,
        _ => PlanetTransitionEdges.Left
    };

    private sealed class Selection
    {
        private readonly PlanetVector _observer;
        private readonly double _radius;
        private readonly HashSet<PlanetPatchKey> _previousBranches = [];
        private readonly HashSet<PlanetPatchKey> _leaves = new(MaximumLeaves);
        private readonly HashSet<PlanetPatchKey> _support = [];
        private int _leafCount = Faces.Length;

        internal Selection(PlanetVector observer, double radius, PlanetTerrainPlan? previous)
        {
            _observer = observer;
            _radius = radius;
            if (previous is not null)
            {
                foreach (var tile in previous.Tiles)
                {
                    var patch = tile.Patch;
                    while (patch.Level > 0 && _previousBranches.Add(Parent(patch)))
                        patch = Parent(patch);
                }
            }
        }

        internal bool TryBuild(out PlanetTerrainPlan? plan)
        {
            plan = null;
            foreach (var face in Faces)
                if (!SelectLeaves(new PlanetPatchKey(face, 0, 0, 0)))
                    return false;

            // The immediate finest neighbors keep the support mesh unstitched,
            // even where the actual support band ends inside a patch.
            foreach (var patch in _support)
                foreach (var edge in Edges)
                    if (!RequireFinest(PlanetCoordinates.Neighbor(patch, edge).Patch))
                        return false;

            while (FindUnbalancedNeighbor() is { } coarse)
                if (!SplitLeaf(coarse))
                    return false;

            var tiles = _leaves.OrderBy(patch => patch.Face).ThenBy(patch => patch.Level)
                .ThenBy(patch => patch.TileV).ThenBy(patch => patch.TileU)
                .Select(patch => new PlanetTerrainTile(patch, GetTransitions(patch), _support.Contains(patch)))
                .ToArray();
            plan = new PlanetTerrainPlan(tiles);
            return true;
        }

        private bool SelectLeaves(PlanetPatchKey patch)
        {
            var gap = AngularGap(patch, 0, 0, Intervals, out var cap);
            var supportsCollision = gap * _radius <= SupportBandMeters && IntersectsSupportBand(patch);
            var ratio = _previousBranches.Contains(patch) ? RetainRatio : RefineRatio;
            if (patch.Level == LevelLimit || (!supportsCollision && gap > ratio * cap))
            {
                _leaves.Add(patch);
                if (supportsCollision)
                    _support.Add(patch);
                return true;
            }

            if (!ReserveSplit())
                return false;
            for (var child = 0; child < 4; child++)
                if (!SelectLeaves(Child(patch, child)))
                    return false;
            return true;
        }

        private bool IntersectsSupportBand(PlanetPatchKey patch)
        {
            // Four smaller caps cover the tile more tightly at the support boundary.
            const int half = Intervals / 2;
            for (var v = 0; v < Intervals; v += half)
                for (var u = 0; u < Intervals; u += half)
                    if (AngularGap(patch, u, v, half, out _) * _radius <= SupportBandMeters)
                        return true;
            return false;
        }

        private double AngularGap(PlanetPatchKey patch, int u, int v, int width, out double cap)
        {
            var center = Direction(patch, u + width / 2, v + width / 2);
            // The cap contains the chart rectangle. Its points are normalized positive
            // combinations of the corner directions, all within the same hemisphere.
            cap = Math.Max(Math.Max(Angle(center, Direction(patch, u, v)),
                    Angle(center, Direction(patch, u + width, v))),
                Math.Max(Angle(center, Direction(patch, u, v + width)),
                    Angle(center, Direction(patch, u + width, v + width))));
            return Math.Max(0, Angle(_observer, center) - cap - 1e-13);
        }

        private bool RequireFinest(PlanetPatchKey finest)
        {
            while (FindCoveringLeaf(finest) is { } covering && covering.Level < LevelLimit)
                if (!SplitLeaf(covering))
                    return false;
            return true;
        }

        private PlanetPatchKey? FindUnbalancedNeighbor()
        {
            foreach (var patch in _leaves)
                foreach (var edge in Edges)
                    if (FindCoveringLeaf(PlanetCoordinates.Neighbor(patch, edge).Patch) is { } neighbor &&
                        neighbor.Level < patch.Level - 1)
                        return neighbor;
            return null;
        }

        private PlanetTransitionEdges GetTransitions(PlanetPatchKey patch)
        {
            var transitions = PlanetTransitionEdges.None;
            foreach (var edge in Edges)
                if (FindCoveringLeaf(PlanetCoordinates.Neighbor(patch, edge).Patch) is { } neighbor &&
                    neighbor.Level < patch.Level)
                    transitions |= Transition(edge);
            return transitions;
        }

        private PlanetPatchKey? FindCoveringLeaf(PlanetPatchKey patch)
        {
            while (true)
            {
                if (_leaves.Contains(patch))
                    return patch;
                if (patch.Level == 0)
                    return null;
                patch = Parent(patch);
            }
        }

        private bool SplitLeaf(PlanetPatchKey patch)
        {
            if (!ReserveSplit())
                return false;
            _leaves.Remove(patch);
            for (var child = 0; child < 4; child++)
                _leaves.Add(Child(patch, child));
            return true;
        }

        private bool ReserveSplit()
        {
            if (_leafCount > MaximumLeaves - 3)
                return false;
            _leafCount += 3;
            return true;
        }
    }
}
