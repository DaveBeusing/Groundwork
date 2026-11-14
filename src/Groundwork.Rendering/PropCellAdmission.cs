namespace Groundwork.Rendering;

/// <summary>Owner-thread prop admission and visibility. Prop slots are independent of terrain admission.</summary>
/// <remarks>Call publication, installation and selection serially at frame boundaries. Workers receive only request values.</remarks>
public sealed class PropCellAdmission
{
    private readonly record struct Instance(PropInstanceId Id, int Model, PropInstanceData Data);
    private sealed class Cell
    {
        internal PropCellKey Key;
        internal ulong Request;
        internal int Reservation;
        internal Instance[]? Staged;
        internal PropPoint Origin;
        internal PropPoint StagedOrigin;
        internal double Radius;
        internal double StagedRadius;
        internal Instance[] Instances = [];
        internal bool[] Near = [];
    }

    private readonly PropCellLimits _limits;
    private readonly ulong _seed;
    private readonly uint _recipe;
    private readonly PropModel[] _models;
    private readonly Cell?[] _cells;
    private readonly Dictionary<PropCellKey, int> _slots;
    private readonly HashSet<PropCellKey> _coverage = [];
    private readonly Dictionary<PropCellRequest, int> _workers = new();
    private ulong _generation;
    private PropPublication _publication;

    public PropCellUsage Usage { get; private set; }
    public PropCellUsage Peak { get; private set; }

    public PropCellAdmission(PropCellLimits limits, ulong seed, uint recipe, ReadOnlySpan<PropModel> models)
    {
        if (limits.Cells <= 0 || limits.PendingCells <= 0 || limits.PendingCells > limits.Cells ||
            limits.PendingInstances <= 0 || limits.ResidentInstances <= 0 || limits.Groups is <= 0 or > 96)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (recipe == 0 || models.IsEmpty || models.Length > limits.Groups / 2)
            throw new ArgumentOutOfRangeException(nameof(models));

        var identities = new HashSet<(uint, uint)>();
        foreach (var model in models)
            if (!model.IsValid || !identities.Add((model.Model, model.Material)))
                throw new ArgumentException("Invalid or duplicate prop model.", nameof(models));

        _limits = limits;
        _seed = seed;
        _recipe = recipe;
        _models = models.ToArray();
        _cells = new Cell[limits.Cells];
        _slots = new(limits.Cells);
        _coverage.EnsureCapacity(limits.Cells);
    }

    /// <summary>Replace the eligible coverage cut. Null invalidation drops older terrain revisions in full.</summary>
    public void Publish(PropPublication publication, ReadOnlySpan<PropCellKey> coveredCells,
        IReadOnlySet<PropCellKey>? invalidated = null)
    {
        if (!publication.IsValid || coveredCells.Length > _limits.Cells ||
            (_publication.World == publication.World &&
             (publication.RendererEpoch < _publication.RendererEpoch ||
              (publication.RendererEpoch == _publication.RendererEpoch &&
               (publication.CoverageGeneration <= _publication.CoverageGeneration ||
                publication.TerrainRevision < _publication.TerrainRevision)))))
            throw new ArgumentException("Invalid or out-of-order prop publication.", nameof(publication));

        _coverage.Clear();
        foreach (var key in coveredCells)
            _coverage.Add(key);
        var sameOwner = _publication.World == publication.World && _publication.RendererEpoch == publication.RendererEpoch;
        var sameRevision = _publication.TerrainRevision == publication.TerrainRevision;
        for (var slot = 0; slot < _cells.Length; slot++)
        {
            if (_cells[slot] is not { } cell)
                continue;
            Cancel(cell);
            if (!sameOwner || !_coverage.Contains(cell.Key) ||
                (!sameRevision && invalidated is null) || invalidated?.Contains(cell.Key) == true ||
                cell.Instances.Length == 0)
                Remove(slot, cell);
        }
        _publication = publication;
    }

    /// <summary>Reserve placement capacity before creating a worker or allocating its result.</summary>
    public PropCellResult TryRequest(PropCellKey key, int maxInstances, out PropCellRequest request)
    {
        request = default;
        if (maxInstances <= 0)
            return PropCellResult.Invalid;
        if (!_coverage.Contains(key))
            return PropCellResult.MissingCoverage;
        var exists = _slots.TryGetValue(key, out var slot);
        if (exists && _cells[slot]!.Request != 0)
            return PropCellResult.Pending;
        if (Usage.PendingCells == _limits.PendingCells || maxInstances > _limits.PendingInstances - Usage.PendingInstances ||
            (!exists && Usage.Cells == _limits.Cells))
            return PropCellResult.Quota;

        var generation = checked(++_generation);
        if (!exists)
        {
            slot = Array.IndexOf(_cells, null);
            _cells[slot] = new Cell { Key = key };
            _slots.Add(key, slot);
        }
        var cell = _cells[slot]!;
        cell.Request = generation;
        cell.Reservation = maxInstances;
        Usage = new(_slots.Count, Usage.PendingCells + 1, Usage.PendingInstances + maxInstances, Usage.ResidentInstances);
        Peak = new(Math.Max(Peak.Cells, Usage.Cells), Math.Max(Peak.PendingCells, Usage.PendingCells),
            Math.Max(Peak.PendingInstances, Usage.PendingInstances), Math.Max(Peak.ResidentInstances, Usage.ResidentInstances));
        request = new(key, _publication, generation);
        _workers.Add(request, maxInstances);
        return PropCellResult.Accepted;
    }

