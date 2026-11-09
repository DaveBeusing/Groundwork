using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Terrain
{
    private const int Regions = 8;
    private const int Warmup = 5;
    private const string TableHash = "83A5511346B54C42E4E66DEC916D3971C92F4FBDA1C7878CBAD5901A820DCAB4";
    private static readonly uint[] Roots = [40, 81, 82, 80, 41, 41];
    private static readonly string[] Lanes = ["old", "stream", "stream", "move", "edit", "recover"];

    internal static void Run(uint vendor, string tablePath, int repetitions)
    {
        Require(repetitions > 0, "positive repetitions");
        var source = File.ReadAllBytes(tablePath);
        Require(Convert.ToHexString(SHA256.HashData(source)) == TableHash, "pinned Transvoxel tables");
        var tables = new Tables(System.Text.Encoding.UTF8.GetString(source));
        using var gpu = new Gpu(vendor);
        using var surface = new Gpu.Surface(gpu);
        using var process = Process.GetCurrentProcess();
        var sizes = new Shape[Roots.Length];
        for (var i = 0; i < sizes.Length; i++)
        {
            var (vertices, transitions) = Build(tables, i);
            var bytes = (ulong)vertices.Length * 12;
            sizes[i] = new Shape(vertices.Length / 3, transitions, new Charge(bytes, gpu.AllocationSize(bytes + 8), gpu.AllocationSize(bytes + 8), 1));
        }
        var capacities = sizes.Select((shape, i) => (Lane: Lanes[i], shape.Charge))
            .GroupBy(x => x.Lane).ToDictionary(x => x.Key, x => x.Select(y => y.Charge).Aggregate((a, b) => a + b));
        var samples = new List<Trial>();
        for (var i = -Warmup; i < repetitions; i++)
        {
            var trial = RunRoute(gpu, surface, process, tables, sizes, capacities);
            if (i >= 0)
                samples.Add(trial);
        }

        process.Refresh();
        var timings = new Dictionary<string, Stats>();
        foreach (var key in samples[0].Timing.Keys)
            timings[key] = Summarize(samples.Select(sample => sample.Timing[key]));
        var peaks = new
        {
            retainedVertexBytes = samples.Max(x => x.VertexBytes),
            processPeakWorkingSetBytes = process.PeakWorkingSet64,
            processPeakPrivateSampleBytes = samples.Max(x => x.PrivateBytes),
            defaultVertexAllocationBytes = samples.Max(x => x.DefaultBytes),
            uploadAllocationBytes = samples.Max(x => x.UploadBytes),
            totalD3D12ResourceBytes = samples.Max(x => x.ResourceBytes),
            dxgiLocalUsageBytes = samples.Max(x => x.Local),
            dxgiNonLocalUsageBytes = samples.Max(x => x.NonLocal),
            dxgiCombinedUsageBytes = samples.Max(x => x.Combined)
        };
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            adapter = gpu.Adapter, gpu.VendorId, repetitions, warmup = Warmup, regionsPerJob = Regions,
            present = "visible HWND, 640x480, two RGBA8 buffers, Present(1, 0), S_OK",
            tableSha256 = TableHash, swapchainAllocationBytes = surface.Bytes, readbackAllocationBytes = gpu.ReadbackBytes,
            shapes = sizes.Select((shape, i) => new { lane = Lanes[i], root = Roots[i], shape }),
            capacities, peaks, timings, trials = samples
        }));
    }

    private static Trial RunRoute(Gpu gpu, Gpu.Surface surface, Process process, Tables tables,
        Shape[] sizes, Dictionary<string, Charge> capacities)
    {
        var book = new Ledger(capacities);
        var live = new List<Job>();
        var timing = new Dictionary<string, double>();
        var trial = new Trial(timing);
        var world = new RouteState();
        var route = Stopwatch.GetTimestamp();
        try
        {
            var old = Add(0);
            var a = Add(1);
            var b = Add(2);
            var saturated = Stopwatch.GetTimestamp();
            Require(!book.TryReserve("stream", new(1, 0, 0, 0)) &&
                !book.TryReserve("stream", new(0, 1, 0, 0)) &&
                !book.TryReserve("stream", new(0, 0, 1, 0)) &&
                !book.TryReserve("stream", new(0, 0, 0, 1)), "all speculative limits reached");

            var move = Add(3);
            world.CoverageReady = true;
            Frame("movement", old);
            Require(world.TryStep() && world.Position == 1, "movement progressed with old geometry");
            timing["movementReadyMs"] = Elapsed(saturated);

            Require(!world.TryCommit(42), "second edit held");
            var editStart = Stopwatch.GetTimestamp();
            var edit = Add(4);
            Require(!old.Vertices.AsSpan().SequenceEqual(edit.Vertices), "edit changed terrain geometry");
            Require(world.TryCommit(edit.Buffers.Marker), "first edit committed");
            timing["firstEditCommittedMs"] = Elapsed(editStart);
            Frame("stale", old);
            Require(!world.TryAcknowledge(old.Buffers.Marker) && !world.TryStep() && !world.TryCommit(42),
                "stale present held movement and second edit");

            var recoveryStart = Stopwatch.GetTimestamp();
            var recovery = Add(5);
            Require(recovery.Vertices.AsSpan().SequenceEqual(edit.Vertices), "recovery rebuilt the committed edit geometry");
            Frame("recovery", recovery);
            Require(world.TryAcknowledge(recovery.Buffers.Marker) && world.TryStep() &&
                world.Position == 2 && world.NextEdit == 42, "matching present resumed movement");
            timing["recoveryReadyMs"] = Elapsed(recoveryStart);

            var retirement = Stopwatch.GetTimestamp();
            var fence = gpu.BlockRetirement(old.Buffers, a.Buffers);
            Require(!gpu.IsComplete(fence) && !book.TryReserve("stream", a.Charge), "old and cancelled resources still charged");
            Sample();
            gpu.OpenRetirement();
            gpu.Wait(fence);
            Require(gpu.StreamingComplete, "speculative queue settled");
            gpu.VerifyRetiredMarkers(old.Buffers.Marker, a.Buffers.Marker);
            Drop(old);
            Drop(a);
            Require(book.TryReserve("stream", a.Charge), "speculative credits reusable after fence");
            book.Release("stream", a.Charge);
            timing["retirementMs"] = Elapsed(retirement);
            Drop(b);
            Drop(move);
            Drop(edit);
            Drop(recovery);
            Require(book.Total == default && live.Count == 0, "all credits settled");
            timing["routeMs"] = Elapsed(route);
            return trial;
        }
        finally
        {
            gpu.Drain();
            foreach (var job in live)
                job.Buffers.Dispose();
        }

        Job Add(int index)
        {
            var lane = Lanes[index];
            var shape = sizes[index];
            Require(book.TryReserve(lane, shape.Charge), $"admit {lane} before allocation");
            var start = Stopwatch.GetTimestamp();
            var (vertices, transitions) = Build(tables, index);
            timing[$"{lane}{index}MeshMs"] = Elapsed(start);
            Require(vertices.Length / 3 == shape.Triangles && transitions == shape.TransitionTriangles,
                "repeatable mesh shape and allocation");
            start = Stopwatch.GetTimestamp();
            var data = MemoryMarshal.AsBytes(vertices.AsSpan());
            var buffers = gpu.Allocate((ulong)data.Length + 8, Roots[index], data);
            var job = new Job(lane, shape.Charge, buffers, vertices);
            live.Add(job);
            if (lane == "stream")
            {
                gpu.EnqueueSpeculative(buffers);
                timing[$"{lane}{index}UploadEnqueueMs"] = Elapsed(start);
                Sample();
            }
            else
            {
                gpu.CopyAndVerify(buffers);
                timing[$"{lane}{index}UploadVerifiedMs"] = Elapsed(start);
                Sample();
                buffers.ReleaseUpload();
                var settled = new Charge(0, 0, job.Charge.Upload, 1);
                book.Release(lane, settled);
                job.Charge -= settled;
            }
            return job;
        }

        void Frame(string label, Job job)
        {
            Require(!gpu.StreamingComplete, "streaming held before present");
            var (present, frame, gpuDraw) = surface.Draw(job.Buffers, (uint)job.Vertices.Length);
            Require(!gpu.StreamingComplete, "streaming held after present");
            timing[$"{label}PresentMs"] = present;
            timing[$"{label}FrameFenceMs"] = frame;
            timing[$"{label}GpuDrawMs"] = gpuDraw;
            Sample();
        }

        void Drop(Job job)
        {
            job.Buffers.Dispose();
            book.Release(job.Lane, job.Charge);
            live.Remove(job);
        }

        void Sample()
        {
            var charge = live.Aggregate(default(Charge), (sum, job) => sum + job.Charge);
            Require(charge.Fits(book.Total), "actual resources covered by reservations");
            var usage = gpu.Usage();
            process.Refresh();
            trial.VertexBytes = Math.Max(trial.VertexBytes, charge.Cpu);
            trial.DefaultBytes = Math.Max(trial.DefaultBytes, charge.Gpu);
            trial.UploadBytes = Math.Max(trial.UploadBytes, charge.Upload);
            trial.ResourceBytes = Math.Max(trial.ResourceBytes, charge.Gpu + charge.Upload + gpu.ReadbackBytes + surface.Bytes);
            trial.Local = Math.Max(trial.Local, usage.Local);
            trial.NonLocal = Math.Max(trial.NonLocal, usage.NonLocal);
            trial.Combined = Math.Max(trial.Combined, usage.Local + usage.NonLocal);
            trial.PrivateBytes = Math.Max(trial.PrivateBytes, process.PrivateMemorySize64);
        }
    }

    private static (Vector3[] Vertices, int Transitions) Build(Tables tables, int job)
    {
        var meshes = new Mesh[Regions];
        for (var i = 0; i < Regions; i++)
        {
            DensityEdit[] edits = job >= 4 ? [new(new Vector3(15, 16, 15), 3, false)] : [];
            var fixture = (i + (job < 4 ? job : 0)) % 2 == 0 ? "bridge" : "cavity";
            var grid = new Grid(32, fixture, i % 2 == 0 ? "face" : "corner",
                false, false, false, false, false, false, edits);
            meshes[i] = Marching.Extract(grid, tables);
        }

        var packed = new Vector3[meshes.Sum(x => x.Count) * 3];
        var at = 0;
        for (var i = 0; i < meshes.Length; i++)
        {
            var center = new Vector3(-.75f + .5f * (i % 4), -.5f + (i / 4), .2f);
            foreach (var triangle in meshes[i])
            {
                packed[at++] = Place(triangle.A);
                packed[at++] = Place(triangle.B);
                packed[at++] = Place(triangle.C);
            }
            Vector3 Place(Vector3 p) => center + (p - new Vector3(16)) * .014f;
        }

        var transitions = meshes.Sum(x => x.TransitionTriangles);
        Require(packed.Length > 0 && transitions > 0, "nonempty terrain and LOD transitions");
        return (packed, transitions);
    }

    private static double Elapsed(long timestamp) => Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;

    private static Stats Summarize(IEnumerable<double> times)
    {
        var sorted = times.Order().ToArray();
        double At(double p) => sorted[(int)Math.Ceiling(sorted.Length * p) - 1];
        return new Stats(At(.50), At(.95), At(.99), sorted[^1]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Terrain capacity assertion failed: {message}.");
    }

    private sealed record Shape(int Triangles, int TransitionTriangles, Charge Charge);
    private sealed record Stats(double P50, double P95, double P99, double Max);

    private sealed class Job(string lane, Charge charge, Gpu.Buffers buffers, Vector3[] vertices)
    {
        internal string Lane { get; } = lane;
        internal Charge Charge { get; set; } = charge;
        internal Gpu.Buffers Buffers { get; } = buffers;
        internal Vector3[] Vertices { get; } = vertices;
    }

    private sealed record Trial(Dictionary<string, double> Timing)
    {
        public ulong VertexBytes { get; set; }
        public ulong DefaultBytes { get; set; }
        public ulong UploadBytes { get; set; }
        public ulong ResourceBytes { get; set; }
        public ulong Local { get; set; }
        public ulong NonLocal { get; set; }
        public ulong Combined { get; set; }
        public long PrivateBytes { get; set; }
    }
}
