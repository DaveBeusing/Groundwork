using System.Collections.Frozen;
using Groundwork.Core;

namespace Groundwork.Jobs;

/// <summary>A per-world dispatcher. Core owns the authoritative job and publication states.</summary>
public sealed partial class JobScheduler : IJobScheduler
{
    private readonly object _gate = new();
    private readonly IJobExecutionAuthority _authority;
    private readonly IJobInputSource _inputs;
    private readonly IRuntimeOwnership _ownership;
    private readonly IFaultReporter _faults;
    private readonly TimeProvider _clock;
    private readonly FrozenDictionary<JobHandlerId, JobHandler> _handlers;
    private readonly JobSchedulerLimits _limits;
    private readonly List<Entry> _pending = [];
    private readonly Dictionary<JobId, Work> _running = [];
    private readonly TaskCompletionSource _quiesced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private bool _admitting;
    private bool _dispatching;
    private int _stoppingQueued;
    private ulong _lastId;
    private long _accepted;
    private long _rejected;
    private long _started;
    private long _succeeded;
    private long _cancelled;
    private long _faulted;
    private long _queueTicks;
    private long _workerTicks;
    private long _cancellationTicks;
    private long _dispatchTicks;

    public JobScheduler(WorldId world, JobSchedulerLimits limits, IJobExecutionAuthority authority,
        IJobInputSource inputs, IRuntimeOwnership ownership, IFaultReporter faults, TimeProvider clock,
        IReadOnlyDictionary<JobHandlerId, JobHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(faults);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(handlers);
        if (world.Value == 0)
            throw new ArgumentOutOfRangeException(nameof(world));
        if (limits.QueuedJobs <= 0 || limits.Workers <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (handlers.Any(entry => entry.Key.Value == 0 || entry.Value is null))
            throw new ArgumentException("Each job handler requires a nonzero identity and a delegate.", nameof(handlers));

        World = world;
        _limits = limits;
        _authority = authority;
        _ownership = ownership;
        _faults = faults;
        _inputs = inputs;
        _clock = clock;
        _handlers = handlers.ToFrozenDictionary();
    }

    public WorldId World { get; }

    public JobAdmissionResult TrySubmit(JobSubmission submission, JobDispatchPhase phase)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(phase);
        JobId id;
        bool needsCaptureHeadroom;
        lock (_gate)
        {
            if (_closed || _lastId == ulong.MaxValue)
                return Reject(JobAdmissionStatus.Closed);
            if (submission.Source.World != World || phase.Stamp.World != World ||
                !_handlers.ContainsKey(submission.Handler))
                return Reject(JobAdmissionStatus.InvalidDescriptor);
            if (_admitting || _dispatching || _pending.Count >= _limits.QueuedJobs)
                return Reject(JobAdmissionStatus.CapacityExceeded);

            _admitting = true;
            id = new JobId(World, ++_lastId);
            needsCaptureHeadroom = submission.InputKind != JobInputKind.None || _pending.Any(entry => entry.NeedsCapture);
        }

        try
        {
            // Keep enough room beyond Core's persistent job record for its
            // temporary dispatch pin and our later invocation custody lease.
            // Capture additionally needs a pin while the custody lease is live.
            using var dispatchHeadroom = _ownership.TryTrack(this);
            using var captureHeadroom = needsCaptureHeadroom && dispatchHeadroom is not null
                ? _ownership.TryTrack(this) : null;
            if (dispatchHeadroom is null || needsCaptureHeadroom && captureHeadroom is null)
            {
                lock (_gate)
                    return Reject(_closed ? JobAdmissionStatus.Closed : JobAdmissionStatus.CapacityExceeded);
            }

            var admittedAt = TimeProvider.System.GetTimestamp();
            var result = _authority.TryAdmit(id, submission, phase);
            if (result is not { Status: JobAdmissionStatus.Accepted, Handle: { } handle })
            {
                lock (_gate)
                    return Reject(result.Status);
            }

            var entry = new Entry(handle, submission.Source, submission.ResultKind, submission.InputKind != JobInputKind.None,
                _handlers[submission.Handler], admittedAt);
            lock (_gate)
            {
                if (!_closed)
                {
                    _pending.Add(entry);
                    Increment(ref _accepted);
                    return new JobAdmissionResult(JobAdmissionStatus.Accepted, entry);
                }
            }

            entry.CancelOwned();
            entry.Finish(entry.ObserveOwned().Execution, null);
            entry.Dispose();
            lock (_gate)
                return Reject(JobAdmissionStatus.Closed);
        }
        finally
        {
            lock (_gate)
            {
                _admitting = false;
                CheckQuiesced();
            }
        }
    }

