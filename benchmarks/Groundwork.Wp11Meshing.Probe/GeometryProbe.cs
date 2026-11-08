using System.Numerics;

static class GeometryProbe
{
    private const float Epsilon = 0.00001f;
    private const float CollisionTolerance = 0.75f;

    internal static object CheckSharedBoundary(Mesh left, Mesh right, float plane)
    {
        var first = BoundaryEdges(left);
        var second = BoundaryEdges(right);
        return new
        {
            leftTriangles = left.Count, rightTriangles = right.Count,
            leftEdges = first.Count, rightEdges = second.Count,
            unmatchedLeft = first.Count(edge => !second.Contains((edge.To, edge.From))),
            unmatchedRight = second.Count(edge => !first.Contains((edge.To, edge.From)))
        };

        HashSet<(Vector3 From, Vector3 To)> BoundaryEdges(Mesh mesh)
        {
            var edges = new HashSet<(Vector3, Vector3)>();
            foreach (var t in mesh)
            {
                Add(t.A, t.B);
                Add(t.B, t.C);
                Add(t.C, t.A);
            }
            return edges;

            void Add(Vector3 a, Vector3 b)
            {
                if (a.X == plane && b.X == plane && a != b && !edges.Remove((b, a)))
                    edges.Add((a, b));
            }
        }
    }

    internal static object Check(Mesh mesh, Grid grid, string fixture, string layout)
    {
        VerifyIntersectionDetector();
        var normals = mesh.Select((triangle, index) =>
        {
            var center = (triangle.A + triangle.B + triangle.C) / 3;
            var normal = Vector3.Normalize(Normal(triangle));
            var gradient = grid.Gradient(center);
            var dot = gradient.LengthSquared() > 0 ? Vector3.Dot(normal, Vector3.Normalize(gradient)) : 0;
            return new { index, center, dot, source = mesh.Sources![index] };
        }).ToArray();
        var normalSummary = normals.GroupBy(x => x.source).Select(group => new
        {
            source = group.Key, faces = group.Count(), opposite = group.Count(x => x.dot < -Epsilon),
            undefinedOrPerpendicular = group.Count(x => MathF.Abs(x.dot) <= Epsilon),
            worst = group.OrderBy(x => x.dot).Take(3).ToArray()
        }).ToArray();

        var bounds = mesh.Select(t => (Min: Vector3.Min(t.A, Vector3.Min(t.B, t.C)),
            Max: Vector3.Max(t.A, Vector3.Max(t.B, t.C)))).ToArray();
        var intersections = new List<object>();
        var testedPairs = 0;
        for (var i = 0; i < mesh.Count; i++)
        for (var j = i + 1; j < mesh.Count; j++)
        {
            if (!Overlap(bounds[i], bounds[j]))
                continue;
            testedPairs++;
            var kind = Intersection(mesh[i], mesh[j]);
            if (kind is not null)
                intersections.Add(new { i, j, kind, first = mesh.Sources![i], second = mesh.Sources[j],
                    firstTriangle = mesh[i], secondTriangle = mesh[j] });
        }

        var groups = new Dictionary<string, CollisionStats>();
        var examples = new List<CollisionExample>();
        for (var axis = 0; axis < 3; axis++)
        {
            var u = I3.Axis((axis + 1) % 3).Position;
            var v = I3.Axis((axis + 2) % 3).Position;
            for (var a = 0.173f; a < grid.Extent; a += 0.5f)
            for (var b = 0.319f; b < grid.Extent; b += 0.5f)
                Trace("grid", axis, u * a + v * b);
        }

        if (fixture == "bridge")
        {
            // Rays along the passage stay in the analytic bridge core. Transverse
            // rays sample its cross-section on either side of the fine/coarse plane.
            for (var y = 15.57f; y <= 16.73f; y += 0.19f)
            for (var z = 14.94f; z <= 16.08f; z += 0.19f)
                Trace("bridge-core", 0, new Vector3(0, y, z));
            for (var x = 14.025f; x <= 18; x += 0.125f)
            {
                Trace("bridge-cross-section", 1, new Vector3(x, 0, 15.51f));
                Trace("bridge-cross-section", 2, new Vector3(x, 16.15f, 0));
            }
        }

