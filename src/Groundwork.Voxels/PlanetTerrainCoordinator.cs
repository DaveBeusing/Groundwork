using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Groundwork.Voxels;

/// <summary>An outstanding mesh reservation. The worker builds the indicated immutable tile.</summary>
public readonly record struct PlanetTerrainWork
{
    internal PlanetTerrainWork(object owner, ulong generation, int index, PlanetTerrainTile tile, ulong editRevision)
    {
        Owner = owner;
        Generation = generation;
        Index = index;
        Tile = tile;
        EditRevision = editRevision;
    }

    internal object? Owner { get; }
    internal int Index { get; }
    public ulong Generation { get; }
    public PlanetTerrainTile Tile { get; }
    public ulong EditRevision { get; }
}

/// <summary>The origin, view and edit snapshot used to build a terrain generation.</summary>
public readonly record struct PlanetTerrainStamp(ulong Generation, PlanetLocalFrame Frame, PlanetTerrainView View, ulong EditRevision);

/// <summary>
/// Coordinates terrain generation on one owning thread. Workers can build reserved tiles
/// independently using a shared surface recipe and return results to that thread for
/// settlement. The caller owns presentation and acknowledges a lease after its terrain
/// and camera have presented.
/// </summary>
public sealed class PlanetTerrainCoordinator
{
    public const int MaximumInFlightMeshes = 2;

    // At the fixed patch resolution, each record owns less than 24 KiB of CPU geometry,
    // including planet positions, sample keys, indices and the local positions.
    public const int MaximumRetainedPatches = 384;

    private readonly object _owner = new();
    private readonly PlanetTerrainWork[] _work = new PlanetTerrainWork[MaximumInFlightMeshes];
    private PlanetTerrainGeneration? _active;
    private PlanetTerrainGeneration? _staged;
    private PlanetTerrainLease? _reader;
    private PlanetTerrainLease? _presentation;
    private ulong _generation;
    private bool _stopped;

    public int InFlightMeshCount { get; private set; }
    public bool IsSettled => _stopped && InFlightMeshCount == 0 && _reader is null && _presentation is null;

    public int RetainedPatchCount
    {
        get
        {
            var count = (_active?.Plan.Tiles.Length ?? 0) + (_staged?.Plan.Tiles.Length ?? 0);
            count += CountLeasedPatches(_staged);
            foreach (var work in _work)
            {
                if (work.Owner is not null && work.Generation != _staged?.Id)
                    count++;
            }
            return count;
        }
    }

    public bool TryStage(PlanetTerrainPlan plan, PlanetLocalFrame frame)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (frame.Revision == 0)
            throw new ArgumentException("Create a local frame before staging terrain.", nameof(frame));
        if (_stopped || _presentation is { IsPinned: true } ||
            plan.EditRevision < (_active?.Plan.EditRevision ?? 0) ||
            plan.EditRevision < (_staged?.Plan.EditRevision ?? 0))
            return false;
        if (_active is { Frame: var current } &&
            (frame.Revision < current.Revision || frame.Revision == current.Revision && frame != current))
            return false;

        // Replacing a prepared generation preserves its lease, and replacing an
        // unprepared one preserves all outstanding worker reservations.
        var retained = (_active?.Plan.Tiles.Length ?? 0) + InFlightMeshCount + CountLeasedPatches(null);
        if (retained + plan.Tiles.Length > MaximumRetainedPatches)
            return false;

