namespace Groundwork.Rendering.D3D12;

/// <summary>Immutable near and far meshes for one WP16 model and material.</summary>
public readonly record struct D3D12PropAsset(PropModel Model, RenderMesh Near, RenderMesh Far)
{
    public bool Transparent { get; init; }
}

public readonly record struct D3D12PropStatistics(int UploadBytes, int PayloadBytes, int MetadataBytes, int Draws,
    int Instances, int DeferredCells, long NativeBytes);

/// <summary>Bounded prop input shared by the placement producer and a single D3D12 renderer.</summary>
public sealed class D3D12PropCells
{
    public const int MaximumUploadBytes = 2 * 1024 * 1024;
    private const int MaximumGpuBytes = 64 * 1024 * 1024;
    internal readonly Lock Gate = new();
    internal readonly PropCellAdmission Admission;
    internal readonly D3D12PropAsset[] Assets;
    internal readonly StagedCell[] Ready;
    internal readonly PropCellLimits Limits;
    internal readonly int InstancesPerCell;
    internal readonly int GeometryBytes;
    internal PropPublication Publication;
    internal ulong ViewVersion;
    private Guid _world;
    private PropCellKey[] _requiredCells = [];
    private ulong _sceneRevision;
    private bool _trackScenePresentation;
    private PropPublication _presentedPublication;
    private ulong _presentedRevision;
    private ulong _presentedSequence;
    internal bool Attached;
    internal bool Closed;
    internal D3D12PropStatistics Statistics;

    internal readonly record struct StagedCell(PropCellRequest Request, int[] PayloadIndices);

