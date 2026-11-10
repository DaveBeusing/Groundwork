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
        byte lodMask, VolumeTriangle[] triangles)
    {
        Descriptor = descriptor;
        Origin = origin;
        TerrainRevision = revision;
        LodMask = lodMask;
        _triangles = triangles;
    }

    public const uint MesherVersion = 1;
    public PlanetDescriptor Descriptor { get; }
    public LatticeCoordinate Origin { get; }
    public ulong TerrainRevision { get; }
    public byte LodMask { get; }
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
    /// Counts output before allocation and returns null if the triangle payload quota is insufficient.
    /// Inputs must contain all eight bricks in a complete neighborhood, in any order, at stride 1 or 2.
    /// Fine requests are closed to their bounding Cartesian subcube to avoid concave transition junctions.
    /// Unresolved outward-normal inversions trigger a full-neighborhood refinement or fail closed.
    /// LodMask reports the resulting strides. The caller retains leases until this operation returns.
    /// External region boundaries require separate coverage and halo qualification before streaming use.
    /// </summary>
    public static PlanetVolumeMesh? Build(ReadOnlySpan<VolumeMeshInput> inputs, ulong terrainRevision, long payloadLimit)
    {
        ArgumentOutOfRangeException.ThrowIfZero(terrainRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLimit);
        if (inputs.Length != 8)
            throw new ArgumentException("A complete eight-brick neighborhood is required.", nameof(inputs));

        var minX = long.MaxValue;
        var minY = long.MaxValue;
        var minZ = long.MaxValue;
        PlanetDescriptor? descriptor = null;
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input.Brick);
            if (input.Stride is not (1 or 2))
                throw new ArgumentOutOfRangeException(nameof(inputs), "Only strides 1 and 2 are supported.");
            descriptor ??= input.Brick.Descriptor;
            if (descriptor != input.Brick.Descriptor)
                throw new ArgumentException("All bricks must share the exact source descriptor.", nameof(inputs));
            _ = input.Brick.ReadDensity(0, 0, 0);
            minX = Math.Min(minX, input.Brick.Key.X);
            minY = Math.Min(minY, input.Brick.Key.Y);
            minZ = Math.Min(minZ, input.Brick.Key.Z);
        }

        Span<int> order = stackalloc int[8];
        order.Fill(-1);
        byte mask = 0;
        for (var i = 0; i < inputs.Length; i++)
        {
            var key = inputs[i].Brick.Key;
            var x = checked(key.X - minX);
            var y = checked(key.Y - minY);
            var z = checked(key.Z - minZ);
            if (x > 1 || y > 1 || z > 1)
                throw new ArgumentException("Bricks must form one contiguous two-brick cube.", nameof(inputs));
            var slot = (int)(x + 2 * y + 4 * z);
            if (order[slot] != -1)
                throw new ArgumentException("Duplicate brick.", nameof(inputs));
            order[slot] = i;
            if (inputs[i].Stride == 2)
                mask |= (byte)(1 << slot);
        }

        mask = CloseRefinement(mask);
        var count = Extract(inputs, order, mask, null, out var inverted);
        if (inverted && mask != 0)
        {
            mask = 0;
            count = Extract(inputs, order, mask, null, out inverted);
        }
        if (inverted)
            throw new InvalidOperationException("The sampled field cannot produce consistently outward triangles at this resolution.");
        if (checked((long)count * TrianglePayloadBytes) > payloadLimit)
            return null;

        var triangles = new VolumeTriangle[count];
        var written = Extract(inputs, order, mask, triangles, out var changedOrientation);
        if (written != count || changedOrientation)
            throw new InvalidOperationException("Input changed between mesh passes.");
        return new PlanetVolumeMesh(descriptor!, new LatticeBrick(minX, minY, minZ).Vertex(0, 0, 0),
            terrainRevision, mask, triangles);
    }

    private static int Extract(ReadOnlySpan<VolumeMeshInput> inputs, ReadOnlySpan<int> order, byte mask, VolumeTriangle[]? output, out bool inverted)
    {
        inverted = false;
        var count = 0;
        Span<long> density = stackalloc long[13];
        Span<Vector3> positions = stackalloc Vector3[13];
        Span<Vector3> samplePositions = stackalloc Vector3[13];
        Span<VolumeVertex> vertices = stackalloc VolumeVertex[12];
        for (var slot = 0; slot < 8; slot++)
        {
            var origin = Corner(slot) * 32;
            var step = (mask & (1 << slot)) == 0 ? 1 : 2;
            for (var z = 0; z < 32; z += step)
            for (var y = 0; y < 32; y += step)
            for (var x = 0; x < 32; x += step)
            {
                var code = 0;
                for (var i = 0; i < 8; i++)
                {
                    var point = origin + new Vector3(x, y, z) + Corner(i) * step;
                    samplePositions[i] = point;
                    positions[i] = step == 1 ? FinePosition(point, slot, mask) : point;
                    density[i] = Read(point, inputs, order);
                    if (density[i] < 0)
                        code |= 1 << i;
                }

                count = Emit(TransvoxelTables.RegularCells[TransvoxelTables.RegularClass[code]],
                    TransvoxelTables.RegularVertices[code], density, positions, samplePositions,
                    false, vertices, inputs, order, output, count, ref inverted);
            }

            if (step != 1)
                continue;
            for (var axis = 0; axis < 3; axis++)
            {
                var direction = ((slot >> axis) & 1) == 0 ? 1 : -1;
                if ((mask & (1 << (slot ^ (1 << axis)))) == 0)
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
                        positions[i] = FinePosition(samplePositions[i], slot, mask);
                        density[i] = Read(samplePositions[i], inputs, order);
                    }
                    for (var i = 0; i < 4; i++)
                    {
                        samplePositions[9 + i] = point + u * ((i & 1) * 2) + v * ((i >> 1) * 2);
                        positions[9 + i] = samplePositions[9 + i];
                        density[9 + i] = Read(samplePositions[9 + i], inputs, order);
                    }
                    var code = 0;
                    for (var i = 0; i < FaceBits.Length; i++)
                        if (density[FaceBits[i]] < 0)
                            code |= 1 << i;
                    var cellClass = TransvoxelTables.TransitionClass[code];
                    count = Emit(TransvoxelTables.TransitionCells[cellClass & 127],
                        TransvoxelTables.TransitionVertices[code], density, positions, samplePositions,
                        ((cellClass & 128) != 0) == (direction > 0), vertices, inputs, order, output, count, ref inverted);
                }
            }
        }
        return count;
    }

    private static int Emit(byte[] cell, byte[] edges, ReadOnlySpan<long> density,
        ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> samples, bool reverse,
        Span<VolumeVertex> vertices, ReadOnlySpan<VolumeMeshInput> inputs, ReadOnlySpan<int> order,
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
            var position = positions[a] + (positions[b] - positions[a]) * (weight / (float)Quantization);
            var normal = Vector3.Lerp(Gradient(samples[a], inputs, order), Gradient(samples[b], inputs, order),
                weight / (float)Quantization);
            if (normal.LengthSquared() > 0)
                normal /= (float)Math.Sqrt((double)normal.X * normal.X + (double)normal.Y * normal.Y + (double)normal.Z * normal.Z);
            ushort material = 0;
            if (output is not null)
            {

                material = Material(samples[a], inputs, order);
            }
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

    // Close fine requests to one Cartesian subcube. This removes concave/checkerboard
    // junctions before extraction, while retaining Transvoxel on its convex boundary.
    private static byte CloseRefinement(byte mask)
    {
        if (mask == 255)
            return mask;
        var low = new Vector3(1);
        var high = Vector3.Zero;
        for (var slot = 0; slot < 8; slot++)
        {
            if ((mask & (1 << slot)) != 0)
                continue;
            low = Vector3.Min(low, Corner(slot));
            high = Vector3.Max(high, Corner(slot));
        }
        for (var slot = 0; slot < 8; slot++)
        {
            var point = Corner(slot);
            if (point.X >= low.X && point.X <= high.X &&
                point.Y >= low.Y && point.Y <= high.Y &&
                point.Z >= low.Z && point.Z <= high.Z)
                mask = (byte)(mask & ~(1 << slot));
        }
        return mask;
    }

    private static Vector3 FinePosition(Vector3 position, int slot, byte mask)
    {
        var result = position;
        for (var axis = 0; axis < 3; axis++)
        {
            if (position[axis] == 32 && (mask & (1 << (slot ^ (1 << axis)))) != 0)
                result -= Axis(axis) * (((slot >> axis) & 1) == 0 ? 0.5f : -0.5f);
        }
        return result;
    }

    private static Vector3 Gradient(Vector3 point, ReadOnlySpan<VolumeMeshInput> inputs, ReadOnlySpan<int> order)
    {
        var result = Vector3.Zero;
        for (var axis = 0; axis < 3; axis++)
        {
            var low = point;
            var high = point;
            low[axis] = Math.Max(0, point[axis] - 1);
            high[axis] = Math.Min(64, point[axis] + 1);
            result[axis] = (float)(((double)Read(high, inputs, order) - Read(low, inputs, order)) / (high[axis] - low[axis]));
        }
        return result;
    }

    private static long Read(Vector3 point, ReadOnlySpan<VolumeMeshInput> inputs, ReadOnlySpan<int> order)
    {
        var slot = Slot(point, out var local);
        return inputs[order[slot]].Brick.ReadDensity((int)local.X, (int)local.Y, (int)local.Z);
    }

    private static ushort Material(Vector3 point, ReadOnlySpan<VolumeMeshInput> inputs, ReadOnlySpan<int> order)
    {
        point = Vector3.Min(point, new Vector3(63));
        var slot = Slot(point, out var local);
        return inputs[order[slot]].Brick.ReadMaterial((int)local.X, (int)local.Y, (int)local.Z);
    }

    private static int Slot(Vector3 point, out Vector3 local)
    {
        var x = point.X >= 32 ? 1 : 0;
        var y = point.Y >= 32 ? 1 : 0;
        var z = point.Z >= 32 ? 1 : 0;
        local = point - new Vector3(x, y, z) * 32;
        return x + 2 * y + 4 * z;
    }

    private static Vector3 Corner(int i) => new(i & 1, (i >> 1) & 1, (i >> 2) & 1);
    private static Vector3 Axis(int axis) => axis switch
    {
        0 => Vector3.UnitX,
        1 => Vector3.UnitY,
        _ => Vector3.UnitZ
    };
}
