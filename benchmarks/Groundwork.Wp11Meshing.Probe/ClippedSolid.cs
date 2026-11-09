using System.Numerics;

sealed class ClippedSolid
{
    private const float SurfaceTolerance = .00001f;
    internal const int MaxEdits = 8;
    private readonly CollisionSphere[] _surfaces;
    private readonly DensityEdit[] _edits;
    private readonly float? _planeX;
    private readonly Edge[] _edges;
    private readonly Vector3[] _corners;

    private ClippedSolid(CollisionSphere solid, DensityEdit[] edits, float? planeX = null)
    {
        _edits = edits;
        _planeX = planeX;
        _surfaces = [solid, .. edits.Select(edit => new CollisionSphere(edit.Center, edit.Radius))];
        var edges = new List<Edge>();
        var corners = new List<Vector3>();
        var candidates = new List<Vector3>(2);
        for (var i = 0; i < _surfaces.Length; i++)
        for (var j = i + 1; j < _surfaces.Length; j++)
        {
            var intersection = i == 0 && _planeX is { } face
                ? IntersectPlane(face, _surfaces[j], j)
                : Intersect(_surfaces[i], _surfaces[j], i, j);
            if (intersection is not { } edge)
                continue;
            edges.Add(edge);
            for (var k = j + 1; k < _surfaces.Length; k++)
            {
                candidates.Clear();
                edge.AppendIntersections(_surfaces[k], candidates);
                foreach (var candidate in candidates)
                    if (Valid(candidate, i, j, SurfaceTolerance, k))
                        corners.Add(candidate);
            }
        }
        _edges = edges.ToArray();
        _corners = corners.ToArray();
    }

    internal static ClippedSolid? Create(CollisionSphere solid, DensityEdit[] edits) =>
        edits.Length is >= 1 and <= MaxEdits && edits.Any(edit => !edit.Add &&
            Intersect(solid, new CollisionSphere(edit.Center, edit.Radius), 0, 1).HasValue)
            ? new ClippedSolid(solid, RemoveOverwrittenAdditions(edits)) : null;

    internal static ClippedSolid? CreatePlanar(float planeX, DensityEdit[] edits) =>
        edits.Length is >= 1 and <= MaxEdits && edits.Any(edit => !edit.Add &&
            MathF.Abs(edit.Center.X - planeX) < edit.Radius)
            ? new ClippedSolid(default, RemoveOverwrittenAdditions(edits), planeX) : null;

    private static DensityEdit[] RemoveOverwrittenAdditions(DensityEdit[] edits)
    {
        var remaining = new List<DensityEdit>(edits.Length);
        for (var i = 0; i < edits.Length; i++)
        {
            var edit = edits[i];
            if (!edit.Add || !edits.Skip(i + 1).Any(later => !later.Add && Contains(later, edit)))
                remaining.Add(edit);
        }
        return remaining.ToArray();

        static bool Contains(DensityEdit outer, DensityEdit inner)
        {
            // Any point changed by this addition is overwritten by the
            // later subtraction, even if other edits intervene.
            var margin = (double)outer.Radius - inner.Radius;
            var x = (double)outer.Center.X - inner.Center.X;
            var y = (double)outer.Center.Y - inner.Center.Y;
            var z = (double)outer.Center.Z - inner.Center.Z;
            return margin >= 0 && x * x + y * y + z * z <= margin * margin;
        }
    }

    internal float Distance(Vector3 point)
    {
        if (Valid(point, -1, -1, 0))
            return 0;

        // The nearest point belongs to a face, a clipped intersection
        // circle, or an endpoint where all three surfaces meet.
        var distance = float.PositiveInfinity;
        for (var i = 0; i < _surfaces.Length; i++)
        {
            if (i == 0 && _planeX is { } face)
            {
                var projected = new Vector3(face, point.Y, point.Z);
                if (Valid(projected, 0, -1, SurfaceTolerance))
                    distance = Math.Min(distance, MathF.Abs(point.X - face));
                continue;
            }
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

    private bool Valid(Vector3 point, int skipA, int skipB, float tolerance, int skipC = -1)
    {
        var inside = false;
        for (var i = 0; i < _surfaces.Length; i++)
        {
            var surface = _surfaces[i];
            var signed = i == skipA || i == skipB || i == skipC ? 0 :
                i == 0 && _planeX is { } face ? point.X - face :
                Vector3.Distance(point, surface.Center) - surface.Radius;
            // Keep the edit order. A later union may restore a point in an
            // earlier subtraction. Boundary points belong to the closure.
            inside = i == 0 || _edits[i - 1].Add
                ? inside || signed <= tolerance
                : inside && signed >= -tolerance;
        }
        return inside;
    }
    private static Edge? IntersectPlane(float face, CollisionSphere sphere, int second)
    {
        var offset = sphere.Center.X - face;
        if (MathF.Abs(offset) >= sphere.Radius)
            return null;
        var radius = MathF.Sqrt(sphere.Radius * sphere.Radius - offset * offset);
        return new Edge(new Vector3(face, sphere.Center.Y, sphere.Center.Z), Vector3.UnitX, radius, 0, second);
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