    /// <summary>Stage a validated copy after the worker has stopped and released its terrain lease.</summary>
    /// <remarks>Placements arrive in strictly increasing ordinal order. On failure, retry staging or call CompleteWorker after releasing the result.</remarks>
    public PropCellResult TryStage(PropCellRequest request, PropPoint origin, ReadOnlySpan<PropPlacement> placements)
    {
        if (!TryMatch(request, out var slot))
            return PropCellResult.Stale;
        var cell = _cells[slot]!;
        if (cell.Staged is not null)
            return PropCellResult.Pending;
        if (placements.Length > cell.Reservation)
            return PropCellResult.Quota;
        if (!origin.IsValid)
            return PropCellResult.Invalid;

        var radius = 0d;
        uint previous = 0;
        for (var i = 0; i < placements.Length; i++)
        {
            ref readonly var item = ref placements[i];
            var rotationLength = item.Orientation.LengthSquared();
            if ((i != 0 && item.Ordinal <= previous) || (uint)item.ModelIndex >= (uint)_models.Length ||
                !float.IsFinite(item.Scale) || item.Scale <= 0 ||
                !(MathF.Abs(rotationLength - 1) <= 0.0001f) ||
                !(MathF.Abs(item.Translation.X) <= 4096 && MathF.Abs(item.Translation.Y) <= 4096 && MathF.Abs(item.Translation.Z) <= 4096))
                return PropCellResult.Invalid;
            previous = item.Ordinal;
            radius = Math.Max(radius, Math.Sqrt(new PropPoint(item.Translation.X, item.Translation.Y, item.Translation.Z).LengthSquared) + (double)item.Scale * _models[item.ModelIndex].Radius);
        }

        var instances = new Instance[placements.Length];
        for (var i = 0; i < instances.Length; i++)
        {
            ref readonly var item = ref placements[i];
            instances[i] = new(new(_seed, _recipe, cell.Key, item.Ordinal), item.ModelIndex,
                PropInstanceData.Pack(item, (uint)slot));
        }
        cell.StagedOrigin = origin;
        cell.StagedRadius = radius;
        cell.Staged = instances;
        _workers.Remove(request);
        return PropCellResult.Accepted;
    }

    /// <summary>Copy an entire staged cell for upload, independent of the current visibility selection.</summary>
    /// <remarks>The required count is returned on Quota. No partial payload is written.</remarks>
    public PropCellResult CopyStaged(PropCellRequest request, Span<PropInstanceData> destination, out int count, out PropPoint origin)
    {
        count = 0;
        origin = default;
        if (!TryMatch(request, out var slot))
            return PropCellResult.Stale;
        var cell = _cells[slot]!;
        if (cell.Staged is not { } instances)
            return PropCellResult.Pending;
        count = instances.Length;
        if (destination.Length < count)
            return PropCellResult.Quota;

        for (var i = 0; i < count; i++)
            destination[i] = instances[i].Data;
        origin = cell.StagedOrigin;
        return PropCellResult.Accepted;
    }

    public PropCellResult TryInstall(PropCellRequest request)
    {
        if (!TryMatch(request, out var slot))
            return PropCellResult.Stale;
        var cell = _cells[slot]!;
        if (cell.Staged is not { } instances)
            return PropCellResult.Pending;
        var retained = Usage.ResidentInstances - cell.Instances.Length;
        if (instances.Length > _limits.ResidentInstances - retained)
            return PropCellResult.Quota;

        var near = new bool[instances.Length];
        var old = 0;
        for (var i = 0; i < instances.Length; i++)
        {
            var ordinal = instances[i].Id.Ordinal;
            while (old < cell.Instances.Length && cell.Instances[old].Id.Ordinal < ordinal)
                old++;
            near[i] = old < cell.Instances.Length && cell.Instances[old].Id.Ordinal == ordinal &&
                cell.Instances[old].Model == instances[i].Model && cell.Near[old];
        }
        cell.Origin = cell.StagedOrigin;
        cell.Radius = cell.StagedRadius;
        cell.Instances = instances;
        cell.Near = near;
        Cancel(cell);
        Usage = Usage with { ResidentInstances = retained + instances.Length };
        Peak = Peak with { ResidentInstances = Math.Max(Peak.ResidentInstances, Usage.ResidentInstances) };
        if (instances.Length == 0)
            Remove(slot, cell);
        return PropCellResult.Accepted;
    }

