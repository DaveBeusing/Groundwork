using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Groundwork.Voxels;

public readonly record struct VolumeMeshRequest(LatticeBrick Brick, int Stride);

/// <summary>
/// Immutable, bounded LOD and sample snapshot shared by independently built eight-brick meshes.
/// A plan owns its brick leases until disposal and completion of any in-flight builds.
/// Neighboring meshes must use the same plan identity and terrain revision.
/// </summary>
public sealed class VolumeMeshPlan : IDisposable
{
    public const int MaximumBricksPerAxis = 8;
    private readonly object _gate = new();
    private readonly VolumeMeshInput[] _inputs;
    private readonly bool _ownsLeases;
    private readonly byte[]? _editDigest;
    private int _readers;
    private bool _disposed;

    private VolumeMeshPlan(VolumeMeshInput[] inputs, LatticeBrick minimum, int width, int height, int depth,
        ulong terrainRevision, bool ownsLeases, byte[]? editDigest = null)
    {
        _inputs = inputs;
        Minimum = minimum;
        Width = width;
        Height = height;
        Depth = depth;
        TerrainRevision = terrainRevision;
        Descriptor = inputs[0].Brick.Descriptor;
        _ownsLeases = ownsLeases;
        _editDigest = editDigest;
    }

    public PlanetDescriptor Descriptor { get; }
    public LatticeBrick Minimum { get; }
    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public ulong TerrainRevision { get; }
    public string Identity { get; private set; } = string.Empty;
    public long MetadataPayloadBytes => (long)_inputs.Length * Unsafe.SizeOf<VolumeMeshInput>();

    /// <summary>
    /// Takes leases on a complete rectangular window, with an even brick count on each axis.
    /// Returns null for insufficient metadata bytes, unavailable bricks or exhausted lease quota.
    /// Input order does not affect plan identity or output order.
    /// </summary>
    public static VolumeMeshPlan? TryCreate(PlanetBrickStore store, ReadOnlySpan<VolumeMeshRequest> requests,
        ulong terrainRevision, long metadataByteLimit) =>
        TryCreate(store, requests, terrainRevision, metadataByteLimit, null);

    internal static VolumeMeshPlan? TryCreate(PlanetBrickStore store, ReadOnlySpan<VolumeMeshRequest> requests,
        ulong terrainRevision, long metadataByteLimit, byte[]? editDigest)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfZero(terrainRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(metadataByteLimit);
        var (min, width, height, depth) = Validate(requests);
        if ((long)requests.Length * Unsafe.SizeOf<VolumeMeshInput>() > metadataByteLimit)
            return null;

        var inputs = new VolumeMeshInput[requests.Length];
        VolumeMeshPlan? result = null;
        try
        {
            foreach (var request in requests)
            {
                var lease = store.TryAcquire(request.Brick);
                if (lease is null)
                    return null;
                var index = Index(request.Brick, min, width, height);
                inputs[index] = new VolumeMeshInput(lease, request.Stride);
            }

            result = new VolumeMeshPlan(inputs, min, width, height, depth, terrainRevision, ownsLeases: true, editDigest);
            result.Prepare();
            return result;
        }
        catch
        {
            result?.Dispose();
            throw;
        }
        finally
        {
            if (result is null)
                foreach (var input in inputs)
                    input.Brick?.Dispose();
        }
    }

