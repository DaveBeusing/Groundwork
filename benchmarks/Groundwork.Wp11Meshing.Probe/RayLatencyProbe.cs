using System.Diagnostics;
using System.Numerics;

static class RayLatencyProbe
{
    private static readonly Ray[] Grazing =
    [
        new("original-cap-x", new(0, 20.173f, 16.819f), Vector3.UnitX),
        new("original-cap-y", new(19.319f, 0, 13.673f), Vector3.UnitY),
        new("original-cap-z-a", new(8.173f, 20.319f, 0), Vector3.UnitZ),
        new("original-cap-z-b", new(9.173f, 20.319f, 0), Vector3.UnitZ)
    ];
    private static readonly Ray Added = new("added-off-seam", new(0, 23.173f, 24.319f), Vector3.UnitX);
    private static readonly Ray Seam = new("added-in-seam", new(16.3f, 0, 24.319f), Vector3.UnitY);
    private static readonly Ray Straddling = new("added-across-seam", new(16.02f, 0, 22.319f), Vector3.UnitY);
    private static readonly Ray Cancelled = new("added-then-removed", new(15.7f, 0, 26.319f), Vector3.UnitY);
    private static readonly Ray Clear = new("clear", new(0, 1.173f, 1.319f), Vector3.UnitX);

    internal static object Run(Tables tables, int samples)
    {
        if (samples is < 100 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(samples));

        DensityEdit[] edits =
        [
            new(new(23.17f, 20.173f, 16.819f), .4f, false),
            new(new(24.3f, 23.173f, 24.319f), .1f, true),
            new(new(16.3f, 24.173f, 24.319f), .1f, true),
            new(new(16.02f, 24.673f, 22.319f), .1f, true),
            new(new(15.7f, 25.173f, 26.319f), .1f, true),
            new(new(15.7f, 25.173f, 26.319f), .15f, false)
        ];
        var edgeSeamEditGroups = CheckUpperEditGroups(tables, edits, spatial: true, atSeam: true, planar: true, atEdge: true);
        var flatSeamEditGroups = CheckUpperEditGroups(tables, edits, spatial: true, atSeam: true, planar: true);
        var seamEditGroups = CheckUpperEditGroups(tables, edits, spatial: true, atSeam: true);
        var cancelledBrushPair = CheckCancelledBrushPair(tables, edits);
        var spatialEditGroups = CheckUpperEditGroups(tables, edits, spatial: true);
        var original = Create([]);
        var sourceEdits = edits.ToArray();
        var edited = Create(sourceEdits);
        // The collision snapshot must own its edits, even if the caller reuses its buffer.
        Array.Clear(sourceEdits);

        var before = original.CreateGrazingFallback()!;
        var after = edited.CreateGrazingFallback()!;
        var work = new (string Name, GrazingRayFallback Query, Ray[] Rays)[]
        {
            ("original-grazing-hit", before, Grazing),
            ("original-clear", before, [Clear]),
            ("edited-grazing-hit", after, Grazing[1..]),
            ("edited-added-hit", after, [Added]),
            ("edited-removed-clear", after, [Grazing[0]]),
            ("edited-seam-excluded", after, [Seam])
        };

        var latency = work.Select(x => Measure(x.Name, x.Query, x.Rays, samples)).ToArray();
        var originalMesh = Marching.Extract(original, tables);
        var editedMesh = Marching.Extract(edited, tables, trackSources: true);
        var originalChecksum = Audit.Digest(originalMesh);
        var editedChecksum = Audit.Digest(editedMesh);
        if (originalChecksum == editedChecksum)
            throw new InvalidOperationException("The edited collision snapshot did not change the mesh.");

        if (edited.CollisionSpheres.Length != 2 || !original.CollisionSpheres.IsEmpty)
            throw new InvalidOperationException("Expected two independent seam primitives and none in the old snapshot.");
        var originalQuery = new CollisionRayQuery(original, originalMesh);
        var editedQuery = new CollisionRayQuery(edited, editedMesh);
        var rays = Grazing.Concat([Added, Seam, Straddling, Cancelled, Clear]).ToArray();
        var originalCases = rays.Select(ray => Check(original, originalQuery, ray, false)).ToArray();
        var editedCases = rays.Select(ray => Check(edited, editedQuery, ray, ray == Seam || ray == Straddling)).ToArray();
        var cutRays = new[]
        {
            new Ray("subtractive-cut-forward", new(23.173f, 19.819f, 0), Vector3.UnitZ),
            new Ray("subtractive-cut-reverse", new(23.173f, 19.819f, 32), -Vector3.UnitZ)
        };
        var cutCases = cutRays.Select(ray => CheckCut(originalQuery, edited, editedQuery, ray)).ToArray();
        var sphereCases = CheckEditedSweeps(original, originalMesh, edited, editedMesh);
        var openingCases = CheckOpeningSweeps(edited, editedMesh);
        var editedGeometry = GeometryProbe.Check(editedMesh, edited, "bridge", "face");

