namespace Groundwork.Voxels;

public enum VolumeQueryStatus { Clear, Contact, InitialOverlap, CoverageUnavailable, BudgetExceeded, Invalid }
public readonly record struct VolumeRayHit(double Distance, PlanetVector Position, PlanetVector Normal, ushort Material, bool IsTransition = false);
public readonly record struct VolumeRayResult(VolumeQueryStatus Status, int Count, VolumePublicationInfo Terrain, int TriangleTests);
/// <summary>Axis segment endpoints and radius in planet-centered metres. Equal endpoints form a sphere.</summary>
public readonly record struct VolumeCapsule(PlanetVector A, PlanetVector B, double Radius);
public readonly record struct VolumeSweepResult(VolumeQueryStatus Status, double Fraction,
    PlanetVector Normal, VolumePublicationInfo Terrain, int TriangleTests, bool IsTransition = false);
/// <summary>Hard per-query ceilings. Exhaustion fails closed, never reports empty terrain.</summary>
public readonly record struct VolumeQueryLimits(int TriangleTests = 2_000_000, int Iterations = 64)
{
    public VolumeQueryLimits() : this(2_000_000, 64) { }
    public static VolumeQueryLimits Default => new(2_000_000, 64);
    internal bool Valid => TriangleTests is > 0 and <= 16_000_000 && Iterations is > 0 and <= 256;
}

