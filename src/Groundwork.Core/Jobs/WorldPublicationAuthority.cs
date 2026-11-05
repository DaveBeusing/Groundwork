namespace Groundwork.Core;

internal sealed partial class WorldPublicationAuthority
{
    private readonly WorldControl _control;
    private readonly IJobScopePolicy _policy;
    private readonly JobQuota _quota;
    private readonly TimeProvider _clock;
    private readonly IRuntimeOwnership _ownership;
    private readonly Dictionary<JobId, JobRecord> _jobs = [];
    private readonly LinkedList<JobRecord> _admissionOrder = [];
    private readonly HashSet<JobId> _active = [];
    private IWorldStateRoot? _root;
    private BarrierStamp _stamp;
    private JobDispatchPhase? _phase;
    private TickPhase _lastPhase;
    private BorrowDrain? _drain;
    private CandidateSet? _candidates;
    private PreparedBarrierPublication? _prepared;
    private ObserverFrame? _previousFrame;
    private ObserverFrame? _currentFrame;
    private ulong _lastJobId;
    private ulong _lastSourceOrdinal;
    private ulong _activeGeneration;
    private int _borrowed;
    private int _retentionHandles;
    private int _dependencyEdges;
    private int _deferred;
    private int _reservedDeferredBatches;
    private int _receiptRecords;
    private long _descriptorBytes;
    private long _snapshotBytes;
    private long _resultBytes;

    internal WorldPublicationAuthority(WorldControl control, WorldId world, IWorldStateRoot initialRoot,
        IJobScopePolicy policy, JobQuota quota, TimeProvider clock, IRuntimeOwnership ownership)
    {
        _control = control;
        _policy = policy;
        _quota = quota;
        _clock = clock;
        _ownership = ownership;
        _root = initialRoot;
        _stamp = new BarrierStamp(world, 0, 0, 0, 0);
        Ports = new WorldPublicationPorts(this);
    }

    internal WorldPublicationPorts Ports { get; }
    internal IBorrowBarrierControl BorrowControl => Ports.Borrow;
    private IWorldStateRoot CurrentRoot => _root ?? throw new ObjectDisposedException(nameof(WorldPublicationAuthority));

    internal BarrierStamp Stamp
    {
        get
        {
            lock (_control.Gate)
                return _stamp;
        }
    }

    internal CommittedWorldView ReadCommitted()
    {
        lock (_control.Gate)
            return new CommittedWorldView(_stamp, CurrentRoot);
    }

    internal JobPhaseOpenResult TryOpen(BarrierStamp stamp, TickPhase phase)
    {
        lock (_control.Gate)
        {
            if (_control.IsTerminal)
                return new(JobPhaseOpenStatus.Closed, null);
            if (_phase is not null)
                return new(JobPhaseOpenStatus.AlreadyOpen, null);
            if (_drain is not null || stamp != _stamp || stamp.Kind != BarrierKind.BeginTick ||
                phase is not (TickPhase.Gameplay or TickPhase.Base) || phase <= _lastPhase)
                return new(JobPhaseOpenStatus.InvalidPhase, null);

            _lastPhase = phase;
            _phase = new JobDispatchPhase(stamp, phase);
            return new(JobPhaseOpenStatus.Opened, _phase);
        }
    }

    internal void Close(JobDispatchPhase phase)
    {
        lock (_control.Gate)
        {
            if (ReferenceEquals(_phase, phase))
                _phase = null;
        }
    }

    internal JobCapturePermitResult TryAcquire(JobDispatchPermit permit)
    {
        ArgumentNullException.ThrowIfNull(permit);
        lock (_control.Gate)
        {
            if (!TryFindPermit(permit, out var job) || permit.InputKind == JobInputKind.None)
                return new(JobCapturePermitStatus.InvalidPermit, null);
            if (job.CaptureConsumed)
                return new(JobCapturePermitStatus.AlreadyConsumed, null);
            if (_control.IsTerminal)
            {
                // Capture can observe the terminal fence before the permit's stop observer runs.
                permit.SignalCancellation();
                return new(JobCapturePermitStatus.Expired, null);
            }
            if (job.CancellationRequested || job.Execution != JobExecutionStatus.Running ||
                _drain is not null || _phase?.Phase != permit.DispatchPhase || _stamp != permit.DispatchStamp)
                return new(JobCapturePermitStatus.Expired, null);
            if (_borrowed >= _quota.BorrowedLeases)
                return new(JobCapturePermitStatus.CapacityExceeded, null);

            var pin = new CaptureLease(this, job, new CommittedWorldView(_stamp, CurrentRoot));
            if (_ownership.TryTrack(pin) is not { } tracked)
            {
                if (_control.IsTerminal)
                {
                    permit.SignalCancellation();
                    return new(JobCapturePermitStatus.Expired, null);
                }
                return new(JobCapturePermitStatus.CapacityExceeded, null);
            }

            pin.Tracked = tracked;
            job.CaptureConsumed = true;
            job.CaptureOpen = true;
            _borrowed++;
            return new(JobCapturePermitStatus.Authorized, pin);
        }
    }

