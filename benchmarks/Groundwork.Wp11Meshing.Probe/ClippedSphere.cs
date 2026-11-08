using System.Numerics;

sealed class ClippedSphere
{
    private const float SurfaceTolerance = .00001f;
    private readonly CollisionSphere[] _surfaces;
    private readonly Edge[] _edges;
    private readonly Vector3[] _corners;

    private ClippedSphere(CollisionSphere solid, CollisionSphere[] cuts)
    {
        _surfaces = [solid, .. cuts];
        var edges = new List<Edge>();
        var corners = new List<Vector3>();
        for (var i = 0; i < _surfaces.Length; i++)
        for (var j = i + 1; j < _surfaces.Length; j++)
        {
            if (Intersect(_surfaces[i], _surfaces[j], i, j) is not { } edge)
                continue;
            edges.Add(edge);
            for (var k = j + 1; k < _surfaces.Length; k++)
                edge.AppendIntersections(_surfaces[k], corners);
        }
        _edges = edges.ToArray();
        _corners = corners.ToArray();
    }

    internal static ClippedSphere? Create(CollisionSphere solid, CollisionSphere[] cuts) =>
        cuts.Length is 1 or 2 && cuts.All(cut => Intersect(solid, cut, 0, 1).HasValue)
            ? new ClippedSphere(solid, cuts) : null;

    internal float Distance(Vector3 point)
    {
        if (Valid(point, -1, -1, 0))
            return 0;

        // The nearest point belongs to a face, a clipped intersection
        // circle, or an endpoint where all three surfaces meet.
        var distance = float.PositiveInfinity;
        for (var i = 0; i < _surfaces.Length; i++)
        {
            var surface = _surfaces[i];
            var offset = point - surface.Center;
            var length = offset.Length();
            if (length > 0 && Valid(surface.Center + offset * (surface.Radius / length), i, -1, SurfaceTolerance))
                distance = Math.Min(distance, MathF.Abs(length - surface.Radius));
        }
        foreach (var edge in _edges)
        {
            var offset = point - edge.Center;
            var axial = Vector3.Dot(offset, edge.Axis);
            var planar = offset - axial * edge.Axis;
            var planarLength = planar.Length();
            var direction = planarLength > 0 ? planar / planarLength : Perpendicular(edge.Axis);
            if (Valid(edge.Center + edge.Radius * direction, edge.First, edge.Second, SurfaceTolerance))
            {
                var radial = planarLength - edge.Radius;
                distance = Math.Min(distance, MathF.Sqrt(axial * axial + radial * radial));
            }
        }
        foreach (var corner in _corners)
            distance = Math.Min(distance, Vector3.Distance(point, corner));
        return distance;
    }

    private bool Valid(Vector3 point, int skipA, int skipB, float tolerance)
    {
        for (var i = 0; i < _surfaces.Length; i++)
        {
            if (i == skipA || i == skipB)
                continue;
            var surface = _surfaces[i];
            var distance = Vector3.Distance(point, surface.Center);
            if (i == 0 ? distance > surface.Radius + tolerance : distance < surface.Radius - tolerance)
                return false;
        }
        return true;
    }

    private static Edge? Intersect(CollisionSphere a, CollisionSphere b, int first, int second)
    {
        var delta = b.Center - a.Center;
        var separation = delta.Length();
        if (separation <= MathF.Abs(a.Radius - b.Radius) || separation >= a.Radius + b.Radius)
            return null;
        var axis = delta / separation;
        var along = (a.Radius * a.Radius - b.Radius * b.Radius + separation * separation) / (2 * separation);
        return new Edge(a.Center + along * axis, axis,
            MathF.Sqrt(Math.Max(0, a.Radius * a.Radius - along * along)), first, second);
    }

    private static Vector3 Perpendicular(Vector3 axis) =>
        Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.X) < .8f ? Vector3.UnitX : Vector3.UnitY));

    private readonly record struct Edge(Vector3 Center, Vector3 Axis, float Radius, int First, int Second)
    {
        internal void AppendIntersections(CollisionSphere third, List<Vector3> points)
        {
            var offset = third.Center - Center;
            var planar = offset - Vector3.Dot(offset, Axis) * Axis;
            var planarLength = planar.Length();
            if (planarLength == 0)
                return;
            var along = (Radius * Radius + offset.LengthSquared() - third.Radius * third.Radius) /
                (2 * planarLength);
            if (MathF.Abs(along) > Radius)
                return;
            var direction = planar / planarLength;
            var midpoint = Center + along * direction;
            var lateral = Vector3.Cross(Axis, direction) * MathF.Sqrt(Math.Max(0, Radius * Radius - along * along));
            points.Add(midpoint + lateral);
            points.Add(midpoint - lateral);
        }
    }
}