        var id = checked(_generation + 1);
        _staged = new PlanetTerrainGeneration(id, plan, frame);
        _generation = id;
        return true;
    }

    public bool TryReserveMesh(out PlanetTerrainWork work)
    {
        work = default;
        if (_stopped || _staged is not { } staged || staged.NextTile == staged.Plan.Tiles.Length ||
            InFlightMeshCount == MaximumInFlightMeshes)
            return false;

        var slot = Array.FindIndex(_work, static item => item.Owner is null);
        var index = staged.NextTile++;
        work = new PlanetTerrainWork(_owner, staged.Id, index, staged.Plan.Tiles[index], staged.Plan.EditRevision);
        _work[slot] = work;
        InFlightMeshCount++;
        return true;
    }

    /// <summary>Settles one reservation. A null or mismatched mesh cancels its current candidate.</summary>
    /// <returns>Whether the result was accepted by the current candidate.</returns>
    public bool CompleteMesh(PlanetTerrainWork work, PlanetPatchMesh? mesh)
    {
        if (work.Owner != _owner)
            return false;
        var slot = Array.IndexOf(_work, work);
        if (slot < 0)
            return false;

        _work[slot] = default;
        InFlightMeshCount--;
        if (_staged is not { } staged || staged.Id != work.Generation)
            return false;

        if (mesh is null || mesh.Patch != work.Tile.Patch || mesh.Transitions != work.Tile.Transitions)
        {
            _staged = null;
            return false;
        }

        try
        {
            staged.AddMesh(work.Index, mesh);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            _staged = null;
            return false;
        }
    }

    /// <summary>Prepares the staged mesh and local camera together without admitting its collision coverage.</summary>
    public bool TryPreparePresentation(PlanetVector cameraPosition, [NotNullWhen(true)] out PlanetTerrainLease? lease)
    {
        lease = null;
        if (_stopped || _presentation is not null || _staged is not { IsReady: true } staged)
            return false;

        staged.PrepareSupport();
        lease = new PlanetTerrainLease(this, staged, staged.Frame.ToLocal(cameraPosition), isActive: false);
        _presentation = lease;
        return true;
    }

    /// <summary>Admits the collision coverage corresponding to a successful terrain and camera presentation.</summary>
    public bool AcknowledgePresented(PlanetTerrainLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (_stopped || _staged is null || lease != _presentation || lease.Data != _staged)
            return false;

        _active = _staged;
        _staged = null;
        lease.MarkActive();
        return true;
    }

    /// <summary>Reads the active generation and projects the camera into its frame.</summary>
    public bool TryReadActive(PlanetVector cameraPosition, [NotNullWhen(true)] out PlanetTerrainLease? lease)
    {
        lease = null;
        if (_stopped || _reader is not null || _active is not { } active)
            return false;

        lease = new PlanetTerrainLease(this, active, active.Frame.ToLocal(cameraPosition), isActive: true);
        _reader = lease;
        return true;
    }

    /// <summary>Closes admission. Outstanding leases and worker reservations still require settlement.</summary>
    public void Stop()
    {
        _stopped = true;
        _staged = null;
        _active = null;
    }

    internal void Release(PlanetTerrainLease lease)
    {
        if (_reader == lease)
            _reader = null;
        if (_presentation == lease)
            _presentation = null;
    }

    internal bool CanPinPresentation(PlanetTerrainLease lease) =>
        !_stopped && _staged is not null && _presentation == lease && lease.Data == _staged;

    private int CountLeasedPatches(PlanetTerrainGeneration? countedStage)
    {
        var reader = _reader?.Data;
        var count = reader is not null && reader != _active && reader != countedStage
            ? reader.Plan.Tiles.Length
            : 0;
        if (_presentation is { Data: var presented } &&
            presented != reader && presented != _active && presented != countedStage)
            count += presented.Plan.Tiles.Length;
        return count;
    }
}

/// <summary>
/// The coordinator allows one active read and one presentation lease at a time.
/// Use the lease and any returned spans on the owning thread before disposing it.
/// </summary>
public sealed class PlanetTerrainLease : IPlanetSurface, IDisposable
{
    private PlanetTerrainCoordinator? _owner;
    private PlanetTerrainGeneration? _data;
    private readonly Vector3 _cameraPosition;
    private bool _isActive;