    private void ReleaseCapture(CaptureLease pin, JobRecord job)
    {
        lock (_control.Gate)
        {
            if (pin.Tracked is not { } tracked)
                return;

            pin.Tracked = null;
            pin.Clear();
            job.CaptureOpen = false;
            ReleaseBorrowPin();

            TryReleaseJob(job);
            tracked.Dispose();
        }
    }

    internal TargetVersion Issue(in TargetVersionStamp stamp)
    {
        lock (_control.Gate)
        {
            var stopped = _control.IsTerminal;
            if (stopped && TryFindPermit(stamp.Permit, out _))
                stamp.Permit.SignalCancellation();
            if (stopped || !TryFindPermit(stamp.Permit, out var job) || !job.CaptureOpen ||
                stamp.Target.Entity.World != _stamp.World || stamp.Target.Entity.Generation == 0 ||
                stamp.Target.Component.Value == 0 && !stamp.Present ||
                !CouldCover(job.Submission.ReadScopes, stamp.Target))
                throw new InvalidOperationException("A target version requires an active capture for the same world.");

            return new TargetVersion(this, stamp);
        }
    }

    internal bool TryRead(TargetVersion version, JobDispatchPermit permit, ComponentTarget target, out TargetVersionStamp stamp)
    {
        stamp = default;
        if (!ReferenceEquals(version.Owner, this) ||
            !ReferenceEquals(version.Stamp.Permit, permit) || version.Stamp.Target != target)
            return false;

        stamp = version.Stamp;
        return true;
    }

    private bool TryFindPermit(JobDispatchPermit? permit, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JobRecord? job)
    {
        job = null;
        return permit is not null && _jobs.TryGetValue(permit.Job, out job) && ReferenceEquals(job.Permit, permit);
    }

    internal ObserverReadResult AcquireObserver()
    {
        lock (_control.Gate)
        {
            if (_control.IsTerminal)
                return new(ObserverReadStatus.Closed, null);
            if (_currentFrame is not { } current)
                return new(ObserverReadStatus.Empty, null);
            if (_retentionHandles >= _quota.RetentionHandles)
                return new(ObserverReadStatus.CapacityExceeded, null);

            var lease = new ObserverLease(this, _previousFrame, current);
            if (_ownership.TryTrack(lease) is not { } tracked)
                return new(ObserverReadStatus.CapacityExceeded, null);

            lease.Tracked = tracked;
            _retentionHandles++;
            return new(ObserverReadStatus.Acquired, lease);
        }
    }

    private void ReleaseObserver(ObserverLease lease)
    {
        lock (_control.Gate)
        {
            if (lease.Tracked is not { } tracked)
                return;
            lease.Tracked = null;
            lease.Clear();
            _retentionHandles--;
            tracked.Dispose();
        }
    }

    private sealed class CaptureLease(WorldPublicationAuthority owner, JobRecord job, CommittedWorldView world) : IJobCaptureLease
    {
        private CommittedWorldView? _world = world;
        private JobRecord? _job = job;

        internal IRuntimeWorkLease? Tracked { get; set; }

        public CommittedWorldView World
        {
            get
            {
                lock (owner._control.Gate)
                {
                    return _world ?? throw new ObjectDisposedException(nameof(CaptureLease));
                }
            }
        }

        internal void Clear()
        {
            _world = null;
            _job = null;
        }

        public void Dispose()
        {
            lock (owner._control.Gate)
            {
                if (_job is { } current)
                    owner.ReleaseCapture(this, current);
            }
        }
    }

    private sealed class ObserverLease(WorldPublicationAuthority owner, ObserverFrame? previous, ObserverFrame current) : IObserverSnapshotLease
    {
        private ObserverFrame? _previous = previous;
        private ObserverFrame? _current = current;

        internal IRuntimeWorkLease? Tracked { get; set; }

        public ObserverFrame? Previous
        {
            get
            {
                lock (owner._control.Gate)
                {
                    ObjectDisposedException.ThrowIf(Tracked is null, this);
                    return _previous;
                }
            }
        }

        public ObserverFrame Current
        {
            get
            {
                lock (owner._control.Gate)
                {
                    return _current ?? throw new ObjectDisposedException(nameof(ObserverLease));
                }
            }
        }

        internal void Clear()
        {
            _previous = null;
            _current = null;
        }

        public void Dispose() => owner.ReleaseObserver(this);
    }
}
