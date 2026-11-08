using System.Numerics;

static class SphereSweep
{
    private const float ContactTolerance = 0.0001f;
    private const float DiagnosticError = 0.25f;

    internal static object Check(Mesh mesh, Grid grid, string fixture, string layout)
    {
        VerifyControls();
        var cases = new List<SweepCase>();
        foreach (var radius in new[] { 0.15f, 0.35f })
        {
            if (fixture == "bridge")
            {
                for (var x = 14.125f; x < 18; x += 0.25f)
                {
                    cases.Add(new("bridge-transverse", new(x, 12, 15.51f), new(x, 21, 15.51f), radius));
                    cases.Add(new("bridge-transverse", new(x, 16.15f, 11), new(x, 16.15f, 20), radius));
                }
                foreach (var offset in new[] { -0.12f, 0.12f })
                {
                    var y = 16.15f + 0.77f + radius + offset;
                    cases.Add(new("bridge-along", new(14, y, 15.51f), new(18, y, 15.51f), radius));
                    cases.Add(new("bridge-along", new(18, y, 15.51f), new(14, y, 15.51f), radius));
                }
                cases.Add(new("bridge-diagonal", new(14, 13, 15.51f), new(18, 19, 15.51f), radius));
                cases.Add(new("bridge-diagonal", new(18, 19, 15.51f), new(14, 13, 15.51f), radius));
            }
            else if (layout != "uniform")
            {
                // Pick short sweeps centered on actual density crossings close to
                // the LOD planes and tilt them so the bodies also cross a plane.
                var axes = layout == "corner" ? 3 : 1;
                for (var axis = 0; axis < axes; axis++)
                {
                    var normal = I3.Axis(axis).Position;
                    var u = I3.Axis((axis + 1) % 3).Position;
                    var v = I3.Axis((axis + 2) % 3).Position;
                    var extent = layout == "corner" ? 16f : 32f;
                    for (var a = 0.375f; a < extent; a += 1.25f)
                    {
                        var origin = normal * 16 + u * a;
                        var prior = grid.Field(origin);
                        for (var b = 0.25f; b < extent; b += 0.25f)
                        {
                            var point = origin + v * b;
                            var field = grid.Field(point);
                            if ((field < 0) != (prior < 0))
                            {
                                var outside = field < 0 ? -v : v;
                                cases.Add(new("lod-crossing", point + outside * 1.5f - normal,
                                    point - outside * 1.5f + normal, radius));
                                cases.Add(new("lod-crossing", point + outside * 1.5f + normal,
                                    point - outside * 1.5f - normal, radius));
                            }
                            prior = field;
                        }
                    }
                }
            }
        }

        var traces = cases.Select(s =>
        {
            var reference = Sweep(s, p => Math.Max(0, grid.Field(p)));
            var actual = GeometryProbe.IsInside(mesh, s.Start)
                ? new Contact("initial-overlap", 0)
                : Sweep(s, p => MeshDistance(mesh, p));
            float? error = reference.Distance is { } a && actual.Distance is { } b ? MathF.Abs(a - b) : null;
            return new { path = s, reference, actual, error };
        }).ToArray();
        var summary = traces.GroupBy(x => x.path.Group).Select(g => new
        {
            group = g.Key, sweeps = g.Count(),
            expectedHits = g.Count(x => x.reference.State == "hit"),
            expectedInitialOverlaps = g.Count(x => x.reference.State == "initial-overlap"),
            actualHits = g.Count(x => x.actual.State == "hit"),
            actualInitialOverlaps = g.Count(x => x.actual.State == "initial-overlap"),
            missed = g.Count(x => x.reference.Distance.HasValue && x.actual.State == "clear"),
            unexpected = g.Count(x => x.reference.State == "clear" && x.actual.Distance.HasValue),
            statusDifferences = g.Count(x => x.reference.State != x.actual.State),
            indeterminate = g.Count(x => x.reference.State == "indeterminate" || x.actual.State == "indeterminate"),
            overTolerance = g.Count(x => x.error > DiagnosticError),
            maxPairedError = g.Max(x => x.error)
        }).ToArray();
        return new
        {
            diagnosticError = DiagnosticError, summary,
            examples = traces.Where(x => x.reference.State != x.actual.State || x.error > DiagnosticError).Take(12).ToArray()
        };
    }

