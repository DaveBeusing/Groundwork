namespace Groundwork.Voxels;

/// <summary>The inclusive horizontal bounds of a generated region in chunk layer zero.</summary>
public readonly record struct RegionBounds(int MinX, int MaxX, int MinZ, int MaxZ)
{
    public bool Contains(ChunkCoordinate coordinate) => coordinate.Y == 0 &&
        coordinate.X >= MinX && coordinate.X <= MaxX && coordinate.Z >= MinZ && coordinate.Z <= MaxZ;

    internal RegionBounds Around(ChunkCoordinate anchor, int radius) => new(
        Math.Max(MinX, anchor.X - radius), Math.Min(MaxX, anchor.X + radius),
        Math.Max(MinZ, anchor.Z - radius), Math.Min(MaxZ, anchor.Z + radius));

    internal IEnumerable<ChunkCoordinate> Coordinates()
    {
        for (var x = MinX; x <= MaxX; x++)
            for (var z = MinZ; z <= MaxZ; z++)
                yield return new ChunkCoordinate(x, 0, z);
    }

    internal static int FloorDivide(int value, int divisor)
    {
        var quotient = Math.DivRem(value, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}

/// <summary>A bounded borrow of the published collision and mesh region.</summary>
/// <remarks>Use and dispose on the region coordinator. Mesh spans are valid only during the borrow.</remarks>
public sealed class RegionReadLease : IVoxelOccupancy, IDisposable
{
    private WalkableRegion? _owner;
    private PublishedRegion? _region;

    internal RegionReadLease(WalkableRegion owner, PublishedRegion region)
    {
        _owner = owner;
        _region = region;
    }

    private PublishedRegion Region => _region ?? throw new ObjectDisposedException(nameof(RegionReadLease));

    public ChunkCoordinate Anchor => Region.Anchor;
    public ulong Generation => Region.Generation;
    public int MeshCount => Region.Meshes.Length;

    public VoxelOccupancy GetCell(int x, int y, int z) => Region.GetCell(x, y, z);

    public ChunkCoordinate GetMeshCoordinate(int index) => Region.Meshes[index].Coordinate;

    public ReadOnlySpan<System.Numerics.Vector3> GetMeshPositions(int index) => Region.Meshes[index].Positions;

    public ReadOnlySpan<uint> GetMeshIndices(int index) => Region.Meshes[index].Indices;

    /// <summary>Reads a load identity from the inner region or its meshing halo.</summary>
    public bool TryGetLoadIdentity(ChunkCoordinate coordinate, out ulong identity)
    {
        var region = Region;
        foreach (var chunk in region.Data)
        {
            if (chunk.Snapshot.Coordinate == coordinate)
            {
                identity = chunk.Identity;
                return true;
            }
        }

        identity = 0;
        return false;
    }

    public void Dispose()
    {
        var owner = _owner;
        _owner = null;
        _region = null;
        owner?.ReleaseRead();
    }
}

internal sealed record ResidentChunk(ulong Identity, ChunkSnapshot Snapshot);

internal sealed class PublishedRegion(RegionBounds world, ChunkCoordinate anchor, ulong generation,
    ResidentChunk[] data, ChunkMesh[] meshes) : IVoxelOccupancy
{
    private readonly RegionBounds _inner = world.Around(anchor, 1);
    private readonly RegionBounds _footprint = world.Around(anchor, 2);

    internal ChunkCoordinate Anchor { get; } = anchor;
    internal ulong Generation { get; } = generation;
    internal ResidentChunk[] Data { get; } = data;
    internal ChunkMesh[] Meshes { get; } = meshes;

    internal long MeshBytes { get; } = meshes.Sum(mesh => 12L * mesh.Positions.Length + 4L * mesh.Indices.Length);

    internal bool Holds(ChunkCoordinate coordinate) => _footprint.Contains(coordinate);

    public VoxelOccupancy GetCell(int x, int y, int z)
    {
        const int size = ChunkSnapshot.Size;
        var coordinate = new ChunkCoordinate(RegionBounds.FloorDivide(x, size), 0, RegionBounds.FloorDivide(z, size));
        if (!world.Contains(coordinate) || y < 0)
            return VoxelOccupancy.Solid;
        if (y >= size)
            return VoxelOccupancy.Air;
        if (!_inner.Contains(coordinate))
            return VoxelOccupancy.Unavailable;

        var depth = _footprint.MaxZ - _footprint.MinZ + 1;
        var index = (coordinate.X - _footprint.MinX) * depth + coordinate.Z - _footprint.MinZ;
        return Data[index].Snapshot[x - coordinate.X * size, y, z - coordinate.Z * size] == 0
            ? VoxelOccupancy.Air : VoxelOccupancy.Solid;
    }
}
