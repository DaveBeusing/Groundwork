using System.Numerics;
using System.Runtime.CompilerServices;

namespace Groundwork.Voxels;

/// <summary>A brick reader and its sampling stride. Adjacent strides must be equal or differ by 2:1.</summary>
public readonly record struct VolumeMeshInput(PlanetBrickStore.BrickLease Brick, int Stride);

public readonly record struct VolumeVertex(Vector3 Position, Vector3 Normal, ushort Material);
public readonly record struct VolumeTriangle(VolumeVertex A, VolumeVertex B, VolumeVertex C);

/// <summary>Immutable mesh of one complete 2×2×2 brick neighborhood, relative to its canonical origin.</summary>
public sealed class PlanetVolumeMesh
{
    private readonly VolumeTriangle[] _triangles;

    internal PlanetVolumeMesh(PlanetDescriptor descriptor, LatticeCoordinate origin, ulong revision,
        byte lodMask, string planIdentity, VolumeTriangle[] triangles)
    {
        Descriptor = descriptor;
        Origin = origin;
        TerrainRevision = revision;
        LodMask = lodMask;
        PlanIdentity = planIdentity;
        _triangles = triangles;
    }

    public const uint MesherVersion = 2;
    public PlanetDescriptor Descriptor { get; }
    public LatticeCoordinate Origin { get; }
    public ulong TerrainRevision { get; }
    public byte LodMask { get; }
    public string PlanIdentity { get; }
    public ReadOnlySpan<VolumeTriangle> Triangles => _triangles;
}

/// <summary>
/// Modified Marching Cubes and Transvoxel extraction of an immutable brick neighborhood.
/// Coordinates are in lattice-cell units relative to the origin, independent of chart and render frame.
/// </summary>
public static class PlanetVolumeMesher
{
    public static readonly int TrianglePayloadBytes = Unsafe.SizeOf<VolumeTriangle>();
    private const int Quantization = 65536;
    private static readonly int[] FaceBits = [0, 1, 2, 5, 8, 7, 6, 3, 4];

    /// <summary>
    /// Builds a standalone eight-brick mesh. Use a shared VolumeMeshPlan for neighboring outputs.
    /// </summary>
    public static PlanetVolumeMesh? Build(ReadOnlySpan<VolumeMeshInput> inputs, ulong terrainRevision, long payloadLimit)
    {
        ArgumentOutOfRangeException.ThrowIfZero(terrainRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLimit);
        using var plan = VolumeMeshPlan.Borrow(inputs, terrainRevision);
        return Build(plan, plan.Minimum, payloadLimit);
    }