        var offset = Vector3.UnitX * .21f;
        DensityEdit[] overlapEdits =
        [
            edits[0] with { Center = edits[0].Center - offset },
            edits[0] with { Center = edits[0].Center + offset },
            .. edits[1..]
        ];
        var overlapping = Create(overlapEdits);
        var overlapMesh = Marching.Extract(overlapping, tables, trackSources: true);
        if (overlapping.OpeningCuts.Length != 2 || !overlapping.CollisionCuts.IsEmpty)
            throw new InvalidOperationException("Overlapping cuts must bypass the isolated-cut shortcut.");
        var overlapOpeningCases = CheckOpeningSweeps(overlapping, overlapMesh, overlappingCuts: true);
        var overlapQuery = new CollisionRayQuery(overlapping, overlapMesh);
        var overlapRay = CheckOverlapRay(overlapping, overlapQuery);
        var overlapNearMiss = CheckOverlapNearMiss(overlapping, overlapQuery);
        var overlapGeometry = GeometryProbe.Check(overlapMesh, overlapping, "bridge", "face");
        var mixedEdits = CheckMixedEdits(tables, edits);
        var largerEditGroups = CheckLargerEditGroups(tables, edits);
        var upperEditGroups = CheckUpperEditGroups(tables, edits);
        var added = after.Trace(Added.Origin, Added.Direction);
        if (added.Hits.Count != 2 || MathF.Abs(added.Hits[0] - 24.2f) > .0001f ||
            MathF.Abs(added.Hits[1] - 24.4f) > .0001f ||
            before.Trace(Added.Origin, Added.Direction).Hits.Count != 0 ||
            before.Trace(Grazing[0].Origin, Grazing[0].Direction).Hits.Count != 2 ||
            after.Trace(Grazing[0].Origin, Grazing[0].Direction).Hits.Count != 0)
            throw new InvalidOperationException("Closed-form edit or snapshot control failed.");

        // Exercise the fail-closed budget explicitly using a zero field.
        var bounded = new GrazingRayFallback(_ => 0, 32).Trace(Clear.Origin, Clear.Direction);
        if (!bounded.Exhausted || bounded.Hits.Count != 0 || bounded.Samples != 4096)
            throw new InvalidOperationException("Fallback budget control failed.");

        return new
        {
            samplesPerCase = samples, stopwatchFrequencyHz = Stopwatch.Frequency,
            latency, originalChecksum, editedChecksum, edits, originalCases, editedCases, cutCases, sphereCases, openingCases, editedGeometry,
            collisionSpheres = edited.CollisionSpheres.ToArray(),
            overlapEdits, overlapOpeningCases, overlapRay, overlapNearMiss, overlapGeometry, overlapChecksum = Audit.Digest(overlapMesh), mixedEdits, largerEditGroups, upperEditGroups, spatialEditGroups, seamEditGroups, flatSeamEditGroups, edgeSeamEditGroups, cancelledBrushPair,
            budgetControl = new { bounded.Exhausted, bounded.Samples }
        };
    }

    private static Grid Create(IReadOnlyList<DensityEdit> edits) =>
        new(32, "bridge", "face", false, false, false, true, false, false, edits);

    private static object Check(Grid grid, CollisionRayQuery query, Ray ray, bool requirePrimitive)
    {
        var expected = GeometryProbe.DensityHits(grid, ray.Origin, ray.Direction);
        var result = query.Trace(ray.Origin, ray.Direction);
        var actual = result.Hits;
        var error = expected.Count == actual.Count
            ? expected.Zip(actual, (a, b) => MathF.Abs(a - b)).DefaultIfEmpty().Max() : (float?)null;
        if (result.Exhausted || error is null || error > .001f ||
            (requirePrimitive && (result.PrimitiveCrossings != 2 || result.MeshHits.Count != 0 ||
                result.Samples != 0 || result.UsedFallback)))
            throw new InvalidOperationException($"Edited ray check failed for {ray.Name}.");
        return new { ray.Name, ray.Origin, ray.Direction, expected, meshHits = result.MeshHits, actual,
            result.Samples, result.PrimitiveCrossings, result.UsedFallback, error };
    }

    private static object CheckCut(CollisionRayQuery original, Grid edited, CollisionRayQuery query, Ray ray)
    {
        var before = original.Trace(ray.Origin, ray.Direction);
        var actual = query.Trace(ray.Origin, ray.Direction);
        var expected = GeometryProbe.DensityHits(edited, ray.Origin, ray.Direction);
        if (before.Hits.Count != 2 || before.RepairedIntervals != 0 || actual.MeshHits.Count != 2 ||
            actual.Hits.Count != 4 || actual.RepairedIntervals != 1 || actual.Exhausted ||
            actual.UsedFallback || actual.PrimitiveCrossings != 0 || expected.Count != 4 ||
            MathF.Abs(expected[1] - actual.Hits[1]) > .001f ||
            MathF.Abs(expected[2] - actual.Hits[2]) > .001f)
            throw new InvalidOperationException($"Subtractive interval regression for {ray.Name}.");
        return new { ray.Name, ray.Origin, ray.Direction, expected, originalHits = before.Hits,
            meshHits = actual.MeshHits, actual = actual.Hits, actual.RepairedIntervals };
    }

