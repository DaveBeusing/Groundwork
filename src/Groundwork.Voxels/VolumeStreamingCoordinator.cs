using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Groundwork.Core;

namespace Groundwork.Voxels;

/// <summary>
/// Bounded WP11 terrain authority. Workers prepare privately, Core swaps one composed root,
/// and a later BeginTick releases movement after matching provider evidence.
/// </summary>
public sealed class VolumeStreamingCoordinator : IDisposable
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
            if (_stopped)
                return _view;
            if (rendererEpoch < _view.RendererEpoch || frame.Revision < _view.Frame.Revision ||
                frame.Revision == _view.Frame.Revision && frame != _view.Frame)
                throw new ArgumentException("Origin and renderer identities cannot move backwards.", nameof(frame));
            var nextView = new VolumeViewIdentity(rendererEpoch, checked(_view.View + 1), frame);
            if (!Record(VolumeEventKind.ViewChanged, 0, view: nextView))
                return _view;
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

    public bool ConfirmMask(VolumeViewIdentity view)
    {
        lock (_gate)
        {
            if (_stopped || _movementOpen || view != _view || !Record(VolumeEventKind.Accepted, 0))
                return false;
            _maskReady = true;
            return true;
        }
    }

    public VolumeAdmission TryStage(VolumeWorkClass kind, VolumeStreamingSelection selection,
        ulong sourceRevision, ulong targetRevision, long meshBytes, VolumeResources resources,
        IPlanetBrickSource source, out VolumeStreamingWork? work)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(source);
        work = null;
        if (kind is VolumeWorkClass.Baseline || !Enum.IsDefined(kind) || selection.Descriptor != _descriptor ||
            source.Descriptor != _descriptor || targetRevision == 0 || targetRevision < sourceRevision ||
            !resources.IsNonnegative || resources.CpuBytes < RequiredCpuBytes(selection, meshBytes) ||
            resources.LeaseSlots < selection.Requests.Length || resources.DensityJobs < 1 ||
            resources.MeshJobs < 1 || resources.QueueSlots < 1 || resources.RetirementSlots < 1)
            return VolumeAdmission.Invalid;
        lock (_gate)
        {
            Refresh();
            if (_stopped)
                return VolumeAdmission.Closed;
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
            work = new VolumeStreamingWork(this, entry, source);
            return VolumeAdmission.Accepted;
        }
    }

    internal bool Complete(VolumeGeneration entry, VolumeArtifacts? artifacts)
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
            entry.Artifacts = artifacts;
            entry.Info = new VolumePublicationInfo(entry.Id, entry.TargetRevision, entry.View, entry.Selection.Minimum,
                entry.Selection.Width, artifacts.Plan.Identity);
            return true;
        }
    }

    /// <summary>Composes a terrain root at Core's sealed CommitResults boundary. Core finalizes it separately.</summary>
    public PreparedBarrierPublication? TryPreparePublication(ulong generation, CandidateSet candidates,
        ImmutableArray<BatchDisposition> dispositions, IWorldStateRoot? composedState = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_gate)
        {
            Refresh();
            var entry = Array.Find(_entries, e => e?.Id == generation);
            if (_stopped || !_maskReady || _prepared is not null || entry is null || entry.Outstanding ||
                entry.Artifacts is null || entry.Cancelled || entry.Kind == VolumeWorkClass.Speculative ||
                entry.View != _view || entry.SourceRevision != CurrentRevision() ||
                _active is not null && entry.View.View <= _active.View.View ||
                candidates.Request.Kind != BarrierKind.CommitResults)
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
            var root = new VolumeWorldRoot(state, this, entry);
            var permit = _world.Publication.RootChanges.TryReserve(candidates, root);
            if (permit is null)
                return null;
            // This evidence slot resolves from Core's atomic permit outcome, even after Stop.
            Record(VolumeEventKind.Commit, entry.Id, permit, terrainRevision: entry.TargetRevision);
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
            if (_stopped || _active is not { } active || active.Id != generation || active.View != view || view != _view ||
                frame <= _lastFrame || !HasJournalSpace())
                return null;
            var lease = Acquire(active);
            if (lease is null)
                return null;
            var registration = new VolumeFrameRegistration(this, active, lease, frame);
            _lastFrame = frame;
            Record(VolumeEventKind.Registered, generation);
            return registration;
        }
    }

    /// <summary>Provider Present and queue Signal succeeded for this registered frame. Native ownership remains pinned.</summary>
    public bool AcknowledgeFrame(VolumeFrameRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            Refresh();
            if (_stopped || registration.Owner != this || registration.Disposed || registration.Acknowledged ||
                registration.Generation != _active || registration.Info.View != _view ||
                registration.Frame != _lastFrame || !Record(VolumeEventKind.Accepted, registration.Info.Generation))
                return false;
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
                _accepted is not { } accepted || accepted.Generation != _active || accepted.Info.View != _view ||
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
                record = record with { Kind = status == RootChangeStatus.Committed ? VolumeEventKind.Commit : VolumeEventKind.Rejected,
                    Stamp = permit.CommittedStamp };
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
        VolumeViewIdentity? view = null, ulong terrainRevision = 0)
    {
        if (!HasJournalSpace())
            return false;
        var index = (_journalHead + _journalCount++) % _journal.Length;
        _journal[index] = new VolumeStreamingEvent(++_sequence, kind, generation, _world.Publication.Committed.Read().Stamp,
            view ?? _view, terrainRevision == 0 ? CurrentRevision() : terrainRevision, _lastFrame);
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
        if (entry.Released || entry.Outstanding || entry.Readers != 0)
            return;
        entry.Artifacts?.Dispose();
        entry.Artifacts = null;
        entry.Released = true;
        _used[(int)entry.Envelope] -= entry.Resources;
        var index = Array.IndexOf(_entries, entry);
        if (index >= 0)
            _entries[index] = null;
        if (_firstEdit == entry)
            _firstEdit = null;
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
