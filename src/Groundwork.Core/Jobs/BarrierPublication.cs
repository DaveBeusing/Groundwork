using System.Collections.Immutable;

namespace Groundwork.Core;

internal sealed partial class WorldPublicationAuthority
{
    private List<BatchRecord>? _selected;

    private sealed class BorrowDrain(BarrierRequest request, RuntimeDeadline deadline)
    {
        internal BarrierRequest Request { get; } = request;
        internal RuntimeDeadline Deadline { get; } = deadline;
        internal TaskCompletionSource<BorrowDrainStatus> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal async ValueTask<BorrowDrainStatus> CloseAndDrainAsync(BarrierRequest request, RuntimeDeadline deadline)
    {
        BorrowDrain drain;
        lock (_control.Gate)
        {
            if (_control.IsTerminal)
                return BorrowDrainStatus.Stopped;
            if (_phase is not null || !IsNextBarrier(request) ||
                _drain is { } current && current.Request != request)
                return BorrowDrainStatus.InvalidBarrier;

            if (_drain is null)
            {
                // Validate the caller's clock before changing phase state.
                _ = deadline.GetRemainingForSettlement(_clock);
                _drain = new BorrowDrain(request, deadline);
            }

            drain = _drain;
            if (_borrowed == 0)
                drain.Completion.TrySetResult(BorrowDrainStatus.Drained);
            else if (drain.Deadline.IsExpiredForSettlement())
                TimeOutBorrowDrain(drain);
        }

        while (!drain.Completion.Task.IsCompleted)
        {
            // The watchdog bounds the wait even if the injected clock cannot be sampled.
            var remaining = drain.Deadline.GetRemainingForSettlement(_clock);
            var wait = TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 1, TimeSpan.FromDays(1).TotalMilliseconds));
            try
            {
                return await drain.Completion.Task.WaitAsync(wait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (_control.Gate)
                {
                    if (_control.IsTerminal)
                        drain.Completion.TrySetResult(BorrowDrainStatus.Stopped);
                    else if (drain.Deadline.IsExpiredForSettlement())
                        TimeOutBorrowDrain(drain);
                }
            }
        }

        return await drain.Completion.Task.ConfigureAwait(false);
    }

    private void ReleaseBorrowPin()
    {
        _borrowed--;
        if (_borrowed != 0 || _drain is not { } drain || drain.Completion.Task.IsCompleted)
            return;

        if (_control.IsTerminal)
            drain.Completion.TrySetResult(BorrowDrainStatus.Stopped);
        else if (drain.Deadline.IsExpiredForSettlement())
            TimeOutBorrowDrain(drain);
        else
            drain.Completion.TrySetResult(BorrowDrainStatus.Drained);
    }

    private void TimeOutBorrowDrain(BorrowDrain drain)
    {
        if (drain.Completion.TrySetResult(BorrowDrainStatus.TimedOut))
            _control.StopWorld();
    }

    private bool IsNextBarrier(BarrierRequest request)
    {
        if (request.Entry != _stamp || request.Tick == 0)
            return false;

        return request.Kind switch
        {
            BarrierKind.BeginTick => _stamp.Tick != ulong.MaxValue && request.Tick == _stamp.Tick + 1 &&
                (_stamp.Kind == 0 || _stamp.Kind == BarrierKind.CommitResults && _currentFrame?.Stamp == _stamp),
            BarrierKind.CommitResults => _stamp.Kind == BarrierKind.BeginTick && request.Tick == _stamp.Tick,
            _ => false,
        };
    }

    internal CandidateSealResult SealCandidates(in BarrierRequest request)
    {
        lock (_control.Gate)
        {
            if (_control.IsTerminal)
                return new(CandidateSealStatus.Stopped, null);
            if (_candidates is not null || _borrowed != 0 || _phase is not null)
                return new(CandidateSealStatus.BusyBarrier, null);
            if (_drain is not { } drain || drain.Request != request || !IsNextBarrier(request) ||
                !drain.Completion.Task.IsCompletedSuccessfully || drain.Completion.Task.Result != BorrowDrainStatus.Drained)
                return new(CandidateSealStatus.InvalidBarrier, null);
            if (_stamp.Epoch == ulong.MaxValue)
            {
                _control.StopWorld();
                return new(CandidateSealStatus.Stopped, null);
            }

            bool takeDeferred = request.Kind == BarrierKind.BeginTick;
            var selected = _jobs.Values.SelectMany(job => job.Batches)
                .Where(batch => batch.Receipt is null && batch.Deferred == takeDeferred)
                .OrderBy(batch => batch.Id.Source.Tick).ThenBy(batch => batch.Id.Source.Phase)
                .ThenBy(batch => batch.Id.Source.System).ThenBy(batch => batch.Id.Source.Partition)
                .ThenBy(batch => batch.Id.Source.Ordinal).ThenBy(batch => batch.Id.Sequence)
                .ToList();
            var snapshot = ImmutableArray.CreateBuilder<CandidateBatch>(selected.Count);
            foreach (var batch in selected)
            {
                var generation = _jobs[batch.Permit.Job].CancellationRequested ? 1UL : 0UL;
                snapshot.Add(new CandidateBatch(batch.Permit, batch.Id, generation, batch.Deferred, batch.Commands));
            }

            var candidates = new CandidateSet(this, request, snapshot.MoveToImmutable());
            foreach (var batch in selected)
                batch.Selected = true;

            _selected = selected;
            _candidates = candidates;
            return new(CandidateSealStatus.Sealed, candidates);
        }
    }

