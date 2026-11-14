using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Diagnostics.Tracing;
using Groundwork.Voxels;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args is [var mode, ..] && mode is "profiles" or "baseline" or "linear" or "smooth" or "paired")
{
    if (mode == "paired" && args.Length == 3)
        ThreadCycles.Pin(int.Parse(args[2], CultureInfo.InvariantCulture));
    var recipe = new PlanetLandformRecipe(61, 0, 0, new(17, 0), new(11, 0), new(5, 0), 13, 7, 19);
    ulong[] seeds = [42, 20261111, ulong.MaxValue];
    foreach (var seed in seeds)
    {
        var field = new PlanetDensityField(new(new("1c078372-bceb-4d60-aa87-a0d217401797"),
            new("93f87211-c2ae-4415-a010-a14771ed243e"), seed, 1, 1, 1, recipe.ParameterSha256, 4096), recipe);
        var evaluator = new Relief(seed);
        if (mode == "profiles")
        {
            Profiles(field, evaluator);
            foreach (var (name, feature, key) in Scenarios(field, evaluator))
            {
                VerifyLinearBrick(evaluator, key);
                Console.Error.WriteLine($"seed={seed} {name} surface={feature.X},{feature.Y},{feature.Z} brick={key.X},{key.Y},{key.Z}");
            }
        }
        else if (mode == "paired")
            MeasurePaired(field, evaluator, args.Length >= 2 ? args[1] : null);
        else
            Measure(field, evaluator, mode);
    }
    using var process = Process.GetCurrentProcess();
    Console.Error.WriteLine($"process peakWorkingSetBytes={process.PeakWorkingSet64} peakPagedBytes={process.PeakPagedMemorySize64}");
    return 0;
}
Console.Error.WriteLine("Specify profiles, baseline, linear, smooth or paired.");
return 2;

static void Profiles(PlanetDensityField field, Relief relief)
{
    var profiles = new List<(string Name, long X, long Z)>();
    for (var z = -128; z <= 128; z += 8)
    {
        var (ridge, valley, tributary) = relief.Centres(z);
        profiles.Add(("ridge", ridge, z));
        profiles.Add(("main", valley, z));
        profiles.Add(("control", valley + 64, z));
        if (z is >= -128 and <= 0)
            profiles.Add(("tributary", tributary, z));
    }

    var cut = relief.Centres(40).Ridge - 9;
    profiles.Add(("cut", cut, 40));
    profiles.Add(("cut-control", cut, 24));
    var signature = new byte[profiles.Count * 16];
    var reordered = new byte[signature.Length];
    for (var i = 0; i < profiles.Count; i++)
    {
        var (name, x, z) = profiles[i];
        var (linear, smooth) = relief.SampleProfile(x, z);
        if (name == "ridge" && Math.Min(linear, smooth) <= 0 ||
            name is "main" or "tributary" && Math.Max(linear, smooth) >= 0 ||
            name == "control" && (linear != 0 || smooth != 0))
            throw new InvalidOperationException($"Profile bound failed for {name} at {x},{z}.");
        WriteSample(i, signature);
        Console.WriteLine($"{field.Descriptor.Seed},{name},{x},{z},{field.SampleLandform(Relief.Vertex(x, z)).Elevation},{linear},{smooth}");
    }
    Parallel.For(0, profiles.Count, i => WriteSample(profiles.Count - i - 1, reordered));
    if (!signature.AsSpan().SequenceEqual(reordered))
        throw new InvalidOperationException("Parallel samples differ.");
    Console.Error.WriteLine($"seed={field.Descriptor.Seed} profileSha256={Convert.ToHexStringLower(SHA256.HashData(signature))}");

    void WriteSample(int index, byte[] destination)
    {
        var (_, x, z) = profiles[index];
        var (linear, smooth) = relief.SampleProfile(x, z);
        BinaryPrimitives.WriteInt64LittleEndian(destination.AsSpan(index * 16), linear);
        BinaryPrimitives.WriteInt64LittleEndian(destination.AsSpan(index * 16 + 8), smooth);
    }
}

