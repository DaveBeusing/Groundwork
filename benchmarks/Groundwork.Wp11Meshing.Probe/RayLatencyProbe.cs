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
    private static readonly Ray Seam = new("seam-excluded", new(16.3f, 0, 24.319f), Vector3.UnitY);
    private static readonly Ray Clear = new("clear", new(0, 1.173f, 1.319f), Vector3.UnitX);

    internal static object Run(Tables tables, int samples)
    {
        if (samples is < 100 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(samples));

        DensityEdit[] edits =
        [
            new(new(23.17f, 20.173f, 16.819f), .4f, false),
            new(new(24.3f, 23.173f, 24.319f), .1f, true),
            new(new(16.3f, 24.173f, 24.319f), .1f, true)
        ];
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
        var editedMesh = Marching.Extract(edited, tables);
        var originalChecksum = Audit.Digest(originalMesh);
        var editedChecksum = Audit.Digest(editedMesh);
        if (originalChecksum == editedChecksum)
            throw new InvalidOperationException("The edited collision snapshot did not change the mesh.");

        var originalCases = Grazing.Concat([Added, Seam, Clear])
            .Select(ray => Check(original, originalMesh, before, ray, false)).ToArray();
        var editedCases = Grazing.Concat([Added, Seam, Clear])
            .Select(ray => Check(edited, editedMesh, after, ray, ray == Seam)).ToArray();

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
            latency, originalChecksum, editedChecksum, edits, originalCases, editedCases,
            budgetControl = new { bounded.Exhausted, bounded.Samples }
        };
    }

    private static Grid Create(IReadOnlyList<DensityEdit> edits) =>
        new(32, "bridge", "face", false, false, false, true, false, false, edits);

    private static object Check(Grid grid, Mesh mesh, GrazingRayFallback fallback, Ray ray, bool seamExcluded)
    {
        var expected = GeometryProbe.DensityHits(grid, ray.Origin, ray.Direction);
        var meshHits = GeometryProbe.MeshHits(mesh, ray.Origin, ray.Direction);
        var queried = meshHits.Count == 0;
        var (actual, exhausted, samples) = queried ? fallback.Trace(ray.Origin, ray.Direction) : (meshHits, false, 0);
        var error = expected.Count == actual.Count
            ? expected.Zip(actual, (a, b) => MathF.Abs(a - b)).DefaultIfEmpty().Max() : (float?)null;
        if (exhausted || (seamExcluded
            ? expected.Count != 2 || actual.Count != 0 || samples != 0
            : error is null || error > .001f))
            throw new InvalidOperationException($"Ray {ray.Name}: expected {expected.Count}, mesh {meshHits.Count}, query {actual.Count}, error {error}, exhausted {exhausted}.");
        return new { ray.Name, ray.Origin, ray.Direction, expected, meshHits, actual, queried, samples, error, seamExcluded };
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
