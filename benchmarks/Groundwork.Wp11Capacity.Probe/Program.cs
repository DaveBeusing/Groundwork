using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

const ulong MiB = 1024 * 1024;
var vendor = args.Length == 0 ? 0x10DEu : uint.Parse(args[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
if (args.Length == 4 && args[1] is "terrain" or "concurrent-terrain" or "pooled-terrain" or "backlogged-terrain" or "reserved-terrain" or "optimized-terrain" or "paced-terrain" or "varied-terrain" or "tuned-terrain" or "ready-present-terrain" or "immediate-present-terrain")
{
    Terrain.Run(vendor, args[2], int.Parse(args[3], CultureInfo.InvariantCulture), args[1] != "terrain",
        args[1] is "pooled-terrain" or "backlogged-terrain" or "reserved-terrain" or "optimized-terrain" or "paced-terrain" or "varied-terrain" or "tuned-terrain" or "ready-present-terrain" or "immediate-present-terrain",
        args[1] is "backlogged-terrain" or "reserved-terrain" or "optimized-terrain" or "paced-terrain" or "varied-terrain" or "tuned-terrain" or "ready-present-terrain" or "immediate-present-terrain",
        args[1] is "reserved-terrain" or "optimized-terrain" or "paced-terrain" or "varied-terrain" or "tuned-terrain" or "ready-present-terrain" or "immediate-present-terrain", args[1] is "optimized-terrain" or "paced-terrain" or "varied-terrain" or "tuned-terrain" or "ready-present-terrain" or "immediate-present-terrain", args[1] is "paced-terrain" or "varied-terrain" or "tuned-terrain" or "ready-present-terrain" or "immediate-present-terrain", args[1] is "varied-terrain" or "tuned-terrain" or "ready-present-terrain" or "immediate-present-terrain", args[1] is "tuned-terrain" or "ready-present-terrain" or "immediate-present-terrain", args[1] == "ready-present-terrain", args[1] == "immediate-present-terrain" ? 0u : 1u);
    return;
}

using var gpu = new Gpu(vendor);
using var process = Process.GetCurrentProcess();
var budget = new Dictionary<string, Charge>
{
    ["active-old"] = Make(16, 32, 1),
    ["speculative"] = Make(16, 32, 2),
    ["movement"] = Make(6, 12, 1),
    ["head-edit"] = Make(10, 20, 1),
    ["recovery"] = Make(12, 24, 1)
};
var book = new Ledger(budget);
var trace = new List<Sample>();
var live = new List<Allocation>();
var route = Stopwatch.StartNew();
var world = new RouteState();
SampleState("baseline");

try
{
    var old = Build("active-old", "old-root", 16, 32, 1);
    FinishWorker(old);
    SettleUpload(old);
    var specA = Build("speculative", "stream-a", 8, 16, 2);
    var specB = Build("speculative", "stream-b", 8, 16, 3);
    Require(!book.TryReserve("speculative", new Charge(1, 0, 0, 0)), "full speculative CPU");
    Require(!book.TryReserve("speculative", new Charge(0, 1, 0, 0)), "full speculative GPU");
    Require(!book.TryReserve("speculative", new Charge(0, 0, 1, 0)), "full speculative upload");
    Require(!book.TryReserve("speculative", new Charge(0, 0, 0, 1)), "full speculative slots");
    Require(!gpu.StreamingComplete, "speculative GPU work is pending");
    SampleState("streaming-saturated-denied");

    specA.Cancelled = true;
    Require(!book.TryReserve("speculative", specA.Charge), "cancel before settlement retains charge");
    SampleState("cancelled-stream-still-charged");

    var movement = Build("movement", "movement-coverage", 6, 12, 4);
    Require(!gpu.StreamingComplete && old.Payload![0] == 1, "movement progress while streaming and old held");
    world.CoverageReady = true;
    Require(world.TryStep() && world.Position == 1, "covered movement progresses under saturation");
    SampleState("movement-coverage-ready");
    FinishWorker(movement);
    SettleUpload(movement);

    Require(!world.TryCommit(42), "later edit cannot pass head");
    var head = Build("head-edit", "edit-41", 10, 20, 41);
    Require(!book.TryReserve("head-edit", new Charge(0, 0, 0, 1)), "second head slot unavailable");
    FinishWorker(head);
    SettleUpload(head);
    Require(world.TryCommit(head.Buffers.Marker), "head edit committed");
    Require(!world.TryAcknowledge(40) && !world.TryStep() && world.Position == 1 &&
        !world.TryCommit(42), "stale presentation holds movement and next edit");
    SampleState("first-edit-committed-stale-presentation-held");

    var recovery = Build("recovery", "recover-edit-41", 12, 24, head.Buffers.Marker);
    Require(!gpu.StreamingComplete && old.Payload![0] == 1 && recovery.Buffers.Marker == head.Buffers.Marker,
        "recovery matches committed root while old is held");
    SampleState("recovery-ready-old-retained");
    Require(world.TryAcknowledge(recovery.Buffers.Marker) && world.TryStep() &&
        world.Position == 2 && world.NextEdit == 42, "matching acknowledgement resumes covered movement");
    FinishWorker(recovery);
    SettleUpload(recovery);

    var retire = gpu.BlockRetirement(old.Buffers, specA.Buffers);
    Require(!gpu.IsComplete(retire) && specA.Cancelled, "retirement blocked");
    Require(!book.TryReserve("speculative", specA.Charge), "blocked retirement retains all speculative credits");
    SampleState("old-and-cancelled-held-behind-fence");
    gpu.OpenRetirement();
    gpu.Wait(retire);
    Require(gpu.StreamingComplete, "streaming fence settled");
    gpu.VerifyRetiredMarkers(old.Buffers.Marker, specA.Buffers.Marker);
    Drop(old);
    Drop(specA);
    Require(book.TryReserve("speculative", specA.Charge), "confirmed retirement frees credits");
    book.Release("speculative", specA.Charge);
    SampleState("retirement-confirmed");
    Drop(specB);
    Drop(movement);
    Drop(head);
    Drop(recovery);
    Require(book.Total == default && live.Count == 0, "balanced settlement");
    SampleState("settled");
}
finally
{
    gpu.Drain();
    foreach (var allocation in live)
        allocation.Buffers.Dispose();
}

var peaks = new
{
    cpuPayloadBytes = trace.Max(x => x.Materialized.Cpu),
    processPeakPrivateSampleBytes = trace.Max(x => x.PrivateBytes),
    gpuDefaultAllocationBytes = trace.Max(x => x.Materialized.Gpu),
    uploadAllocationBytes = trace.Max(x => x.Materialized.Upload),
    totalD3D12BufferBytes = trace.Max(x => x.Materialized.Gpu + x.Materialized.Upload) + gpu.ReadbackBytes,
    processPeakWorkingSetBytes = process.PeakWorkingSet64,
    dxgiLocalUsageBytes = trace.Max(x => x.Local),
    dxgiNonLocalUsageBytes = trace.Max(x => x.NonLocal),
    dxgiCombinedUsageBytes = trace.Max(x => x.Local + x.NonLocal)
};
Console.WriteLine(JsonSerializer.Serialize(new
{
    adapter = gpu.Adapter, gpu.VendorId, readbackAllocationBytes = gpu.ReadbackBytes,
    budgets = budget, baseline = trace[0], peaks, trace, finalRoute = world, checks = new
    {
        atomicAdmission = true, plusMinusOne = Ledger.BoundaryChecks(),
        protectedMovement = true, firstEdit = true, protectedRecovery = true, fenceRetention = true
    }
}));

Charge Make(ulong cpu, ulong geometry, int slots)
{
    var bytes = gpu.AllocationSize(geometry * MiB);
    return new Charge(cpu * MiB, bytes, bytes, slots);
}

Allocation Build(string lane, string name, ulong cpu, ulong bytes, uint marker)
{
    var charge = Make(cpu, bytes, 1);
    Require(book.TryReserve(lane, charge), $"{name} protected admission");
    SampleState($"{name}-reserved");
    var payload = new byte[checked((int)charge.Cpu)];
    Array.Fill(payload, (byte)marker);
    var buffers = gpu.Allocate(bytes * MiB, marker);
    var allocation = new Allocation(lane, charge, buffers, payload);
    live.Add(allocation);
    SampleState($"{name}-allocated");
    if (lane == "speculative")
    {
        gpu.EnqueueSpeculative(buffers);
        SampleState($"{name}-gpu-pending");
    }
    else
    {
        gpu.CopyAndVerify(buffers);
        SampleState($"{name}-gpu-verified");
    }
    return allocation;
}

void FinishWorker(Allocation allocation)
{
    var previous = book.Total;
    book.Release(allocation.Lane, new Charge(0, 0, 0, 1));
    allocation.Charge = allocation.Charge with { Slots = 0 };
    Require(book.Total == previous with { Slots = previous.Slots - 1 }, "worker release preserves all bytes");
    SampleState($"{allocation.Buffers.Marker}-worker-finished-buffers-retained");
}

void SettleUpload(Allocation allocation)
{
    allocation.Buffers.ReleaseUpload();
    book.Release(allocation.Lane, new Charge(0, 0, allocation.Charge.Upload, 0));
    allocation.Charge = allocation.Charge with { Upload = 0 };
    SampleState($"{allocation.Buffers.Marker}-upload-settled");
}

void Drop(Allocation allocation)
{
    allocation.Buffers.Dispose();
    allocation.Payload = null;
    live.Remove(allocation);
    book.Release(allocation.Lane, allocation.Charge);
}

void SampleState(string phase)
{
    var usage = gpu.Usage();
    var materialized = live.Aggregate(default(Charge), (sum, allocation) => sum + allocation.Charge);
    Require(materialized.Fits(book.Total), "materialized allocations were reserved");
    process.Refresh();
    trace.Add(new Sample(phase, route.Elapsed.TotalMilliseconds, book.Total, materialized, book.Snapshot(),
        usage.Local, usage.NonLocal, usage.LocalBudget, usage.NonLocalBudget,
        process.WorkingSet64, process.PrivateMemorySize64));
}

static void Require(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException($"Capacity assertion failed: {name}.");
}

readonly record struct Charge(ulong Cpu, ulong Gpu, ulong Upload, int Slots)
{
    public static Charge operator +(Charge a, Charge b) =>
        new(checked(a.Cpu + b.Cpu), checked(a.Gpu + b.Gpu), checked(a.Upload + b.Upload), checked(a.Slots + b.Slots));
    public static Charge operator -(Charge a, Charge b) =>
        new(checked(a.Cpu - b.Cpu), checked(a.Gpu - b.Gpu), checked(a.Upload - b.Upload), checked(a.Slots - b.Slots));
    internal bool Fits(Charge limit) => Cpu <= limit.Cpu && Gpu <= limit.Gpu && Upload <= limit.Upload && Slots <= limit.Slots;
}

sealed class Ledger(Dictionary<string, Charge> limits)
{
    private readonly Dictionary<string, Charge> _charged = limits.Keys.ToDictionary(x => x, _ => default(Charge));
    internal Charge Total => _charged.Values.Aggregate(default(Charge), (sum, item) => sum + item);
    internal Dictionary<string, Charge> Snapshot() => new(_charged);

    internal bool TryReserve(string lane, Charge charge)
    {
        var next = _charged[lane] + charge;
        if (!next.Fits(limits[lane]))
            return false;
        _charged[lane] = next;
        return true;
    }

    internal void Release(string lane, Charge charge) => _charged[lane] -= charge;

    internal static bool BoundaryChecks()
    {
        var max = new Charge(8, 8, 8, 2);
        foreach (var (below, delta) in new (Charge, Charge)[]
        {
            (max with { Cpu = 7 }, new(1, 0, 0, 0)), (max with { Gpu = 7 }, new(0, 1, 0, 0)),
            (max with { Upload = 7 }, new(0, 0, 1, 0)), (max with { Slots = 1 }, new(0, 0, 0, 1))
        })
        {
            var ledger = new Ledger(new() { ["lane"] = max });
            if (!ledger.TryReserve("lane", below) || !ledger.TryReserve("lane", delta) || ledger.Total != max ||
                ledger.TryReserve("lane", delta) || ledger.Total != max)
                throw new InvalidOperationException("Limit -1 / exact / +1 failed.");
        }
        return true;
    }
}

sealed class RouteState
{
    private readonly Queue<uint> _pending = new([41, 42]);
    public uint AuthoritativeRoot { get; private set; } = 40;
    public uint PresentedRoot { get; private set; } = 40;
    public int Position { get; private set; }
    public uint NextEdit => _pending.Peek();
    internal bool CoverageReady { get; set; }

    internal bool TryStep()
    {
        if (!CoverageReady || PresentedRoot != AuthoritativeRoot)
            return false;
        Position++;
        return true;
    }

    internal bool TryCommit(uint edit)
    {
        if (PresentedRoot != AuthoritativeRoot || _pending.Peek() != edit)
            return false;
        AuthoritativeRoot = _pending.Dequeue();
        return true;
    }

    internal bool TryAcknowledge(uint root)
    {
        if (root != AuthoritativeRoot)
            return false;
        PresentedRoot = root;
        return true;
    }
}

sealed class Allocation(string lane, Charge charge, Gpu.Buffers buffers, byte[] payload)
{
    internal string Lane { get; } = lane;
    internal Charge Charge { get; set; } = charge;
    internal Gpu.Buffers Buffers { get; } = buffers;
    internal byte[]? Payload { get; set; } = payload;
    internal bool Cancelled { get; set; }
}

sealed record Sample(string Phase, double ElapsedMs, Charge Reserved, Charge Materialized,
    Dictionary<string, Charge> Lanes, ulong Local, ulong NonLocal, ulong LocalBudget,
    ulong NonLocalBudget, long WorkingSetBytes, long PrivateBytes);