        return new
        {
            normalSummary, testedPairs, intersections, collisionTolerance = CollisionTolerance,
            collision = groups, movingBodies = SphereSweep.Check(mesh, grid, fixture, layout), examples
        };

        void Trace(string group, int axis, Vector3 origin)
        {
            var direction = I3.Axis(axis).Position;
            var expected = DensityHits(grid, origin, direction);
            var actual = MeshHits(mesh, origin, direction);
            Compare(group);
            if (layout != "uniform" && (NearTransition(expected) || NearTransition(actual)))
                Compare("lod-band");

            bool NearTransition(List<float> hits) => hits.Any(t =>
            {
                var p = origin + direction * t;
                return layout == "corner"
                    ? (MathF.Abs(p.X - grid.Extent / 2) <= 1 && p.Y <= grid.Extent / 2 + 1 && p.Z <= grid.Extent / 2 + 1) ||
                      (MathF.Abs(p.Y - grid.Extent / 2) <= 1 && p.X <= grid.Extent / 2 + 1 && p.Z <= grid.Extent / 2 + 1) ||
                      (MathF.Abs(p.Z - grid.Extent / 2) <= 1 && p.X <= grid.Extent / 2 + 1 && p.Y <= grid.Extent / 2 + 1)
                    : MathF.Abs(p.X - grid.Extent / 2) <= 1;
            });

            void Compare(string name)
            {
                if (!groups.TryGetValue(name, out var stats))
                    groups.Add(name, stats = new CollisionStats());
                stats.Rays++;
                if (expected.Count > 0) stats.DensityHitRays++;
                if (actual.Count > 0) stats.MeshHitRays++;
                if (expected.Count > 0 && actual.Count == 0) stats.MissedHitRays++;
                if (expected.Count == 0 && actual.Count > 0) stats.UnexpectedHitRays++;
                if (expected.Count != actual.Count) stats.CrossingCountMismatches++;
                if (name == "bridge-core")
                {
                    for (var x = 14.0625f; x < 18; x += 0.125f)
                    {
                        stats.CoreOccupancySamples++;
                        if ((actual.Count(hit => hit < x) & 1) == 0)
                            stats.MissingCoreOccupancySamples++;
                    }
                }
                var error = expected.Count == actual.Count && expected.Count > 0
                    ? expected.Zip(actual, (a, b) => MathF.Abs(a - b)).Max() : 0;
                stats.MaxPairedError = Math.Max(stats.MaxPairedError, error);
                if (error > CollisionTolerance) stats.OverToleranceRays++;
                if ((expected.Count != actual.Count || error > CollisionTolerance) &&
                    examples.Count(x => x.Group == name) < 5)
                    examples.Add(new CollisionExample(name, axis, origin, expected, actual));
            }
        }
    }

    private sealed record CollisionExample(string Group, int Axis, Vector3 Origin, List<float> Density, List<float> Mesh);

    private sealed class CollisionStats
    {
        public int Rays { get; set; }
        public int DensityHitRays { get; set; }
        public int MeshHitRays { get; set; }
        public int MissedHitRays { get; set; }
        public int UnexpectedHitRays { get; set; }
        public int CrossingCountMismatches { get; set; }
        public int CoreOccupancySamples { get; set; }
        public int MissingCoreOccupancySamples { get; set; }
        public int OverToleranceRays { get; set; }
        public float MaxPairedError { get; set; }
    }