    private sealed record SweepCase(string Group, Vector3 Start, Vector3 End, float Radius);
    private readonly record struct Contact(string State, float? Distance);

    private static Contact Sweep(SweepCase sweep, Func<Vector3, float> distanceToSurface)
    {
        var delta = sweep.End - sweep.Start;
        var length = delta.Length();
        var direction = delta / length;
        var traveled = 0f;
        // Distance to a closed set is 1-Lipschitz. Advancing by the current
        // clearance cannot step past a first sphere contact, even on a long move.
        for (var iteration = 0; iteration < 512; iteration++)
        {
            var clearance = distanceToSurface(sweep.Start + direction * traveled) - sweep.Radius;
            if (clearance <= ContactTolerance)
                return new(traveled == 0 ? "initial-overlap" : "hit", traveled);
            if (clearance > length - traveled)
                return new("clear", null);
            traveled += clearance;
        }
        return new("indeterminate", null);
    }

    private static float MeshDistance(Mesh mesh, Vector3 p)
    {
        var min = float.PositiveInfinity;
        foreach (var triangle in mesh)
            min = Math.Min(min, DistanceSquared(triangle, p));
        return MathF.Sqrt(min);
    }

    private static float DistanceSquared(Triangle t, Vector3 p)
    {
        var ab = t.B - t.A;
        var ac = t.C - t.A;
        var normal = Vector3.Cross(ab, ac);
        var normalSquared = normal.LengthSquared();
        if (normalSquared > 0)
        {
            var ap = p - t.A;
            var height = Vector3.Dot(ap, normal);
            // The cross products dotted with the normal test barycentric signs
            // directly and avoid dividing by a poorly conditioned Gram matrix.
            var u = Vector3.Dot(Vector3.Cross(ap, ac), normal);
            var v = Vector3.Dot(Vector3.Cross(ab, ap), normal);
            if (u >= 0 && v >= 0 && u + v <= normalSquared)
                return height * height / normalSquared;
        }
        return Math.Min(SegmentDistance(t.A, t.B, p),
            Math.Min(SegmentDistance(t.B, t.C, p), SegmentDistance(t.C, t.A, p)));

        static float SegmentDistance(Vector3 a, Vector3 b, Vector3 p)
        {
            var edge = b - a;
            var lengthSquared = edge.LengthSquared();
            var t = lengthSquared > 0 ? Math.Clamp(Vector3.Dot(p - a, edge) / lengthSquared, 0, 1) : 0;
            return Vector3.DistanceSquared(p, a + t * edge);
        }
    }

    private static void VerifyControls()
    {
        var mesh = new Mesh { new(Vector3.Zero, Vector3.UnitX, Vector3.UnitY) };
        var through = new SweepCase("control", new(.25f, .25f, 10), new(.25f, .25f, -10), .2f);
        var miss = through with { Start = new(2, 2, 10), End = new(2, 2, -10) };
        var overlap = through with { Start = new(.25f, .25f, .1f) };
        var hit = Sweep(through, p => MeshDistance(mesh, p));
        if (hit.State != "hit" || hit.Distance is not { } distance || MathF.Abs(distance - 9.8f) > ContactTolerance ||
            Sweep(miss, p => MeshDistance(mesh, p)).State != "clear" ||
            Sweep(overlap, p => MeshDistance(mesh, p)).State != "initial-overlap")
            throw new InvalidOperationException("Sphere sweep controls failed.");
    }
}
