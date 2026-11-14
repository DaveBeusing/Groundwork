using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Groundwork.Voxels;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args is [var mode] && mode is "profiles" or "baseline" or "linear" or "smooth")
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
        else
            Measure(field, evaluator, mode);
    }
    using var process = Process.GetCurrentProcess();
    Console.Error.WriteLine($"process peakWorkingSetBytes={process.PeakWorkingSet64} peakPagedBytes={process.PeakPagedMemorySize64}");
    return 0;
}
Console.Error.WriteLine("Specify profiles, baseline, linear or smooth.");
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
    foreach (var (name, x, z) in new[] { ("open", 96, 0), ("ridge", -96, -96), ("junction", 32, -32) })
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