    private static object[] CheckEditedSweeps(Grid original, Mesh originalMesh, Grid edited, Mesh editedMesh)
    {
        var cases = new List<(SphereSweep.SweepCase Sweep, string State, float? ExpectedDistance)>();
        foreach (var sphere in edited.CollisionSpheres)
        foreach (var radius in new[] { .15f, .35f })
        {
            var left = new Vector3(14, sphere.Center.Y, sphere.Center.Z);
            var right = new Vector3(18, sphere.Center.Y, sphere.Center.Z);
            var contactRadius = sphere.Radius + radius;
            cases.Add((new("seam-addition-forward", left, right, radius), "hit", sphere.Center.X - 14 - contactRadius));
            cases.Add((new("seam-addition-reverse", right, left, radius), "hit", 18 - sphere.Center.X - contactRadius));
            var clearOffset = Vector3.UnitZ * .6f;
            cases.Add((new("seam-addition-clear", left + clearOffset, right + clearOffset, radius), "clear", null));
            cases.Add((new("seam-addition-overlap", sphere.Center, right, radius), "initial-overlap", 0));
        }
        var cutCenter = new Vector3(23.17f, 20.173f, 16.819f);
        var centerLine = new Vector3(23.173f, 19.819f, cutCenter.Z);
        var radialSquared = Vector3.DistanceSquared(cutCenter, centerLine);
        foreach (var radius in new[] { .015f, .03f })
        {
            var eroded = .4f - radius;
            var axial = MathF.Sqrt(eroded * eroded - radialSquared);
            var travel = Vector3.UnitZ * .25f;
            cases.Add((new("narrow-cut-forward", centerLine, centerLine + travel, radius), "hit", axial));
            cases.Add((new("narrow-cut-reverse", centerLine, centerLine - travel, radius), "hit", axial));
            var shortTravel = Vector3.UnitZ * .04f;
            cases.Add((new("narrow-cut-clear", centerLine - shortTravel, centerLine + shortTravel, radius), "clear", null));
        }
        cases.Add((new("removed-cap-clear", new(23.03f, 20.173f, 16.819f),
            new(23.31f, 20.173f, 16.819f), .1f), "clear", null));
        return cases.Select(test =>
        {
            var path = test.Sweep;
            var reference = SphereSweep.TraceDensity(edited, path);
            var actual = SphereSweep.TraceCollision(edited, editedMesh, path);
            var meshOnly = SphereSweep.TraceMesh(editedMesh, path);
            var old = SphereSweep.TraceCollision(original, originalMesh, path);
            var oldReference = SphereSweep.TraceDensity(original, path);
            if (reference.State != test.State || actual.State != test.State ||
                (test.ExpectedDistance is { } distance &&
                    (actual.Distance is not { } contact || MathF.Abs(contact - distance) > .001f ||
                     reference.Distance is not { } expected || MathF.Abs(expected - distance) > .001f)) ||
                (path.Group == "removed-cap-clear" && (old.State != "initial-overlap" || oldReference.State != old.State)) ||
                (path.Group.StartsWith("narrow-cut", StringComparison.Ordinal) &&
                    (meshOnly.State != "initial-overlap" || old.State != "initial-overlap")) ||
                (path.Group.StartsWith("seam-addition", StringComparison.Ordinal) &&
                    (meshOnly.State != "clear" || old.State != "clear")))
                throw new InvalidOperationException($"Edited sphere check failed for {path.Group}: {actual}, {reference}, old {old}.");
            return (object)new { path, reference, actual, meshOnly, old, oldReference, test.ExpectedDistance };
        }).ToArray();
    }
    private static object[] CheckOpeningSweeps(Grid grid, Mesh mesh, bool overlappingCuts = false)
    {
        var first = grid.OpeningCuts[0];
        var second = overlappingCuts ? grid.OpeningCuts[1] : first;
        var halfSeparation = Vector3.Distance(first.Center, second.Center) / 2;
        var cut = new CollisionSphere((first.Center + second.Center) / 2,
            MathF.Sqrt(first.Radius * first.Radius - halfSeparation * halfSeparation));
        var terrain = Grid.BridgeRight;
        var separation = Vector3.Distance(cut.Center, terrain.Center);
        var outward = (cut.Center - terrain.Center) / separation;
        var lateral = overlappingCuts ? Vector3.Normalize(Vector3.Cross(Vector3.UnitX, outward)) : Vector3.UnitX;
        var rimAxial = ((terrain.Radius - separation) * (terrain.Radius + separation) - cut.Radius * cut.Radius) /
            (2 * separation);
        var rimSide = MathF.Sqrt(cut.Radius * cut.Radius - rimAxial * rimAxial);
        var rim = new Vector2(rimAxial, rimSide);
        var cases = new List<(SphereSweep.SweepCase Sweep, float? ExpectedDistance)>();
        foreach (var radius in new[] { .08f, .16f })
        {
            var outside = Point(.62f, 0);
            var inside = Point(-.16f, 0);
            // Along the symmetry axis the eroded cut and the exterior of the
            // expanded terrain sphere overlap, proving a free passage.
            if (terrain.Radius + radius - separation >= cut.Radius - radius ||
                .16f >= cut.Radius - radius)
                throw new InvalidOperationException("Opening clear path is not certified.");
            cases.Add((new("opening-enter-clear", outside, inside, radius), null));
            cases.Add((new("opening-exit-clear", inside, outside, radius), null));
        }
        var scale = cut.Radius / first.Radius;
        AddRim("opening-enter-rim", new Vector2(.52f, .34f) * scale,
            new Vector2(-.16f, .34f) * scale, .1f * scale);
        AddRim("opening-exit-rim", new Vector2(-.05f, .20f) * scale,
            new Vector2(.27f, .72f) * scale, .1f * scale);

        return cases.Select(test =>
        {
            var actual = SphereSweep.TraceCollision(grid, mesh, test.Sweep);
            var meshOnly = SphereSweep.TraceMesh(mesh, test.Sweep);
            var rawField = SphereSweep.TraceDensity(grid, test.Sweep);
            var expectedState = test.ExpectedDistance is null ? "clear" : "hit";
            if (actual.State != expectedState ||
                (test.ExpectedDistance is { } distance &&
                    (actual.Distance is not { } contact || MathF.Abs(contact - distance) > .001f)))
                throw new InvalidOperationException($"Opening sweep failed for {test.Sweep.Group}: {actual}, expected {test.ExpectedDistance}.");
            return (object)new { path = test.Sweep, expectedState, test.ExpectedDistance, actual, meshOnly, rawField };
        }).ToArray();

        Vector3 Point(float axial, float side) => cut.Center + axial * outward + side * lateral;

        void AddRim(string label, Vector2 start, Vector2 end, float radius)
        {
            // In this meridian the nearest rim point is fixed. Solve for
            // first intersection of the center line with its radius circle.
            var delta = end - start;
            var direction = Vector2.Normalize(delta);
            var offset = rim - start;
            var projected = Vector2.Dot(offset, direction);
            var perpendicular = (offset - projected * direction).LengthSquared();
            var distance = projected - MathF.Sqrt(radius * radius - perpendicular);
            if (!float.IsFinite(distance) || distance <= 0 || distance >= delta.Length())
                throw new InvalidOperationException("Opening rim control has no contact.");
            cases.Add((new(label, Point(start.X, start.Y), Point(end.X, end.Y), radius), distance));
        }
    }