    /// <summary>
    /// Independently extracts an aligned eight-brick neighborhood using the plan's common LODs
    /// and density halo. Plan identity must match for meshes admitted together.
    /// Counts triangles before allocation. Returns null if their payload exceeds the limit.
    /// </summary>
    public static PlanetVolumeMesh? Build(VolumeMeshPlan plan, LatticeBrick minimum, long payloadLimit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLimit);
        plan.Enter();
        try
        {
            var region = plan.RegionIndex(minimum);
            var count = Extract(plan, region, null, out var inverted);
            if (inverted)
                throw new InvalidOperationException("Plan geometry no longer matches its sealed layout.");
            if (checked((long)count * TrianglePayloadBytes) > payloadLimit)
                return null;

            var triangles = new VolumeTriangle[count];
            var written = Extract(plan, region, triangles, out inverted);
            if (written != count || inverted)
                throw new InvalidOperationException("Input changed between mesh passes.");

            byte mask = 0;
            for (var slot = 0; slot < 8; slot++)
            {
                var c = Corner(slot);
                var index = region + (int)c.X + (int)c.Y * plan.Width + (int)c.Z * plan.Width * plan.Height;
                if (plan.Stride(index) == 2)
                    mask |= (byte)(1 << slot);
            }
            return new PlanetVolumeMesh(plan.Descriptor, minimum.Vertex(0, 0, 0), plan.TerrainRevision,
                mask, plan.Identity, triangles);
        }
        finally
        {
            plan.Exit();
        }
    }

    internal static bool HasInvertedTriangles(VolumeMeshPlan plan)
    {
        for (var z = 0; z < plan.Depth; z += 2)
        for (var y = 0; y < plan.Height; y += 2)
        for (var x = 0; x < plan.Width; x += 2)
        {
            _ = Extract(plan, (z * plan.Height + y) * plan.Width + x, null, out var inverted);
            if (inverted)
                return true;
        }
        return false;
    }

    private static int Extract(VolumeMeshPlan plan, int region, VolumeTriangle[]? output, out bool inverted)
    {
        inverted = false;
        var count = 0;
        Span<long> density = stackalloc long[13];
        Span<Vector3> positions = stackalloc Vector3[13];
        Span<Vector3> samplePositions = stackalloc Vector3[13];
        Span<VolumeVertex> vertices = stackalloc VolumeVertex[12];
        var anchor = plan.BrickPosition(region);
        for (var slot = 0; slot < 8; slot++)
        {
            var corner = Corner(slot);
            var brickIndex = region + (int)corner.X + (int)corner.Y * plan.Width + (int)corner.Z * plan.Width * plan.Height;
            var origin = plan.BrickPosition(brickIndex);
            var step = plan.Stride(brickIndex);
            for (var z = 0; z < 32; z += step)
            for (var y = 0; y < 32; y += step)
            for (var x = 0; x < 32; x += step)
            {
                var code = 0;
                for (var i = 0; i < 8; i++)
                {
                    var point = origin + new Vector3(x, y, z) + Corner(i) * step;
                    samplePositions[i] = point;
                    positions[i] = step == 1 ? FinePosition(point, brickIndex, plan) : point;
                    density[i] = plan.Read(point);
                    if (density[i] < 0)
                        code |= 1 << i;
                }

                count = Emit(TransvoxelTables.RegularCells[TransvoxelTables.RegularClass[code]],
                    TransvoxelTables.RegularVertices[code], density, positions, samplePositions,
                    false, vertices, plan, anchor, output, count, ref inverted);
            }

            if (step != 1)
                continue;
            for (var axis = 0; axis < 3; axis++)
            for (var direction = -1; direction <= 1; direction += 2)
            {
                var neighbor = plan.Neighbor(brickIndex, axis, direction);
                if (neighbor < 0 || plan.Stride(neighbor) != 2)
                    continue;
                var normal = Axis(axis);
                var u = Axis((axis + 1) % 3);
                var v = Axis((axis + 2) % 3);
                var start = origin + normal * (direction > 0 ? 32 : 0);
                for (var y = 0; y < 32; y += 2)
                for (var x = 0; x < 32; x += 2)
                {
                    var point = start + u * x + v * y;
                    for (var i = 0; i < 9; i++)
                    {
                        samplePositions[i] = point + u * (i % 3) + v * (i / 3);
                        positions[i] = FinePosition(samplePositions[i], brickIndex, plan);
                        density[i] = plan.Read(samplePositions[i]);
                    }
                    for (var i = 0; i < 4; i++)
                    {
                        samplePositions[9 + i] = point + u * ((i & 1) * 2) + v * ((i >> 1) * 2);
                        positions[9 + i] = samplePositions[9 + i];
                        density[9 + i] = plan.Read(samplePositions[9 + i]);
                    }
                    var code = 0;
                    for (var i = 0; i < FaceBits.Length; i++)
                        if (density[FaceBits[i]] < 0)
                            code |= 1 << i;
                    var cellClass = TransvoxelTables.TransitionClass[code];
                    count = Emit(TransvoxelTables.TransitionCells[cellClass & 127],
                        TransvoxelTables.TransitionVertices[code], density, positions, samplePositions,
                        ((cellClass & 128) != 0) == (direction > 0), vertices, plan, anchor, output, count, ref inverted);
                }
            }
        }
        return count;
    }

    private static int Emit(byte[] cell, byte[] edges, ReadOnlySpan<long> density,
        ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> samples, bool reverse,
        Span<VolumeVertex> vertices, VolumeMeshPlan plan, Vector3 anchor,
        VolumeTriangle[]? output, int count, ref bool inverted)
    {
        for (var i = 0; i < (cell[0] >> 4); i++)
        {
            var a = edges[i] >> 4;
            var b = edges[i] & 15;
            if (density[a] >= 0)
                (a, b) = (b, a);
            var numerator = -(Int128)density[a];
            var denominator = (Int128)density[b] - density[a];
            if (denominator <= 0)
                throw new InvalidOperationException("Table edge does not cross the isosurface.");

            // Keep exact-zero vertices off cell corners with a fixed 1/65536-cell symbolic offset.
            var weight = (int)Int128.Clamp((numerator * Quantization + denominator / 2) / denominator, 1, Quantization - 1);
            var position = Vector3.Zero;
            for (var axis = 0; axis < 3; axis++)
            {
                // Canonical Q17 arithmetic precedes the neighborhood-relative float conversion.
                var ticks = (long)(positions[a][axis] * 2) * Quantization +
                    (long)((positions[b][axis] - positions[a][axis]) * 2) * weight;
                position[axis] = (ticks - (long)anchor[axis] * (2 * Quantization)) / (float)(2 * Quantization);
            }
            var normal = Vector3.Lerp(plan.Gradient(samples[a]), plan.Gradient(samples[b]),
                weight / (float)Quantization);
            if (normal.LengthSquared() > 0)
                normal /= (float)Math.Sqrt((double)normal.X * normal.X + (double)normal.Y * normal.Y + (double)normal.Z * normal.Z);
            ushort material = 0;
            if (output is not null)
                material = plan.Material(samples[a]);
            vertices[i] = new VolumeVertex(position, normal, material);
        }

        for (var i = 1; i <= (cell[0] & 15) * 3; i += 3)
        {
            var a = vertices[cell[i]];
            var b = vertices[cell[i + (reverse ? 2 : 1)]];
            var c = vertices[cell[i + (reverse ? 1 : 2)]];
            if (Vector3.Cross(b.Position - a.Position, c.Position - a.Position) == Vector3.Zero)
                continue;
            if (Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal + b.Normal + c.Normal) <= 0)
                inverted = true;
            if (output is not null)
                output[count] = new VolumeTriangle(a, b, c);
            count++;
        }
        return count;
    }

    private static Vector3 FinePosition(Vector3 position, int brickIndex, VolumeMeshPlan plan)
    {
        var result = position;
        var origin = plan.BrickPosition(brickIndex);
        for (var axis = 0; axis < 3; axis++)
        for (var direction = -1; direction <= 1; direction += 2)
        {
            var boundary = origin[axis] + (direction > 0 ? 32 : 0);
            if (position[axis] != boundary)
                continue;
            var neighbor = plan.Neighbor(brickIndex, axis, direction);
            if (neighbor >= 0 && plan.Stride(neighbor) == 2)
                result -= Axis(axis) * (0.5f * direction);
        }
        return result;
    }
    private static Vector3 Corner(int i) => new(i & 1, (i >> 1) & 1, (i >> 2) & 1);
    private static Vector3 Axis(int axis) => axis switch
    {
        0 => Vector3.UnitX,
        1 => Vector3.UnitY,
        _ => Vector3.UnitZ
    };
}