    public D3D12PropCells(PropCellLimits limits, int instancesPerCell, ulong seed, uint recipe,
        ReadOnlySpan<D3D12PropAsset> assets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instancesPerCell);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.Cells);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.ResidentInstances);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.PendingInstances);
        if (limits.Cells > 4096 || limits.Groups is <= 0 or > 96 || assets.IsEmpty || assets.Length > limits.Groups / 2 ||
            limits.PendingInstances > 65_536 || limits.ResidentInstances > 65_536 ||
            instancesPerCell > Math.Min(limits.PendingInstances, limits.ResidentInstances) ||
            (limits.Cells + 1L) * instancesPerCell * PropInstanceData.ByteSize > MaximumGpuBytes)
            throw new ArgumentOutOfRangeException(nameof(limits));

        var models = new PropModel[assets.Length];
        long meshBytes = 0;
        for (var i = 0; i < assets.Length; i++)
        {
            var asset = assets[i];
            if (asset.Near is null || asset.Far is null)
                throw new ArgumentException("Both prop LOD meshes are required.", nameof(assets));
            models[i] = asset.Model;
            foreach (var mesh in new[] { asset.Near, asset.Far })
            {
                var radius = (double)asset.Model.Radius;
                if (mesh.Indices.IsEmpty)
                    throw new ArgumentException("Empty prop asset.", nameof(assets));
                foreach (var vertex in mesh.Positions)
                    if ((double)vertex.X * vertex.X + (double)vertex.Y * vertex.Y + (double)vertex.Z * vertex.Z > radius * radius)
                        throw new ArgumentException("Prop radius must enclose both LOD meshes.", nameof(assets));
                var bytes = (long)mesh.Positions.Length * (mesh.PackedNormals.IsEmpty ? 16 : 20) + 4L * mesh.Indices.Length;
                meshBytes += bytes;
                if (bytes > MaximumUploadBytes - 16L * limits.ResidentInstances)
                    throw new ArgumentException("A prop mesh exceeds the per-frame upload slice.", nameof(assets));
            }
        }

        // Include the resident payload and visibility records, both reference tables,
        // provisional indices and LOD scratch. Pending records have no visibility tables.
        if (meshBytes + 128L * limits.PendingInstances + 224L * limits.ResidentInstances + 768L * limits.Cells > MaximumGpuBytes)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var payloadBytes = (limits.Cells + 1L) * instancesPerCell * PropInstanceData.ByteSize;
        if (meshBytes + payloadBytes + 16L * limits.ResidentInstances + 128 * 1024 > MaximumGpuBytes)
            throw new ArgumentOutOfRangeException(nameof(limits));
        GeometryBytes = (int)meshBytes;
        Admission = new(limits, seed, recipe, models);
        Limits = limits;
        InstancesPerCell = instancesPerCell;
        Assets = assets.ToArray();
        Ready = new StagedCell[limits.PendingCells];
    }

    public void Publish(PropPublication publication, ReadOnlySpan<PropCellKey> coverage, IReadOnlySet<PropCellKey>? invalidated = null)
    {
        if (coverage.Length > Limits.Cells)
            throw new ArgumentException("Prop coverage exceeds cell capacity.", nameof(coverage));
        var covered = coverage.ToArray();
        HashSet<PropCellKey>? excluded = null;
        if (invalidated is not null)
        {
            excluded = new(covered.Length);
            foreach (var cell in covered)
                if (invalidated.Contains(cell))
                    excluded.Add(cell);
        }

        lock (Gate)
        {
            if (Closed)
                return;
            if (_world != Guid.Empty && publication.World != _world)
                throw new ArgumentException("Prop publication belongs to a different world.", nameof(publication));
            Admission.Publish(publication, covered, excluded);
            Publication = publication;
            Array.Clear(Ready);
            _trackScenePresentation = false;
            _requiredCells = [];
            RequiredDraw = null;
            _presentedSequence = 0;
            ViewVersion++;
        }
    }

    /// <summary>Change prop coverage inside the current terrain cut and request a scene presentation receipt.</summary>
    /// <remarks>If requiredDraw is supplied, that cell must issue a draw in the presented frame.
    /// This does not measure pixel visibility after depth testing.</remarks>
    public bool TryReviseCoverage(PropPublication publication, PropCellKey[] coverage, PropCellKey[] invalidated,
        PropCellKey[] required, ulong sceneRevision, PropCellKey? requiredDraw = null)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(invalidated);
        ArgumentNullException.ThrowIfNull(required);
        if (coverage.Length > Limits.Cells || required.Length > coverage.Length || invalidated.Length > Limits.Cells)
            return false;
        var covered = coverage.ToHashSet();
        var excluded = invalidated.ToHashSet();
        var requiredCells = (PropCellKey[])required.Clone();
        if (requiredCells.Any(cell => !covered.Contains(cell)) ||
            requiredDraw is { } draw && !requiredCells.Contains(draw))
            return false;

        lock (Gate)
        {
            if (Closed || Publication != publication || !publication.IsValid ||
                _trackScenePresentation && sceneRevision <= _sceneRevision)
                return false;
            Admission.ReviseCoverage(covered, excluded);
            for (var i = 0; i < Ready.Length; i++)
                if (Ready[i].Request.Generation != 0 && !Admission.IsCurrent(Ready[i].Request))
                    Ready[i] = default;
            _requiredCells = requiredCells;
            RequiredDraw = requiredDraw;
            _sceneRevision = sceneRevision;
            _trackScenePresentation = true;
            _presentedSequence = 0;
            ViewVersion++;
            return true;
        }
    }

    internal PropCellKey? RequiredDraw { get; private set; }

    /// <summary>Read an actual successful Present receipt for a coherent prop scene.</summary>
    public bool HasPresentedScene(PropPublication publication, ulong sceneRevision, ulong minimumSequence)
    {
        lock (Gate)
            return !Closed && _presentedSequence != 0 && _presentedSequence >= minimumSequence &&
                _presentedRevision == sceneRevision && _presentedPublication == publication;
    }

    internal bool TryGetCompleteScene(out ulong sceneRevision)
    {
        sceneRevision = _sceneRevision;
        if (!_trackScenePresentation)
            return false;
        foreach (var cell in _requiredCells)
            if (!Admission.HasResident(cell))
                return false;
        return true;
    }

    internal void ObserveScenePresent(PropPublication publication, ulong sceneRevision, ulong viewVersion, ulong sequence)
    {
        lock (Gate)
        {
            if (Closed || !_trackScenePresentation || ViewVersion != viewVersion || Publication != publication ||
                _sceneRevision != sceneRevision)
                return;
            _presentedPublication = publication;
            _presentedRevision = sceneRevision;
            _presentedSequence = sequence;
        }
    }

    public PropCellResult TryRequest(PropCellKey cell, int maxInstances, out PropCellRequest request)
    {
        lock (Gate)
        {
            request = default;
            return Closed ? PropCellResult.Stale : maxInstances > InstancesPerCell ? PropCellResult.Quota :
                Admission.TryRequest(cell, maxInstances, out request);
        }
    }

    /// <summary>Transfer the result after worker exit and terrain lease release.</summary>
    public PropCellResult TryStage(PropCellRequest request, PropPoint origin, ReadOnlySpan<PropPlacement> instances)
    {
        lock (Gate)
        {
            if (Closed)
                return PropCellResult.Stale;
            var result = Admission.TryStage(request, origin, instances);
            if (result == PropCellResult.Accepted)
            {
                try
                {
                    Ready[Array.IndexOf(Ready, default)] = new(request, new int[instances.Length]);
                }
                catch
                {
                    Admission.TryCancel(request);
                    throw;
                }
            }
            return result;
        }
    }

    public bool CompleteWorker(PropCellRequest request)
    {
        lock (Gate)
            return Admission.CompleteWorker(request);
    }

    public bool TryCancel(PropCellRequest request)
    {
        lock (Gate)
        {
            if (Closed || !Admission.TryCancel(request))
                return false;
            for (var i = 0; i < Ready.Length; i++)
            {
                if (Ready[i].Request != request)
                    continue;
                Ready[i] = default;
                ViewVersion++;
                break;
            }
            return true;
        }
    }

    public bool TryEvict(PropCellKey cell)
    {
        lock (Gate)
        {
            if (Closed || !Admission.TryEvict(cell))
                return false;
            for (var i = 0; i < Ready.Length; i++)
                if (Ready[i].Request.Cell == cell)
                    Ready[i] = default;
            ViewVersion++;
            return true;
        }
    }

    public D3D12PropStatistics ReadStatistics()
    {
        lock (Gate)
            return Statistics;
    }

    internal void BindWorld(Guid world)
    {
        if (Publication.World != Guid.Empty && Publication.World != world)
            throw new InvalidOperationException("The prop stream belongs to a different world.");
        _world = world;
    }

    internal void Close()
    {
        lock (Gate)
        {
            Closed = true;
            Admission.Close();
            Array.Clear(Ready);
            ViewVersion++;
        }
    }
}