    internal BarrierPreparationResult TryPrepare(BarrierPublicationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var world = draft.NextRoot?.World;
        lock (_control.Gate)
        {
            if (_control.IsTerminal)
                return new(BarrierPreparationStatus.Stopped, null);
            if (_prepared is not null || _candidates is null || !ReferenceEquals(_candidates, draft.Candidates) ||
                draft.NextRoot is null || world != _stamp.World || draft.Dispositions.IsDefault ||
                draft.Dispositions.Length != _selected!.Count)
                return new(BarrierPreparationStatus.InvalidCandidate, null);

            // Validate and canonicalize by candidate order before the final publication transition.
            var supplied = new Dictionary<BatchId, BatchDisposition>(draft.Dispositions.Length);
            foreach (var disposition in draft.Dispositions)
            {
                if (!supplied.TryAdd(disposition.Batch, disposition))
                    return new(BarrierPreparationStatus.InvalidCandidate, null);
            }

            bool changed = !ReferenceEquals(_root, draft.NextRoot);
            bool commits = false;
            int deferred = _deferred;
            var ordered = ImmutableArray.CreateBuilder<BatchDisposition>(_selected.Count);
            foreach (var batch in _selected)
            {
                if (!supplied.TryGetValue(batch.Id, out var disposition) ||
                    !CanApplyDisposition(_candidates.Request.Kind, batch, disposition))
                    return new(BarrierPreparationStatus.InvalidCandidate, null);

                if (batch.Deferred)
                    deferred--;
                if (disposition.Kind == BatchDispositionKind.Defer)
                {
                    if (deferred == _quota.DeferredBatches)
                        return new(BarrierPreparationStatus.CapacityExceeded, null);
                    deferred++;
                }

                commits |= disposition.Kind == BatchDispositionKind.Commit;
                ordered.Add(disposition);
            }

            if (changed && !commits)
                return new(BarrierPreparationStatus.InvalidCandidate, null);

            _prepared = new PreparedBarrierPublication(draft with { Dispositions = ordered.MoveToImmutable() }, changed);
            return new(BarrierPreparationStatus.Prepared, _prepared);
        }
    }

    private static bool CanApplyDisposition(BarrierKind barrier, BatchRecord batch, BatchDisposition disposition)
        => disposition.Kind switch
        {
            BatchDispositionKind.Commit => disposition.Rejection == BatchRejection.None &&
                (barrier == BarrierKind.BeginTick || !batch.Structural),
            BatchDispositionKind.Defer => disposition.Rejection == BatchRejection.None &&
                barrier == BarrierKind.CommitResults && batch.Structural,
            BatchDispositionKind.Reject => disposition.Rejection != BatchRejection.None &&
                Enum.IsDefined(disposition.Rejection),
            _ => false,
        };

    internal BarrierOutcome TryFinalize(PreparedBarrierPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        lock (_control.Gate)
        {
            if (!ReferenceEquals(_prepared, publication) || _selected is null)
                return new(BarrierStatus.Stopped, null);
            if (_control.IsTerminal)
            {
                _control.StopWorld();
                return new(BarrierStatus.Stopped, null);
            }

            bool rejectOnly = false;
            for (int index = 0; index < _selected.Count; index++)
            {
                var batch = _selected[index];
                var generation = _jobs[batch.Permit.Job].CancellationRequested ? 1UL : 0UL;
                rejectOnly |= generation != publication.Draft.Candidates.Batches[index].CancellationGeneration;
            }

            bool changesWorld = publication.ChangesWorld && !rejectOnly;
            if (changesWorld && _stamp.Revision == ulong.MaxValue ||
                !_control.TryAuthorizePublication(publication.Draft.ExpectedSafetyGeneration))
            {
                _control.StopWorld();
                return new(BarrierStatus.Stopped, null);
            }

            // No extension code is invoked after authorization. All outcome storage was reserved at admission.
            var request = publication.Draft.Candidates.Request;
            var stamp = new BarrierStamp(_stamp.World, request.Tick, request.Kind, _stamp.Epoch + 1,
                changesWorld ? _stamp.Revision + 1 : _stamp.Revision);
            if (changesWorld)
                _root = publication.Draft.NextRoot;
            _stamp = stamp;
            for (int index = 0; index < _selected.Count; index++)
            {
                var batch = _selected[index];
                var disposition = publication.Draft.Dispositions[index];
                if (rejectOnly)
                {
                    RejectBatch(batch, _jobs[batch.Permit.Job].CancellationRequested
                        ? BatchRejection.Cancelled : BatchRejection.BarrierInvalidated, stamp);
                }
                else if (disposition.Kind == BatchDispositionKind.Defer)
                {
                    batch.Deferred = true;
                    _deferred++;
                }
                else if (disposition.Kind == BatchDispositionKind.Commit)
                {
                    SetBatchReceipt(batch, BatchReceiptStatus.Committed, BatchRejection.None, stamp);
                }
                else
                {
                    RejectBatch(batch, disposition.Rejection, stamp);
                }

                batch.Selected = false;
            }

            ClearBarrier();
            _lastPhase = 0;
            RefreshJobs();
            if (rejectOnly)
            {
                var phase = request.Kind == BarrierKind.BeginTick ? TickPhase.BeginTick : TickPhase.CommitResults;
                var context = new FaultContext(stamp.World, stamp.Tick, phase, null, null, null);
                var fault = new FaultObservation(FaultClass.Cancelled, FaultSeverity.Warning, FaultSubsystem.Jobs,
                    FaultOrigin.Primary, 0, 0, context, [], []);
                _control.RecordJobFault(in fault);
            }
            return new(changesWorld ? BarrierStatus.Committed : BarrierStatus.Unchanged, stamp);
        }
    }

