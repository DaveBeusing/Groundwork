namespace Groundwork.Voxels;

/// <summary>Current or peak ownership of the portable region, including reserved work.</summary>
public readonly record struct RegionUsage(int SnapshotSlots, int Loads, int MeshWork, long MeshBytes);

/// <summary>A current load or mesh failure to report through the host's supervision.</summary>
public sealed record RegionFailure(ChunkCoordinate Coordinate, ulong Generation, Exception Error);

/// <summary>Owns one published region and the bounded work needed to replace it.</summary>
/// <remarks>
/// The host serializes all coordinator calls, including completion and read disposal.
/// It holds a Core work lease for each dispatched reservation until the worker returns.
/// Workers can only observe their reservation and produce a result. Settle each reservation
/// exactly once after its worker returns, including after Stop or executor rejection.
/// </remarks>
public sealed class WalkableRegion
{
    public const int MaximumInnerChunks = 9;
    public const int MaximumSnapshotSlots = 50;
    public const int MaximumLoads = 2;
    public const long MaximumCpuMeshBytes = 96L * 1024 * 1024;

    private const long MeshReservationBytes = MaximumInnerChunks *
        (12L * ChunkMesher.MaximumVerticesPerChunk + 4L * ChunkMesher.MaximumIndicesPerChunk);

    private readonly RegionBounds _world;
    private readonly Dictionary<ChunkCoordinate, ResidentChunk> _chunks = new(MaximumSnapshotSlots);
    private readonly List<RegionLoadWork> _loads = new(MaximumLoads);
    private RegionBounds _target;
    private ulong _loadIdentity;
    private RegionMeshWork? _mesh;
    private PublishedRegion? _ready;
    private PublishedRegion? _active;
    private PublishedRegion? _reader;
    private bool _stopped;

    public WalkableRegion(RegionBounds bounds)
    {
        if (bounds.MinX > bounds.MaxX || bounds.MinZ > bounds.MaxZ ||
            !new ChunkCoordinate(bounds.MinX, 0, bounds.MinZ).IsValid ||
            !new ChunkCoordinate(bounds.MaxX, 0, bounds.MaxZ).IsValid)
            throw new ArgumentOutOfRangeException(nameof(bounds));

        _world = bounds;
    }

    public ChunkCoordinate? DesiredAnchor { get; private set; }
    public ulong DesiredGeneration { get; private set; }
    public RegionFailure? Failure { get; private set; }
    public bool IsSettled => _stopped && _loads.Count == 0 && _mesh is null && _reader is null;
    public RegionUsage Usage => new(_chunks.Count + _loads.Count, _loads.Count,
        _mesh is null && _ready is null ? 0 : 1, MeshBytes);
    public RegionUsage HighWater { get; private set; }
    private bool AdmissionClosed => _stopped || Failure is not null;

    /// <summary>Updates the desired anchor and may activate a complete region after a successful tick.</summary>
    public bool PublishTick(VoxelPose pose, bool published)
    {
        if (!published || AdmissionClosed)
            return false;

        VoxelCollision.ValidatePose(pose);
        const int chunkUnits = ChunkSnapshot.Size * VoxelCollision.UnitsPerVoxel;
        var anchor = new ChunkCoordinate(RegionBounds.FloorDivide(pose.X, chunkUnits), 0,
            RegionBounds.FloorDivide(pose.Z, chunkUnits));
        if (!_world.Contains(anchor))
            throw new ArgumentOutOfRangeException(nameof(pose), "The published player center is outside the world.");

        if (DesiredAnchor != anchor)
        {
            if (DesiredGeneration == ulong.MaxValue)
                return Fail(anchor, DesiredGeneration, new InvalidOperationException("Region generations exhausted."));

            DesiredGeneration++;
            DesiredAnchor = anchor;
            _target = _world.Around(anchor, 2);
            _ready = null;
            _mesh?.Cancel();
            foreach (var load in _loads)
                if (!_target.Contains(load.Coordinate))
                    load.Cancel();

            Prune();
        }

        if (_ready is not { } ready || !VoxelCollision.IsPublishablePose(ready, pose))
            return false;

        _active = ready;
        _ready = null;
        Prune();
        return true;
    }

    /// <summary>Reserves the next missing chunk in deterministic near-to-far order.</summary>
    public RegionLoadWork? TryTakeLoad()
    {
        if (AdmissionClosed || DesiredAnchor is not { } anchor || _loads.Count == MaximumLoads ||
            Usage.SnapshotSlots == MaximumSnapshotSlots)
            return null;

        for (var distance = 0; distance <= 2; distance++)
        {
            foreach (var coordinate in _target.Coordinates())
            {
                if (Math.Max(Math.Abs(coordinate.X - anchor.X), Math.Abs(coordinate.Z - anchor.Z)) != distance ||
                    _chunks.ContainsKey(coordinate) || HasReservedLoad(coordinate))
                    continue;

                if (_loadIdentity == ulong.MaxValue)
                {
                    Fail(coordinate, DesiredGeneration, new InvalidOperationException("Chunk load identities exhausted."));
                    return null;
                }

                var work = new RegionLoadWork(coordinate, ++_loadIdentity, DesiredGeneration);
                _loads.Add(work);
                ObserveUsage();
                return work;
            }
        }

        return null;
    }

    /// <summary>Settles a load, with null representing cancellation or executor rejection.</summary>
    public bool CompleteLoad(RegionLoadWork work, ChunkSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!_loads.Remove(work))
            return false;