static void Measure(PlanetDensityField field, Relief relief, string mode)
{
    const int repeats = 192;
    var timings = new long[repeats];
    foreach (var (name, feature, key) in Scenarios(field, relief))
    {
        var samples = new LatticeCoordinate[PlanetBrickStore.VertexCount];
        Console.Error.WriteLine($"seed={field.Descriptor.Seed} {name} surface={feature.X},{feature.Y},{feature.Z} brick={key.X},{key.Y},{key.Z}");
        var index = 0;
        for (var dz = 0; dz < PlanetBrickStore.VertexEdge; dz++)
        for (var dy = 0; dy < PlanetBrickStore.VertexEdge; dy++)
        for (var dx = 0; dx < PlanetBrickStore.VertexEdge; dx++)
            samples[index++] = key.Vertex(dx, dy, dz);
        long checksum = 0;
        for (var i = 0; i < 12; i++)
            checksum = SampleBrick();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < timings.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            checksum ^= SampleBrick();
            timings[i] = Stopwatch.GetTimestamp() - start;
        }
        var allocationDelta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Array.Sort(timings);
        Console.WriteLine($"{mode},{field.Descriptor.Seed},{name},{repeats},{Ms(.5):F4},{Ms(.95):F4},{Ms(.99):F4},{allocationDelta},{checksum}");

        double Ms(double percentile) => timings[(int)Math.Ceiling(percentile * repeats) - 1] * 1000d / Stopwatch.Frequency;
        long SampleBrick()
        {
            long sum = 0;
            foreach (var vertex in samples)
                sum += field.SampleDensity(vertex) - (mode == "baseline" ? 0 : relief.Delta(vertex, mode == "smooth"));
            return sum;
        }
    }
}

static IEnumerable<(string Name, LatticeCoordinate Feature, LatticeBrick Key)> Scenarios(
    PlanetDensityField field, Relief relief)
{
    var crest = relief.Centres(-96).Ridge;
    var (ridge, main, tributary) = relief.Centres(0);
    if (main != tributary || ridge == main)
        throw new InvalidOperationException("Expected the tributary to meet the main valley.");
    foreach (var (name, x, z, sign) in new[] { ("open", 96L, 0, 0), ("ridge", crest, -96, 1), ("junction", main, 0, -1) })
    {
        var y = Relief.Vertex(x, z).Y;
        long Density(long height)
        {
            var vertex = new LatticeCoordinate(x, height, z);
            return field.SampleDensity(vertex) - relief.Delta(vertex, false);
        }

        var delta = relief.Delta(new(x, y, z), false);
        if (Math.Sign(delta) != sign)
            throw new InvalidOperationException($"Unexpected relief at {name}.");
        // Find the last solid vertex below the candidate's first non-solid vertex.
        var low = y - 32;
        var high = y + 32;
        if (Density(low) >= 0 || Density(high) < 0)
            throw new InvalidOperationException($"Surface is outside the bounded search at {name}.");
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (Density(middle) < 0)
                low = middle;
            else
                high = middle;
        }
        var feature = new LatticeCoordinate(x, low, z);
        // Put the junction on the upper Z face so the brick includes the incoming branch.
        var key = (name == "junction" ? feature with { Z = z - 1 } : feature).Locate().Brick;
        var minimum = key.Vertex(0, 0, 0);
        var maximum = key.Vertex(32, 32, 32);
        if (x < minimum.X || x > maximum.X || z < minimum.Z || z > maximum.Z ||
            low < minimum.Y || high > maximum.Y || Density(low) >= 0 || Density(high) < 0)
            throw new InvalidOperationException($"Measured brick does not straddle the candidate surface at {name}.");
        yield return (name, feature, key);
    }
}

