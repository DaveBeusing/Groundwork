using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Groundwork.Core;

namespace Groundwork.Voxels;

/// <summary>
/// Bounded WP11 terrain authority. Workers prepare privately, Core swaps one composed root,
/// and a later BeginTick releases movement after matching provider evidence.
/// </summary>
public sealed partial class VolumeStreamingCoordinator : IDisposable
{
    private readonly object _gate = new();
    private IRuntimeWorkLease? _custody;
    private readonly WorldSafetyPorts _world;
    private readonly VolumeStreamingLimits _limits;
    private readonly PlanetDescriptor _descriptor;
    private readonly VolumeResources[] _used = new VolumeResources[5];
    private readonly VolumeGeneration?[] _entries;
    private readonly VolumeStreamingEvent[] _journal;
    private readonly RootChangePermit?[] _journalPermits;
    private VolumeResources _peak;
    private VolumeGeneration? _active;
    private VolumeGeneration? _prepared;
    private VolumeGeneration? _firstEdit;
    private VolumeViewIdentity _view;
    private VolumeFrameRegistration? _accepted;
    private ulong _generation;
    private ulong _sequence;
    private ulong _lastFrame;
    private ulong _invalidRendererEpoch;
    private VolumeRendererAuthority? _rendererAuthority;
    private bool PresentationEpochOpen => _view.RendererEpoch > _invalidRendererEpoch;
    private int _journalHead;
    private int _journalCount;
    private int _readers;
    private bool _stopped;
    private bool _movementOpen;
    private bool _maskReady;