        var accepted = !AdmissionClosed && !work.IsCancellationRequested && _target.Contains(work.Coordinate) &&
            snapshot is not null;
        if (accepted)
        {
            if (snapshot!.Coordinate != work.Coordinate)
                return Fail(work.Coordinate, work.Generation, new ArgumentException("The loaded coordinate differs from its reservation.", nameof(snapshot)));

            _chunks.Add(work.Coordinate, new ResidentChunk(work.LoadIdentity, snapshot));
        }

        Prune();
        return accepted;
    }

    /// <summary>Settles a failed load. A current failure closes admission and reports to the host.</summary>
    public bool FailLoad(RegionLoadWork work, Exception error)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(error);
        if (!_loads.Remove(work))
            return false;

        if (!AdmissionClosed && !work.IsCancellationRequested && _target.Contains(work.Coordinate))
        {
            Fail(work.Coordinate, work.Generation, error);
            return true;
        }

        Prune();
        return false;
    }

    /// <summary>Reserves a region build after every center and halo snapshot is resident.</summary>
    public RegionMeshWork? TryTakeMesh()
    {
        if (AdmissionClosed || DesiredAnchor is not { } anchor || _mesh is not null || _ready is not null ||
            _active?.Generation == DesiredGeneration || MeshBytes + MeshReservationBytes > MaximumCpuMeshBytes)
            return null;

        foreach (var coordinate in _target.Coordinates())
            if (!_chunks.ContainsKey(coordinate))
                return null;

        var data = _target.Coordinates().Select(coordinate => _chunks[coordinate]).ToArray();
        _mesh = new RegionMeshWork(_world, anchor, DesiredGeneration, data);
        ObserveUsage();
        return _mesh;
    }

    /// <summary>Settles the reservation and stages the complete result only while its intent is current.</summary>
    public bool CompleteMesh(RegionMeshWork work, RegionMeshResult? result)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!ReferenceEquals(work, _mesh))
            return false;
        if (result is not null && !ReferenceEquals(work, result.Work))
            throw new ArgumentException("The result belongs to a different mesh reservation.", nameof(result));

        var accepted = !AdmissionClosed && !work.IsCancellationRequested && work.Generation == DesiredGeneration &&
            result is not null && CapturesMatch(work);
        if (accepted)
            _ready = new PublishedRegion(_world, work.Anchor, work.Generation, work.Data, work.Meshes);

        _mesh = null;
        work.Release();
        Prune();
        return accepted;
    }

    /// <summary>Settles a failed build. A current failure closes admission and reports to the host.</summary>
    public bool FailMesh(RegionMeshWork work, Exception error)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(error);
        if (!ReferenceEquals(work, _mesh))
            return false;

        _mesh = null;
        work.Release();
        if (!AdmissionClosed && !work.IsCancellationRequested && work.Generation == DesiredGeneration)
        {
            Fail(work.Anchor, work.Generation, error);
            return true;
        }

        Prune();
        return false;
    }

    /// <summary>Borrows the current region, with at most one outstanding borrow.</summary>
    public RegionReadLease? TryRead()
    {
        if (_active is null || _reader is not null)
            return null;

        _reader = _active;
        return new RegionReadLease(this, _reader);
    }

    /// <summary>Closes new work and reads while keeping outstanding work and borrows in custody.</summary>
    public void Stop()
    {
        if (_stopped)
            return;

        _stopped = true;
        _active = null;
        ClosePendingWork();
    }

    internal void ReleaseRead()
    {
        _reader = null;
        Prune();
    }

    private long MeshBytes => (_active?.MeshBytes ?? 0) + (_ready?.MeshBytes ?? 0) +
        (ReferenceEquals(_reader, _active) ? 0 : _reader?.MeshBytes ?? 0) +
        (_mesh is null ? 0 : MeshReservationBytes);

    private bool HasReservedLoad(ChunkCoordinate coordinate)
    {
        foreach (var load in _loads)
            if (load.Coordinate == coordinate)
                return true;

        return false;
    }

    private bool CapturesMatch(RegionMeshWork work)
    {
        foreach (var captured in work.Data)
            if (!_chunks.TryGetValue(captured.Snapshot.Coordinate, out var current) || !ReferenceEquals(current, captured))
                return false;

        return true;
    }

    private bool Fail(ChunkCoordinate coordinate, ulong generation, Exception error)
    {
        Failure = new RegionFailure(coordinate, generation, error);
        // Leave the coherent published region readable until the host stops.
        ClosePendingWork();
        return false;
    }

    private void ClosePendingWork()
    {
        _ready = null;
        _mesh?.Cancel();
        foreach (var load in _loads)
            load.Cancel();

        Prune();
    }

    private void Prune()
    {
        // Dictionary permits removing current keys during enumeration.
        foreach (var coordinate in _chunks.Keys)
        {
            if ((!AdmissionClosed && DesiredAnchor.HasValue && _target.Contains(coordinate)) ||
                _active?.Holds(coordinate) == true || _reader?.Holds(coordinate) == true ||
                _mesh?.Holds(coordinate) == true)
                continue;

            _chunks.Remove(coordinate);
        }

        ObserveUsage();
    }

    private void ObserveUsage()
    {
        var current = Usage;
        HighWater = new RegionUsage(Math.Max(HighWater.SnapshotSlots, current.SnapshotSlots),
            Math.Max(HighWater.Loads, current.Loads), Math.Max(HighWater.MeshWork, current.MeshWork),
            Math.Max(HighWater.MeshBytes, current.MeshBytes));
    }
}