    private static object CheckOverlapRay(Grid grid, CollisionRayQuery query)
    {
        var origin = new Vector3(23.17f, 19.873f, 0);
        var expected = GeometryProbe.DensityHits(grid, origin, Vector3.UnitZ);
        var actual = query.Trace(origin, Vector3.UnitZ);
        if (expected.Count != 4 || actual.Hits.Count != 4 || actual.Exhausted ||
            MathF.Abs(expected[1] - actual.Hits[1]) > .001f ||
            MathF.Abs(expected[2] - actual.Hits[2]) > .001f)
            throw new InvalidOperationException("Overlapping subtraction ray failed.");
        return new { origin, expected, actual = actual.Hits, actual.MeshHits, actual.RepairedIntervals };
    }

    private static object CheckOverlapNearMiss(Grid grid, CollisionRayQuery query)
    {
        var origin = new Vector3(23.173f, 19.819f, 0);
        var expected = GeometryProbe.DensityHits(grid, origin, Vector3.UnitZ);
        var actual = query.Trace(origin, Vector3.UnitZ);
        if (expected.Count != 2 || actual.MeshHits.Count != 4 || actual.Hits.Count != 2 ||
            actual.RepairedIntervals != 1 || actual.Exhausted)
            throw new InvalidOperationException("Overlapping subtraction produced a spurious gap.");
        return new { origin, expected, actual = actual.Hits, actual.MeshHits, actual.RepairedIntervals };
    }

    private static object[] CheckMixedEdits(Tables tables, DensityEdit[] edits)
    {
        var cut = edits[0];
        var terrain = Grid.BridgeRight;
        var separation = Vector3.Distance(cut.Center, terrain.Center);
        var outward = (cut.Center - terrain.Center) / separation;
        var addition = new DensityEdit(cut.Center + .16f * outward, .30f, true);
        return new[] { false, true }.Select(addLast =>
        {
            DensityEdit[] ordered = addLast ? [cut, addition, .. edits[1..]] : [addition, cut, .. edits[1..]];
            var grid = Create(ordered);
            var mesh = Marching.Extract(grid, tables, trackSources: true);
            if (grid.OpeningCuts.Length != 1 || !grid.CollisionCuts.IsEmpty)
                throw new InvalidOperationException("Mixed edit must bypass the isolated-cut shortcut.");

            var cases = new List<(SphereSweep.SweepCase Path, float? Distance)>();
            foreach (var radius in new[] { .04f, .08f })
            {
                cases.Add((new("mixed-axis-enter", Point(.62f, 0), Point(-.30f, 0), radius), .62f - .46f - radius));
                var firstSolid = addLast ? -.14f : cut.Radius;
                cases.Add((new("mixed-axis-exit", Point(-.30f, 0), Point(.62f, 0), radius), firstSolid + .30f - radius));
            }
            const float side = .34f;
            const float smallRadius = .025f;
            var freeAxial = MathF.Sqrt(MathF.Pow(cut.Radius - smallRadius, 2) - side * side);
            if (side <= addition.Radius + smallRadius || freeAxial <= .10f ||
                freeAxial <= MathF.Sqrt(MathF.Pow(terrain.Radius + smallRadius, 2) - side * side) - separation)
                throw new InvalidOperationException("Mixed bypass cannot be certified.");
            cases.Add((new("mixed-bypass-enter", Point(.62f, side), Point(-.10f, side), smallRadius), null));
            cases.Add((new("mixed-bypass-exit", Point(-.10f, side), Point(.62f, side), smallRadius), null));

            const float rimAxialPath = .23f;
            const float rimStartSide = .10f;
            const float rimBody = .085f;
            const float brushSeparation = .16f;
            var rimAxial = (cut.Radius * cut.Radius - addition.Radius * addition.Radius +
                brushSeparation * brushSeparation) / (2 * brushSeparation);
            var rimSide = MathF.Sqrt(cut.Radius * cut.Radius - rimAxial * rimAxial);
            var rimDistance = rimSide - MathF.Sqrt(rimBody * rimBody - MathF.Pow(rimAxial - rimAxialPath, 2)) - rimStartSide;
            cases.Add((new("mixed-intersection-rim", Point(rimAxialPath, rimStartSide),
                Point(rimAxialPath, .4f), rimBody), addLast ? 0 : rimDistance));

            var sweeps = cases.Select(test =>
            {
                var actual = SphereSweep.TraceCollision(grid, mesh, test.Path);
                var expectedState = test.Distance switch { null => "clear", 0 => "initial-overlap", _ => "hit" };
                if (actual.State != expectedState || test.Distance is { } distance &&
                    (actual.Distance is not { } hit || MathF.Abs(hit - distance) > .001f))
                    throw new InvalidOperationException($"Mixed edit {addLast}, {test.Path.Group}: {actual}, expected {test.Distance}.");
                return new { test.Path, expectedState, expectedDistance = test.Distance, actual,
                    meshOnly = SphereSweep.TraceMesh(mesh, test.Path), rawField = SphereSweep.TraceDensity(grid, test.Path) };
            }).ToArray();

            var origin = Point(-12, 0);
            var query = new CollisionRayQuery(grid, mesh);
            var ray = query.Trace(origin, outward);
            var expected = GeometryProbe.DensityHits(grid, origin, outward);
            if (expected.Count != 4 || ray.Hits.Count != 4 || ray.Exhausted ||
                MathF.Abs(expected[1] - ray.Hits[1]) > .001f ||
                MathF.Abs(expected[2] - ray.Hits[2]) > .001f ||
                MathF.Abs(expected[3] - ray.Hits[3]) > .001f)
                throw new InvalidOperationException($"Mixed edit ray failed for order {addLast}.");
            return (object)new { addLast, edits = ordered, sweeps,
                ray = new { origin, direction = outward, expected, actual = ray.Hits, ray.MeshHits, ray.RepairedIntervals },
                checksum = Audit.Digest(mesh), geometry = GeometryProbe.Check(mesh, grid, "bridge", "face") };
        }).ToArray();