    public VolumeStreamingCoordinator(WorldSafetyPorts world, PlanetDescriptor descriptor, VolumeStreamingLimits limits)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(limits);
        _world = world;
        _descriptor = descriptor;
        _limits = limits;
        _entries = new VolumeGeneration[limits.Candidates];
        _journal = new VolumeStreamingEvent[limits.JournalRecords];
        _journalPermits = new RootChangePermit[limits.JournalRecords];
        _view = new VolumeViewIdentity(1, 1, PlanetLocalFrame.Create(default));
        _custody = world.Ownership.TryTrack(this) ?? throw new InvalidOperationException("Runtime custody quota is unavailable.");
    }

    public VolumeViewIdentity View
    {
        get { lock (_gate) return _view; }
    }

    private VolumeStreamingEvent? _terminalEvidence;
    public VolumeStreamingEvent? TerminalEvidence
    {
        get { lock (_gate) return _terminalEvidence; }
    }

    public VolumeStreamingStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return new VolumeStreamingStatistics(TotalUsed(), _peak, _entries.Count(e => e is not null),
                    _readers, !_movementOpen, _stopped);
            }
        }
    }

    public static long RequiredCpuBytes(VolumeStreamingSelection selection, long meshBytes)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentOutOfRangeException.ThrowIfNegative(meshBytes);
        return checked((long)selection.Requests.Length * (PlanetBrickStore.BrickPayloadBytes +
            Unsafe.SizeOf<VolumeMeshInput>() + Unsafe.SizeOf<VolumeMeshRequest>()) +
            (long)selection.MeshCount * IntPtr.Size + meshBytes);
    }

    public VolumeViewIdentity ChangeView(PlanetLocalFrame frame, ulong rendererEpoch)
    {
        if (frame.Revision == 0 || rendererEpoch == 0)
            throw new ArgumentOutOfRangeException(nameof(frame));
        lock (_gate)
        {
            Refresh();
            if (_stopped || rendererEpoch <= _invalidRendererEpoch)
                return _view;
            if (rendererEpoch < _view.RendererEpoch || frame.Revision < _view.Frame.Revision ||
                frame.Revision == _view.Frame.Revision && frame != _view.Frame)
                throw new ArgumentException("Origin and renderer identities cannot move backwards.", nameof(frame));
            var nextView = new VolumeViewIdentity(rendererEpoch, checked(_view.View + 1), frame);
            if (!Record(VolumeEventKind.ViewChanged, 0, view: nextView))
                return _view;
            _movementPermit?.Revoke();
            _movementPermit = null;
            _view = nextView;
            _movementOpen = false;
            _maskReady = false;
            _accepted = null;
            foreach (var entry in _entries)
                if (entry is not null && entry != _active)
                    Cancel(entry);
            return _view;
        }
    }

    /// <summary>Binds a single provider to a fresh held renderer epoch.</summary>
    public VolumeRendererAuthority? TryBindRenderer(ulong epoch)
    {
        lock (_gate)
        {
            Refresh();
            if (_stopped || !PresentationEpochOpen || _view.RendererEpoch != epoch ||
                _rendererAuthority?.Epoch == epoch || _maskReady || _prepared is not null || _active?.View == _view)
                return null;
            return _rendererAuthority = new VolumeRendererAuthority(epoch);
        }
    }

    private bool OwnsPresentation(VolumeRendererAuthority? renderer) =>
        _rendererAuthority is null ? renderer is null :
        ReferenceEquals(_rendererAuthority, renderer) && renderer!.Epoch == _view.RendererEpoch;

    public bool CanRequestMask(VolumeViewIdentity view)
    {
        lock (_gate)
        {
            Refresh();
            return !_stopped && PresentationEpochOpen && !_movementOpen && !_maskReady && view == _view && _active?.View != view;
        }
    }

    public bool ConfirmMask(VolumeViewIdentity view) => ConfirmMask(view, null);

    public bool ConfirmMask(VolumeViewIdentity view, VolumeRendererAuthority? renderer, ulong frame = 0)
    {
        lock (_gate)
        {
            if (_stopped || !PresentationEpochOpen || !OwnsPresentation(renderer) ||
                renderer is not null && frame == 0 || _movementOpen || view != _view ||
                !Record(VolumeEventKind.Accepted, 0, frame: frame == 0 ? null : frame))
                return false;
            _maskReady = true;
            return true;
        }
    }

    public VolumeAdmission TryStage(VolumeWorkClass kind, VolumeStreamingSelection selection,
        ulong sourceRevision, ulong targetRevision, long meshBytes, VolumeResources resources,
        IPlanetBrickSource source, out VolumeStreamingWork? work) =>
        Stage(kind, selection, sourceRevision, targetRevision, meshBytes, resources, source, null, out work);

    private VolumeAdmission Stage(VolumeWorkClass kind, VolumeStreamingSelection selection,
        ulong sourceRevision, ulong targetRevision, long meshBytes, VolumeResources resources,
        IPlanetBrickSource source, VolumeBrushIntent? edit, out VolumeStreamingWork? work)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(source);
        work = null;
        if (kind is VolumeWorkClass.Baseline || !Enum.IsDefined(kind) || selection.Descriptor != _descriptor ||
            source.Descriptor != _descriptor || targetRevision == 0 || targetRevision < sourceRevision ||
            !resources.IsNonnegative || resources.CpuBytes < RequiredEditableCpuBytes(selection, meshBytes) ||
            resources.LeaseSlots < selection.Requests.Length || resources.DensityJobs < 1 ||
            resources.MeshJobs < 1 || resources.QueueSlots < 1 || resources.RetirementSlots < 1)
            return VolumeAdmission.Invalid;
        if (_editBaseline is not null && (!ReferenceEquals(source, _editBaseline) ||
            (edit is null && (kind == VolumeWorkClass.FirstEdit || targetRevision != Math.Max(sourceRevision, 1UL)))))
            return VolumeAdmission.Invalid;
        lock (_gate)
        {
            Refresh();
            if (_stopped || !PresentationEpochOpen)
                return VolumeAdmission.Closed;
            var head = FindEditHead();
            if (_editBaseline is not null && edit is null && head >= 0 &&
                (kind != VolumeWorkClass.Recovery || _edits![head].Status == VolumeEditStatus.Building))
                return VolumeAdmission.Backpressure;
            if (sourceRevision != CurrentRevision())
                return VolumeAdmission.Stale;
            if (kind == VolumeWorkClass.FirstEdit && _firstEdit is not null)
                return Deny();
            var slot = Array.FindIndex(_entries, static e => e is null);
            if (slot < 0 || !resources.Fits(_limits.Envelope(kind) - _used[(int)kind]))
                return Deny();
            if (!Record(VolumeEventKind.Admitted, checked(_generation + 1), terrainRevision: targetRevision))
                return VolumeAdmission.Closed;
            var entry = new VolumeGeneration(++_generation, kind, selection, sourceRevision, targetRevision, _view, meshBytes, resources);
            _used[(int)kind] += resources;
            UpdatePeak();
            _entries[slot] = entry;
            if (kind == VolumeWorkClass.FirstEdit)
                _firstEdit = entry;
            entry.Overlay = _active?.Overlay ?? _emptyOverlay;
            entry.Edit = edit;
            work = new VolumeStreamingWork(this, entry, source, _overlayLimit);
            return VolumeAdmission.Accepted;
        }
    }

    internal bool Complete(VolumeGeneration entry, VolumeArtifacts? artifacts, VolumeEditOverlay? overlay = null)
    {
        lock (_gate)
        {
            Refresh();
            if (entry.Released || !entry.Outstanding)
            {
                artifacts?.Dispose();
                return false;
            }
            entry.Outstanding = false;
            _used[(int)entry.Envelope] -= entry.Resources - entry.Resources.Retained;
            entry.Resources = entry.Resources.Retained;
            if (_stopped || entry.Cancelled || entry.View != _view || entry.SourceRevision != CurrentRevision() ||
                artifacts is null || !Record(VolumeEventKind.WorkReady, entry.Id, terrainRevision: entry.TargetRevision))
            {
                artifacts?.Dispose();
                entry.Cancelled = true;
                Release(entry);
                return false;
            }
            entry.Overlay = overlay;
            entry.Artifacts = artifacts;
            entry.Info = new VolumePublicationInfo(entry.Id, entry.TargetRevision, entry.View, entry.Selection.Minimum,
                entry.Selection.Width, artifacts.Plan.Identity);
            return true;
        }
    }

    /// <summary>Pins completed private geometry for bounded provider preparation.</summary>
    public VolumeStreamingLease? TryAcquirePreparedGeometry(VolumeRendererAuthority renderer, ulong generation, out VolumeResources credits,
        out long packingBytes)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        lock (_gate)
        {
            Refresh();
            credits = default;
            packingBytes = 0;
            var entry = FindPresentationCandidate(generation);
            if (!OwnsPresentation(renderer) || entry is null || entry == _active)
                return null;
            long meshBytes = 0;
            foreach (var mesh in entry.Artifacts!.Meshes)
                meshBytes += (long)mesh.Triangles.Length * PlanetVolumeMesher.TrianglePayloadBytes;
            credits = entry.Resources;
            packingBytes = credits.CpuBytes - RequiredEditableCpuBytes(entry.Selection, meshBytes);
            return Acquire(entry);
        }
    }

    /// <summary>Checks that a prepared or newly committed candidate still owns its view.</summary>
    public bool IsCurrentPresentationCandidate(VolumePublicationInfo info)
    {
        lock (_gate)
        {
            Refresh();
            return FindPresentationCandidate(info.Generation)?.Info == info;
        }
    }

    private VolumeGeneration? FindPresentationCandidate(ulong generation)
    {
        if (_stopped || !PresentationEpochOpen)
            return null;
        var entry = Array.Find(_entries, candidate => candidate?.Id == generation);
        return entry is { Artifacts: not null, Outstanding: false, Cancelled: false, Retired: false } &&
            entry.Kind != VolumeWorkClass.Speculative && entry.View == _view &&
            (entry == _active ? IsCurrentRoot(entry) : entry.SourceRevision == CurrentRevision()) ? entry : null;
    }

    /// <summary>Composes a terrain root at Core's sealed CommitResults boundary. Core finalizes it separately.</summary>
    public PreparedBarrierPublication? TryPreparePublication(ulong generation, CandidateSet candidates,
        ImmutableArray<BatchDisposition> dispositions, IWorldStateRoot? composedState = null) =>
        TryPreparePublication(generation, candidates, dispositions, composedState, null);

    public PreparedBarrierPublication? TryPreparePublication(ulong generation, CandidateSet candidates,
        ImmutableArray<BatchDisposition> dispositions, IWorldStateRoot? composedState, VolumeRendererAuthority? renderer)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_gate)
        {
            Refresh();
            var entry = Array.Find(_entries, e => e?.Id == generation);
            if (_stopped || !PresentationEpochOpen || !OwnsPresentation(renderer) || !_maskReady || _prepared is not null || entry is null || entry.Outstanding ||
                entry.Artifacts is null || entry.Cancelled || entry.Kind == VolumeWorkClass.Speculative ||
                entry.View != _view || entry.SourceRevision != CurrentRevision() ||
                _active is not null && entry.View.View <= _active.View.View ||
                candidates.Request.Kind != BarrierKind.CommitResults ||
                (entry.Edit is { } edit && (candidates.Request.Tick < edit.Tick || _active?.Id != _editSourceGeneration)))
                return null;
            var editHead = FindEditHead();
            if (entry.Edit is null && editHead >= 0 && _edits![editHead].Status == VolumeEditStatus.Building)
                return null;
            if (!entry.Resources.Fits(_limits.Envelope(VolumeWorkClass.Baseline) - _used[(int)VolumeWorkClass.Baseline]))
            {
                Deny();
                return null;
            }
            if (!HasJournalSpace())
                return null;
            var current = _world.Publication.Committed.Read().Root;
            if (current is VolumeWorldRoot foreign && foreign.Owner != this)
                return null;
            var state = composedState ?? (current is VolumeWorldRoot volume ? volume.State : current);
            var player = (current as VolumeWorldRoot)?.Player;
            if (player is { } pose && VolumeWalker.Validate(entry, pose, VolumeQueryLimits.Default).Status != VolumeQueryStatus.Clear)
                return null;
            var root = new VolumeWorldRoot(state, this, entry, player);
            var permit = _world.Publication.RootChanges.TryReserve(candidates, root);
            if (permit is null)
                return null;
            // This evidence slot resolves from Core's atomic permit outcome, even after Stop.
            Record(VolumeEventKind.Commit, entry.Id, permit, terrainRevision: entry.TargetRevision, edit: entry.Edit);
            var preparation = _world.Publication.Preparation.TryPrepare(new BarrierPublicationDraft(
                candidates, root, _world.State.Read().PendingFaultGeneration, dispositions, permit));
            if (preparation.Publication is not { } prepared)
            {
                permit.Revoke();
                return null;
            }
            _used[(int)entry.Envelope] -= entry.Resources;
            entry.Envelope = VolumeWorkClass.Baseline;
            _used[(int)entry.Envelope] += entry.Resources;
            entry.Permit = permit;
            _prepared = entry;
            _movementOpen = false;
            _accepted = null;
            UpdatePeak();
            return prepared;
        }
    }

    public VolumeStreamingLease? TryRead()
    {
        lock (_gate)
        {
            Refresh();
            return _stopped || _active is null || !IsCurrentRoot(_active) ? null : Acquire(_active);
        }
    }

    /// <summary>Tests the admitted support halo. C02 additionally validates body contacts against this density revision.</summary>
    public bool HasAdmittedCoverage(PlanetVector position)
    {
        lock (_gate)
        {
            Refresh();
            return !_stopped && _movementOpen && _active is { } active &&
                IsCurrentRoot(active) && active.Selection.Contains(_descriptor.SampleAt(position).Vertex);
        }
    }

    public VolumeFrameRegistration? TryRegisterFrame(ulong generation, VolumeViewIdentity view, ulong frame)
    {
        lock (_gate)
        {
            Refresh();
            if (_active is not { } active || active.Id != generation || active.View != view ||
                !CanRegisterFrame(active, frame, null))
                return null;
            var lease = Acquire(active);
            return lease is null ? null : RegisterFrame(active, frame, lease);
        }
    }

    /// <summary>Transfers the provider's private reader into its committed frame without another reader credit.</summary>
    public VolumeFrameRegistration? TryRegisterPreparedFrame(VolumeRendererAuthority renderer,
        VolumeStreamingLease prepared, ulong frame)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(prepared);
        return prepared.TransferToFrame(this, renderer, frame);
    }

    internal VolumeFrameRegistration? RegisterRetainedFrame(VolumeGeneration generation,
        VolumeRendererAuthority renderer, ulong frame)
    {
        lock (_gate)
        {
            Refresh();
            if (!CanRegisterFrame(generation, frame, renderer))
                return null;
            return RegisterFrame(generation, frame, new VolumeStreamingLease(this, generation));
        }
    }

    private bool CanRegisterFrame(VolumeGeneration generation, ulong frame, VolumeRendererAuthority? renderer) =>
        !_stopped && PresentationEpochOpen && OwnsPresentation(renderer) && generation == _active &&
        generation.View == _view && IsCurrentRoot(generation) && frame > _lastFrame && HasJournalSpace();

    private VolumeFrameRegistration RegisterFrame(VolumeGeneration generation, ulong frame, VolumeStreamingLease lease)
    {
        var registration = new VolumeFrameRegistration(this, generation, lease, frame);
        _lastFrame = frame;
        Record(VolumeEventKind.Registered, generation.Id);
        return registration;
    }

    /// <summary>Rechecks a provider's retained registration at installation and submission.</summary>
    public bool IsCurrentFrame(VolumeFrameRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            Refresh();
            return !_stopped && PresentationEpochOpen && registration.Owner == this && !registration.Disposed &&
                registration.Generation == _active && registration.Info.View == _view &&
                IsCurrentRoot(registration.Generation);
        }
    }

    /// <summary>Registers the exact camera frame against an already held generation before drawing it.</summary>
    public bool AuthorizeCameraFrame(VolumeFrameRegistration registration, ulong sequence)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            if (!IsCurrentFrame(registration) || !registration.Acknowledged || sequence < _lastFrame)
                return false;
            if (sequence == _lastFrame)
                return registration.LastCameraFrame == sequence;
            if (!Record(VolumeEventKind.Registered, registration.Info.Generation, frame: sequence))
                return false;
            _lastFrame = sequence;
            registration.LastCameraFrame = sequence;
            return true;
        }
    }

    /// <summary>Closes admission for a renderer that can no longer present. A newer renderer is unaffected.</summary>
    public void InvalidateRenderer(ulong epoch)
    {
        lock (_gate)
        {
            if (!_stopped && PresentationEpochOpen && _view.RendererEpoch == epoch)
            {
                ChangeView(_view.Frame, epoch);
                _invalidRendererEpoch = epoch;
            }
        }
    }

    /// <summary>Provider Present and queue Signal succeeded for this registered frame. Native ownership remains pinned.</summary>
    public bool AcknowledgeFrame(VolumeFrameRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            Refresh();
            if (_stopped || !PresentationEpochOpen || registration.Owner != this || registration.Disposed || registration.Acknowledged ||
                registration.Generation != _active || registration.Info.View != _view || !IsCurrentRoot(registration.Generation) ||
                registration.Frame != _lastFrame || !Record(VolumeEventKind.Accepted, registration.Info.Generation))
                return false;
            registration.AcknowledgedTick = _world.Publication.Committed.Read().Stamp.Tick;
            registration.Acknowledged = true;
            _accepted = registration;
            return true;
        }
    }

    /// <summary>Revalidates matching evidence at a finalized Core BeginTick. No pending movement is replayed.</summary>
    public bool BeginTick(BarrierStamp stamp)
    {
        lock (_gate)
        {
            Refresh();
            if (_stopped || stamp.Kind != BarrierKind.BeginTick || stamp != _world.Publication.Committed.Read().Stamp ||
                _accepted is not { } accepted || stamp.Tick <= accepted.AcknowledgedTick || accepted.Generation != _active || accepted.Info.View != _view || !IsCurrentRoot(accepted.Generation) ||
                !Record(VolumeEventKind.Opened, accepted.Info.Generation))
                return false;
            _movementOpen = true;
            _accepted = null;
            if (_firstEdit?.Permit?.Status == RootChangeStatus.Committed)
                _firstEdit = null;
            return true;
        }
    }

    public void Dispose() => Stop();

    internal bool IsCancelled(VolumeGeneration generation)
    {
        lock (_gate)
        {
            Refresh();
            return _stopped || generation.Cancelled;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            Refresh();
            StopInternal();
        }
    }

    public bool TryReadEvent(out VolumeStreamingEvent record)
    {
        lock (_gate)
        {
            if (_journalCount == 0)
            {
                record = default;
                return false;
            }
            record = _journal[_journalHead];
            if (_journalPermits[_journalHead] is { } permit)
            {
                var status = permit.Status;
                if (status == RootChangeStatus.Pending)
                    return false;
                record = record with
                {
                    Kind = record.Movement is not null
                        ? status == RootChangeStatus.Committed ? VolumeEventKind.MovementCommit : VolumeEventKind.MovementRejected
                        : status == RootChangeStatus.Committed ? VolumeEventKind.Commit : VolumeEventKind.Rejected,
                    Stamp = permit.CommittedStamp,
                    EditStatus = record.Edit is null ? null :
                        status == RootChangeStatus.Committed ? VolumeEditStatus.Committed : VolumeEditStatus.Rejected
                };
            }
            _journalPermits[_journalHead] = null;
            _journalHead = (_journalHead + 1) % _journal.Length;
            _journalCount--;
            return true;
        }
    }

    internal void ReleaseReader(VolumeGeneration entry)
    {
        lock (_gate)
        {
            entry.Readers--;
            _readers--;
            _used[(int)VolumeWorkClass.Baseline] -= ReaderCharge;
            if (entry.Readers == 0 && entry.Retired)
                Release(entry);
        }
    }

    internal void ReleaseFrame(VolumeFrameRegistration registration)
    {
        lock (_gate)
        {
            if (registration.Disposed)
                return;
            registration.Disposed = true;
        }
        registration.Lease.Dispose();
    }

    private static VolumeResources ReaderCharge => new(0, 0, 0, 0, 0, 0, 0, 1, 0);

    private VolumeStreamingLease? Acquire(VolumeGeneration entry)
    {
        if (_readers == _limits.Readers ||
            !ReaderCharge.Fits(_limits.Envelope(VolumeWorkClass.Baseline) - _used[(int)VolumeWorkClass.Baseline]))
            return null;
        var lease = new VolumeStreamingLease(this, entry);
        entry.Readers++;
        _readers++;
        _used[(int)VolumeWorkClass.Baseline] += ReaderCharge;
        UpdatePeak();
        return lease;
    }

    private void Refresh()
    {
        if (!_stopped && (_world.State.Read().Conditions & SafetyCondition.TerminalStop) != 0)
        {
            StopInternal();
            return;
        }
        if (_prepared is not { Permit: { } permit } entry)
            return;
        var status = permit.Status;
        if (status == RootChangeStatus.Pending)
            return;
        _prepared = null;
        if (status == RootChangeStatus.Committed)
        {
            var old = _active;
            _active = entry;
            if (entry.Edit is { } edit)
                ResolveEdit(edit, VolumeEditStatus.Committed, permit.CommittedStamp);
            if (old is not null)
            {
                old.Retired = true;
                if (old.Readers == 0)
                    Release(old);
            }
        }
        else
        {
            entry.Cancelled = true;
            Release(entry);
        }
        foreach (var other in _entries)
        {
            if (other is null || other == _active || other.Cancelled || other.SourceRevision == CurrentRevision())
                continue;
            other.Cancelled = true;
            Record(VolumeEventKind.Discarded, other.Id);
            if (!other.Outstanding)
                Release(other);
        }
    }

    private bool IsCurrentRoot(VolumeGeneration generation) =>
        _world.Publication.Committed.Read().Root is VolumeWorldRoot root &&
        root.Owner == this && root.Generation == generation;

    private ulong CurrentRevision() => _active?.TargetRevision ?? 0;

    private VolumeAdmission Deny()
    {
        return Record(VolumeEventKind.Backpressure, 0) ? VolumeAdmission.Backpressure : VolumeAdmission.Closed;
    }

    private bool HasJournalSpace()
    {
        if (_journalCount < _journal.Length && _sequence != ulong.MaxValue)
            return true;
        StopInternal();
        return false;
    }

    private bool Record(VolumeEventKind kind, ulong generation, RootChangePermit? permit = null,
        VolumeViewIdentity? view = null, ulong terrainRevision = 0, VolumeMovementEvidence? movement = null, ulong? frame = null, VolumeBrushIntent? edit = null, VolumeEditStatus? editStatus = null)
    {
        if (!HasJournalSpace())
            return false;
        var index = (_journalHead + _journalCount++) % _journal.Length;
        _journal[index] = new VolumeStreamingEvent(++_sequence, kind, generation, _world.Publication.Committed.Read().Stamp,
            view ?? _view, terrainRevision == 0 ? CurrentRevision() : terrainRevision, frame ?? _lastFrame, movement, edit, editStatus);
        _journalPermits[index] = permit;
        return true;
    }

    private void Cancel(VolumeGeneration entry)
    {
        entry.Permit?.Revoke();
        Refresh();
        if (entry == _active)
            return;
        entry.Cancelled = true;
        if (!entry.Outstanding)
            Release(entry);
    }

    private void StopInternal()
    {
        if (_stopped)
            return;
        _movementPermit?.Revoke();
        _movementPermit = null;
        _stopped = true;
        _movementOpen = false;
        _accepted = null;

        foreach (var entry in _entries)
        {
            if (entry is null)
                continue;
            entry.Permit?.Revoke();
            entry.Cancelled = true;
        }
        Refresh();
        RejectUnresolvedEditsOnStop();
        _terminalEvidence = new VolumeStreamingEvent(_sequence, VolumeEventKind.Stopped, _active?.Id ?? 0,
            _world.Publication.Committed.Read().Stamp, _view, CurrentRevision(), _lastFrame);
        foreach (var entry in _entries)
        {
            if (entry is null)
                continue;
            entry.Retired = true;
            if (!entry.Outstanding && entry.Readers == 0)
                Release(entry);
        }
        ReleaseCustodyIfSettled();
    }

    private void Release(VolumeGeneration entry)
    {
        if (entry.Released || entry.Outstanding)
            return;
        if (entry.Readers != 0)
        {
            entry.Retired = true;
            return;
        }
        entry.Artifacts?.Dispose();
        entry.Artifacts = null;
        entry.Overlay = null;
        entry.Released = true;
        _used[(int)entry.Envelope] -= entry.Resources;
        var index = Array.IndexOf(_entries, entry);
        if (index >= 0)
            _entries[index] = null;
        if (_firstEdit == entry)
            _firstEdit = null;
        if (entry.Edit is { } edit && entry.Permit?.Status != RootChangeStatus.Committed)
            ResolveEdit(edit, VolumeEditStatus.Rejected, recordOutcome: entry.Permit is null);
        ReleaseCustodyIfSettled();
    }

    private void ReleaseCustodyIfSettled()
    {
        if (_stopped && _readers == 0 && _entries.All(static e => e is null))
        {
            _custody?.Dispose();
            _custody = null;
        }
    }

    private VolumeResources TotalUsed()
    {
        var total = default(VolumeResources);
        foreach (var used in _used)
            total += used;
        return total;
    }

    private void UpdatePeak() => _peak = VolumeResources.Peak(_peak, TotalUsed());
}
