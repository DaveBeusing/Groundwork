using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Groundwork.Voxels;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args is [var mode] && mode is "profiles" or "baseline" or "linear" or "smooth" or "paired")
{
    var recipe = new PlanetLandformRecipe(61, 0, 0, new(17, 0), new(11, 0), new(5, 0), 13, 7, 19);
    ulong[] seeds = [42, 20261111, ulong.MaxValue];
    foreach (var seed in seeds)
    {
        var field = new PlanetDensityField(new(new("1c078372-bceb-4d60-aa87-a0d217401797"),
            new("93f87211-c2ae-4415-a010-a14771ed243e"), seed, 1, 1, 1, recipe.ParameterSha256, 4096), recipe);
        var evaluator = new Relief(seed);
        if (mode == "profiles")
            Profiles(field, evaluator);
        else if (mode == "paired")
            MeasurePaired(field, evaluator);
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
    foreach (var (name, x, z) in Scenarios())
    {
        var samples = new LatticeCoordinate[PlanetBrickStore.VertexCount];
        var key = Relief.Vertex(x, z).Locate().Brick;
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

static (string Name, int X, int Z)[] Scenarios() =>
    [("open", 96, 0), ("ridge", -96, -96), ("junction", 32, -32)];

static void MeasurePaired(PlanetDensityField field, Relief relief)
{
    const int sweeps = 192;
    const int pairs = 5;
    const int edge = PlanetBrickStore.VertexEdge;
    Span<long> columns = stackalloc long[edge * edge];
    var baseline = new long[sweeps];
    var linear = new long[sweeps];
    var vertices = new LatticeCoordinate[PlanetBrickStore.VertexCount];
    foreach (var (name, x, z) in Scenarios())
    {
        var key = Relief.Vertex(x, z).Locate().Brick;
        var index = 0;
        for (var dz = 0; dz < edge; dz++)
        for (var dy = 0; dy < edge; dy++)
        for (var dx = 0; dx < edge; dx++)
            vertices[index++] = key.Vertex(dx, dy, dz);

        relief.FillLinearColumns(key, columns);
        for (var i = 0; i < vertices.Length; i++)
            if (ColumnDelta(vertices[i], i, columns) != relief.Delta(vertices[i], false))
                throw new InvalidOperationException("Linear brick column differs from the point evaluator.");

        long checksum = 0;
        for (var i = 0; i < 32; i++)
        {
            checksum ^= Baseline(field, vertices);
            checksum ^= Linear(field, relief, key, vertices, columns);
        }

        for (var pair = 0; pair < pairs; pair++)
        {
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
            Array.Sort(baseline);
            Array.Sort(linear);
            Console.WriteLine($"{field.Descriptor.Seed},{name},{pair + 1},{sweeps},{Ms(baseline, .95):F6},{Ms(linear, .95):F6},{Ms(baseline, .99):F6},{Ms(linear, .99):F6},{allocated},{checksum}");
        }

        void BaselineTimed(int i)
        {
            var start = Stopwatch.GetTimestamp();
            checksum = unchecked(checksum + Baseline(field, vertices));
            baseline[i] = Stopwatch.GetTimestamp() - start;
        }

        void LinearTimed(int i, Span<long> scratch)
        {
            var start = Stopwatch.GetTimestamp();
            checksum = unchecked(checksum + Linear(field, relief, key, vertices, scratch));
            linear[i] = Stopwatch.GetTimestamp() - start;
        }
    }

    static double Ms(long[] samples, double percentile) =>
        samples[(int)Math.Ceiling(percentile * samples.Length) - 1] * 1000d / Stopwatch.Frequency;
}

static long Baseline(PlanetDensityField field, LatticeCoordinate[] vertices)
{
    long sum = 0;
    foreach (var vertex in vertices)
        sum += field.SampleDensity(vertex);
    return sum;
}

static long Linear(PlanetDensityField field, Relief relief, LatticeBrick key, LatticeCoordinate[] vertices, Span<long> columns)
{
    const int edge = PlanetBrickStore.VertexEdge;
    relief.FillLinearColumns(key, columns);
    long sum = 0;
    for (var z = 0; z < edge; z++)
    for (var y = 0; y < edge; y++)
    {
        var row = columns.Slice(z * edge, edge);
        var start = (z * edge + y) * edge;
        var active = vertices[start].Y > 0;
        for (var x = 0; x < edge; x++)
            sum += field.SampleDensity(vertices[start + x]) - (active ? row[x] : 0);
    }
    return sum;
}

static long ColumnDelta(LatticeCoordinate vertex, int index, ReadOnlySpan<long> columns)
{
    const int edge = PlanetBrickStore.VertexEdge;
    return vertex.Y > 0 ? columns[index / (edge * edge) * edge + index % edge] : 0;
}