        Vector3 Point(float axial, float side) => cut.Center + axial * outward + side * Vector3.UnitX;
    }

    private static object[] CheckLargerEditGroups(Tables tables, DensityEdit[] edits)
    {
        var cut = edits[0];
        var terrain = Grid.BridgeRight;
        var separation = Vector3.Distance(cut.Center, terrain.Center);
        var outward = (cut.Center - terrain.Center) / separation;
        var add = new DensityEdit(cut.Center + .16f * outward, .30f, true);
        var trim = new DensityEdit(cut.Center + .38f * outward, .18f, false);
        var restore = new DensityEdit(cut.Center + .40f * outward, .12f, true);
        var groups = new (string Name, DensityEdit[] Ordered, bool RestoreLast)[]
        {
            ("three-brushes", [cut, add, trim], false),
            ("four-restore-last", [cut, add, trim, restore], true),
            ("four-trim-last", [cut, add, restore, trim], false)
        };

        return groups.Select(group =>
        {
            DensityEdit[] ordered = [.. group.Ordered, .. edits[1..]];
            var grid = Create(ordered);
            var mesh = Marching.Extract(grid, tables, trackSources: true);
            if (grid.OpeningCuts.Length != 2 || !grid.CollisionCuts.IsEmpty)
                throw new InvalidOperationException("Larger group must bypass the isolated-cut shortcut.");

            var cases = new List<(SphereSweep.SweepCase Path, float? Distance)>();
            foreach (var radius in new[] { .04f, .08f })
            {
                var firstSolid = group.RestoreLast ? .52f : .20f;
                cases.Add((new("group-axis-enter", Point(.68f, 0), Point(-.30f, 0), radius), .68f - firstSolid - radius));
                cases.Add((new("group-axis-exit", Point(-.30f, 0), Point(.68f, 0), radius), .16f - radius));
            }
            cases.Add((new("group-bypass-enter", Point(.68f, .34f), Point(-.10f, .34f), .025f), null));
            cases.Add((new("group-bypass-exit", Point(-.10f, .34f), Point(.68f, .34f), .025f), null));

            const float circleSeparation = .22f;
            var circleAxial = .16f + (add.Radius * add.Radius - trim.Radius * trim.Radius +
                circleSeparation * circleSeparation) / (2 * circleSeparation);
            var circleSide = MathF.Sqrt(add.Radius * add.Radius - MathF.Pow(circleAxial - .16f, 2));
            const float rimBody = .085f;
            var rimDistance = circleSide - MathF.Sqrt(rimBody * rimBody - MathF.Pow(.45f - circleAxial, 2)) - .10f;
            cases.Add((new("group-intersection-rim", Point(.45f, .10f), Point(.45f, .24f), rimBody),
                group.RestoreLast ? 0 : rimDistance));

            var sweeps = cases.Select(test =>
            {
                var actual = SphereSweep.TraceCollision(grid, mesh, test.Path);
                var expectedState = test.Distance switch { null => "clear", 0 => "initial-overlap", _ => "hit" };
                if (actual.State != expectedState || test.Distance is { } distance &&
                    (actual.Distance is not { } hit || MathF.Abs(hit - distance) > .001f))
                    throw new InvalidOperationException($"{group.Name}, {test.Path.Group}: {actual}, expected {test.Distance}.");
                return new { test.Path, expectedState, expectedDistance = test.Distance, actual,
                    meshOnly = SphereSweep.TraceMesh(mesh, test.Path) };
            }).ToArray();

            var origin = Point(-12, 0);
            var ray = new CollisionRayQuery(grid, mesh).Trace(origin, outward);
            var expected = GeometryProbe.DensityHits(grid, origin, outward);
            float[] editedCrossings = group.RestoreLast ? [11.60f, 11.86f, 12.20f, 12.28f, 12.52f] :
                [11.60f, 11.86f, 12.20f];
            if (expected.Count != editedCrossings.Length + 1 || ray.Hits.Count != expected.Count || ray.Exhausted ||
                editedCrossings.Where((crossing, index) => MathF.Abs(expected[index + 1] - crossing) > .001f ||
                    MathF.Abs(ray.Hits[index + 1] - crossing) > .001f).Any())
                throw new InvalidOperationException($"Large edit group ray failed for {group.Name}.");

            return (object)new { group.Name, brushCount = group.Ordered.Length, edits = ordered, sweeps,
                ray = new { origin, direction = outward, expected, actual = ray.Hits, ray.MeshHits, ray.RepairedIntervals },
                checksum = Audit.Digest(mesh), geometry = GeometryProbe.Check(mesh, grid, "bridge", "face") };
        }).ToArray();

        Vector3 Point(float axial, float side) => cut.Center + axial * outward + side * Vector3.UnitX;
    }

    private static object CheckCancelledBrushPair(Tables tables, DensityEdit[] edits)
    {
        var cut = edits[0];
        var outward = Vector3.Normalize(cut.Center - Grid.BridgeRight.Center);
        var add = new DensityEdit(cut.Center + .16f * outward, .30f, true);
        var trim = new DensityEdit(cut.Center + .38f * outward, .18f, false);
        var restore = new DensityEdit(cut.Center + .40f * outward, .12f, true);
        var cancelled = new DensityEdit(cut.Center + .59f * outward, .04f, true);
        var grid = Create([cut, add, trim, restore, cancelled, cancelled with { Add = false }, .. edits[1..]]);
        var mesh = Marching.Extract(grid, tables);
        var path = new SphereSweep.SweepCase("cancelled-shell", cut.Center + .72f * outward,
            cut.Center - .30f * outward, .02f);
        var actual = SphereSweep.TraceCollision(grid, mesh, path);
        const float expectedDistance = .18f;
        if (actual.State != "hit" || actual.Distance is not { } hit || MathF.Abs(hit - expectedDistance) > .001f)
            throw new InvalidOperationException($"Cancelled brush shell contact: {actual}, expected {expectedDistance}.");
        var origin = cut.Center - 12 * outward;
        var ray = new CollisionRayQuery(grid, mesh).Trace(origin, outward);
        var expected = GeometryProbe.DensityHits(grid, origin, outward);
        if (expected.Count != 6 || ray.Hits.Count != 6 || ray.Exhausted ||
            MathF.Abs(ray.Hits[^1] - 12.52f) > .001f)
            throw new InvalidOperationException("Cancelled brush shell produced a spurious ray hit.");
        return new { path, actual, expectedDistance, meshOnly = SphereSweep.TraceMesh(mesh, path),
            ray = new { origin, expected, actual = ray.Hits } };
    }