    internal BarrierAbortStatus TryAbort(CandidateSet candidates, BarrierAbortReason reason)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_control.Gate)
        {
            if (!ReferenceEquals(candidates.Owner, this) || !Enum.IsDefined(reason))
                return BarrierAbortStatus.InvalidCandidate;
            if (!ReferenceEquals(_candidates, candidates))
                return BarrierAbortStatus.AlreadySettled;

            _control.StopWorld();
            return BarrierAbortStatus.Stopped;
        }
    }

    internal ObserverPublication TryPublish(in ObserverPublicationDraft draft)
    {
        if (draft.State is null || draft.State.World != draft.Source.World)
            return ObserverPublication.PreparationFailed;

        lock (_control.Gate)
        {
            if (_control.IsTerminal || _drain is not null || draft.Source != _stamp ||
                draft.Source.Kind != BarrierKind.CommitResults || _currentFrame?.Stamp == draft.Source ||
                !_control.TryAuthorizePublication(draft.ExpectedSafetyGeneration))
                return ObserverPublication.Blocked;

            _previousFrame = _currentFrame;
            _currentFrame = new ObserverFrame(draft.Source, draft.State);
            return ObserverPublication.Published;
        }
    }

    internal void OnStopped()
    {
        lock (_control.Gate)
        {
            _phase = null;
            _drain?.Completion.TrySetResult(BorrowDrainStatus.Stopped);
            foreach (var job in _jobs.Values)
            {
                if (job.Execution is JobExecutionStatus.Waiting or JobExecutionStatus.Ready)
                    job.Execution = JobExecutionStatus.Cancelled;
                if (job.Execution == JobExecutionStatus.Running)
                {
                    job.CancellationRequested = true;
                    job.Permit?.SignalCancellation();
                }
                if (job.Commit == JobCommitStatus.Pending)
                    job.Commit = JobCommitStatus.Rejected;
                foreach (var batch in job.Batches)
                {
                    batch.Selected = false;
                    batch.Receipt ??= new BatchReceipt(batch.Id, _stamp, BatchReceiptStatus.Rejected, BatchRejection.Stopped);
                }
                job.UnsettledBatches = 0;
            }

            ClearBarrier();
            RefreshJobs();
        }
    }

    internal void OnReleased()
    {
        lock (_control.Gate)
        {
            ClearBarrier();
            _jobs.Clear();
            _admissionOrder.Clear();
            _active.Clear();
            _root = null;
            _previousFrame = null;
            _currentFrame = null;
        }
    }

    private void ClearBarrier()
    {
        _drain = null;
        _selected = null;
        _candidates = null;
        _prepared = null;
    }

    private void RejectBatch(BatchRecord batch, BatchRejection reason, BarrierStamp stamp)
        => SetBatchReceipt(batch, BatchReceiptStatus.Rejected, reason, stamp);

    private void SetBatchReceipt(BatchRecord batch, BatchReceiptStatus status, BatchRejection reason, BarrierStamp stamp)
    {
        if (batch.Receipt is not null)
            return;

        if (batch.Deferred)
        {
            batch.Deferred = false;
            _deferred--;
        }

        batch.Receipt = new BatchReceipt(batch.Id, stamp, status, reason);
        var job = _jobs[batch.Permit.Job];
        job.UnsettledBatches--;
        if (status == BatchReceiptStatus.Rejected)
            job.Commit = JobCommitStatus.Rejected;
        else if (job.Commit == JobCommitStatus.Pending && job.UnsettledBatches == 0)
            job.Commit = JobCommitStatus.Committed;
    }
}
