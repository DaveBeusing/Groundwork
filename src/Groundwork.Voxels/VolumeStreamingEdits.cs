using System.Runtime.CompilerServices;
using Groundwork.Core;

namespace Groundwork.Voxels;

public sealed partial class VolumeStreamingCoordinator
{
    public const int MaximumEditsPerTick = 32;
    public const int MaximumOverlaySamples = 65_536;
    public const int MaximumBrushAxis = 16;
    private readonly IPlanetBrickSource? _editBaseline;
    private VolumeEditOverlay? _initialOverlay;
    private readonly ulong _initialTerrainRevision = 1;
    private readonly PlanetWalkState? _initialPlayer;
    private readonly int _overlayLimit;
    private readonly EditSlot[]? _edits;
    private int _editCount;
    private ulong _sealedEditTick;
    private ulong _closedEditTick;
    private ulong _editSourceGeneration;

    private record struct EditSlot(VolumeBrushIntent Intent, VolumeEditStatus Status, BarrierStamp? Commit = null);

    /// <summary>Creates a world with one immutable generator lineage and a bounded sparse edit overlay.</summary>
    public VolumeStreamingCoordinator(WorldSafetyPorts world, IPlanetBrickSource baseline, VolumeStreamingLimits limits,
        int maximumOverlaySamples)
        : this(world, ValidateEditConfiguration(baseline, maximumOverlaySamples), limits)
    {
        _editBaseline = baseline;
        _initialOverlay = new VolumeEditOverlay(baseline);
        _overlayLimit = maximumOverlaySamples;
        _edits = new EditSlot[MaximumEditsPerTick];
    }

    private VolumeResources _startupOverlayCharge;

    /// <summary>Restores the sparse overlay before admitting the first terrain publication.</summary>
    public VolumeStreamingCoordinator(WorldSafetyPorts world, IPlanetBrickSource baseline, VolumeStreamingLimits limits,
        int maximumOverlaySamples, PlanetDescriptor checkpointPlanet, ulong restoredTerrainRevision, ReadOnlySpan<byte> overlay,
        Func<ushort, bool> supportsMaterial, PlanetWalkState? restoredPlayer = null)
        : this(world, baseline, limits, maximumOverlaySamples, restoredTerrainRevision, restoredPlayer,
            ReadInitialOverlay(baseline, limits, maximumOverlaySamples, checkpointPlanet, restoredTerrainRevision, overlay, supportsMaterial))
    {
    }

    private VolumeStreamingCoordinator(WorldSafetyPorts world, IPlanetBrickSource baseline, VolumeStreamingLimits limits,
        int maximumOverlaySamples, ulong restoredTerrainRevision, PlanetWalkState? restoredPlayer, VolumeEditOverlay overlay)
        : this(world, baseline.Descriptor, limits)
    {
        _editBaseline = baseline;
        _initialOverlay = overlay;
        _initialTerrainRevision = restoredTerrainRevision;
        _initialPlayer = restoredPlayer;
        _overlayLimit = maximumOverlaySamples;
        _edits = new EditSlot[MaximumEditsPerTick];
        _startupOverlayCharge = InitialOverlayCharge(overlay.Count);
        _used[(int)VolumeWorkClass.Baseline] += _startupOverlayCharge;
        UpdatePeak();
    }

    private static VolumeEditOverlay ReadInitialOverlay(IPlanetBrickSource baseline, VolumeStreamingLimits limits,
        int maximumOverlaySamples, PlanetDescriptor checkpointPlanet, ulong revision, ReadOnlySpan<byte> bytes,
        Func<ushort, bool> supportsMaterial)
    {
        if (checkpointPlanet != ValidateEditConfiguration(baseline, maximumOverlaySamples))
            throw new InvalidDataException("The restored checkpoint belongs to a different generator lineage.");
        ArgumentOutOfRangeException.ThrowIfZero(revision);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(supportsMaterial);
        if (!InitialOverlayCharge(bytes.Length / 32).Fits(limits.Envelope(VolumeWorkClass.Baseline)))
            throw new InvalidDataException("Restored overlay exceeds baseline custody.");
        return VolumeEditOverlay.Restore(baseline, maximumOverlaySamples, bytes, supportsMaterial);
    }

    private static VolumeResources InitialOverlayCharge(int rows) => new(32L * rows, 0, 0, 0, 0, 0, 0, 0, 0);