    private static object[] CheckUpperEditGroups(Tables tables, DensityEdit[] edits, bool spatial = false, bool atSeam = false, bool planar = false, bool atEdge = false)
    {
        var terrain = atSeam ? Grid.SeamOpeningTerrain : Grid.BridgeRight;
        var cut = atSeam ? edits[0] with { Center = new Vector3(16, atEdge ? Grid.SeamFlatSide - .16f : 16, 16) } : edits[0];
        var outward = atSeam ? Vector3.UnitX : Vector3.Normalize(cut.Center - terrain.Center);
        var lateral = atSeam ? Vector3.UnitY : Vector3.UnitX;
        var add = new DensityEdit(cut.Center + .16f * outward, .30f, true);
        var trim = new DensityEdit(cut.Center + .38f * outward, .18f, false);
        var restore = new DensityEdit(cut.Center + .40f * outward, .12f, true);
        var trimRestore = new DensityEdit(cut.Center + .46f * outward, .095f, false);
        var outerAdd = new DensityEdit(cut.Center + .49f * outward, .07f, true);
        var outerTrim = new DensityEdit(cut.Center + .52f * outward, .065f, false);
        var tip = new DensityEdit(cut.Center + .56f * outward, .045f, true);
        var transverse = Vector3.Normalize(Vector3.Cross(outward, lateral));
        if (spatial)
        {
            add = Offset(add, .021f, -.018f);
            trim = Offset(trim, -.014f, .017f);
            restore = Offset(restore, .026f, .011f);
            trimRestore = Offset(trimRestore, .026f, .011f);
            outerAdd = Offset(outerAdd, -.020f, .018f);
            outerTrim = Offset(outerTrim, -.020f, .018f);
            tip = Offset(tip, .013f, -.019f);
            var volume = Vector3.Dot(add.Center - cut.Center,
                Vector3.Cross(trim.Center - cut.Center, restore.Center - cut.Center));
            if (MathF.Abs(volume) < 1e-5f)
                throw new InvalidOperationException("Spatial brushes must span three dimensions.");
        }
        DensityEdit[] brushes = [cut, add, trim, restore, trimRestore, outerAdd, outerTrim, tip];
        var groups = new (int Count, float FirstSolid, float[] AxialCrossings)[]
        {
            (5, .365f, [11.60f, 11.86f, 12.20f, 12.28f, 12.365f]),
            (6, .56f, [11.60f, 11.86f, 12.20f, 12.28f, 12.365f, 12.42f, 12.56f]),
            (7, .455f, [11.60f, 11.86f, 12.20f, 12.28f, 12.365f, 12.42f, 12.455f]),
            (8, .605f, [11.60f, 11.86f, 12.20f, 12.28f, 12.365f, 12.42f, 12.455f, 12.515f, 12.605f])
        };