    internal static VolumeMeshPlan Borrow(ReadOnlySpan<VolumeMeshInput> inputs, ulong terrainRevision)
    {
        Span<VolumeMeshRequest> requests = stackalloc VolumeMeshRequest[8];
        if (inputs.Length != requests.Length)
            throw new ArgumentException("A complete eight-brick neighborhood is required.", nameof(inputs));
        PlanetDescriptor? descriptor = null;
        for (var i = 0; i < inputs.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(inputs[i].Brick);
            descriptor ??= inputs[i].Brick.Descriptor;
            if (descriptor != inputs[i].Brick.Descriptor)
                throw new ArgumentException("All bricks must share the source descriptor.", nameof(inputs));
            _ = inputs[i].Brick.ReadDensity(0, 0, 0);
            requests[i] = new VolumeMeshRequest(inputs[i].Brick.Key, inputs[i].Stride);
        }

        var (min, width, height, depth) = Validate(requests);
        var sorted = new VolumeMeshInput[8];
        foreach (var input in inputs)
            sorted[Index(input.Brick.Key, min, width, height)] = input;
        var plan = new VolumeMeshPlan(sorted, min, width, height, depth, terrainRevision, ownsLeases: false);
        plan.Prepare();
        return plan;
    }

    private void Prepare()
    {
        var low = new Vector3(Width, Height, Depth);
        var high = new Vector3(-1);
        for (var i = 0; i < _inputs.Length; i++)
        {
            if (_inputs[i].Stride != 1)
                continue;
            var position = BrickPosition(i) / 32;
            low = Vector3.Min(low, position);
            high = Vector3.Max(high, position);
        }

        for (var i = 0; i < _inputs.Length; i++)
        {
            var p = BrickPosition(i) / 32;
            if (p.X >= low.X && p.X <= high.X && p.Y >= low.Y && p.Y <= high.Y && p.Z >= low.Z && p.Z <= high.Z)
                _inputs[i] = _inputs[i] with { Stride = 1 };
        }

        if (PlanetVolumeMesher.HasInvertedTriangles(this))
        {
            for (var i = 0; i < _inputs.Length; i++)
                _inputs[i] = _inputs[i] with { Stride = 1 };
            if (PlanetVolumeMesher.HasInvertedTriangles(this))
                throw new InvalidOperationException("The field cannot produce outward triangles at this resolution.");
        }
        Identity = ComputeIdentity();
    }

