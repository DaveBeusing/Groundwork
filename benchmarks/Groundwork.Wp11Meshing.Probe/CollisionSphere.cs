using System.Numerics;

readonly record struct CollisionSphere(Vector3 Center, float Radius)
{
    internal (float Enter, float Exit)? Roots(Vector3 origin, Vector3 direction, float expansion = 0)
    {
        var x = (double)Center.X - origin.X;
        var y = (double)Center.Y - origin.Y;
        var z = (double)Center.Z - origin.Z;
        var along = x * direction.X + y * direction.Y + z * direction.Z;
        var directionSquared = (double)direction.X * direction.X + (double)direction.Y * direction.Y +
            (double)direction.Z * direction.Z;
        var radius = (double)Radius + expansion;
        var discriminant = along * along - directionSquared * (x * x + y * y + z * z - radius * radius);
        if (discriminant < 0 || directionSquared == 0)
            return null;
        var halfSpan = Math.Sqrt(discriminant);
        return ((float)((along - halfSpan) / directionSquared), (float)((along + halfSpan) / directionSquared));
    }

    internal void AppendCrossings(List<float> hits, Vector3 origin, Vector3 direction, float limit)
    {
        if (Roots(origin, direction) is not { } roots || roots.Enter == roots.Exit)
            return;
        if (roots.Enter >= 0 && roots.Enter <= limit)
            hits.Add(roots.Enter);
        if (roots.Exit >= 0 && roots.Exit <= limit)
            hits.Add(roots.Exit);
    }
}

sealed class CollisionRayQuery(Grid grid, Mesh mesh)
{
    private readonly GrazingRayFallback? _fallback = grid.CreateGrazingFallback();
    private readonly SubtractiveRayRepair? _repair = grid.CreateSubtractiveRayRepair();

    internal Result Trace(Vector3 origin, Vector3 direction)
    {
        var meshHits = GeometryProbe.MeshHits(mesh, origin, direction);
        var (baseHits, exhausted, samples) = meshHits.Count == 0 && _fallback is not null
            ? _fallback.Trace(origin, direction) : (meshHits, false, 0);
        var usedFallback = meshHits.Count == 0 && baseHits.Count > 0;
        var repairedIntervals = 0;
        if (_repair is not null && (meshHits.Count > 0 || _repair.AllowsEmptyMesh))
        {
            (baseHits, repairedIntervals, var repairExhausted) = _repair.Trace(origin, direction, baseHits);
            exhausted |= repairExhausted;
        }
        if (grid.CollisionSpheres.IsEmpty)
            return new(meshHits, baseHits, 0, exhausted, samples, usedFallback, repairedIntervals);
        var primitiveHits = new List<float>();
        foreach (var sphere in grid.CollisionSpheres)
            sphere.AppendCrossings(primitiveHits, origin, direction, grid.Extent);
        if (primitiveHits.Count == 0)
            return new(meshHits, baseHits, 0, exhausted, samples, usedFallback, repairedIntervals);
        primitiveHits.AddRange(baseHits);
        primitiveHits.Sort();
        return new(meshHits, primitiveHits, primitiveHits.Count - baseHits.Count, exhausted, samples, usedFallback, repairedIntervals);
    }

    internal readonly record struct Result(List<float> MeshHits, List<float> Hits, int PrimitiveCrossings,
        bool Exhausted, int Samples, bool UsedFallback, int RepairedIntervals);
}