        return groups.Select(group =>
        {
            DensityEdit[] ordered = atSeam ? brushes[..group.Count] : [.. brushes[..group.Count], .. edits[1..]];
            var fixture = atEdge ? "seam-edge-opening" : planar ? "seam-flat-opening" : atSeam ? "seam-opening" : "bridge";
            var grid = atSeam ? new Grid(32, fixture, "face", false, false, false, true, false, false, ordered) :
                Create(ordered);
            var mesh = Marching.Extract(grid, tables, trackSources: true);
            if (!grid.CollisionCuts.IsEmpty)
                throw new InvalidOperationException("Upper edit group used isolated-cut shortcut.");

            var cases = new List<(SphereSweep.SweepCase Path, float? Distance)>();
            var entryOffset = spatial ? Lateral(brushes[group.Count - 1]) : Vector3.Zero;
            var exitOffset = spatial ? Lateral(add) : Vector3.Zero;
            var bypass = spatial ? -.35f : .34f;
            foreach (var radius in new[] { .02f, .04f })
            {
                cases.Add((new("upper-axis-enter", Point(.72f, 0) + entryOffset, Point(-.30f, 0) + entryOffset, radius),
                    .72f - group.FirstSolid - radius));
                cases.Add((new("upper-axis-exit", Point(-.30f, 0) + exitOffset, Point(.72f, 0) + exitOffset, radius), .16f - radius));
            }
            cases.Add((new("upper-bypass-enter", Point(.72f, bypass), Point(-.10f, bypass), .025f), null));
            cases.Add((new("upper-bypass-exit", Point(-.10f, bypass), Point(.72f, bypass), .025f), null));

            if (spatial && group.Count == 5)
            {
                const float separation = .06f;
                const float radius = .025f;
                const float axial = .49f;
                var along = (restore.Radius * restore.Radius - trimRestore.Radius * trimRestore.Radius +
                    separation * separation) / (2 * separation);
                var circleRadius = MathF.Sqrt(restore.Radius * restore.Radius - along * along);
                var contact = circleRadius - MathF.Sqrt(radius * radius - MathF.Pow(axial - .40f - along, 2)) - .04f;
                var offset = Lateral(restore);
                cases.Add((new("spatial-trim-rim", Point(axial, .04f) + offset,
                    Point(axial, .11f) + offset, radius), contact));
            }

            if (atSeam)
            {
                if (mesh.TransitionTriangles == 0 || cases.Take(6).Any(test =>
                    (test.Path.Start.X < 16) == (test.Path.End.X < 16)))
                    throw new InvalidOperationException("Spatial sweeps must cross an active 2:1 plane.");
                const float radius = .025f;
                const float side = -.35f;
                var wall = MathF.Sqrt(MathF.Pow(cut.Radius - radius, 2) - side * side);
                cases.Add((new("seam-crossing-inner-wall", Point(.72f, side), Point(-.30f, side), radius), .72f + wall));
            }

            if (planar)
            {
                const float radius = .04f;
                var side = atEdge ? -.365f : .365f;
                var face = Grid.SeamFlatFront - cut.Center.X;
                var openingRadius = MathF.Sqrt(cut.Radius * cut.Radius - face * face);
                var delta = openingRadius - MathF.Abs(side);
                var contact = .72f - face - MathF.Sqrt(radius * radius - delta * delta);
                cases.Add((new("flat-face-rim", Point(.72f, side), Point(-.12f, side), radius), contact));
            }

            if (atEdge)
            {
                var edge = new Vector3(Grid.SeamFlatFront, Grid.SeamFlatSide, cut.Center.Z);
                var untouched = edge + Vector3.UnitZ * .7f;
                const float radius = .06f;
                var cornerPath = new SphereSweep.SweepCase("box-edge-diagonal",
                    untouched + new Vector3(.27f, .27f, 0),
                    untouched - new Vector3(.20f, .20f, 0), radius);
                cases.Add((cornerPath, .27f * MathF.Sqrt(2) - radius));

                // The two plane projections are outside the other face.
                // Contact is at the endpoint of the cut along the box edge.
                var transverse = edge - cut.Center;
                var cutEnd = MathF.Sqrt(cut.Radius * cut.Radius - transverse.LengthSquared());
                const float endRadius = .055f;
                const float offsetX = .03f, offsetY = .025f, startZ = .31f;
                var reach = MathF.Sqrt(endRadius * endRadius - offsetX * offsetX - offsetY * offsetY);
                var pathStart = edge + new Vector3(offsetX, offsetY, startZ);
                var pathEnd = edge + new Vector3(offsetX, offsetY, .48f);
                cases.Add((new("box-edge-cut-endpoint", pathStart, pathEnd, endRadius), cutEnd - reach - startZ));
            }

            var sweeps = cases.Select(test =>
            {
                var actual = SphereSweep.TraceCollision(grid, mesh, test.Path);
                var expectedState = test.Distance.HasValue ? "hit" : "clear";
                if (actual.State != expectedState || test.Distance is { } distance &&
                    (actual.Distance is not { } hit || MathF.Abs(hit - distance) > .001f))
                    throw new InvalidOperationException($"Brush count {group.Count}, {test.Path.Group}: {actual}, expected {test.Distance}.");
                return new { test.Path, expectedState, expectedDistance = test.Distance, actual,
                    meshOnly = SphereSweep.TraceMesh(mesh, test.Path) };
            }).ToArray();

            var origin = Point(-12, 0);
            var ray = new CollisionRayQuery(grid, mesh).Trace(origin, outward);
            var expected = GeometryProbe.DensityHits(grid, origin, outward);
            var slab = planar ? (Grid.SeamFlatBack - origin.X, Grid.SeamFlatFront - origin.X) : ((float, float)?)null;
            var analyticCrossings = spatial ? OrderedAxisCrossings(origin, outward, terrain, brushes[..group.Count], slab) : null;
            var editedCrossings = analyticCrossings is null ? group.AxialCrossings : analyticCrossings[1..];
            if (expected.Count != editedCrossings.Length + 1 || ray.Hits.Count != expected.Count || ray.Exhausted ||
                editedCrossings.Where((crossing, index) => MathF.Abs(expected[index + 1] - crossing) > .001f ||
                    MathF.Abs(ray.Hits[index + 1] - crossing) > .001f).Any())
                throw new InvalidOperationException($"Upper group ray failed for {group.Count}: reference {expected.Count}, query {ray.Hits.Count}.");

            object? seamGrazing = null;
            if (atSeam && !planar)
            {
                var grazingOrigin = new Vector3(15.819f, 0, 17.173f);
                var grazing = new CollisionRayQuery(grid, mesh).Trace(grazingOrigin, Vector3.UnitY);
                var grazingReference = OrderedAxisCrossings(grazingOrigin, Vector3.UnitY, terrain, brushes[..group.Count]);
                if (grazingReference.Length != 2 || grazing.MeshHits.Count != 0 || grazing.Hits.Count != 2 ||
                    grazing.RepairedIntervals == 0 || grazing.Exhausted ||
                    grazingReference.Where((hit, index) => MathF.Abs(hit - grazing.Hits[index]) > .001f).Any())
                    throw new InvalidOperationException($"Seam grazing ray failed for {group.Count} brushes.");
                seamGrazing = new { origin = grazingOrigin, expected = grazingReference, actual = grazing.Hits };
            }

            object? flatFace = null;
            if (planar)
            {
                var faceOrigin = new Vector3(0, 17.173f, 16.319f);
                var face = new CollisionRayQuery(grid, mesh).Trace(faceOrigin, Vector3.UnitX);
                float[] faceReference = [Grid.SeamFlatBack, Grid.SeamFlatFront];
                if (face.Hits.Count != 2 || face.Exhausted ||
                    faceReference.Where((hit, index) => MathF.Abs(hit - face.Hits[index]) > .001f).Any())
                    throw new InvalidOperationException($"Unedited flat face ray failed for {group.Count} brushes.");
                flatFace = new { origin = faceOrigin, expected = faceReference, actual = face.Hits };
            }

            Func<Vector3, Vector3, List<float>>? edgeReference = null;
            object? thinEdgeRay = null;
            if (atEdge)
            {
                edgeReference = (start, direction) =>
                    OrderedAxisCrossings(start, direction, terrain, ordered, BoxInterval(start, direction)).ToList();
                var thinOrigin = new Vector3(15.819f, 0, 15.673f);
                var thin = new CollisionRayQuery(grid, mesh).Trace(thinOrigin, Vector3.UnitY);
                var exact = edgeReference(thinOrigin, Vector3.UnitY);
                var sampled = GeometryProbe.DensityHits(grid, thinOrigin, Vector3.UnitY);
                if (exact.Count != 4 || sampled.Count != 2 || thin.Hits.Count != 4 || thin.Exhausted ||
                    MathF.Abs(exact[2] - thin.Hits[2]) > .001f || MathF.Abs(exact[3] - thin.Hits[3]) > .001f)
                    throw new InvalidOperationException($"Thin box-edge ray failed for {group.Count} brushes.");
                thinEdgeRay = new { origin = thinOrigin, exact, sampled, actual = thin.Hits };
            }

            return (object)new { brushCount = group.Count, spatial, atSeam, planar, atEdge, edits = ordered, sweeps, flatFace, thinEdgeRay,
                transitionTriangles = mesh.TransitionTriangles, seamGrazing,
                ray = new { origin, direction = outward, expected, analyticCrossings, actual = ray.Hits, ray.MeshHits, ray.RepairedIntervals },
                checksum = Audit.Digest(mesh), geometry = GeometryProbe.Check(mesh, grid, fixture, "face", edgeReference) };
        }).ToArray();