    public ValueTask DispatchReadyAsync(JobDispatchPhase phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(phase);
        cancellationToken.ThrowIfCancellationRequested();
        var start = TimeProvider.System.GetTimestamp();
        Entry[] scan;
        lock (_gate)
        {
            if (_closed)
                return ValueTask.CompletedTask;
            if (phase.Stamp.World != World)
                throw new ArgumentException("The dispatch phase belongs to another world.", nameof(phase));
            if (_dispatching)
                throw new InvalidOperationException("The phase coordinator already has a dispatch scan in progress.");
            if (_admitting)
                return ValueTask.CompletedTask;

            scan = [.. _pending.OrderBy(entry => entry.Source.Tick)
                .ThenBy(entry => entry.Source.Phase).ThenBy(entry => entry.Source.System)
                .ThenBy(entry => entry.Source.Partition).ThenBy(entry => entry.Source.Ordinal)];
            _dispatching = true;
        }

        try
        {
            foreach (var entry in scan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Visit(entry, phase, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            var elapsed = Elapsed(start);
            lock (_gate)
            {
                Add(ref _dispatchTicks, elapsed);
                _dispatching = false;
                CheckQuiesced();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Stops this dispatcher and waits until its owned work reaches quiescence.</summary>
    public async ValueTask StopAsync(RuntimeDeadline deadline, CancellationToken cancellationToken = default)
    {
        // A foreign or invalid deadline must not change scheduler state.
        _ = deadline.GetRemainingForSettlement(_clock);
        RequestStop();
        while (!_quiesced.Task.IsCompleted)
        {
            var remaining = deadline.GetRemainingForSettlement(_clock);
            if (remaining == TimeSpan.Zero)
            {
                if (_quiesced.Task.IsCompleted)
                    return;
                throw new IncompleteCleanupException(FaultSubsystem.Jobs);
            }

            var wait = TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 1,
                TimeSpan.FromDays(1).TotalMilliseconds));
            try
            {
                await _quiesced.Task.WaitAsync(wait, TimeProvider.System, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                // Recheck the cached deadline after the trusted timer fires.
            }
        }
    }

    public JobSchedulerMetrics ReadMetrics()
    {
        var frequency = TimeProvider.System.TimestampFrequency;
        lock (_gate)
            return new JobSchedulerMetrics(_accepted, _rejected, _pending.Count,
                _running.Values.Count(work => work.Permit is not null), _started,
                _succeeded, _cancelled, _faulted, _queueTicks, _workerTicks, _cancellationTicks,
                _dispatchTicks, frequency);
    }

    private void RequestStop()
    {
        Entry[] queued;
        lock (_gate)
        {
            if (_closed)
                return;

            _closed = true;
            queued = [.. _pending.Where(entry => !_running.ContainsKey(entry.Id))];
            foreach (var entry in queued)
                _pending.Remove(entry);
            foreach (var work in _running.Values)
                work.Stop.TrySetResult();
            _stoppingQueued = queued.Length;
            CheckQuiesced();
        }

        if (queued.Length != 0)
            _ = Task.Run(() => CancelQueued(queued));
    }

    private void CancelQueued(Entry[] queued)
    {
        foreach (var entry in queued)
        {
            try
            {
                FinishQueued(entry);
            }
            finally
            {
                lock (_gate)
                {
                    _stoppingQueued--;
                    CheckQuiesced();
                }
            }
        }
    }

    private void Visit(Entry entry, JobDispatchPhase phase, CancellationToken cancellationToken)
    {
        Work work;
        lock (_gate)
        {
            if (_closed || !_pending.Contains(entry))
                return;

            // Hold the entry in the pending queue while reserving the attempt so stop cannot release it.
            work = new Work(entry);
            _running.Add(entry.Id, work);
        }

        var launched = false;
        var removePending = false;
        RuntimeFailure? failure = null;
        try
        {
            var observation = entry.ObserveOwned();
            if (observation.Execution is not (JobExecutionStatus.Waiting or JobExecutionStatus.Ready))
            {
                removePending = true;
                return;
            }
            if (observation.Execution != JobExecutionStatus.Ready || entry.Source.Phase != phase.Phase)
                return;
            lock (_gate)
            {
                // The current probe occupies one dictionary slot but does not represent a worker yet.
                if (_closed || _running.Count > _limits.Workers)
                    return;
            }

            if (entry.NeedsCapture)
            {
                // Capture retains its pin beside the invocation lease. Reserve custody
                // first so Core tests the remaining pin capacity before issuing a permit.
                work.Retention = _ownership.TryTrack(work);
                if (work.Retention is null)
                    return;
            }

            var dispatch = _authority.TryAuthorizeDispatch(entry.Id, phase);
            if (dispatch is not { Status: JobDispatchStatus.Authorized, Permit: { } permit })
            {
                if (dispatch.Status == JobDispatchStatus.InvalidAccess)
                {
                    failure = FailWithoutPermit(entry, phase,
                        new InvalidOperationException("The job's admitted scope is no longer authorized."));
                    removePending = true;
                }
                else if (dispatch.Status is JobDispatchStatus.Closed or JobDispatchStatus.InvalidJob)
                {
                    removePending = true;
                }
                return;
            }

            lock (_gate)
                work.Permit = permit;
            removePending = true;
            // Jobs without input reuse Core's released policy-pin credit for custody.
            work.Retention ??= _ownership.TryTrack(work);
            if (work.Retention is null)
            {
                _authority.TrySettle(new JobExecutionCompletion(permit, JobExecutionStatus.Cancelled, [], []));
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                work.Stop.TrySetResult();
            Capture(work);
            if (cancellationToken.IsCancellationRequested)
                work.Stop.TrySetResult();
            var queuedFor = Elapsed(entry.AdmittedAt);
            lock (_gate)
            {
                _pending.Remove(entry);
                Add(ref _queueTicks, queuedFor);
            }

            // An authorized permit needs settlement even if the phase caller cancels now.
            work.Invocation = Task.Run(() => RunAsync(work), CancellationToken.None);
            launched = true;
        }
        catch (Exception exception) when (work.Permit is null)
        {
            failure = FailWithoutPermit(entry, phase, exception);
            removePending = true;
        }
        finally
        {
            if (!launched)
                EndProbe(work, removePending, failure);
        }
    }

    private void EndProbe(Work work, bool removePending, RuntimeFailure? failure)
    {
        bool finish;
        lock (_gate)
        {
            finish = _closed || removePending;
            if (finish)
                _pending.Remove(work.Entry);
            else
                _running.Remove(work.Entry.Id);
        }

        try
        {
            if (finish)
                FinishQueued(work.Entry, failure);
        }
        finally
        {
            work.Retention?.Dispose();
            lock (_gate)
            {
                if (finish)
                    _running.Remove(work.Entry.Id);
                CheckQuiesced();
            }
        }
    }

    private void FinishQueued(Entry entry, RuntimeFailure? failure = null)
    {
        entry.CancelOwned();
        var status = entry.ObserveOwned().Execution;
        entry.Finish(status, failure);
        lock (_gate)
            CountTerminal(status);
    }

    private JobAdmissionResult Reject(JobAdmissionStatus status)
    {
        Increment(ref _rejected);
        return new JobAdmissionResult(status, null);
    }

    private void CheckQuiesced()
    {
        if (_closed && !_admitting && !_dispatching && _pending.Count == 0 &&
            _running.Count == 0 && _stoppingQueued == 0)
            _quiesced.TrySetResult();
    }

    private void CountTerminal(JobExecutionStatus status)
    {
        switch (status)
        {
            case JobExecutionStatus.Succeeded:
                Increment(ref _succeeded);
                break;
            case JobExecutionStatus.Faulted:
                Increment(ref _faulted);
                break;
            case JobExecutionStatus.Cancelled or JobExecutionStatus.DependencyFailed:
                Increment(ref _cancelled);
                break;
        }
    }

    private static long Elapsed(long start) =>
        (long)Int128.Clamp((Int128)TimeProvider.System.GetTimestamp() - start, 0, long.MaxValue);
    private static void Increment(ref long count) => Add(ref count, 1);
    private static void Add(ref long count, long amount) => count += Math.Min(long.MaxValue - count, amount);
}