    internal PlanetTerrainLease(PlanetTerrainCoordinator owner, PlanetTerrainGeneration data, Vector3 cameraPosition, bool isActive)
    {
        _owner = owner;
        _data = data;
        _cameraPosition = cameraPosition;
        _isActive = isActive;
    }

    internal PlanetTerrainGeneration Data
    {
        get
        {
            ObjectDisposedException.ThrowIf(_data is null, this);
            return _data;
        }
    }
    public ulong Generation => Data.Id;
    public PlanetLocalFrame Frame => Data.Frame;
    public PlanetTerrainStamp Stamp => new(Data.Id, Data.Frame, Data.Plan.View, Data.Plan.EditRevision);
    public Vector3 CameraPosition
    {
        get
        {
            _ = Data;
            return _cameraPosition;
        }
    }
    public int MeshCount => Data.Plan.Tiles.Length;
    public ReadOnlySpan<PlanetTerrainTile> Tiles => Data.Plan.Tiles;
    public ReadOnlySpan<Vector3> GetPositions(int index) => Data.GetPositions(index);
    /// <summary>Returns the canonical sample for each vertex, in the same order as its position.</summary>
    public ReadOnlySpan<PlanetSampleKey> GetSamples(int index) => Data.GetSamples(index);
    public ReadOnlySpan<uint> GetIndices(int index) => Data.GetIndices(index);

    public bool TryGetSupport(PlanetVector direction, out PlanetSupport support)
    {
        var data = Data;
        support = default;
        return _isActive && data.Surface.TryGetSupport(direction, out support);
    }

    internal bool IsPinned { get; private set; }

    /// <summary>Prevents superseding this candidate while a renderer can present it.</summary>
    /// <remarks>Keep this lease until the presentation is acknowledged or the renderer has stopped.</remarks>
    public bool TryPinPresentation()
    {
        if (_owner?.CanPinPresentation(this) != true)
            return false;

        IsPinned = true;
        return true;
    }

    internal void MarkActive()
    {
        IsPinned = false;
        _isActive = true;
    }

    public void Dispose()
    {
        _owner?.Release(this);
        _owner = null;
        _data = null;
    }
}

internal sealed class PlanetTerrainGeneration(ulong id, PlanetTerrainPlan plan, PlanetLocalFrame frame)
{
    private readonly PlanetPatchMesh[] _meshes = new PlanetPatchMesh[plan.Tiles.Length];
    private readonly Vector3[][] _positions = new Vector3[plan.Tiles.Length][];
    private PlanetSurface? _surface;
    private int _completed;

    internal ulong Id { get; } = id;
    internal PlanetTerrainPlan Plan { get; } = plan;
    internal PlanetLocalFrame Frame { get; } = frame;
    internal int NextTile { get; set; }
    internal bool IsReady => _completed == _meshes.Length;
    internal PlanetSurface Surface => _surface ?? throw new InvalidOperationException("Prepare terrain before presenting it.");

    internal void AddMesh(int index, PlanetPatchMesh mesh)
    {
        var positions = new Vector3[mesh.Positions.Length];
        for (var i = 0; i < positions.Length; i++)
            positions[i] = Frame.ToLocal(mesh.Positions[i]);
        _meshes[index] = mesh;
        _positions[index] = positions;
        _completed++;
    }

    internal void PrepareSupport()
    {
        if (_surface is not null)
            return;
        var support = new List<PlanetPatchMesh>();
        for (var i = 0; i < _meshes.Length; i++)
        {
            if (Plan.Tiles[i].SupportsCollision)
                support.Add(_meshes[i]);
        }
        _surface = new PlanetSurface(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(support));
    }

    internal ReadOnlySpan<Vector3> GetPositions(int index) => _positions[index];
    internal ReadOnlySpan<PlanetSampleKey> GetSamples(int index) => _meshes[index].Samples;
    internal ReadOnlySpan<uint> GetIndices(int index) => _meshes[index].Indices;
}