        Vector3 Point(float axial, float side) => cut.Center + axial * outward + side * lateral;

        DensityEdit Offset(DensityEdit edit, float x, float side) =>
            edit with { Center = edit.Center + x * lateral + side * transverse };

        Vector3 Lateral(DensityEdit edit)
        {
            var delta = edit.Center - cut.Center;
            return delta - Vector3.Dot(delta, outward) * outward;
        }
    }

    private static (float Start, float End) BoxInterval(Vector3 origin, Vector3 direction)
    {
        var min = Grid.SeamFlatMin;
        var max = Grid.SeamFlatMax;
        var start = 0f;
        var end = 32f;
        for (var axis = 0; axis < 3; axis++)
        {
            var component = direction[axis];
            if (component == 0)
            {
                if (origin[axis] <= min[axis] || origin[axis] >= max[axis])
                    return (0, 0);
                continue;
            }
            var a = (min[axis] - origin[axis]) / component;
            var b = (max[axis] - origin[axis]) / component;
            start = Math.Max(start, Math.Min(a, b));
            end = Math.Min(end, Math.Max(a, b));
        }
        return start < end ? (start, end) : (0, 0);
    }

    private static float[] OrderedAxisCrossings(Vector3 origin, Vector3 direction, CollisionSphere terrain, DensityEdit[] edits, (float Start, float End)? slab = null)
    {
        // Independently combine one-dimensional intervals in edit order.
        // The tested ray stays in the meridian of one lobe or inside the slab sides.
        DensityEdit[] solids = [new(terrain.Center, terrain.Radius, true), .. edits];
        var intervals = solids.Select((edit, index) =>
        {
            if (index == 0 && slab is { } face)
                return (Start: face.Start, End: face.End, Add: true);
            var offset = edit.Center - origin;
            var center = Vector3.Dot(offset, direction);
            var perpendicular = (offset - center * direction).LengthSquared();
            var halfSpan = MathF.Sqrt(Math.Max(0, edit.Radius * edit.Radius - perpendicular));
            return (Start: center - halfSpan, End: center + halfSpan, Add: edit.Add);
        }).ToArray();
        var endpoints = intervals.Where(x => x.Start < x.End)
            .SelectMany(x => new[] { x.Start, x.End }).Distinct().Order().ToArray();
        var hits = new List<float>();
        var prior = false;
        for (var i = 0; i < endpoints.Length; i++)
        {
            var inside = false;
            var sample = i + 1 < endpoints.Length ? (endpoints[i] + endpoints[i + 1]) * .5f : float.PositiveInfinity;
            foreach (var interval in intervals)
                if (sample > interval.Start && sample < interval.End)
                    inside = interval.Add;
            if (inside != prior)
                hits.Add(endpoints[i]);
            prior = inside;
        }
        return hits.ToArray();
    }

    private static object Measure(string name, GrazingRayFallback query, Ray[] rays, int samples)
    {
        for (var i = 0; i < 512; i++)
        {
            var ray = rays[i % rays.Length];
            query.Trace(ray.Origin, ray.Direction);
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var durations = new long[samples];
        var densitySamples = new int[samples];
        var hitQueries = 0;
        var exhaustedQueries = 0;
        for (var i = 0; i < samples; i++)
        {
            var ray = rays[i % rays.Length];
            var start = Stopwatch.GetTimestamp();
            var result = query.Trace(ray.Origin, ray.Direction);
            durations[i] = Stopwatch.GetTimestamp() - start;
            densitySamples[i] = result.Samples;
            if (result.Hits.Count > 0) hitQueries++;
            if (result.Exhausted) exhaustedQueries++;
        }
        Array.Sort(durations);
        Array.Sort(densitySamples);
        return new
        {
            name, samples, hitQueries, exhaustedQueries,
            p50us = Microseconds(.50), p95us = Microseconds(.95), p99us = Microseconds(.99),
            maxUs = durations[^1] * 1_000_000d / Stopwatch.Frequency,
            p50DensitySamples = densitySamples[(samples + 1) / 2 - 1],
            maxDensitySamples = densitySamples[^1]
        };

        double Microseconds(double fraction) =>
            durations[(int)Math.Ceiling(samples * fraction) - 1] * 1_000_000d / Stopwatch.Frequency;
    }

    private readonly record struct Ray(string Name, Vector3 Origin, Vector3 Direction);
}