// All arithmetic is double in the bounded plan-local lattice. No render floats or chart-local IDs.
internal readonly record struct CollisionVector(double X, double Y, double Z)
{
    public CollisionVector(PlanetVector p) : this(p.X, p.Y, p.Z) { }
    public PlanetVector Planet => new(X, Y, Z);
    public double Length => Math.Sqrt(Dot(this, this));
    public bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public static CollisionVector operator +(CollisionVector a, CollisionVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static CollisionVector operator -(CollisionVector a, CollisionVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static CollisionVector operator *(CollisionVector a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public static CollisionVector operator /(CollisionVector a, double b) => a * (1 / b);
    public static double Dot(CollisionVector a, CollisionVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static CollisionVector Cross(CollisionVector a, CollisionVector b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}

internal static class VolumeCollision
{
    internal static bool Covers(VolumeGeneration generation, CollisionVector low, CollisionVector high)
    {
        var extent = generation.Selection.Width * 32;
        var inside = low.Finite && high.Finite && low.X >= 0 && low.Y >= 0 && low.Z >= 0 &&
            high.X <= extent && high.Y <= extent && high.Z <= extent;
        if (!inside || !generation.Selection.RequiresFineCoverage)
            return inside;

        var plan = generation.Artifacts!.Plan;
        for (var z = First(low.Z); z <= Last(high.Z); z++)
        for (var y = First(low.Y); y <= Last(high.Y); y++)
        for (var x = First(low.X); x <= Last(high.X); x++)
            if (plan.Stride((z * plan.Height + y) * plan.Width + x) != 1)
                return false;
        return true;

        static int First(double coordinate) => Math.Max(0, (int)Math.Ceiling(coordinate / 32) - 1);
        int Last(double coordinate) => Math.Min(generation.Selection.Width - 1, (int)(coordinate / 32));
    }
    internal static CollisionVector Local(VolumeGeneration generation, PlanetVector p)
    {
        var origin = generation.Selection.Minimum.Vertex(0, 0, 0);
        var cell = generation.Selection.Descriptor.CellSizeMeters;
        return new(p.X / cell - origin.X, p.Y / cell - origin.Y, p.Z / cell - origin.Z);
    }
    private static CollisionVector Vertex(VolumeGeneration g, PlanetVolumeMesh m, System.Numerics.Vector3 p)
    {
        var origin = g.Selection.Minimum.Vertex(0, 0, 0);
        return new((double)((Int128)m.Origin.X - origin.X) + p.X,
            (double)((Int128)m.Origin.Y - origin.Y) + p.Y, (double)((Int128)m.Origin.Z - origin.Z) + p.Z);
    }
    private static CollisionVector Min(CollisionVector a, CollisionVector b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    private static CollisionVector Max(CollisionVector a, CollisionVector b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    internal static VolumeRayResult Ray(VolumeGeneration g, PlanetVector start, PlanetVector end,
        Span<VolumeRayHit> hits, VolumeQueryLimits limits)
    {
        var a = Local(g, start); var b = Local(g, end); var delta = b - a; var length = delta.Length;
        if (!limits.Valid || hits.Length > 256 || !a.Finite || !b.Finite || !double.IsFinite(length) || length <= 0)
            return new(VolumeQueryStatus.Invalid, 0, g.Info, 0);
        if (!Covers(g, Min(a, b), Max(a, b))) return new(VolumeQueryStatus.CoverageUnavailable, 0, g.Info, 0);
        var count = 0; var tests = 0;
        foreach (var mesh in g.Artifacts!.Meshes)
            foreach (var t in mesh.Triangles)
            {
                if (++tests > limits.TriangleTests) return new(VolumeQueryStatus.BudgetExceeded, 0, g.Info, tests - 1);
                var x = Vertex(g, mesh, t.A.Position); var y = Vertex(g, mesh, t.B.Position); var z = Vertex(g, mesh, t.C.Position);
                if (!Intersect(a, delta, x, y, z, out var fraction)) continue;
                var distance = fraction * length * g.Selection.Descriptor.CellSizeMeters;
                var duplicate = false;
                for (var i = 0; i < count; i++) if (Math.Abs(hits[i].Distance - distance) <= 1e-7 * g.Selection.Descriptor.CellSizeMeters)
                {
                    if (t.IsTransition) hits[i] = hits[i] with { IsTransition = true };
                    duplicate = true; break;
                }
                if (duplicate) continue;
                if (count == hits.Length) return new(VolumeQueryStatus.BudgetExceeded, 0, g.Info, tests);
                var n = CollisionVector.Cross(y - x, z - x); n /= n.Length;
                var point = new CollisionVector(start) + (new CollisionVector(end) - new CollisionVector(start)) * fraction;
                var hit = new VolumeRayHit(distance, point.Planet, n.Planet, t.A.Material, t.IsTransition);
                var slot = count++;
                while (slot > 0 && hits[slot - 1].Distance > distance) { hits[slot] = hits[slot - 1]; slot--; }
                hits[slot] = hit;
            }
        return new(count == 0 ? VolumeQueryStatus.Clear : VolumeQueryStatus.Contact, count, g.Info, tests);
    }

    internal static VolumeSweepResult Sweep(VolumeGeneration g, VolumeCapsule capsule, PlanetVector displacement, VolumeQueryLimits limits, PlanetVector? endA = null, PlanetVector? endB = null)
    {
        var a = Local(g, capsule.A); var b = Local(g, capsule.B);
        var cell = g.Selection.Descriptor.CellSizeMeters;
        var d = new CollisionVector(displacement) / cell; var r = capsule.Radius / cell;
        var da = endA is { } targetA ? Local(g, targetA) - a : d;
        var db = endB is { } targetB ? Local(g, targetB) - b : d;
        var padding = new CollisionVector(r, r, r);
        if (!limits.Valid || !a.Finite || !b.Finite || !d.Finite || !double.IsFinite(d.Length) ||
            !da.Finite || !db.Finite || !double.IsFinite(da.Length) || !double.IsFinite(db.Length) || !double.IsFinite(r) || r <= 0 || r > 32 || (b - a).Length > 64)
            return new(VolumeQueryStatus.Invalid, 0, default, g.Info, 0);
        if (!Covers(g, Min(Min(a, b), Min(a + da, b + db)) - padding, Max(Max(a, b), Max(a + da, b + db)) + padding))
            return new(VolumeQueryStatus.CoverageUnavailable, 0, default, g.Info, 0);
        if (Solid(g, a) || Solid(g, b) || Solid(g, (a + b) * 0.5))
            return new(VolumeQueryStatus.InitialOverlap, 0, default, g.Info, 0);
        var speed = Math.Max(da.Length, db.Length); var fraction = 0d; var tests = 0;
        const double skin = 1e-4;
        for (var iteration = 0; iteration < limits.Iterations; iteration++)
        {
            var nearest = double.PositiveInfinity; var normal = default(CollisionVector);
            var nearestTransition = false;
            foreach (var mesh in g.Artifacts!.Meshes)
                foreach (var triangle in mesh.Triangles)
                {
                    if (++tests > limits.TriangleTests) return new(VolumeQueryStatus.BudgetExceeded, 0, default, g.Info, tests - 1);
                    var x = Vertex(g, mesh, triangle.A.Position); var y = Vertex(g, mesh, triangle.B.Position); var z = Vertex(g, mesh, triangle.C.Position);
                    var face = CollisionVector.Cross(y - x, z - x);
                    if (face.Length > 1e-12)
                    {
                        face /= face.Length;
                        var fromA = CollisionVector.Dot(a + da * fraction - x, face);
                        var fromB = CollisionVector.Dot(b + db * fraction - x, face);
                        var toA = CollisionVector.Dot(a + da - x, face);
                        var toB = CollisionVector.Dot(b + db - x, face);
                        // A separating triangle plane proves safety for the entire remaining sweep,
                        // including coplanar floor triangles at a shared edge.
                        if (Math.Min(Math.Min(fromA, fromB), Math.Min(toA, toB)) >= r - skin ||
                            Math.Max(Math.Max(fromA, fromB), Math.Max(toA, toB)) <= -r + skin) continue;
                    }
                    var distance = SegmentTriangle(a + da * fraction, b + db * fraction, x, y, z, out var separation);
                    var n = distance > 1e-12 ? separation / distance : CollisionVector.Cross(y - x, z - x);
                    if (n.Length > 0) n /= n.Length;
                    if (fraction == 0 && distance < r - skin) return new(VolumeQueryStatus.InitialOverlap, 0, n.Planet, g.Info, tests);
                    if (distance < nearest - 1e-10) { nearest = distance; normal = n; nearestTransition = triangle.IsTransition; }
                    else if (Math.Abs(distance - nearest) <= 1e-10 && triangle.IsTransition) nearestTransition = true;
                }
            if (nearest <= r + skin) return new(VolumeQueryStatus.Contact, fraction, normal.Planet, g.Info, tests, nearestTransition);
            if (speed == 0 || double.IsPositiveInfinity(nearest)) return new(VolumeQueryStatus.Clear, 1, default, g.Info, tests);
            var advance = (nearest - r - skin) / speed;
            if (fraction + advance > 1) return new(VolumeQueryStatus.Clear, 1, default, g.Info, tests);
            if (advance <= 1e-12) return new(VolumeQueryStatus.Contact, fraction, normal.Planet, g.Info, tests, nearestTransition);
            fraction += advance;
        }
        return new(VolumeQueryStatus.BudgetExceeded, 0, default, g.Info, tests);
    }

    private static bool Solid(VolumeGeneration g, CollisionVector p)
    {
        // Trilinear density only rejects an initially embedded body. Contact geometry is the
        // exact committed mesh, including its Transvoxel transition triangles.
        var x = (int)Math.Floor(p.X); var y = (int)Math.Floor(p.Y); var z = (int)Math.Floor(p.Z);
        var value = 0d;
        for (var k = 0; k < 2; k++)
            for (var j = 0; j < 2; j++)
                for (var i = 0; i < 2; i++)
                {
                    var weight = (i == 0 ? 1 - (p.X - x) : p.X - x) *
                        (j == 0 ? 1 - (p.Y - y) : p.Y - y) * (k == 0 ? 1 - (p.Z - z) : p.Z - z);
                    if (weight != 0)
                        value += (double)g.Artifacts!.Plan.Read(new System.Numerics.Vector3(x + i, y + j, z + k)) * weight;
                }
        return value < 0;
    }
    private static bool Intersect(CollisionVector p, CollisionVector d, CollisionVector a, CollisionVector b, CollisionVector c, out double t)
    {
        var e = b - a; var f = c - a; var h = CollisionVector.Cross(d, f); var det = CollisionVector.Dot(e, h);
        t = 0;
        if (Math.Abs(det) < 1e-14) return false;
        var s = p - a; var u = CollisionVector.Dot(s, h) / det;
        var q = CollisionVector.Cross(s, e); var v = CollisionVector.Dot(d, q) / det;
        t = CollisionVector.Dot(f, q) / det;
        return u >= -1e-10 && v >= -1e-10 && u + v <= 1 + 1e-10 && t >= 0 && t <= 1;
    }
    private static double SegmentTriangle(CollisionVector a, CollisionVector b, CollisionVector x, CollisionVector y, CollisionVector z, out CollisionVector separation)
    {
        if (Intersect(a, b - a, x, y, z, out _)) { separation = default; return 0; }
        separation = a - Closest(a, x, y, z);
        var best = CollisionVector.Dot(separation, separation);
        Keep(b - Closest(b, x, y, z), ref best, ref separation);
        Keep(Segments(a, b, x, y), ref best, ref separation);
        Keep(Segments(a, b, y, z), ref best, ref separation);
        Keep(Segments(a, b, z, x), ref best, ref separation);
        return Math.Sqrt(Math.Max(0, best));
    }
    private static void Keep(CollisionVector v, ref double best, ref CollisionVector result)
    { var squared = CollisionVector.Dot(v, v); if (squared < best) { best = squared; result = v; } }
    private static CollisionVector Closest(CollisionVector p, CollisionVector a, CollisionVector b, CollisionVector c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a; var d1 = CollisionVector.Dot(ab, ap); var d2 = CollisionVector.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; var d3 = CollisionVector.Dot(ab, bp); var d4 = CollisionVector.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c; var d5 = CollisionVector.Dot(ab, cp); var d6 = CollisionVector.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6));
        var sum = va + vb + vc;
        if (sum <= 0) return a; // Degenerate input cannot produce an unbounded division.
        return a + ab * (vb / sum) + ac * (vc / sum);
    }
    private static CollisionVector Segments(CollisionVector a, CollisionVector b, CollisionVector c, CollisionVector d)
    {
        var u = b - a; var v = d - c; var w = a - c;
        var aa = CollisionVector.Dot(u, u); var bb = CollisionVector.Dot(u, v); var cc = CollisionVector.Dot(v, v);
        var dd = CollisionVector.Dot(u, w); var ee = CollisionVector.Dot(v, w); var denom = aa * cc - bb * bb;
        var s = aa == 0 ? 0 : Math.Clamp(denom > 1e-20 ? (bb * ee - cc * dd) / denom : -dd / aa, 0, 1);
        var t = cc == 0 ? 0 : Math.Clamp((bb * s + ee) / cc, 0, 1);
        s = aa == 0 ? 0 : Math.Clamp((bb * t - dd) / aa, 0, 1);
        return a + u * s - (c + v * t);
    }
}