    private static List<float> DensityHits(Grid grid, Vector3 origin, Vector3 direction)
    {
        var hits = new List<float>();
        const float step = 1f / 32;
        var previousInside = grid.Field(origin) < 0;
        for (var t = step; t <= grid.Extent; t += step)
        {
            var inside = grid.Field(origin + t * direction) < 0;
            if (inside != previousInside)
            {
                var low = t - step;
                var high = t;
                for (var iteration = 0; iteration < 14; iteration++)
                {
                    var mid = (low + high) * 0.5f;
                    if ((grid.Field(origin + mid * direction) < 0) == previousInside) low = mid;
                    else high = mid;
                }
                hits.Add((low + high) * 0.5f);
            }
            previousInside = inside;
        }
        return hits;
    }

    internal static bool IsInside(Mesh mesh, Vector3 point) =>
        (MeshHits(mesh, point, Vector3.Normalize(new(1, .371f, .193f))).Count & 1) != 0;

    private static List<float> MeshHits(Mesh mesh, Vector3 origin, Vector3 direction)
    {
        var hits = new List<float>();
        foreach (var t in mesh)
        {
            var e1 = t.B - t.A;
            var e2 = t.C - t.A;
            var p = Vector3.Cross(direction, e2);
            var determinant = Vector3.Dot(e1, p);
            if (MathF.Abs(determinant) <= 1e-10f)
                continue;
            var delta = origin - t.A;
            var u = Vector3.Dot(delta, p) / determinant;
            if (u < -Epsilon || u > 1 + Epsilon)
                continue;
            var q = Vector3.Cross(delta, e1);
            var v = Vector3.Dot(direction, q) / determinant;
            var distance = Vector3.Dot(e2, q) / determinant;
            if (v >= -Epsilon && u + v <= 1 + Epsilon && distance >= 0)
                hits.Add(distance);
        }
        hits.Sort();
        var unique = new List<float>();
        foreach (var hit in hits)
            if (unique.Count == 0 || hit - unique[^1] > 0.0001f)
                unique.Add(hit);
        return unique;
    }

