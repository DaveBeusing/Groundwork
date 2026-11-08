using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length is not (5 or 6))
    throw new ArgumentException("Usage: <tables.cpp> <mc|dc> <fixture> <layout> <repetitions|geometry|collision-refined|junction-collision|mixed-collision|tangential-collision|tangential-baseline|tangential-full> [benchmark-repetitions]");

const int n = 32;
var algorithm = args[1];
var fixture = args[2];
var layout = args[3];
var refineCollision = args[4] == "collision-refined";
var junctionCollision = args[4] == "junction-collision";
var mixedCollision = args[4] == "mixed-collision";
var tangentialBaseline = args[4] == "tangential-baseline";
var tangentialFull = args[4] == "tangential-full";
var tangentialCollision = args[4] == "tangential-collision" || tangentialBaseline || tangentialFull;
var benchmarkCollision = args.Length == 6;
var geometry = args[4] == "geometry" || refineCollision || junctionCollision || mixedCollision || tangentialCollision;
var repetitions = geometry && !benchmarkCollision ? 1 :
    int.Parse(args[benchmarkCollision ? 5 : 4], System.Globalization.CultureInfo.InvariantCulture);
if (benchmarkCollision && !(tangentialCollision || junctionCollision) || repetitions < 1 || algorithm is not ("mc" or "dc") || layout is not ("uniform" or "face" or "corner" or "pressure"))
    throw new ArgumentOutOfRangeException(nameof(args));
var tables = new Tables(File.ReadAllText(args[0]));
Grid CreateGrid() => new(n, fixture, layout, refineCollision, junctionCollision, mixedCollision, tangentialCollision, tangentialBaseline, tangentialFull);
if (geometry && (algorithm != "mc" || layout == "pressure" || junctionCollision && layout != "corner" ||
    (mixedCollision || tangentialCollision) && layout != "face"))
    throw new ArgumentException("Use MC with uniform, face or corner geometry, corner junction collision, or face mixed collision.");
if (benchmarkCollision)
{
    Console.WriteLine(JsonSerializer.Serialize(CollisionBenchmark.Measure(CreateGrid, tables, fixture, args[4], repetitions)));
    return;
}
var grid = CreateGrid();
if (geometry)
{
    Mesh mesh;
    object? sharedBoundary = null;
    if (mixedCollision)
    {
        var left = Marching.ExtractRegularRegion(grid, tables, 0, grid.Mid);
        var right = Marching.ExtractRegularRegion(grid, tables, grid.Mid, grid.XCells);
        sharedBoundary = GeometryProbe.CheckSharedBoundary(left, right, grid.Extent / 2);
        left.AddRange(right);
        left.Sources!.AddRange(right.Sources!);
        mesh = left;
    }
    else
    {
        mesh = Marching.Extract(grid, tables, trackSources: true);
        if (tangentialCollision)
        {
            var left = new Mesh();
            var right = new Mesh();
            for (var i = 0; i < mesh.Count; i++)
            {
                // The fine owner publishes the transition strip up to the coarse face.
                var owner = mesh.Sources![i] == "regular-coarse" ? right : left;
                owner.Add(mesh[i]);
            }
            sharedBoundary = GeometryProbe.CheckSharedBoundary(left, right, grid.Extent / 2);
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        fixture, layout, mode = args[4], refinementEnabled = refineCollision, grid.ThinFeatureWitness, sharedBoundary,
        grid.CellSize, grid.EffectiveLayout, grid.LocalFineMin, grid.LocalFineMax, grid.SampleCount,
        triangles = mesh.Count, checksum = Audit.Digest(mesh),
        topology = Audit.Check(mesh, grid), geometry = GeometryProbe.Check(mesh, grid, fixture, layout)
    }, new JsonSerializerOptions { IncludeFields = true }));
    return;
}
var regions = layout == "pressure" ? 8 : 1;
var process = Process.GetCurrentProcess();
var times = new double[repetitions];
var allocations = new long[repetitions];
var heapDeltas = new long[repetitions];
var warmup = Extract();
var audit = Audit.Check(warmup[0], grid);
var checksum = Audit.Digest(warmup[0]);
var outputTriangles = warmup.Sum(x => x.Count);
warmup = [];
for (var i = 0; i < repetitions; i++)
{
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    var heap = GC.GetTotalMemory(forceFullCollection: false);
    var before = GC.GetAllocatedBytesForCurrentThread();
    var start = Stopwatch.GetTimestamp();
    var meshes = Extract();
    times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    allocations[i] = GC.GetAllocatedBytesForCurrentThread() - before;
    heapDeltas[i] = GC.GetTotalMemory(forceFullCollection: false) - heap;
    if (Audit.Digest(meshes[0]) != checksum || meshes.Sum(x => x.Count) != outputTriangles)
        throw new InvalidOperationException("Non-repeatable mesh.");
    GC.KeepAlive(meshes);
}
Array.Sort(times);
Array.Sort(allocations);
Array.Sort(heapDeltas);
process.Refresh();
Console.WriteLine(JsonSerializer.Serialize(new
{
    algorithm, fixture, layout, repetitions, regions, outputTriangles, checksum,
    p50ms = Percentile(times, .5), p95ms = Percentile(times, .95), p99ms = Percentile(times, .99),
    medianAllocatedBytes = Percentile(allocations, .5), maxAllocatedBytes = allocations[^1],
    medianHeapDeltaBytes = Percentile(heapDeltas, .5), peakWorkingSetBytes = process.PeakWorkingSet64,
    audit
}));