    /// <summary>Release an unstaged reservation after the worker, its terrain lease and its result have settled.</summary>
    /// <remarks>Also call for requests that were never dispatched. An invalidated token can still settle once.</remarks>
    public bool CompleteWorker(PropCellRequest request)
    {
        if (!_workers.Remove(request, out var reservation))
            return false;
        if (TryMatch(request, out var slot))
        {
            var cell = _cells[slot]!;
            ClearRequest(cell);
            if (cell.Instances.Length == 0)
                Remove(slot, cell);
        }
        ReleasePending(reservation);
        return true;
    }

    public bool TryCancel(PropCellRequest request)
    {
        if (!TryMatch(request, out var slot))
            return false;
        var cell = _cells[slot]!;
        Cancel(cell);
        if (cell.Instances.Length == 0)
            Remove(slot, cell);
        return true;
    }

    public bool TryEvict(PropCellKey key)
    {
        if (!_slots.TryGetValue(key, out var slot))
            return false;
        var cell = _cells[slot]!;
        Cancel(cell);
        Remove(slot, cell);
        return true;
    }

    /// <summary>Produce sorted references into resident cells, without allocations or truncation.</summary>
    public int Select(PropPoint eye, ReadOnlySpan<PropFrustumPlane> frustum, Span<VisibleProp> output)
    {
        if (!eye.IsValid || frustum.Length != 6)
            throw new ArgumentException("A finite eye and six frustum planes are required.");
        foreach (var plane in frustum)
            if (!plane.IsValid)
                throw new ArgumentException("Frustum planes must have unit normals.", nameof(frustum));
        if (output.Length < Usage.ResidentInstances)
            throw new ArgumentException("Visibility scratch must fit the resident instance count.", nameof(output));

        var count = 0;
        for (var slot = 0; slot < _cells.Length; slot++)
        {
            if (_cells[slot] is not { } cell || cell.Instances.Length == 0)
                continue;
            var center = cell.Origin.RelativeTo(eye);
            var cellVisible = Intersects(center, cell.Radius, frustum);
            for (var i = 0; i < cell.Instances.Length; i++)
            {
                ref readonly var instance = ref cell.Instances[i];
                var model = _models[instance.Model];
                var t = instance.Data.Translation;
                var position = new PropPoint(center.X + t.X, center.Y + t.Y, center.Z + t.Z);
                var distance = position.LengthSquared;
                var threshold = (double)(cell.Near[i] ? model.NearExit : model.NearEnter);
                cell.Near[i] = distance <= threshold * threshold;
                if (cellVisible && distance <= (double)model.Range * model.Range &&
                    Intersects(position, (double)instance.Data.Scale * model.Radius, frustum))
                    output[count++] = new(instance.Id, model.Model, model.Material, cell.Near[i] ? (byte)0 : (byte)1, (uint)slot, i);
            }
        }
        output[..count].Sort(static (left, right) => left.CompareTo(right));
        return count;
    }

    /// <summary>Valid until the next owner mutation. The native owner must charge and retain its own uploaded copy.</summary>
    public bool TryRead(VisibleProp visible, out PropInstanceData data, out PropPoint origin)
    {
        if (visible.CellSlot < (uint)_cells.Length && _cells[(int)visible.CellSlot] is { } cell &&
            (uint)visible.InstanceIndex < (uint)cell.Instances.Length &&
            cell.Instances[visible.InstanceIndex].Id == visible.Id)
        {
            data = cell.Instances[visible.InstanceIndex].Data;
            origin = cell.Origin;
            return true;
        }
        data = default;
        origin = default;
        return false;
    }

    private bool TryMatch(PropCellRequest request, out int slot)
    {
        slot = -1;
        return request.Generation != 0 && request.Publication == _publication &&
            _slots.TryGetValue(request.Cell, out slot) && _cells[slot]!.Request == request.Generation;
    }

    private void Cancel(Cell cell)
    {
        if (cell.Request == 0)
            return;
        // Unstaged work remains charged through its worker token after invalidation.
        if (cell.Staged is not null)
            ReleasePending(cell.Reservation);
        ClearRequest(cell);
    }

    private void ReleasePending(int reservation) =>
        Usage = Usage with { PendingCells = Usage.PendingCells - 1, PendingInstances = Usage.PendingInstances - reservation };

    private static void ClearRequest(Cell cell)
    {
        cell.Request = 0;
        cell.Reservation = 0;
        cell.Staged = null;
    }

    private void Remove(int slot, Cell cell)
    {
        _cells[slot] = null;
        _slots.Remove(cell.Key);
        Usage = Usage with { Cells = _slots.Count, ResidentInstances = Usage.ResidentInstances - cell.Instances.Length };
    }

    private static bool Intersects(PropPoint center, double radius, ReadOnlySpan<PropFrustumPlane> frustum)
    {
        foreach (var plane in frustum)
            if (plane.Evaluate(center) < -radius * 1.0001)
                return false;
        return true;
    }
}