    private static bool Overlap((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b) =>
        a.Min.X <= b.Max.X + Epsilon && b.Min.X <= a.Max.X + Epsilon &&
        a.Min.Y <= b.Max.Y + Epsilon && b.Min.Y <= a.Max.Y + Epsilon &&
        a.Min.Z <= b.Max.Z + Epsilon && b.Min.Z <= a.Max.Z + Epsilon;

    private static Vector3 Normal(Triangle t) => Vector3.Cross(t.B - t.A, t.C - t.A);

    private const double IntersectionEpsilon = 1e-9;

    private readonly record struct PreciseTriangle(D3 A, D3 B, D3 C)
    {
        internal PreciseTriangle(Triangle t) : this(new(t.A), new(t.B), new(t.C)) { }
    }

    private readonly record struct D3(double X, double Y, double Z)
    {
        internal D3(Vector3 p) : this(p.X, p.Y, p.Z) { }
        internal double LengthSquared => Dot(this, this);
        internal D3 Unit => this * (1 / Math.Sqrt(LengthSquared));
        internal static double Dot(D3 a, D3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal static D3 Cross(D3 a, D3 b) =>
            new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static D3 operator *(D3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    }
    private static string? Intersection(Triangle firstTriangle, Triangle secondTriangle)
    {
        var a = new PreciseTriangle(firstTriangle);
        var b = new PreciseTriangle(secondTriangle);
        var na = D3.Cross(a.B - a.A, a.C - a.A).Unit;
        var nb = D3.Cross(b.B - b.A, b.C - b.A).Unit;
        var axis = D3.Cross(na, nb);
        if (axis.LengthSquared < 1e-20)
        {
            if (Math.Abs(D3.Dot(b.A - a.A, na)) > IntersectionEpsilon ||
                Math.Abs(D3.Dot(b.B - a.A, na)) > IntersectionEpsilon ||
                Math.Abs(D3.Dot(b.C - a.A, na)) > IntersectionEpsilon)
                return null;
            return CoplanarOverlap(a, b, na) ? "coplanar-area" : null;
        }

        axis = axis.Unit;
        var first = Slice(a, b.A, nb, axis);
        var second = Slice(b, a.A, na, axis);
        var low = Math.Max(first.Min, second.Min);
        var high = Math.Min(first.Max, second.Max);
        if (high - low <= IntersectionEpsilon)
            return null;
        // An ordinary shared edge is allowed, including a crease.
        Span<D3> av = stackalloc D3[] { a.A, a.B, a.C };
        Span<D3> bv = stackalloc D3[] { b.A, b.B, b.C };
        foreach (var x in av)
        foreach (var y in av)
            if ((x - y).LengthSquared > IntersectionEpsilon * IntersectionEpsilon &&
                Shared(x, bv) && Shared(y, bv) &&
                low >= Math.Min(D3.Dot(x, axis), D3.Dot(y, axis)) - IntersectionEpsilon &&
                high <= Math.Max(D3.Dot(x, axis), D3.Dot(y, axis)) + IntersectionEpsilon)
                return null;
        return "crossing-segment";

        static bool Shared(D3 vertex, ReadOnlySpan<D3> vertices)
        {
            foreach (var other in vertices)
                if ((vertex - other).LengthSquared <= IntersectionEpsilon * IntersectionEpsilon)
                    return true;
            return false;
        }
    }

    private static (double Min, double Max) Slice(PreciseTriangle t, D3 point, D3 normal, D3 axis)
    {
        Span<D3> p = stackalloc D3[] { t.A, t.B, t.C };
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        for (var i = 0; i < 3; i++)
        {
            var j = (i + 1) % 3;
            var a = D3.Dot(p[i] - point, normal);
            var b = D3.Dot(p[j] - point, normal);
            if (Math.Abs(a) <= IntersectionEpsilon)
                Add(p[i]);
            if ((a < -IntersectionEpsilon && b > IntersectionEpsilon) || (a > IntersectionEpsilon && b < -IntersectionEpsilon))
                Add(p[i] + (p[j] - p[i]) * (a / (a - b)));
        }
        return (min, max);

        void Add(D3 vertex)
        {
            var projection = D3.Dot(vertex, axis);
            min = Math.Min(min, projection);
            max = Math.Max(max, projection);
        }
    }

    private static bool CoplanarOverlap(PreciseTriangle a, PreciseTriangle b, D3 normal) =>
        !Separates(a, b, normal) && !Separates(b, a, normal);

    private static bool Separates(PreciseTriangle a, PreciseTriangle b, D3 normal)
    {
        Span<D3> points = stackalloc D3[] { a.A, a.B, a.C };
        for (var i = 0; i < 3; i++)
        {
            var axis = D3.Cross(normal, points[(i + 1) % 3] - points[i]).Unit;
            var av = Project(a, axis);
            var bv = Project(b, axis);
            if (Math.Min(av.Max, bv.Max) - Math.Max(av.Min, bv.Min) <= IntersectionEpsilon)
                return true;
        }
        return false;
    }

    private static (double Min, double Max) Project(PreciseTriangle t, D3 axis)
    {
        var a = D3.Dot(t.A, axis);
        var b = D3.Dot(t.B, axis);
        var c = D3.Dot(t.C, axis);
        return (Math.Min(a, Math.Min(b, c)), Math.Max(a, Math.Max(b, c)));
    }

    private static void VerifyIntersectionDetector()
    {
        var baseTriangle = new Triangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        var sharedEdge = new Triangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ);
        var sharedCoplanarEdge = new Triangle(Vector3.Zero, Vector3.UnitX, -Vector3.UnitY);
        var overlap = new Triangle(new(.1f, .1f, 0), new(.6f, .1f, 0), new(.1f, .6f, 0));
        var crossing = new Triangle(new(.2f, .2f, -1), new(.2f, .2f, 1), new(.2f, .6f, 0));
        if (Intersection(baseTriangle, sharedEdge) is not null ||
            Intersection(baseTriangle, sharedCoplanarEdge) is not null ||
            Intersection(baseTriangle, overlap) != "coplanar-area" ||
            Intersection(baseTriangle, crossing) != "crossing-segment")
            throw new InvalidOperationException("Triangle intersection controls failed.");
    }
}