List<Mesh> Extract()
{
    var meshes = new List<Mesh>(regions);
    for (var i = 0; i < regions; i++)
        meshes.Add(algorithm == "mc" ? Marching.Extract(grid, tables) : Dual.Extract(grid));
    return meshes;
}

static T Percentile<T>(T[] sorted, double p) => sorted[(int)Math.Ceiling(p * sorted.Length) - 1];

readonly record struct I3(int X, int Y, int Z)
{
    internal int this[int i] => i == 0 ? X : i == 1 ? Y : Z;
    internal Vector3 Position => new(X, Y, Z);
    public static I3 operator +(I3 a, I3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static I3 operator -(I3 a, I3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static I3 operator *(I3 a, int k) => new(a.X * k, a.Y * k, a.Z * k);
    internal static I3 Axis(int i) => i == 0 ? new(1, 0, 0) : i == 1 ? new(0, 1, 0) : new(0, 0, 1);
    internal static I3 Corner(int i) => new(i & 1, (i >> 1) & 1, i >> 2);
}

sealed class Grid
{
    private readonly float[] _density;
    private readonly string _fixture;
    private readonly string _layout;
    internal int N { get; }
    internal int Mid => N / 2;
    internal I3 BrickSize { get; }
    internal int SampleCount => _density.Length;
    internal float CellSize { get; }
    internal float Extent => N * CellSize;
    internal string EffectiveLayout => _layout;
    internal Vector3? ThinFeatureWitness { get; }
    internal int XCells { get; }
    private readonly bool _mixedCollision;
    private readonly float[][]? _axes;
    private readonly I3 _cellCounts;
    internal Vector3? LocalFineMin { get; }
    internal Vector3? LocalFineMax { get; }

    // Both regions share the half-cell Y/Z face grid. The right region
    // changes only X spacing, so the plane contour is identical on both sides.
    internal Vector3 Position(I3 p)
    {
        if (_axes is { } axes)
            return new Vector3(axes[0][p.X], axes[1][p.Y], axes[2][p.Z]);
        var position = p.Position * CellSize;
        if (_mixedCollision && p.X > Mid)
            position.X = Extent / 2 + (p.X - Mid);
        return position;
    }

    internal Grid(int n, string fixture, string layout, bool refineCollision, bool junctionCollision, bool mixedCollision, bool tangentialCollision, bool tangentialBaseline, bool tangentialFull)
    {
        _fixture = fixture;
        var localRefinement = tangentialCollision && !tangentialBaseline && !tangentialFull;
        var thinSolid = refineCollision || tangentialCollision && !tangentialBaseline
            ? FindThinSolid(n, localRefinement) : null;
        ThinFeatureWitness = thinSolid?.Witness;
        // This probe publishes one collision region. Promote it as a unit so
        // collision never crosses a coarse seam inside the protected feature.
        _mixedCollision = mixedCollision;
        var useFineGrid = refineCollision && ThinFeatureWitness.HasValue || mixedCollision;
        // Keep a true 2:1 seam. If the thin-solid scan triggers, even the
        // coarse side must use at most a quarter cell to retain the contour.
        CellSize = tangentialCollision && ThinFeatureWitness.HasValue ? 0.125f :
            junctionCollision || tangentialCollision ? 0.25f : useFineGrid ? 0.5f : 1f;
        N = (int)(n / CellSize);
        _layout = useFineGrid || junctionCollision ? "uniform" : layout;
        if (localRefinement && thinSolid is { } feature)
        {
            var center = new Vector3(n / 2f);
            var radius = Vector3.Max(center - feature.Min, feature.Max - center) + new Vector3(1.5f);
            // Half-unit alignment keeps each band boundary on an even index,
            // so each coarse face always pairs with two complete fine cells.
            radius = new Vector3(Aligned(radius.X), Aligned(radius.Y), Aligned(radius.Z));
            var min = Vector3.Max(Vector3.Zero, center - radius);
            var max = Vector3.Min(new Vector3(n), center + radius);
            LocalFineMin = min;
            LocalFineMax = max;
            _axes = [Axis(min.X, max.X), Axis(min.Y, max.Y), Axis(min.Z, max.Z)];
        }

        _cellCounts = _axes is { } axes
            ? new I3(axes[0].Length - 1, axes[1].Length - 1, axes[2].Length - 1)
            : new I3(mixedCollision ? Mid + n / 2 : N, N, N);
        XCells = _cellCounts.X;
        BrickSize = _axes is null ? new I3(Mid, Mid, Mid) :
            new I3(_cellCounts.X / 2, _cellCounts.Y / 2, _cellCounts.Z / 2);
        _density = new float[(_cellCounts.X + 1) * (_cellCounts.Y + 1) * (_cellCounts.Z + 1)];
        for (var z = 0; z <= _cellCounts.Z; z++)
        for (var y = 0; y <= _cellCounts.Y; y++)
        for (var x = 0; x <= _cellCounts.X; x++)
        {
            var p = new I3(x, y, z);
            _density[DensityIndex(p)] = Field(Position(p));
        }

        static float Aligned(float radius) => MathF.Ceiling(radius * 2) * 0.5f;

        float[] Axis(float min, float max)
        {
            var positions = new List<float>();
            for (var p = 0f; p < n; p += p >= min && p < max ? CellSize : 0.25f)
                positions.Add(p);
            positions.Add(n);
            return positions.ToArray();
        }
    }

    private (Vector3 Witness, Vector3 Min, Vector3 Max)? FindThinSolid(int extent, bool collectBounds)
    {
        (Vector3 Witness, Vector3 Min, Vector3 Max)? feature = null;
        // A solid core with air on opposite sides a unit away witnesses a
        // feature too narrow for the normal two-unit coarse sampling.
        for (var z = 0.25f; z < extent; z += 0.5f)
        for (var y = 0.25f; y < extent; y += 0.5f)
        for (var x = 0.25f; x < extent; x += 0.5f)
        {
            var p = new Vector3(x, y, z);
            if (Field(p) > -0.25f)
                continue;
            for (var axis = 0; axis < 3; axis++)
            {
                var offset = I3.Axis(axis).Position;
                if (Field(p - offset) > 0 && Field(p + offset) > 0)
                {
                    if (!collectBounds)
                        return (p, p, p);
                    feature = feature is { } prior
                        ? (prior.Witness, Vector3.Min(prior.Min, p), Vector3.Max(prior.Max, p))
                        : (p, p, p);
                    break;
                }
            }
        }
        return feature;
    }

    internal int Step(I3 p) => _layout switch
    {
        "uniform" => 1,
        "corner" => p.X < BrickSize.X && p.Y < BrickSize.Y && p.Z < BrickSize.Z ? 1 : 2,
        _ => p.X < BrickSize.X ? 1 : 2,
    };

    internal int Index(I3 p, int width) => (p.Z * width + p.Y) * width + p.X;
    private int DensityIndex(I3 p) => (p.Z * (_cellCounts.Y + 1) + p.Y) * (_cellCounts.X + 1) + p.X;
    internal float At(I3 p) => _density[DensityIndex(p)];
    internal bool ContainsCell(I3 p) => (uint)p.X < _cellCounts.X && (uint)p.Y < _cellCounts.Y && (uint)p.Z < _cellCounts.Z;

    internal GrazingRayFallback? CreateGrazingFallback()
    {
        if (!LocalFineMin.HasValue)
            return null;
        var fixture = _fixture;
        return new GrazingRayFallback(p => EvaluateField(fixture, p), Extent);
    }

    internal float Field(Vector3 p) => EvaluateField(_fixture, p);

    private static float EvaluateField(string fixture, Vector3 p)
    {
        var center = new Vector3(15.71f, 16.23f, 15.83f);
        return fixture switch
        {
            "cavity" => MathF.Max(Sphere(p, center, 11.2f), -Sphere(p, center + new Vector3(.22f, .11f, -.35f), 5.42f)),
            "overhang" => MathF.Min(Box(p, new(15.37f, 11.47f, 15.33f), new(2.65f, 6.82f, 2.71f)),
                Box(p, new(16.23f, 18.17f, 15.91f), new(8.29f, 2.26f, 5.63f))),
            "bridge" => MathF.Min(MathF.Min(Sphere(p, new(8.67f, 16.19f, 15.53f), 4.16f),
                Sphere(p, new(23.17f, 16.13f, 15.49f), 4.26f)),
                Box(p, new(15.84f, 16.15f, 15.51f), new(7.22f, .77f, .78f))),
            "seam" => Sphere(p, center + new Vector3(-2.1f, 2.3f, 1.4f), 9.38f),
            "junction" => Sphere(p, new(11.37f, 10.8f, 11.8f), 8.15f),
            _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
        };
    }

    internal Vector3 Gradient(Vector3 p)
    {
        const float d = .125f;
        var x = new Vector3(d, 0, 0);
        var y = new Vector3(0, d, 0);
        var z = new Vector3(0, 0, d);
        return new(Field(p + x) - Field(p - x), Field(p + y) - Field(p - y), Field(p + z) - Field(p - z));
    }

    internal Vector3 FinePosition(I3 p, I3 brick)
    {
        var shifted = Position(p);
        for (var axis = 0; axis < 3; axis++)
        {
            if (p[axis] != BrickSize[axis])
                continue;
            var dir = brick[axis] == 0 ? 1 : -1;
            var next = brick + I3.Axis(axis) * (BrickSize[axis] * dir);
            if (ContainsCell(next) && Step(next) == 2)
                shifted -= I3.Axis(axis).Position * (.5f * dir * CellSize);
        }
        return shifted;
    }

    private static float Sphere(Vector3 p, Vector3 c, float r) => Vector3.Distance(p, c) - r;
    private static float Box(Vector3 p, Vector3 c, Vector3 e)
    {
        var q = Vector3.Abs(p - c) - e;
        return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0);
    }
}

readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C);

