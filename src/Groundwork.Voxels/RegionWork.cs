namespace Groundwork.Voxels;

/// <summary>A reserved load. The worker observes cancellation and returns its result to the coordinator.</summary>
/// <remarks>Use one chunk-sized generation buffer and produce at most one immutable snapshot for this reservation.</remarks>
public sealed class RegionLoadWork
{
    private int _canceled;

    internal RegionLoadWork(ChunkCoordinate coordinate, ulong loadIdentity, ulong generation)
    {
        Coordinate = coordinate;
        LoadIdentity = loadIdentity;
        Generation = generation;
    }

    public ChunkCoordinate Coordinate { get; }
    public ulong LoadIdentity { get; }
    public ulong Generation { get; }
    public bool IsCancellationRequested => Volatile.Read(ref _canceled) != 0;

    internal void Cancel() => Volatile.Write(ref _canceled, 1);
}

/// <summary>One immutable mesh build capture. Execute once, away from the coordinator.</summary>
public sealed class RegionMeshWork
{
    private readonly RegionBounds _world;
    private readonly RegionBounds _footprint;
    private ResidentChunk[]? _data;
    private ChunkMesh[]? _meshes;
    private int _canceled;
    private int _started;

    internal RegionMeshWork(RegionBounds world, ChunkCoordinate anchor, ulong generation, ResidentChunk[] data)
    {
        _world = world;
        _footprint = world.Around(anchor, 2);
        Anchor = anchor;
        Generation = generation;
        _data = data;
    }

    public ChunkCoordinate Anchor { get; }
    public ulong Generation { get; }
    public bool IsCancellationRequested => Volatile.Read(ref _canceled) != 0;

    /// <summary>Builds the region once, returning null when cancellation wins.</summary>
    public RegionMeshResult? Build()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A reserved mesh build can only run once.");

        var data = _data ?? throw new InvalidOperationException("The mesh reservation already settled.");
        var inner = _world.Around(Anchor, 1);
        var meshes = new ChunkMesh[(inner.MaxX - inner.MinX + 1) * (inner.MaxZ - inner.MinZ + 1)];
        var neighbors = new ChunkNeighbor[6];
        var index = 0;
        foreach (var coordinate in inner.Coordinates())
        {
            if (IsCancellationRequested)
                return null;

            var (x, _, z) = coordinate;
            neighbors[0] = Neighbor(new ChunkCoordinate(x - 1, 0, z), data);
            neighbors[1] = Neighbor(new ChunkCoordinate(x + 1, 0, z), data);
            neighbors[2] = ChunkNeighbor.ExteriorAir;
            neighbors[3] = ChunkNeighbor.ExteriorAir;
            neighbors[4] = Neighbor(new ChunkCoordinate(x, 0, z - 1), data);
            neighbors[5] = Neighbor(new ChunkCoordinate(x, 0, z + 1), data);
            try
            {
                meshes[index++] = ChunkMesher.Build(GetChunk(coordinate, data), neighbors);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Meshing chunk {coordinate} for region generation {Generation} failed.", error);
            }
        }

        if (IsCancellationRequested)
            return null;

        _meshes = meshes;
        return new RegionMeshResult(this);
    }

    internal ResidentChunk[] Data => _data ?? throw new InvalidOperationException("The mesh reservation already settled.");
    internal ChunkMesh[] Meshes => _meshes ?? throw new InvalidOperationException("The mesh reservation has no result.");
    internal bool Holds(ChunkCoordinate coordinate) => _footprint.Contains(coordinate);
    internal void Cancel() => Volatile.Write(ref _canceled, 1);

    internal void Release()
    {
        _data = null;
        _meshes = null;
    }

    private ChunkNeighbor Neighbor(ChunkCoordinate coordinate, ResidentChunk[] data) =>
        _world.Contains(coordinate) ? new ChunkNeighbor(GetChunk(coordinate, data)) : ChunkNeighbor.ExteriorAir;

    private ChunkSnapshot GetChunk(ChunkCoordinate coordinate, ResidentChunk[] data)
    {
        var depth = _footprint.MaxZ - _footprint.MinZ + 1;
        var index = (coordinate.X - _footprint.MinX) * depth + coordinate.Z - _footprint.MinZ;
        return data[index].Snapshot;
    }
}

/// <summary>Completed geometry owned by the mesh reservation until handed back once.</summary>
public sealed class RegionMeshResult
{
    internal RegionMeshResult(RegionMeshWork work) => Work = work;

    internal RegionMeshWork Work { get; }
}
