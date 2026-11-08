using System.Diagnostics;

static class CollisionBenchmark
{
    internal static object Measure(Func<Grid> createGrid, Tables tables, string fixture, string mode, int repetitions)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var startingWorkingSetBytes = process.WorkingSet64;
        var samplingMs = new double[repetitions];
        var extractionMs = new double[repetitions];
        var totalMs = new double[repetitions];
        var allocated = new long[repetitions];
        var warmup = Extract();
        var checksum = Audit.Digest(warmup.Mesh);
        var triangles = warmup.Mesh.Count;
        var transitionTriangles = warmup.Mesh.TransitionTriangles;
        var cellSize = warmup.CellSize;
        warmup = default;

        for (var i = 0; i < repetitions; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var result = Extract();
            allocated[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            samplingMs[i] = Stopwatch.GetElapsedTime(start, result.Sampled).TotalMilliseconds;
            extractionMs[i] = Stopwatch.GetElapsedTime(result.Sampled, result.Finished).TotalMilliseconds;
            totalMs[i] = Stopwatch.GetElapsedTime(start, result.Finished).TotalMilliseconds;
            if (Audit.Digest(result.Mesh) != checksum)
                throw new InvalidOperationException("Collision mesh changed during benchmark.");
        }

        process.Refresh();
        return new
        {
            fixture, mode, repetitions, triangles, transitionTriangles, cellSize, checksum,
            sampling = Distribution(samplingMs), extraction = Distribution(extractionMs), total = Distribution(totalMs),
            medianAllocatedBytes = Percentile(allocated, .5), startingWorkingSetBytes,
            peakWorkingSetBytes = process.PeakWorkingSet64
        };

        (Mesh Mesh, long Sampled, long Finished, float CellSize) Extract()
        {
            var grid = createGrid();
            var sampled = Stopwatch.GetTimestamp();
            var mesh = Marching.Extract(grid, tables);
            return (mesh, sampled, Stopwatch.GetTimestamp(), grid.CellSize);
        }
    }

    private static object Distribution(double[] values) => new
    {
        p50ms = Percentile(values, .5), p95ms = Percentile(values, .95), p99ms = Percentile(values, .99)
    };

    private static T Percentile<T>(T[] values, double fraction)
    {
        Array.Sort(values);
        return values[(int)Math.Ceiling(fraction * values.Length) - 1];
    }
}
