using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Terrain
{
    private const int Regions = 8;
    private const int RegionCells = 32;
    [ThreadStatic] private static RegionScratch? _scratch;
    private const ulong PackedVertexBudget = 4 * 1024 * 1024;
    private static ReadOnlySpan<byte> VariedFineMasks => [0x55, 0x02, 0x33, 0x08, 0x0F, 0x20, 0xCC, 0x80];
    private const int Warmup = 5;
    private const string TableHash = "83A5511346B54C42E4E66DEC916D3971C92F4FBDA1C7878CBAD5901A820DCAB4";
    private static readonly uint[] Roots = [40, 81, 82, 80, 41, 41];
    private static readonly string[] Lanes = ["old", "stream", "stream", "move", "edit", "recover"];

    internal static void Run(uint vendor, string tablePath, int repetitions, bool concurrent, bool pooled, bool backlogged, bool reserveWorker, bool optimized, bool paced, bool varied, bool tuned, bool waitForDraw, uint syncInterval, bool rowMasked)
    {
        Require(repetitions > 0, "positive repetitions");
        var source = File.ReadAllBytes(tablePath);
        Require(Convert.ToHexString(SHA256.HashData(source)) == TableHash, "pinned Transvoxel tables");
        var tables = new Tables(System.Text.Encoding.UTF8.GetString(source));
        using var gpu = new Gpu(vendor);
        using var surface = new Gpu.Surface(gpu, paced, waitForDraw, syncInterval);
        using var process = Process.GetCurrentProcess();
        var sizes = new Shape[Roots.Length];
        var regionInputs = new List<RegionInput>();
        for (var i = 0; i < sizes.Length; i++)
        {
            var (vertices, transitions, collisionMesh) = Build(tables, i, concurrent, varied: varied,
                regionInputs: varied ? regionInputs : null);
            if (concurrent && i == 0)
                Require(new GroundedRoute().TryTraverse(collisionMesh, new(21.3f, 15.2f)),
                    "unedited terrain supports the original path across the seam");
            if (tuned || !varied && (paced || optimized && i == 4))
            {
                var candidate = Build(tables, i, concurrent, shareEqualRegions: !varied, varied: varied, tuned: tuned, rowMasked: rowMasked);
                Require(candidate.Vertices.AsSpan().SequenceEqual(vertices) && candidate.Transitions == transitions &&
                    candidate.CollisionMesh.SequenceEqual(collisionMesh), "shared mesh extraction preserves exact output");
            }
            var bytes = (ulong)vertices.Length * 12;
            sizes[i] = new Shape(vertices.Length / 3, transitions, new Charge(bytes, gpu.AllocationSize(bytes + 8), gpu.AllocationSize(bytes + 8), 1));
        }
        if (varied)
        {
            foreach (var regions in regionInputs.GroupBy(x => x.Job))
                Require(regions.Count() == Regions && regions.Select(x => x.DensitySha256).Distinct().Count() == Regions &&
                    regions.Select(x => x.FineMask).Distinct().Count() == Regions && regions.All(x => x.TransitionTriangles > 0),
                    $"job {regions.Key} distinct inputs and LOD transitions ({string.Join(", ", regions.Select(x => $"{x.Region}:{x.TransitionTriangles}"))})");
        }
        var capacities = sizes.Select((shape, i) => (Lane: Lanes[i], shape.Charge))
            .GroupBy(x => x.Lane).ToDictionary(x => x.Key, x => x.Select(y => y.Charge).Aggregate((a, b) => a + b));
        var workerCharges = sizes.Skip(1).Take(2).Select(shape => new Charge(shape.Charge.Cpu, 0, 0, 1)).ToArray();
        var activeSpeculativeBudget = reserveWorker
            ? checked(PackedVertexBudget - sizes.Aggregate(0UL, (sum, shape) => sum + shape.Charge.Cpu))
            : 0;
        if (reserveWorker)
        {
            Require(activeSpeculativeBudget >= 2 * workerCharges.Min(x => x.Cpu) &&
                activeSpeculativeBudget >= workerCharges.Max(x => x.Cpu), "speculative budget admits two minimum-size builds");
            workerCharges = [new(activeSpeculativeBudget, 0, 0, MeshingPool.QueueCapacity + MeshingPool.WorkerCount - 1)];
        }
        else if (backlogged)
            workerCharges = Enumerable.Repeat(new Charge(workerCharges.Max(x => x.Cpu), 0, 0, 1),
                MeshingPool.QueueCapacity + MeshingPool.WorkerCount).ToArray();
        if (concurrent)
            capacities["stream"] += workerCharges.Aggregate((a, b) => a + b);
        using var pool = pooled ? new MeshingPool(reserveWorker) : null;
        var firstPooledIndex = paced ? 3 : 4;
        using var protectedUploads = optimized
            ? new Gpu.ProtectedUploads(gpu, sizes.Skip(firstPooledIndex).Select(shape => shape.Charge.Cpu + 8).ToArray())
            : null;
        var samples = new List<Trial>();
        var traceClock = Environment.GetEnvironmentVariable("GROUNDWORK_C00_TRACE_CLOCK") == "1";
        for (var i = -Warmup; i < repetitions; i++)
        {
            var trial = RunRoute(gpu, surface, process, tables, sizes, capacities, concurrent ? workerCharges : [], pool, backlogged, activeSpeculativeBudget, protectedUploads, paced, firstPooledIndex, varied, tuned, waitForDraw, rowMasked, traceClock);
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
            concurrentVertexBytes = samples.Max(x => x.ConcurrentVertexBytes),
            backlogVertexUpperBoundBytes = samples.Max(x => x.VertexUpperBoundBytes),
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
            adapter = gpu.Adapter, gpu.VendorId, repetitions, warmup = Warmup, regionsPerJob = Regions, competingWorkers = backlogged ? MeshingPool.WorkerCount - (reserveWorker ? 1 : 0) : concurrent ? 2 : 0,
            reservedWorkers = reserveWorker ? 1 : 0, packedVertexBudgetBytes = PackedVertexBudget, optimized,
            reservedDefaultBytes = protectedUploads?.GeometryBytes ?? 0, reservedUploadBytes = protectedUploads?.UploadBytes ?? 0,
            activeSpeculativeBudgetBytes = activeSpeculativeBudget,
            poolWorkers = pool is null ? 0 : MeshingPool.WorkerCount, backlogged, queueCapacity = MeshingPool.QueueCapacity,
            serverGc = System.Runtime.GCSettings.IsServerGC, gcLatencyMode = System.Runtime.GCSettings.LatencyMode.ToString(),
            present = paced ? $"visible HWND, 640x480, two RGBA8 buffers, frame-latency wait then Present({syncInterval}, DO_NOT_WAIT), S_OK"
                : "visible HWND, 640x480, two RGBA8 buffers, Present(1, 0), S_OK",
            tableSha256 = TableHash, swapchainAllocationBytes = surface.Bytes, readbackAllocationBytes = gpu.ReadbackBytes,
            shapes = sizes.Select((shape, i) => new { lane = Lanes[i], root = Roots[i], shape }),
            capacities, peaks, timings, varied, tuned, waitForDraw, syncInterval, rowMasked, regionInputs, traceClock = traceClock ? new { pid = Environment.ProcessId, tid = Gpu.Surface.NativeThreadId, frequency = Stopwatch.Frequency } : null, trials = samples
        }));
    }

    private static Trial RunRoute(Gpu gpu, Gpu.Surface surface, Process process, Tables tables,
        Shape[] sizes, Dictionary<string, Charge> capacities, Charge[] workerCharges, MeshingPool? pool, bool backlogged, ulong activeSpeculativeBudget, Gpu.ProtectedUploads? protectedUploads, bool paced, int firstPooledIndex, bool varied, bool tuned, bool waitForDraw, bool rowMasked, bool traceClock)
    {
        var book = new Ledger(capacities);
        var live = new List<Job>();
        var timing = new Dictionary<string, double>();
        var trial = new Trial(timing);
        var world = new RouteState();
        var concurrent = workerCharges.Length != 0;
        var grounded = new GroundedRoute();
        var routeGc = GcSample.Read();
        var background = new System.Collections.Concurrent.ConcurrentQueue<BackgroundBuild>();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var backgroundSequence = 0;
        long packedLive = 0;
        long packedPeak = 0;
        var speculativeCredits = activeSpeculativeBudget == 0 ? null : new VertexCredits(activeSpeculativeBudget);
        var route = Stopwatch.GetTimestamp();
        try
        {
            var old = Add(0);
            var a = Add(1);
            var b = Add(2);
            foreach (var charge in workerCharges)
                Require(book.TryReserve("stream", charge), "admit competing CPU worker");
            if (backlogged)
                {
                packedLive = (long)live.Aggregate(0UL, (sum, job) => sum + job.Charge.Cpu);
                packedPeak = packedLive;
                pool!.StartBacklog(BuildBackground);
            }
            var saturated = Stopwatch.GetTimestamp();
            Require(!book.TryReserve("stream", new(1, 0, 0, 0)) &&
                !book.TryReserve("stream", new(0, 1, 0, 0)) &&
                !book.TryReserve("stream", new(0, 0, 1, 0)) &&
                !book.TryReserve("stream", new(0, 0, 0, 1)), "all speculative limits reached");

            var move = Add(3);
            world.CoverageReady = true;
            Frame("movement", old);
            Require(world.TryStep() && world.Position == 1, "movement progressed with old geometry");
            if (concurrent)
            {
                var collisionStart = Stopwatch.GetTimestamp();
                Require(grounded.TryTraverse(move.CollisionMesh, new(11.3f, 15.2f)), "grounded approach");
                Require(!grounded.TryTraverse(old.CollisionMesh, new(11.3f, 29)), "uncovered ground blocked");
                timing["approachCollisionMs"] = Elapsed(collisionStart);
            }
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

            var heldPosition = grounded.Position;
            var recoveryStart = Stopwatch.GetTimestamp();
            var recovery = Add(5, protectedUploads is null ? null : edit);
            Require(recovery.Vertices.AsSpan().SequenceEqual(edit.Vertices), "recovery rebuilt the committed edit geometry");
            Frame("recovery", recovery);
            Require(world.TryAcknowledge(recovery.Buffers.Marker) && world.TryStep() &&
                world.Position == 2 && world.NextEdit == 42, "matching present resumed movement");
            if (concurrent)
            {
                var collisionStart = Stopwatch.GetTimestamp();
                Require(heldPosition == grounded.Position, "position retained through stale presentation");
                Require(!grounded.TryTraverse(recovery.CollisionMesh, new(21.3f, 15.2f)) &&
                    grounded.Position == heldPosition, "excavation blocks original path");
                Require(grounded.TryTraverse(recovery.CollisionMesh, new(11.3f, 20.2f)) &&
                    grounded.TryTraverse(recovery.CollisionMesh, new(21.3f, 20.2f)) &&
                    grounded.TryTraverse(recovery.CollisionMesh, new(21.3f, 15.2f)), "detour crosses 2:1 seam");
                trial.RouteSteps = grounded.Steps;
                trial.FinalPosition = [grounded.Position.X, grounded.Position.Y, grounded.Position.Z];
                timing["recoveryCollisionMs"] = Elapsed(collisionStart);
            }
            timing["recoveryReadyMs"] = Elapsed(recoveryStart);

            if (backlogged)
            {
                var stop = Stopwatch.GetTimestamp();
                pool!.StopBacklog();
                timing["backlogDrainMs"] = Elapsed(stop);
                if (errors.TryDequeue(out var error))
                    throw new InvalidOperationException("Background meshing failed.", error);
                trial.Background = background.ToArray();
                trial.ConcurrentVertexBytes = (ulong)packedPeak;
                if (speculativeCredits is not null)
                {
                    Require(speculativeCredits.Live == 0 && (ulong)packedPeak <= PackedVertexBudget, "bounded vertex credits settled");
                    trial.SpeculativeReservedPeakBytes = speculativeCredits.Peak;
                    trial.SpeculativeFallbacks = speculativeCredits.Fallbacks;
                }
                Require(trial.Background.Length >= MeshingPool.WorkerCount, "background workers completed jobs");
            }
            foreach (var charge in workerCharges)
                book.Release("stream", charge);
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
            if (pool is not null)
                GcSample.Read().RecordSince(routeGc, timing, "route");
            return trial;
        }
        finally
        {
            if (backlogged)
                pool!.StopBacklog();
            gpu.Drain();
            foreach (var job in live)
                job.Buffers.Dispose();
        }

        Job Add(int index, Job? committed = null)
        {
            var lane = Lanes[index];
            var shape = sizes[index];
            if (committed is not null)
                Require(index == 5 && committed.Buffers.Marker == world.AuthoritativeRoot &&
                    committed.Buffers.Marker == Roots[index] && live.Contains(committed), "recovery reuses the live committed generation");
            var charge = committed is null ? shape.Charge : shape.Charge with { Cpu = 0 };
            Require(book.TryReserve(lane, charge), $"admit {lane} before allocation");
            var start = Stopwatch.GetTimestamp();
            var (vertices, transitions, collisionMesh) = committed is not null
                ? (committed.Vertices, shape.TransitionTriangles, committed.CollisionMesh)
                : concurrent && index >= 3
                ? backlogged ? BuildWithBacklog(index, lane) :
                    pool is null ? BuildWithCompetition(index, lane) : BuildWithPool(pool, index, lane)
                : Build(tables, index, concurrent, shareEqualRegions: paced && !varied, varied: varied, tuned: tuned, rowMasked: rowMasked);
            timing[$"{lane}{index}MeshMs"] = Elapsed(start);
            Require(vertices.Length / 3 == shape.Triangles && transitions == shape.TransitionTriangles,
                "repeatable mesh shape and allocation");
            start = Stopwatch.GetTimestamp();
            var data = MemoryMarshal.AsBytes(vertices.AsSpan());
            var buffers = protectedUploads is not null && index >= firstPooledIndex
                ? protectedUploads.Stage(index - firstPooledIndex, Roots[index], data)
                : gpu.Allocate((ulong)data.Length + 8, Roots[index], data);
            var job = new Job(lane, charge, buffers, vertices, collisionMesh);
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

        (Vector3[] Vertices, int Transitions, Mesh CollisionMesh) BuildWithCompetition(int index, string lane)
        {
            using var start = new Barrier(3);
            var workers = Enumerable.Range(1, 2).Select(worker => Task.Factory.StartNew(() =>
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Competing meshing workers did not start.");
                var begin = Stopwatch.GetTimestamp();
                var (vertices, _, _) = Build(tables, worker, concurrent);
                return (Begin: begin, End: Stopwatch.GetTimestamp(), Vertices: vertices);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            var protectedBegin = 0L;
            var protectedEnd = 0L;
            (Vector3[], int, Mesh) result;
            try
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Protected meshing did not start.");
                protectedBegin = Stopwatch.GetTimestamp();
                result = Build(tables, index, concurrent);
                protectedEnd = Stopwatch.GetTimestamp();
            }
            finally { Task.WaitAll(workers); }

            var latestStart = Math.Max(protectedBegin, workers.Max(task => task.Result.Begin));
            var earliestEnd = Math.Min(protectedEnd, workers.Min(task => task.Result.End));
            Require(earliestEnd > latestStart && !gpu.StreamingComplete, "three concurrent builds while streaming fence held");
            timing[$"{lane}ThreeWorkerOverlapMs"] = Stopwatch.GetElapsedTime(latestStart, earliestEnd).TotalMilliseconds;
            timing[$"{lane}ProtectedBuildMs"] = Stopwatch.GetElapsedTime(protectedBegin, protectedEnd).TotalMilliseconds;
            for (var i = 0; i < workers.Length; i++)
            {
                var worker = workers[i].Result;
                timing[$"{lane}CompetingBuild{i + 1}Ms"] = Stopwatch.GetElapsedTime(worker.Begin, worker.End).TotalMilliseconds;
            }
            var completedWorkerPayload = workers.Select((task, worker) =>
            {
                var bytes = (ulong)task.Result.Vertices.Length * 12;
                Require(bytes == workerCharges[worker].Cpu, "worker payload matches CPU credit");
                return bytes;
            }).Aggregate((a, b) => a + b);
            trial.ConcurrentVertexBytes = Math.Max(trial.ConcurrentVertexBytes,
                live.Aggregate(completedWorkerPayload + (ulong)result.Item1.Length * 12, (sum, job) => sum + job.Charge.Cpu));
            return result;
        }

        (Vector3[] Vertices, int Transitions, Mesh CollisionMesh) BuildWithPool(MeshingPool workerPool, int index, string lane)
        {
            using var start = new Barrier(MeshingPool.WorkerCount);
            var waveGc = GcSample.Read();
            var wave = Stopwatch.GetTimestamp();
            var protectedJob = workerPool.Submit(() => Measure(index));
            var competitorA = workerPool.Submit(() => Measure(1));
            var competitorB = workerPool.Submit(() => Measure(2));
            var results = Task.WhenAll(protectedJob, competitorA, competitorB).GetAwaiter().GetResult();
            var waveMs = Elapsed(wave);
            GcSample.Read().RecordSince(waveGc, timing, lane + "Wave");
            var builds = results.Select(x => x.Result).ToArray();
            var overlapBegin = builds.Max(x => x.Begin);
            var overlapEnd = builds.Min(x => x.End);
            Require(overlapEnd > overlapBegin && !gpu.StreamingComplete, "three pool builds while streaming fence held");
            timing[$"{lane}ThreeWorkerOverlapMs"] = Stopwatch.GetElapsedTime(overlapBegin, overlapEnd).TotalMilliseconds;
            timing[$"{lane}WaveMs"] = waveMs;
            for (var i = 0; i < results.Length; i++)
            {
                var label = lane + (i == 0 ? "Protected" : $"Competing{i}");
                var build = builds[i];
                timing[$"{label}QueueMs"] = results[i].QueueMs;
                timing[$"{label}BarrierMs"] = Stopwatch.GetElapsedTime(build.Entered, build.Begin).TotalMilliseconds;
                timing[$"{label}BuildMs"] = Stopwatch.GetElapsedTime(build.Begin, build.End).TotalMilliseconds;
                timing[$"{label}BuildGcPauseMs"] = build.GcPauseMs;
                timing[$"{label}AllocatedBytes"] = build.AllocatedBytes;
            }

            var workerPayload = 0UL;
            for (var i = 0; i < workerCharges.Length; i++)
            {
                var bytes = (ulong)builds[i + 1].Mesh.Vertices.Length * 12;
                Require(bytes == workerCharges[i].Cpu, "pooled worker payload matches CPU credit");
                workerPayload += bytes;
            }
            var result = builds[0].Mesh;
            trial.ConcurrentVertexBytes = Math.Max(trial.ConcurrentVertexBytes,
                live.Aggregate(workerPayload + (ulong)result.Vertices.Length * 12, (sum, job) => sum + job.Charge.Cpu));
            return result;

            BuildMeasurement Measure(int job)
            {
                var entered = Stopwatch.GetTimestamp();
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("C00 worker pool did not start all mesh builds.");
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var pause = GC.GetTotalPauseDuration();
                var begin = Stopwatch.GetTimestamp();
                var mesh = Build(tables, job, concurrent);
                var end = Stopwatch.GetTimestamp();
                return new(entered, begin, end, (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                    GC.GetAllocatedBytesForCurrentThread() - allocated, mesh);
            }
        }

        void TrackPacked(long bytes)
        {
            var liveBytes = Interlocked.Add(ref packedLive, bytes);
            var peak = Volatile.Read(ref packedPeak);
            while (liveBytes > peak)
            {
                var previous = Interlocked.CompareExchange(ref packedPeak, liveBytes, peak);
                if (previous == peak)
                    break;
                peak = previous;
            }
        }

        void BuildBackground(long queued, long dequeued)
        {
            long payload = 0;
            ulong reserved = 0;
            try
            {
                var index = 1 + (Interlocked.Increment(ref backgroundSequence) & 1);
                if (speculativeCredits is not null)
                {
                    var other = 3 - index;
                    index = speculativeCredits.Reserve(index, sizes[index].Charge.Cpu, other, sizes[other].Charge.Cpu);
                    reserved = sizes[index].Charge.Cpu;
                }
                var begin = Stopwatch.GetTimestamp();
                var pause = GC.GetTotalPauseDuration();
                var (vertices, _, _) = Build(tables, index, concurrent, bytes =>
                {
                    Require((ulong)bytes <= (speculativeCredits is null ? workerCharges[0].Cpu : reserved),
                        "speculative allocation fits its acquired credit");
                    payload = bytes;
                    TrackPacked(bytes);
                }, shareEqualRegions: paced && !varied, varied: varied, tuned: tuned, rowMasked: rowMasked);
                var buildMs = Elapsed(begin);
                Require(vertices.LongLength * 12 == payload, "background packed output matches credit accounting");
                background.Enqueue(new(Stopwatch.GetElapsedTime(queued, dequeued).TotalMilliseconds, buildMs,
                    (GC.GetTotalPauseDuration() - pause).TotalMilliseconds));
            }
            catch (Exception error) { errors.Enqueue(error); }
            finally
            {
                Interlocked.Add(ref packedLive, -payload);
                speculativeCredits?.Release(reserved);
            }
        }

        (Vector3[] Vertices, int Transitions, Mesh CollisionMesh) BuildWithBacklog(int index, string lane)
        {
            Require(pool!.IsFull && !gpu.StreamingComplete, "CPU queue full and GPU held at submit");
            var gc = GcSample.Read();
            var result = pool.Submit(() =>
            {
                var pause = GC.GetTotalPauseDuration();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var begin = Stopwatch.GetTimestamp();
                var mesh = Build(tables, index, concurrent, bytes =>
                {
                    Require((ulong)bytes <= sizes[index].Charge.Cpu, "protected allocation fits its acquired credit");
                    TrackPacked(bytes);
                }, shareEqualRegions: !varied && (paced || protectedUploads is not null && index == 4), varied: varied, tuned: tuned, rowMasked: rowMasked);
                return (Mesh: mesh, BuildMs: Elapsed(begin),
                    GcMs: (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                    Allocated: GC.GetAllocatedBytesForCurrentThread() - allocated);
            }).GetAwaiter().GetResult();
            GcSample.Read().RecordSince(gc, timing, lane + "Dispatch");
            Require(pool.IsFull && !gpu.StreamingComplete, "CPU queue full and GPU held at completion");
            timing[$"{lane}ProtectedQueueMs"] = result.QueueMs;
            timing[$"{lane}ProtectedBuildMs"] = result.Result.BuildMs;
            timing[$"{lane}ProtectedBuildGcPauseMs"] = result.Result.GcMs;
            timing[$"{lane}ProtectedAllocatedBytes"] = result.Result.Allocated;
            var livePayload = live.Aggregate((ulong)result.Result.Mesh.Vertices.Length * 12, (sum, job) => sum + job.Charge.Cpu);
            trial.VertexUpperBoundBytes = Math.Max(trial.VertexUpperBoundBytes,
                livePayload + (speculativeCredits is null
                    ? (ulong)MeshingPool.WorkerCount * workerCharges[0].Cpu : activeSpeculativeBudget));
            return result.Result.Mesh;
        }

        void Frame(string label, Job job)
        {
            Require(!gpu.StreamingComplete && (!backlogged || pool!.IsFull), "streaming held before present");
            var (present, frame, gpuDraw, readyWait, retries, drawFenceWait, clock) = surface.Draw(job.Buffers, (uint)job.Vertices.Length);
            Require(!gpu.StreamingComplete && (!backlogged || pool!.IsFull), "streaming held after present");
            timing[$"{label}PresentMs"] = present;
            timing[$"{label}FrameFenceMs"] = frame;
            timing[$"{label}GpuDrawMs"] = gpuDraw;
            if (traceClock)
                (trial.FrameClocks ??= new())[label] = clock;
            if (paced)
            {
                timing[$"{label}ReadyWaitMs"] = readyWait;
                timing[$"{label}PresentRetries"] = retries;
                if (waitForDraw)
                    timing[$"{label}DrawFenceWaitMs"] = drawFenceWait;
            }
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
            var actualGpu = charge.Gpu;
            var actualUpload = charge.Upload;
            if (protectedUploads is not null)
            {
                var leased = live.Where(job => job.Buffers.Pooled)
                    .Aggregate(default(Charge), (sum, job) => sum + job.Charge);
                actualGpu += protectedUploads.GeometryBytes - leased.Gpu;
                actualUpload += protectedUploads.UploadBytes - leased.Upload;
            }
            trial.DefaultBytes = Math.Max(trial.DefaultBytes, actualGpu);
            trial.UploadBytes = Math.Max(trial.UploadBytes, actualUpload);
            trial.ResourceBytes = Math.Max(trial.ResourceBytes, actualGpu + actualUpload + gpu.ReadbackBytes + surface.Bytes);
            trial.Local = Math.Max(trial.Local, usage.Local);
            trial.NonLocal = Math.Max(trial.NonLocal, usage.NonLocal);
            trial.Combined = Math.Max(trial.Combined, usage.Local + usage.NonLocal);
            trial.PrivateBytes = Math.Max(trial.PrivateBytes, process.PrivateMemorySize64);
        }
    }

    private static (Vector3[] Vertices, int Transitions, Mesh CollisionMesh) Build(Tables tables, int job, bool concurrent, Action<long>? packedAllocated = null, bool shareEqualRegions = false, bool varied = false, List<RegionInput>? regionInputs = null, bool tuned = false, bool rowMasked = false)
    {
        var meshes = new Mesh[Regions];
        var scratch = tuned ? _scratch ??= new RegionScratch() : null;
        var shared = shareEqualRegions ? new Dictionary<(string Fixture, string Layout), Mesh>() : null;
        DensityEdit[] edits = job >= 4 ? [new(new Vector3(15, 16, 15), 3, false)] : [];
        for (var i = 0; i < Regions; i++)
        {

            var fixture = (i + (job < 4 ? job : 0)) % 2 == 0 ? "bridge" : "cavity";
            if (concurrent && i == 0 && job is 0 or 3 or 4 or 5)
                fixture = "capacity-route";
            // Every region in this job uses the same local brush. Placement
            // is applied after extraction and is never part of the cache.
            var layout = i % 2 == 0 ? "face" : "corner";
            var key = (fixture, layout);
            if (shared is not null && shared.TryGetValue(key, out var existing))
            {
                meshes[i] = existing;
                continue;
            }
            var offset = varied ? new Vector3(i * .171875f, (i % 3) * .21875f, -i * .125f) : default;
            byte? fineMask = varied ? VariedFineMasks[i] : null;
            var grid = new Grid(RegionCells, fixture, layout, false, false, false, false, false, false, edits,
                offset, fineMask, scratch?.Density, rowMasked ? scratch!.SignRows : null);
            meshes[i] = Marching.Extract(grid, tables, scratch: i == 0 ? null : scratch?.Meshes[i - 1], fast: tuned);
            if (regionInputs is not null)
                regionInputs.Add(new(job, i, fixture, fineMask!.Value,
                    Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(grid.DensitySamples))),
                    meshes[i].Count, meshes[i].TransitionTriangles));
            shared?.Add(key, meshes[i]);
        }

        var vertexCount = checked(meshes.Sum(x => x.Count) * 3);
        packedAllocated?.Invoke((long)vertexCount * 12);
        var packed = new Vector3[vertexCount];
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
        return (packed, transitions, meshes[0]);
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

    private sealed class VertexCredits(ulong limit)
    {
        private readonly object _sync = new();
        internal ulong Live { get; private set; }
        internal ulong Peak { get; private set; }
        internal int Fallbacks { get; private set; }

        internal int Reserve(int preferred, ulong bytes, int alternative, ulong alternativeBytes)
        {
            lock (_sync)
            {
                if (bytes > limit - Live)
                {
                    preferred = alternative;
                    bytes = alternativeBytes;
                    Fallbacks++;
                }
                Require(bytes <= limit - Live, "speculative vertex admission within cap");
                Live += bytes;
                Peak = Math.Max(Peak, Live);
                return preferred;
            }
        }

        internal void Release(ulong bytes)
        {
            lock (_sync)
                Live = checked(Live - bytes);
        }
    }

    private sealed class RegionScratch
    {
        internal float[] Density { get; } = new float[(RegionCells + 1) * (RegionCells + 1) * (RegionCells + 1)];
        internal ulong[] SignRows { get; } = new ulong[(RegionCells + 1) * (RegionCells + 1)];
        internal Mesh[] Meshes { get; } = Enumerable.Range(0, Regions - 1).Select(_ => new Mesh()).ToArray();
    }

    private sealed record RegionInput(int Job, int Region, string Fixture, byte FineMask, string DensitySha256,
        int Triangles, int TransitionTriangles);

    private sealed record BackgroundBuild(double QueueMs, double BuildMs, double GcPauseMs);

    private sealed record BuildMeasurement(long Entered, long Begin, long End, double GcPauseMs, long AllocatedBytes,
        (Vector3[] Vertices, int Transitions, Mesh CollisionMesh) Mesh);

    private sealed record Shape(int Triangles, int TransitionTriangles, Charge Charge);
    private sealed record Stats(double P50, double P95, double P99, double Max);

    private sealed class Job(string lane, Charge charge, Gpu.Buffers buffers, Vector3[] vertices, Mesh collisionMesh)
    {
        internal string Lane { get; } = lane;
        internal Charge Charge { get; set; } = charge;
        internal Gpu.Buffers Buffers { get; } = buffers;
        internal Vector3[] Vertices { get; } = vertices;
        internal Mesh CollisionMesh { get; } = collisionMesh;
    }

    private sealed record Trial(Dictionary<string, double> Timing)
    {
        public ulong VertexBytes { get; set; }
        public ulong ConcurrentVertexBytes { get; set; }
        public ulong VertexUpperBoundBytes { get; set; }
        public ulong SpeculativeReservedPeakBytes { get; set; }
        public int SpeculativeFallbacks { get; set; }
        public Dictionary<string, Gpu.Surface.FrameClock>? FrameClocks { get; set; }
        public BackgroundBuild[]? Background { get; set; }
        public int RouteSteps { get; set; }
        public float[]? FinalPosition { get; set; }
        public ulong DefaultBytes { get; set; }
        public ulong UploadBytes { get; set; }
        public ulong ResourceBytes { get; set; }
        public ulong Local { get; set; }
        public ulong NonLocal { get; set; }
        public ulong Combined { get; set; }
        public long PrivateBytes { get; set; }
    }
}