    private void ReleaseInitialOverlay()
    {
        _initialOverlay = null;
        _used[(int)VolumeWorkClass.Baseline] -= _startupOverlayCharge;
        _startupOverlayCharge = default;
    }
    private static PlanetDescriptor ValidateEditConfiguration(IPlanetBrickSource baseline, int limit)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumOverlaySamples);
        if (baseline.Descriptor.GeneratorVersion == PlanetGeologyField.GeneratorVersion && baseline is not PlanetGeologyField)
            throw new ArgumentException("Editable V2 terrain requires its natural geology source.", nameof(baseline));
        return baseline.Descriptor;
    }

    /// <summary>CPU credits for terrain, mesh payload and the complete sparse overlay allocation.</summary>
    public long RequiredEditableCpuBytes(VolumeStreamingSelection selection, long meshBytes) =>
        checked(RequiredCpuBytes(selection, meshBytes) + VolumeEditOverlay.ReservedBytes(_overlayLimit) +
            (_overlayLimit == 0 ? 0 : (long)selection.Requests.Length * Unsafe.SizeOf<VolumeMeshRequest>()));

    public static bool TryGetEditDependencies(in VolumeBrushIntent brush, out VolumeEditDependencies dependencies)
    {
        dependencies = default;
        const int halo = 5; // Incident cells, shared vertices, normal stencil and 2:1 transitions.
        if (brush.Tick == 0 || brush.Id == 0 || brush.ExpectedRevision == 0 ||
            brush.Operation is not (VolumeBrushOperation.Add or VolumeBrushOperation.Remove) ||
            (brush.Operation == VolumeBrushOperation.Add) != (brush.Material != 0) ||
            !ValidAxis(brush.Minimum.X, brush.Maximum.X) || !ValidAxis(brush.Minimum.Y, brush.Maximum.Y) ||
            !ValidAxis(brush.Minimum.Z, brush.Maximum.Z))
            return false;
        var lower = new LatticeCoordinate(brush.Minimum.X - halo, brush.Minimum.Y - halo, brush.Minimum.Z - halo);
        var upper = new LatticeCoordinate(brush.Maximum.X + halo, brush.Maximum.Y + halo, brush.Maximum.Z + halo);
        dependencies = new(lower.Locate().Brick, upper.Locate().Brick);
        return true;

        static bool ValidAxis(long min, long max) =>
            min >= -PlanetDensityField.MaximumCoordinate + halo &&
            max <= PlanetDensityField.MaximumCoordinate - halo &&
            max >= min && (Int128)max - min < MaximumBrushAxis;
    }

    /// <summary>Seals one input tick's intents in canonical ID order. Only its unresolved head can stage.</summary>
    public VolumeAdmission SealEditTick(ulong tick, PlanetDescriptor lineage, ReadOnlySpan<VolumeBrushIntent> intents)
    {
        if (_edits is null || tick == 0 || lineage != _descriptor || intents.IsEmpty)
            return VolumeAdmission.Invalid;
        if (intents.Length > MaximumEditsPerTick)
        {
            lock (_gate)
            {
                var stamp = _world.Publication.Committed.Read().Stamp;
                if (stamp.Kind == BarrierKind.BeginTick && stamp.Tick == tick && stamp.Tick > _sealedEditTick)
                    _closedEditTick = stamp.Tick;
            }
            return VolumeAdmission.Invalid;
        }
        Span<VolumeBrushIntent> ordered = stackalloc VolumeBrushIntent[intents.Length];
        intents.CopyTo(ordered);
        ordered.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        for (var index = 0; index < ordered.Length; index++)
            if (ordered[index].Tick != tick || !TryGetEditDependencies(ordered[index], out _) ||
                index > 0 && ordered[index - 1].Id == ordered[index].Id)
                return VolumeAdmission.Invalid;

        lock (_gate)
        {
            Refresh();
            if (_stopped || !PresentationEpochOpen)
                return VolumeAdmission.Closed;
            if (tick == _sealedEditTick)
            {
                if (ordered.Length != _editCount)
                    return VolumeAdmission.Invalid;
                for (var index = 0; index < _editCount; index++)
                    if (_edits[index].Intent != ordered[index])
                        return VolumeAdmission.Invalid;
                return VolumeAdmission.Accepted;
            }

            var stamp = _world.Publication.Committed.Read().Stamp;
            if (tick <= _closedEditTick || tick < _sealedEditTick || stamp.Kind != BarrierKind.BeginTick || tick != stamp.Tick)
                return VolumeAdmission.Stale;
            if (!_movementOpen || _active is not { } active || !IsCurrentRoot(active) || FindEditHead() >= 0)
                return VolumeAdmission.Backpressure;

            if (!CanAppendJournal(ordered.Length))
            {
                _closedEditTick = tick;
                return Deny();
            }
            foreach (var intent in ordered)
            {
                if (intent.ExpectedRevision != CurrentRevision())
                    return VolumeAdmission.Stale;
                if (!TryGetEditDependencies(intent, out var footprint) || !Contains(active.Selection, footprint))
                    return VolumeAdmission.Invalid;
            }
            _editCount = ordered.Length;
            _sealedEditTick = tick;
            _editSourceGeneration = active.Id;
            for (var index = 0; index < _editCount; index++)
            {
                _edits[index] = new(ordered[index], VolumeEditStatus.Pending);
                Record(VolumeEventKind.EditQueued, active.Id, edit: ordered[index]);
            }
            return VolumeAdmission.Accepted;
        }
    }

    /// <summary>Stages the sealed head after the host has changed to a held view.</summary>
    public VolumeAdmission TryStageEdit(ulong tick, ulong id, VolumeStreamingSelection selection, long meshBytes,
        VolumeResources resources, out VolumeStreamingWork? work)
    {
        ArgumentNullException.ThrowIfNull(selection);
        work = null;
        lock (_gate)
        {
            Refresh();
            if (_stopped || !PresentationEpochOpen)
                return VolumeAdmission.Closed;
            var index = FindEditHead();
            if (_edits is null || tick != _sealedEditTick || index < 0 ||
                _active is not { } active || !IsCurrentRoot(active))
                return VolumeAdmission.Stale;
            var slot = _edits[index];
            var intent = slot.Intent;
            if (intent.Id != id || slot.Status != VolumeEditStatus.Pending)
                return VolumeAdmission.Backpressure;
            if (active.Id != _editSourceGeneration || intent.ExpectedRevision != CurrentRevision() ||
                intent.ExpectedRevision == ulong.MaxValue)
            {
                ResolveEdit(intent, VolumeEditStatus.Stale);
                return _stopped ? VolumeAdmission.Closed : VolumeAdmission.Stale;
            }
            if (_movementOpen || active.View == _view || _prepared is not null)
                return VolumeAdmission.Backpressure;
            if (!TryGetEditDependencies(intent, out var footprint) || !Contains(selection, footprint) ||
                selection.RequiresFineCoverage && !selection.CoversFine(footprint))
                return VolumeAdmission.Invalid;

            var result = Stage(VolumeWorkClass.FirstEdit, selection, intent.ExpectedRevision, intent.ExpectedRevision + 1,
                meshBytes, resources, _editBaseline!, intent, out work);
            if (result == VolumeAdmission.Accepted)
                _edits[index] = slot with { Status = VolumeEditStatus.Building };
            return result;
        }
    }

    /// <summary>Rejects an unresolved head. Prepared or committed publications settle through Core.</summary>
    public bool RejectEdit(ulong tick, ulong id)
    {
        lock (_gate)
        {
            Refresh();
            var index = FindEditHead();
            if (_stopped || tick != _sealedEditTick || index < 0 || _edits![index].Intent.Id != id)
                return false;
            var slot = _edits[index];
            if (slot.Status == VolumeEditStatus.Pending)
            {
                ResolveEdit(slot.Intent, VolumeEditStatus.Rejected);
                return true;
            }
            if (_firstEdit is not { Permit: null } entry || entry.Edit != slot.Intent)
                return false;
            Cancel(entry);
            return true;
        }
    }

    public bool TryReadEdit(ulong tick, ulong id, out VolumeEditStatus status, out BarrierStamp? commit)
    {
        lock (_gate)
        {
            Refresh();
            if (_edits is not null && tick == _sealedEditTick)
                for (var index = 0; index < _editCount; index++)
                    if (_edits[index].Intent.Id == id)
                    {
                        status = _edits[index].Status;
                        commit = _edits[index].Commit;
                        return true;
                    }
            status = default;
            commit = null;
            return false;
        }
    }

    private int FindEditHead()
    {
        for (var index = 0; index < _editCount; index++)
            if (_edits![index].Status is VolumeEditStatus.Pending or VolumeEditStatus.Building)
                return index;
        return -1;
    }

    private void ResolveEdit(VolumeBrushIntent intent, VolumeEditStatus status, BarrierStamp? stamp = null,
        bool recordOutcome = true)
    {
        var index = FindEditHead();
        if (index < 0 || _edits![index].Intent != intent)
            return;
        if (status != VolumeEditStatus.Committed)
        {
            if (!_stopped && recordOutcome &&
                !Record(VolumeEventKind.EditRejected, 0, edit: intent, editStatus: status))
                return;
            _edits[index] = new(intent, status, stamp);
            return;
        }
        _edits[index] = new(intent, status, stamp);
        // The permit-backed commit and preceding EditQueued records determine these
        // outcomes without allocating new semantic journal slots after the swap.
        for (index++; index < _editCount; index++)
        {
            var successor = _edits[index].Intent;
            _edits[index] = new(successor, VolumeEditStatus.Stale);
        }
    }

    private void RejectUnresolvedEditsOnStop()
    {
        for (var index = 0; index < _editCount; index++)
            if (_edits![index].Status is VolumeEditStatus.Pending or VolumeEditStatus.Building)
                _edits[index] = _edits[index] with { Status = VolumeEditStatus.Rejected };
    }

    private static bool Contains(VolumeStreamingSelection selection, VolumeEditDependencies footprint)
    {
        var lower = selection.Minimum;
        var upper = footprint.Maximum;
        return footprint.Minimum.X >= lower.X && footprint.Minimum.Y >= lower.Y && footprint.Minimum.Z >= lower.Z &&
            (Int128)upper.X - lower.X < selection.Width && (Int128)upper.Y - lower.Y < selection.Width &&
            (Int128)upper.Z - lower.Z < selection.Width;
    }
}