    internal void Enter()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _readers = checked(_readers + 1);
        }
    }

    internal void Exit()
    {
        lock (_gate)
        {
            _readers--;
            if (_disposed && _readers == 0)
                ReleaseLeases();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_readers == 0)
                ReleaseLeases();
        }
    }

    private void ReleaseLeases()
    {
        if (_ownsLeases)
            foreach (var input in _inputs)
                input.Brick.Dispose();
    }

    internal Vector3 BrickPosition(int index) => new(
        index % Width * 32, index / Width % Height * 32, index / (Width * Height) * 32);

    internal int Stride(int index) => _inputs[index].Stride;

    internal int Neighbor(int index, int axis, int direction)
    {
        var p = BrickPosition(index) / 32;
        p[axis] += direction;
        return p.X < 0 || p.X >= Width || p.Y < 0 || p.Y >= Height || p.Z < 0 || p.Z >= Depth
            ? -1 : ((int)p.Z * Height + (int)p.Y) * Width + (int)p.X;
    }

    internal int RegionIndex(LatticeBrick minimum)
    {
        var x = checked(minimum.X - Minimum.X);
        var y = checked(minimum.Y - Minimum.Y);
        var z = checked(minimum.Z - Minimum.Z);
        if (x < 0 || x >= Width || y < 0 || y >= Height || z < 0 || z >= Depth ||
            (x & 1) != 0 || (y & 1) != 0 || (z & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(minimum), "Neighborhood must be aligned to this plan.");
        return (int)((z * Height + y) * Width + x);
    }

    internal int BrickAt(Vector3 position)
    {
        var x = Math.Min((int)position.X / 32, Width - 1);
        var y = Math.Min((int)position.Y / 32, Height - 1);
        var z = Math.Min((int)position.Z / 32, Depth - 1);
        return (z * Height + y) * Width + x;
    }

    internal long Read(Vector3 point)
    {
        var index = BrickAt(point);
        var local = point - BrickPosition(index);
        return _inputs[index].Brick.ReadDensity((int)local.X, (int)local.Y, (int)local.Z);
    }

    internal Vector3 Gradient(Vector3 point)
    {
        var gradient = Vector3.Zero;
        for (var axis = 0; axis < 3; axis++)
        {
            var low = point;
            var high = point;
            low[axis] = Math.Max(0, point[axis] - 1);
            var limit = axis == 0 ? Width : axis == 1 ? Height : Depth;
            high[axis] = Math.Min(limit * 32, point[axis] + 1);
            gradient[axis] = (float)(((double)Read(high) - Read(low)) / (high[axis] - low[axis]));
        }
        return gradient;
    }

    internal ushort Material(Vector3 point)
    {
        point = Vector3.Min(point, new Vector3(Width * 32 - 1, Height * 32 - 1, Depth * 32 - 1));
        var index = BrickAt(point);
        var local = point - BrickPosition(index);
        return _inputs[index].Brick.ReadMaterial((int)local.X, (int)local.Y, (int)local.Z);
    }

    private string ComputeIdentity()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[PlanetLatticeSample.EncodedSize];
        foreach (var input in _inputs)
        {
            new PlanetLatticeSample(Descriptor.WorldId, Descriptor.PlanetId, input.Brick.Key.Vertex(0, 0, 0)).Write(bytes);
            hash.AppendData(bytes);
            hash.AppendData([(byte)input.Stride]);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, TerrainRevision);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], Descriptor.Seed);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], Descriptor.GeneratorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[20..], Descriptor.TerrainSchemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[24..], Descriptor.MaterialCatalogVersion);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[28..], Descriptor.RadiusMeters);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[36..], Descriptor.CellSizeMeters);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[44..], PlanetVolumeMesh.MesherVersion);
        hash.AppendData(bytes[..48]);
        hash.AppendData(Convert.FromHexString(Descriptor.ParameterSha256));
        if (_editDigest is not null)
            hash.AppendData(_editDigest);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static (LatticeBrick Minimum, int Width, int Height, int Depth) Validate(ReadOnlySpan<VolumeMeshRequest> requests)
    {
        if (requests.Length is < 8 or > MaximumBricksPerAxis * MaximumBricksPerAxis * MaximumBricksPerAxis)
            throw new ArgumentOutOfRangeException(nameof(requests));
        var min = requests[0].Brick;
        var max = min;
        for (var i = 0; i < requests.Length; i++)
        {
            var request = requests[i];
            if (request.Stride is not (1 or 2))
                throw new ArgumentOutOfRangeException(nameof(requests));
            _ = request.Brick.Vertex(32, 32, 32);
            min = new LatticeBrick(Math.Min(min.X, request.Brick.X), Math.Min(min.Y, request.Brick.Y), Math.Min(min.Z, request.Brick.Z));
            max = new LatticeBrick(Math.Max(max.X, request.Brick.X), Math.Max(max.Y, request.Brick.Y), Math.Max(max.Z, request.Brick.Z));
            for (var j = 0; j < i; j++)
                if (requests[j].Brick == request.Brick)
                    throw new ArgumentException("Duplicate brick.", nameof(requests));
        }
        var width = checked(max.X - min.X + 1);
        var height = checked(max.Y - min.Y + 1);
        var depth = checked(max.Z - min.Z + 1);
        if (width > MaximumBricksPerAxis || height > MaximumBricksPerAxis || depth > MaximumBricksPerAxis ||
            (width & 1) != 0 || (height & 1) != 0 || (depth & 1) != 0 ||
            width * height * depth != requests.Length)
            throw new ArgumentException("Bricks must cover a complete even rectangular window.", nameof(requests));
        return (min, (int)width, (int)height, (int)depth);
    }

    private static int Index(LatticeBrick key, LatticeBrick min, int width, int height) =>
        checked((int)(((key.Z - min.Z) * height + key.Y - min.Y) * width + key.X - min.X));
}