sealed class Mesh : List<Triangle>
{
    internal List<string>? Sources { get; init; }
    internal int Missing { get; set; }
    internal int TransitionTriangles { get; set; }
    internal void Emit(Vector3 a, Vector3 b, Vector3 c, bool reverse) =>
        Add(reverse ? new(a, c, b) : new(a, b, c));
}

sealed class Tables
{
    internal int[] RegularClass { get; }
    internal int[][] RegularCells { get; }
    internal int[][] RegularVertices { get; }
    internal int[] TransitionClass { get; }
    internal int[][] TransitionCells { get; }
    internal int[][] TransitionVertices { get; }

    internal Tables(string source)
    {
        source = Regex.Replace(source, @"//[^\r\n]*", "");
        RegularClass = Numbers(Body("regularCellClass"));
        RegularCells = Rows("regularCellData");
        RegularVertices = Rows("regularVertexData");
        TransitionClass = Numbers(Body("transitionCellClass"));
        TransitionCells = Rows("transitionCellData");
        TransitionVertices = Rows("transitionVertexData");
        if (RegularClass.Length != 256 || RegularCells.Length != 16 || RegularVertices.Length != 256 ||
            TransitionClass.Length != 512 || TransitionCells.Length != 56 || TransitionVertices.Length != 512)
            throw new InvalidDataException("Invalid table dimensions.");

        string Body(string name)
        {
            var start = source.IndexOf(name, StringComparison.Ordinal);
            var first = source.IndexOf('{', start);
            var end = source.IndexOf("};", first, StringComparison.Ordinal);
            return source[(first + 1)..end];
        }

        int[][] Rows(string name)
        {
            var result = new List<int[]>();
            var depth = 0;
            var start = 0;
            var body = Body(name);
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] == '{' && depth++ == 0)
                    start = i + 1;
                if (body[i] == '}' && --depth == 0)
                    result.Add(Numbers(body[start..i]));
            }
            return result.ToArray();
        }
    }

    private static int[] Numbers(string text) => Regex.Matches(text, "0x[0-9A-Fa-f]+|[0-9]+")
        .Select(x => x.Value.StartsWith("0x", StringComparison.Ordinal) ? Convert.ToInt32(x.Value[2..], 16) :
            int.Parse(x.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
}

static class Marching
{
    private static readonly int[] FaceBits = [0, 1, 2, 5, 8, 7, 6, 3, 4];
    internal static Mesh Extract(Grid grid, Tables tables, bool trackSources = false)
    {
        var output = new Mesh { Sources = trackSources ? [] : null };
        var mid = grid.BrickSize;
        Span<float> d = stackalloc float[13];
        Span<Vector3> p = stackalloc Vector3[13];
        for (var brickIndex = 0; brickIndex < 8; brickIndex++)
        {
            var corner = I3.Corner(brickIndex);
            var brick = new I3(corner.X * mid.X, corner.Y * mid.Y, corner.Z * mid.Z);
            var step = grid.Step(brick);
            for (var z = 0; z < mid.Z; z += step)
            for (var y = 0; y < mid.Y; y += step)
            for (var x = 0; x < mid.X; x += step)
            {
                var origin = brick + new I3(x, y, z);
                EmitRegular(grid, tables, output, origin, step, brick, d, p);
                RecordSource(step == 1 ? "regular-fine" : "regular-coarse");
            }

            if (step != 1)
                continue;
            for (var axis = 0; axis < 3; axis++)
            for (var direction = -1; direction <= 1; direction += 2)
            {
                var normal = I3.Axis(axis);
                var neighbor = brick + normal * (direction * mid[axis]);
                if (!grid.ContainsCell(neighbor) || grid.Step(neighbor) != 2)
                    continue;
                var u = I3.Axis((axis + 1) % 3);
                var v = I3.Axis((axis + 2) % 3);
                var basePoint = brick + normal * (direction > 0 ? mid[axis] : 0);
                for (var y = 0; y < mid[(axis + 2) % 3]; y += 2)
                for (var x = 0; x < mid[(axis + 1) % 3]; x += 2)
                {
                    var origin = basePoint + u * x + v * y;
                    for (var c = 0; c < 9; c++)
                    {
                        var q = origin + u * (c % 3) + v * (c / 3);
                        d[c] = grid.At(q);
                        p[c] = grid.FinePosition(q, brick);
                    }
                    for (var c = 0; c < 4; c++)
                    {
                        var q = origin + u * ((c & 1) * 2) + v * ((c >> 1) * 2);
                        d[9 + c] = grid.At(q);
                        p[9 + c] = grid.Position(q);
                    }
                    var code = 0;
                    for (var i = 0; i < FaceBits.Length; i++)
                        if (d[FaceBits[i]] < 0)
                            code |= 1 << i;
                    // The table inversion bit and face direction determine the transition winding.
                    var transitionClass = tables.TransitionClass[code];
                    var reverse = ((transitionClass & 128) != 0) == (direction > 0);
                    var before = output.Count;
                    Emit(output, tables.TransitionCells[transitionClass & 127],
                        tables.TransitionVertices[code], d, p, reverse);
                    output.TransitionTriangles += output.Count - before;
                    RecordSource("transition");
                }
            }
        }
        return output;

        void RecordSource(string source)
        {
            if (output.Sources is { } sources)
                while (sources.Count < output.Count)
                    sources.Add(source);
        }
    }

    internal static Mesh ExtractRegularRegion(Grid grid, Tables tables, int startX, int endX)
    {
        var output = new Mesh { Sources = [] };
        Span<float> d = stackalloc float[8];
        Span<Vector3> p = stackalloc Vector3[8];
        for (var z = 0; z < grid.N; z++)
        for (var y = 0; y < grid.N; y++)
        for (var x = startX; x < endX; x++)
            EmitRegular(grid, tables, output, new I3(x, y, z), 1, null, d, p);
        var source = startX == 0 ? "collision-left" : "collision-right";
        for (var i = 0; i < output.Count; i++)
            output.Sources.Add(source);
        return output;
    }

    private static void EmitRegular(Grid grid, Tables tables, Mesh output, I3 origin, int step, I3? brick,
        Span<float> d, Span<Vector3> p)
    {
        var code = 0;
        for (var c = 0; c < 8; c++)
        {
            d[c] = grid.At(origin + I3.Corner(c) * step);
            if (d[c] < 0)
                code |= 1 << c;
        }
        if (code is 0 or 255)
            return;
        for (var c = 0; c < 8; c++)
        {
            var q = origin + I3.Corner(c) * step;
            p[c] = step == 1 && brick is { } fineBrick ? grid.FinePosition(q, fineBrick) : grid.Position(q);
        }
        Emit(output, tables.RegularCells[tables.RegularClass[code]], tables.RegularVertices[code], d, p, false);
    }

    private static void Emit(Mesh output, int[] cell, int[] edges, ReadOnlySpan<float> d, ReadOnlySpan<Vector3> p, bool reverse)
    {
        var count = cell[0] >> 4;
        if (count == 0)
            return;
        Span<Vector3> vertices = stackalloc Vector3[12];
        for (var i = 0; i < count; i++)
        {
            var a = (edges[i] >> 4) & 15;
            var b = edges[i] & 15;
            if (d[a] >= 0)
                (a, b) = (b, a);
            if (!(d[a] < 0 && d[b] >= 0))
                throw new InvalidDataException("Table edge does not cross the isosurface.");
            vertices[i] = Vector3.Lerp(p[a], p[b], d[a] / (d[a] - d[b]));
        }
        for (var i = 1; i <= (cell[0] & 15) * 3; i += 3)
            output.Emit(vertices[cell[i]], vertices[cell[i + 1]], vertices[cell[i + 2]], reverse);
    }
}

static class Leaves
{
    internal static IEnumerable<I3> Bricks(int mid)
    {
        for (var i = 0; i < 8; i++)
            yield return I3.Corner(i) * mid;
    }
}

static class Dual
{
    internal static Mesh Extract(Grid grid)
    {
        var n = grid.N;
        var owner = new int[n * n * n];
        var vertices = new List<Vector3>();
        var present = new List<bool>();
        var steps = new List<int>();
        foreach (var brick in Leaves.Bricks(grid.Mid))
        {
            var step = grid.Step(brick);
            for (var z = 0; z < grid.Mid; z += step)
            for (var y = 0; y < grid.Mid; y += step)
            for (var x = 0; x < grid.Mid; x += step)
            {
                var p = brick + new I3(x, y, z);
                var id = vertices.Count;
                present.Add(TryFit(grid, p, step, out var vertex));
                vertices.Add(vertex);
                steps.Add(step);
                for (var dz = 0; dz < step; dz++)
                for (var dy = 0; dy < step; dy++)
                for (var dx = 0; dx < step; dx++)
                    owner[grid.Index(p + new I3(dx, dy, dz), n)] = id;
            }
        }

        var output = new Mesh();
        Span<int> ring = stackalloc int[4];
        Span<int> polygon = stackalloc int[4];
        var seen = new HashSet<(int, int, int, int)>();
        for (var axis = 0; axis < 3; axis++)
        {
            var direction = I3.Axis(axis);
            var u = I3.Axis((axis + 1) % 3);
            var v = I3.Axis((axis + 2) % 3);
            for (var z = 0; z < n; z++)
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var p = new I3(x, y, z);
                if (p[(axis + 1) % 3] == 0 || p[(axis + 2) % 3] == 0)
                    continue;
                ring[0] = owner[grid.Index(p - u - v, n)];
                ring[1] = owner[grid.Index(p - v, n)];
                ring[2] = owner[grid.Index(p, n)];
                ring[3] = owner[grid.Index(p - u, n)];
                var span = Math.Min(Math.Min(steps[ring[0]], steps[ring[1]]), Math.Min(steps[ring[2]], steps[ring[3]]));
                if (span == 2 && ((x | y | z) & 1) != 0)
                    continue;
                if ((grid.At(p) < 0) == (grid.At(p + direction * span) < 0))
                    continue;
                var count = 0;
                foreach (var id in ring)
                    if (!polygon[..count].Contains(id))
                        polygon[count++] = id;
                if (count < 3)
                    continue;
                var available = true;
                for (var i = 0; i < count; i++)
                    available &= present[polygon[i]];
                if (!available)
                {
                    output.Missing++;
                    continue;
                }
                ring.Fill(-1);
                polygon[..count].CopyTo(ring);
                ring.Sort();
                if (!seen.Add((ring[0], ring[1], ring[2], ring[3])))
                    continue;
                // The owner ring follows the positive edge axis. Orient the entire face
                // from solid to empty, including both triangles of a nonplanar quad.
                var reverse = grid.At(p) >= 0;
                for (var i = 1; i < count - 1; i++)
                    output.Emit(vertices[polygon[0]], vertices[polygon[i]], vertices[polygon[i + 1]], reverse);
            }
        }
        return output;
    }

    private static bool TryFit(Grid grid, I3 p, int step, out Vector3 result)
    {
        Span<float> density = stackalloc float[8];
        for (var i = 0; i < 8; i++)
            density[i] = grid.At(p + I3.Corner(i) * step);
        var mass = Vector3.Zero;
        var rhs = Vector3.Zero;
        float xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
        var count = 0;
        for (var a = 0; a < 8; a++)
        for (var axis = 0; axis < 3; axis++)
        {
            var mask = 1 << axis;
            var b = a | mask;
            if (a == b || (density[a] < 0) == (density[b] < 0))
                continue;
            var hit = Vector3.Lerp(I3.Corner(a).Position, I3.Corner(b).Position, density[a] / (density[a] - density[b]));
            var normal = grid.Gradient(p.Position + hit * step);
            if (normal.LengthSquared() < 1e-12f)
                normal = I3.Axis(axis).Position;
            else
                normal = Vector3.Normalize(normal);
            xx += normal.X * normal.X;
            yy += normal.Y * normal.Y;
            zz += normal.Z * normal.Z;
            xy += normal.X * normal.Y;
            xz += normal.X * normal.Z;
            yz += normal.Y * normal.Z;
            rhs += normal * Vector3.Dot(normal, hit);
            mass += hit;
            count++;
        }
        result = default;
        if (count == 0)
            return false;
        mass /= count;
        const float regularization = .002f;
        xx += regularization;
        yy += regularization;
        zz += regularization;
        rhs += mass * regularization;
        var row0 = new Vector3(xx, xy, xz);
        var row1 = new Vector3(xy, yy, yz);
        var row2 = new Vector3(xz, yz, zz);
        var c0 = Vector3.Cross(row1, row2);
        var determinant = Vector3.Dot(row0, c0);
        var fit = determinant > 1e-12f
            ? (rhs.X * c0 + rhs.Y * Vector3.Cross(row2, row0) + rhs.Z * Vector3.Cross(row0, row1)) / determinant
            : mass;
        result = p.Position + Vector3.Clamp(fit, new(.0001f), new(.9999f)) * step;
        return true;
    }
}