static void MeasurePaired(PlanetDensityField field, Relief relief, string? sweepPath)
{
    const int sweeps = 192;
    const int pairs = 5;
    const int edge = PlanetBrickStore.VertexEdge;
    Span<long> columns = stackalloc long[edge * edge];
    using var output = sweepPath is null ? TextWriter.Null : new StreamWriter(sweepPath, append: true);
    var markers = new SweepEvidence[2 * sweeps];
    var markerIndex = 0;
    var thread = ThreadCycles.NativeThreadId();
    var baseline = new long[sweeps];
    var linear = new long[sweeps];
    var baselineCycles = new ulong[sweeps];
    var linearCycles = new ulong[sweeps];
    var vertices = new LatticeCoordinate[PlanetBrickStore.VertexCount];
    foreach (var (name, feature, key) in Scenarios(field, relief))
    {
        Console.Error.WriteLine($"seed={field.Descriptor.Seed} {name} surface={feature.X},{feature.Y},{feature.Z} brick={key.X},{key.Y},{key.Z}");
        var index = 0;
        for (var dz = 0; dz < edge; dz++)
        for (var dy = 0; dy < edge; dy++)
        for (var dx = 0; dx < edge; dx++)
            vertices[index++] = key.Vertex(dx, dy, dz);

        VerifyLinearBrick(relief, key);
        if (sweepPath is not null)
            SweepTrace.Log.Anchor(Stopwatch.GetTimestamp());

        long checksum = 0;
        for (var i = 0; i < 32; i++)
        {
            checksum ^= Sweep(field, vertices, default);
            checksum ^= Linear(field, relief, key, vertices, columns);
        }

        for (var pair = 0; pair < pairs; pair++)
        {
            markerIndex = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < sweeps; i++)
            {
                if (((i + pair) & 1) == 0)
                {
                    BaselineTimed(i);
                    LinearTimed(i, columns);
                }
                else
                {
                    LinearTimed(i, columns);
                    BaselineTimed(i);
                }
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (sweepPath is not null)
                foreach (var mark in markers)
                    output.WriteLine($"{Environment.ProcessId},{thread},{Stopwatch.Frequency},{field.Descriptor.Seed},{name},{pair + 1},{mark.Index + 1},{(mark.Linear ? "linear" : "baseline")},{mark.Start},{mark.End},{mark.Cycles}");
            Array.Sort(baseline);
            Array.Sort(baselineCycles);
            Array.Sort(linearCycles);
            Array.Sort(linear);
            Console.WriteLine($"{field.Descriptor.Seed},{name},{pair + 1},{sweeps},{Ms(baseline, .95):F6},{Ms(linear, .95):F6},{Ms(baseline, .99):F6},{Ms(linear, .99):F6},{allocated},{checksum},{baselineCycles[PercentileIndex(sweeps, .99)]},{linearCycles[PercentileIndex(sweeps, .99)]}");
        }

        void BaselineTimed(int i)
        {
            var cycles = ThreadCycles.Read();
            var start = Stopwatch.GetTimestamp();
            checksum = unchecked(checksum + Sweep(field, vertices, default));
            var end = Stopwatch.GetTimestamp();
            baseline[i] = end - start;
            baselineCycles[i] = ThreadCycles.Read() - cycles;
            if (sweepPath is not null)
                markers[markerIndex++] = new(i, false, start, end, baselineCycles[i]);
        }

        void LinearTimed(int i, Span<long> scratch)
        {
            var cycles = ThreadCycles.Read();
            var start = Stopwatch.GetTimestamp();
            checksum = unchecked(checksum + Linear(field, relief, key, vertices, scratch));
            var end = Stopwatch.GetTimestamp();
            linear[i] = end - start;
            linearCycles[i] = ThreadCycles.Read() - cycles;
            if (sweepPath is not null)
                markers[markerIndex++] = new(i, true, start, end, linearCycles[i]);
        }
    }

    static double Ms(long[] samples, double percentile) =>
        samples[PercentileIndex(samples.Length, percentile)] * 1000d / Stopwatch.Frequency;
}

static int PercentileIndex(int length, double percentile) => (int)Math.Ceiling(percentile * length) - 1;

static long Linear(PlanetDensityField field, Relief relief, LatticeBrick key, LatticeCoordinate[] vertices, Span<long> columns)
{
    var active = relief.FillLinearColumns(key, columns);
    return Sweep(field, vertices, active ? columns : default);
}

static long Sweep(PlanetDensityField field, LatticeCoordinate[] vertices, ReadOnlySpan<long> columns)
{
    const int edge = PlanetBrickStore.VertexEdge;
    long sum = 0;
    for (var z = 0; z < edge; z++)
    for (var y = 0; y < edge; y++)
    {
        var start = (z * edge + y) * edge;
        if (columns.IsEmpty || vertices[start].Y <= 0)
        {
            for (var x = 0; x < edge; x++)
                sum += field.SampleDensity(vertices[start + x]);
        }
        else
        {
            var row = columns.Slice(z * edge, edge);
            for (var x = 0; x < edge; x++)
                sum += field.SampleDensity(vertices[start + x]) - row[x];
        }
    }
    return sum;
}

static void VerifyLinearBrick(Relief relief, LatticeBrick key)
{
    const int edge = PlanetBrickStore.VertexEdge;
    Span<long> columns = stackalloc long[edge * edge];
    relief.FillLinearColumns(key, columns);
    var i = 0;
    for (var z = 0; z < edge; z++)
    for (var y = 0; y < edge; y++)
    for (var x = 0; x < edge; x++)
    {
        var vertex = key.Vertex(x, y, z);
        if (ColumnDelta(vertex, i++, columns) != relief.Delta(vertex, false))
            throw new InvalidOperationException("Linear brick column differs from the point evaluator.");
    }
}

static long ColumnDelta(LatticeCoordinate vertex, int index, ReadOnlySpan<long> columns)
{
    const int edge = PlanetBrickStore.VertexEdge;
    return vertex.Y > 0 ? columns[index / (edge * edge) * edge + index % edge] : 0;
}

internal readonly record struct SweepEvidence(int Index, bool Linear, long Start, long End, ulong Cycles);

[EventSource(Name = "Groundwork-Landform-Sweeps", Guid = "03c45e87-2afa-4f65-8b53-5ad375f07b21")]
internal sealed class SweepTrace : EventSource
{
    internal static readonly SweepTrace Log = new();

    [Event(1)]
    public void Anchor(long qpc) => WriteEvent(1, qpc);
}