static class Audit
{
    internal static object Check(Mesh mesh, Grid grid)
    {
        var ids = new Dictionary<(int, int, int), int>();
        var edges = new Dictionary<(int, int), (int Count, int Direction)>();
        var graph = new List<List<int>>();
        var degenerates = 0;
        var maxResidual = 0f;
        var minNormalDot = 1f;
        foreach (var t in mesh)
        {
            var a = Id(t.A);
            var b = Id(t.B);
            var c = Id(t.C);
            var ab = t.B - t.A;
            var ac = t.C - t.A;
            var normal = Vector3.Cross(ab, ac);
            // Compare the squared sine of the corner angle so valid tiny faces survive.
            var nearlyCollinear = normal.LengthSquared() <= 1e-12f * ab.LengthSquared() * ac.LengthSquared();
            if (a == b || b == c || c == a || nearlyCollinear)
            {
                degenerates++;
                continue;
            }
            var gradient = grid.Gradient((t.A + t.B + t.C) / 3);
            if (gradient.LengthSquared() > 0)
                minNormalDot = Math.Min(minNormalDot, Vector3.Dot(Vector3.Normalize(normal), Vector3.Normalize(gradient)));
            maxResidual = Math.Max(maxResidual, Math.Max(MathF.Abs(grid.Field(t.A)), Math.Max(MathF.Abs(grid.Field(t.B)), MathF.Abs(grid.Field(t.C)))));
            Edge(a, b);
            Edge(b, c);
            Edge(c, a);
        }
        var components = 0;
        var visited = new HashSet<int>();
        for (var i = 0; i < graph.Count; i++)
        {
            if (graph[i].Count == 0 || !visited.Add(i))
                continue;
            components++;
            var pending = new Queue<int>();
            pending.Enqueue(i);
            while (pending.TryDequeue(out var next))
                foreach (var neighbor in graph[next])
                    if (visited.Add(neighbor))
                        pending.Enqueue(neighbor);
        }
        return new
        {
            weldedVertices = ids.Count, components, degenerates, missingDualFaces = mesh.Missing, mesh.TransitionTriangles,
            openEdges = edges.Values.Count(x => x.Count == 1),
            nonManifoldEdges = edges.Values.Count(x => x.Count > 2),
            windingEdges = edges.Values.Count(x => x.Count == 2 && x.Direction != 0),
            maxFieldResidual = maxResidual, minNormalDot
        };

        int Id(Vector3 p)
        {
            var key = ((int)MathF.Round(p.X * 10000), (int)MathF.Round(p.Y * 10000), (int)MathF.Round(p.Z * 10000));
            if (!ids.TryGetValue(key, out var id))
            {
                id = graph.Count;
                ids.Add(key, id);
                graph.Add([]);
            }
            return id;
        }

        void Edge(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            var prior = edges.GetValueOrDefault(key);
            edges[key] = (prior.Count + 1, prior.Direction + (a < b ? 1 : -1));
            graph[a].Add(b);
            graph[b].Add(a);
        }
    }

    internal static string Digest(Mesh mesh)
    {
        var hash = 14695981039346656037UL;
        foreach (var t in mesh)
        {
            Add(t.A);
            Add(t.B);
            Add(t.C);
        }
        return hash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);

        void Add(Vector3 p)
        {
            hash = unchecked((hash ^ BitConverter.SingleToUInt32Bits(p.X)) * 1099511628211UL);
            hash = unchecked((hash ^ BitConverter.SingleToUInt32Bits(p.Y)) * 1099511628211UL);
            hash = unchecked((hash ^ BitConverter.SingleToUInt32Bits(p.Z)) * 1099511628211UL);
        }
    }
}
