using System.Collections.Immutable;

namespace Groundwork.Core;

internal sealed partial class WorldPublicationAuthority
{
    private bool _admitting;

    internal JobAdmissionResult TryAdmit(JobId id, JobSubmission submission, JobDispatchPhase phase)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!TryGetReservation(submission, out var descriptorBytes, out var resultBytes))
            return new(JobAdmissionStatus.InvalidDescriptor, null);

        CommittedWorldView world;
        IRuntimeWorkLease retention;
        var owner = new JobOwner();
        lock (_control.Gate)
        {
            var admission = CheckAdmission(id, submission, phase, descriptorBytes, resultBytes);
            if (admission != JobAdmissionStatus.Accepted)
                return new(admission, null);
            if (_admitting || _borrowed >= _quota.BorrowedLeases || _ownership.TryTrack(owner) is not { } tracked)
                return new(JobAdmissionStatus.CapacityExceeded, null);

            _admitting = true;
            _borrowed++;
            retention = tracked;
            world = new CommittedWorldView(_stamp, CurrentRoot);
            owner.Root = world.Root;
        }

        bool accepted = false;
        try
        {
            // ImmutableArray is a transport type. Make the authority's copies independently owned.
            var owned = CopySubmission(submission);
            owner.Submission = owned;
            if (!IsDescriptorValid(owned, id.World))
                return new(JobAdmissionStatus.InvalidDescriptor, null);

            var scope = _policy.ValidateAdmission(owned, in world);
            if (scope.Status != JobScopeStatus.Authorized || scope.Scope is null)
                return new(scope.Status == JobScopeStatus.CapacityExceeded
                    ? JobAdmissionStatus.CapacityExceeded : JobAdmissionStatus.InvalidAccess, null);

            lock (_control.Gate)
            {
                var admission = CheckAdmission(id, owned, phase, descriptorBytes, resultBytes);
                if (admission != JobAdmissionStatus.Accepted)
                    return new(admission, null);
                if (_stamp != world.Stamp)
                    return new(JobAdmissionStatus.InvalidAccess, null);

                var job = new JobRecord(id, owned, scope.Scope, retention, descriptorBytes, resultBytes);
                owner.Record = job;
                var handle = new JobHandle(this, job);
                _jobs.Add(id, job);
                _admissionOrder.AddLast(job.AdmissionNode);
                _lastJobId = id.Value;
                _lastSourceOrdinal = owned.Source.Ordinal;
                _descriptorBytes += descriptorBytes;
                _resultBytes += resultBytes;
                _snapshotBytes += owned.Budget.MaxSnapshotBytes;
                _dependencyEdges += owned.Dependencies.Length;
                _receiptRecords += owned.Budget.MaxBatches;
                _reservedDeferredBatches += DeferredReservation(owned);
                _retentionHandles++;
                foreach (var prerequisite in owned.Dependencies)
                    _jobs[prerequisite.Prerequisite].Consumers++;

                RefreshJobs();
                accepted = true;
                return new(JobAdmissionStatus.Accepted, handle);
            }
        }
        finally
        {
            lock (_control.Gate)
            {
                _admitting = false;
                owner.Root = null;
                ReleaseBorrowPin();
                if (!accepted)
                    retention.Dispose();
            }
        }
    }

    private JobAdmissionStatus CheckAdmission(JobId id, JobSubmission submission, JobDispatchPhase phase,
        long descriptorBytes, long resultBytes)
    {
        if (_control.IsTerminal || _drain is not null || !ReferenceEquals(_phase, phase) || phase is null)
            return JobAdmissionStatus.Closed;
        if (id.World != _stamp.World || id.Value <= _lastJobId ||
            submission.Source.World != id.World || submission.Source.Tick != phase.Stamp.Tick ||
            submission.Source.Phase != phase.Phase || submission.Source.Ordinal <= _lastSourceOrdinal)
            return JobAdmissionStatus.InvalidDescriptor;

        if (_jobs.Count >= _quota.Jobs || _retentionHandles >= _quota.RetentionHandles ||
            submission.Dependencies.Length > _quota.DependencyEdges - _dependencyEdges ||
            submission.Budget.MaxBatches > _quota.ReceiptRecords - _receiptRecords ||
            DeferredReservation(submission) > _quota.DeferredBatches - _reservedDeferredBatches ||
            descriptorBytes > _quota.DescriptorBytes - _descriptorBytes ||
            resultBytes > _quota.ResultBytes - _resultBytes ||
            submission.Budget.MaxSnapshotBytes > _quota.SnapshotBytes - _snapshotBytes)
            return JobAdmissionStatus.CapacityExceeded;

        foreach (var dependency in submission.Dependencies)
        {
            if (!_jobs.TryGetValue(dependency.Prerequisite, out var prerequisite) ||
                dependency.Prerequisite.World != id.World ||
                dependency.Kind == DependencyKind.Execution && prerequisite.Submission.ResultKind != JobResultKind.Commandless ||
                dependency.Kind == DependencyKind.Commit && prerequisite.Submission.ResultKind != JobResultKind.Commands)
                return JobAdmissionStatus.InvalidPrerequisite;
        }

        return JobAdmissionStatus.Accepted;
    }

    private static int DeferredReservation(JobSubmission submission) =>
        submission.ResultKind == JobResultKind.Commands ? submission.Budget.MaxBatches : 0;

    internal JobDispatchResult TryAuthorizeDispatch(JobId id, JobDispatchPhase phase)
    {
        CommittedWorldView world;
        JobRecord job;
        IAdmittedJobScope[] activeScopes;
        ulong generation;
        IRuntimeWorkLease retention;
        lock (_control.Gate)
        {
            var status = CheckDispatch(id, phase, out var existing);
            if (status != JobDispatchStatus.Authorized || existing is null)
                return new(status, null);
            job = existing;
            if (_borrowed >= _quota.BorrowedLeases)
                return new(JobDispatchStatus.NotReady, null);

            world = new CommittedWorldView(_stamp, CurrentRoot);
            if (_ownership.TryTrack(world.Root) is not { } tracked)
                return new(JobDispatchStatus.NotReady, null);

            retention = tracked;
            activeScopes = _active.Select(active => _jobs[active].Scope).ToArray();
            generation = _activeGeneration;
            _borrowed++;
            job.PolicyPins++;
        }

        try
        {
            if (!_policy.CanDispatch(job.Scope, in world))
                return new(JobDispatchStatus.InvalidAccess, null);
            foreach (var active in activeScopes)
            {
                if (_policy.EvaluateOverlap(job.Scope, active, in world) != JobScopeOverlap.Independent)
                    return new(JobDispatchStatus.NotReady, null);
            }

            lock (_control.Gate)
            {
                var status = CheckDispatch(id, phase, out _);
                if (status != JobDispatchStatus.Authorized)
                    return new(status, null);
                if (world.Stamp != _stamp || generation != _activeGeneration)
                    return new(JobDispatchStatus.NotReady, null);
                if (!AdvanceActiveGeneration())
                    return new(JobDispatchStatus.Closed, null);

                var permit = new JobDispatchPermit(id, job.Submission, phase, job.Scope,
                    new JobArgumentReader(this, id), new DependencyOutputReader(this, id), _control.RuntimeStopRequested);
                _active.Add(id);
                job.Permit = permit;
                job.Execution = JobExecutionStatus.Running;
                return new(JobDispatchStatus.Authorized, permit);
            }
        }
        finally
        {
            lock (_control.Gate)
            {
                job.PolicyPins--;
                ReleaseBorrowPin();
                TryReleaseJob(job);
                retention.Dispose();
            }
        }
    }

    private JobDispatchStatus CheckDispatch(JobId id, JobDispatchPhase phase, out JobRecord? job)
    {
        job = null;
        if (_control.IsTerminal)
            return JobDispatchStatus.Closed;
        if (_drain is not null || !ReferenceEquals(_phase, phase) || phase is null || phase.Stamp != _stamp)
            return JobDispatchStatus.InvalidBarrier;
        if (!_jobs.TryGetValue(id, out job))
            return JobDispatchStatus.InvalidJob;
        if (job.Submission.Source.Phase != phase.Phase || job.Execution != JobExecutionStatus.Ready ||
            _active.Count >= _quota.Workers)
            return JobDispatchStatus.NotReady;
        return JobDispatchStatus.Authorized;
    }

    private bool AdvanceActiveGeneration()
    {
        if (_activeGeneration == ulong.MaxValue)
        {
            _control.StopWorld();
            return false;
        }

        _activeGeneration++;
        return true;
    }

    internal JobCancellationStatus RequestCancellation(JobId id)
    {
        lock (_control.Gate)
            return _jobs.TryGetValue(id, out var job) ? Cancel(job) : JobCancellationStatus.Released;
    }

    private JobCancellationStatus Cancel(JobRecord job)
    {
        if (job.CancellationRequested)
            return JobCancellationStatus.AlreadyRequested;
        if (IsExecutionTerminal(job.Execution) && job.Commit != JobCommitStatus.Pending)
            return JobCancellationStatus.Settled;

        job.CancellationRequested = true;
        if (job.Execution is JobExecutionStatus.Waiting or JobExecutionStatus.Ready)
            job.Execution = JobExecutionStatus.Cancelled;
        if (job.Commit == JobCommitStatus.Pending)
        {
            job.Commit = JobCommitStatus.Rejected;
            foreach (var batch in job.Batches)
            {
                if (!batch.Selected)
                    RejectBatch(batch, BatchRejection.Cancelled, _stamp);
            }
        }

        RefreshJobs();
        job.Permit?.SignalCancellation();
        return JobCancellationStatus.Requested;
    }

    internal ExecutionSettlementStatus TrySettle(JobExecutionCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        JobRecord job;
        lock (_control.Gate)
        {
            if (!TryFindPermit(completion.Permit, out var current))
                return ExecutionSettlementStatus.InvalidCompletion;
            job = current;
            if (job.Execution != JobExecutionStatus.Running)
                return ExecutionSettlementStatus.AlreadySettled;
            if (completion.Status is not (JobExecutionStatus.Succeeded or JobExecutionStatus.Cancelled))
                return ExecutionSettlementStatus.InvalidCompletion;
            if (job.CaptureOpen || job.CopyingCompletion)
                return ExecutionSettlementStatus.InvalidCompletion;

            bool stopped = _control.IsTerminal;
            if (stopped || job.CancellationRequested || completion.Status == JobExecutionStatus.Cancelled)
            {
                SettleExecution(job, JobExecutionStatus.Cancelled);
                return stopped ? ExecutionSettlementStatus.Stopped : ExecutionSettlementStatus.Cancelled;
            }

            if (completion.Permit.InputKind != JobInputKind.None && !job.CaptureConsumed)
                return ExecutionSettlementStatus.InvalidCompletion;
            job.CopyingCompletion = true;
        }

        try
        {
            var valid = TryCopyCompletion(job, completion, out var batches, out var output);
            lock (_control.Gate)
            {
                var stopped = _control.IsTerminal;
                if (stopped || job.CancellationRequested)
                {
                    SettleExecution(job, JobExecutionStatus.Cancelled);
                    return stopped ? ExecutionSettlementStatus.Stopped : ExecutionSettlementStatus.Cancelled;
                }

                if (!valid)
                    return ExecutionSettlementStatus.InvalidCompletion;

                job.Batches = batches;
                job.UnsettledBatches = batches.Count;
                job.Output = output;
                SettleExecution(job, JobExecutionStatus.Succeeded);
                return ExecutionSettlementStatus.Settled;
            }
        }
        finally
        {
            lock (_control.Gate)
            {
                job.CopyingCompletion = false;
                TryReleaseJob(job);
            }
        }
    }

    internal ExecutionSettlementStatus TrySettleFault(JobDispatchPermit permit, scoped in FaultObservation fault)
    {
        lock (_control.Gate)
        {
            if (!TryFindPermit(permit, out var job))
                return ExecutionSettlementStatus.InvalidCompletion;
            if (job.Execution != JobExecutionStatus.Running)
                return ExecutionSettlementStatus.AlreadySettled;
            if (job.CaptureOpen || job.CopyingCompletion)
                return ExecutionSettlementStatus.InvalidCompletion;

            // Evidence is registered before the state becomes visible under this same gate.
            bool valid = fault.Subsystem == FaultSubsystem.Jobs && fault.Origin == FaultOrigin.Primary &&
                fault.Classification is not (FaultClass.Unspecified or FaultClass.DiagnosticOnly) &&
                Enum.IsDefined(fault.Classification) && fault.Context.World == job.Id.World &&
                (fault.Context.Job is null || fault.Context.Job == job.Id) &&
                (fault.Context.Source is null || fault.Context.Source == permit.Source) &&
                (fault.Context.Tick is null || fault.Context.Tick == permit.Source.Tick ||
                    fault.Context.Tick == permit.DispatchStamp.Tick) &&
                (fault.Context.Phase is null || fault.Context.Phase == permit.DispatchPhase);
            var context = new FaultContext(job.Id.World, permit.Source.Tick, permit.Source.Phase,
                job.Id, permit.Source, fault.Context.Correlation);
            var evidence = valid
                ? new FaultObservation(fault.Classification, fault.Severity, FaultSubsystem.Jobs, FaultOrigin.Primary,
                    fault.Code, fault.Timestamp, context, fault.Targets, fault.RedactedText)
                : new FaultObservation(FaultClass.UnknownCritical, FaultSeverity.Critical, FaultSubsystem.Jobs,
                    FaultOrigin.Primary, 0, fault.Timestamp, context, [], []);
            _control.RecordJobFault(in evidence);
            SettleExecution(job, JobExecutionStatus.Faulted);
            return ExecutionSettlementStatus.Settled;
        }
    }

    private void SettleExecution(JobRecord job, JobExecutionStatus status)
    {
        _active.Remove(job.Id);
        _ = AdvanceActiveGeneration();
        job.Execution = status;
        job.Permit?.SignalSettlement();
        job.Commit = status == JobExecutionStatus.Succeeded
            ? (job.Batches.Count == 0 ? JobCommitStatus.NotApplicable : JobCommitStatus.Pending)
            : (job.Submission.ResultKind == JobResultKind.Commandless ? JobCommitStatus.NotApplicable : JobCommitStatus.Rejected);
        RefreshJobs();
    }

    private static bool IsExecutionTerminal(JobExecutionStatus execution)
        => execution is JobExecutionStatus.Succeeded or JobExecutionStatus.Cancelled or
            JobExecutionStatus.Faulted or JobExecutionStatus.DependencyFailed;

    private void UpdateReadiness(JobRecord job)
    {
        if (job.Execution is not (JobExecutionStatus.Waiting or JobExecutionStatus.Ready))
            return;

        bool waiting = false;
        foreach (var dependency in job.Submission.Dependencies)
        {
            var prerequisite = _jobs[dependency.Prerequisite];
            bool satisfied = dependency.Kind == DependencyKind.Execution
                ? prerequisite.Execution == JobExecutionStatus.Succeeded
                : prerequisite.Commit == JobCommitStatus.Committed;
            if (satisfied)
                continue;

            bool failed = dependency.Kind == DependencyKind.Execution
                ? IsExecutionTerminal(prerequisite.Execution)
                : prerequisite.Commit != JobCommitStatus.Pending;
            if (failed)
            {
                job.Execution = JobExecutionStatus.DependencyFailed;
                if (job.Commit == JobCommitStatus.Pending)
                    job.Commit = JobCommitStatus.Rejected;
                return;
            }

            waiting = true;
        }

        job.Execution = waiting ? JobExecutionStatus.Waiting : JobExecutionStatus.Ready;
    }

    private void RefreshJobs()
    {
        // Admission IDs are strictly increasing. Walk prerequisites before their dependents.
        for (var node = _admissionOrder.First; node is not null;)
        {
            var job = node.Value;
            node = node.Next;
            UpdateReadiness(job);
            if (!IsExecutionTerminal(job.Execution) || job.Commit == JobCommitStatus.Pending)
                continue;
            if (!job.DependenciesReleased)
            {
                job.DependenciesReleased = true;
                foreach (var dependency in job.Submission.Dependencies)
                {
                    var prerequisite = _jobs[dependency.Prerequisite];
                    prerequisite.Consumers--;
                    TryReleaseJob(prerequisite);
                }
            }

            TryReleaseJob(job);
        }
    }

    private void TryReleaseJob(JobRecord job)
    {
        if (job.HandleOpen || !job.DependenciesReleased || job.Consumers != 0 || job.PolicyPins != 0 ||
            job.ReadPins != 0 || job.CopyingCompletion ||
            job.CaptureOpen || job.UnsettledBatches != 0 || !_jobs.Remove(job.Id))
            return;

        _admissionOrder.Remove(job.AdmissionNode);
        _descriptorBytes -= job.DescriptorBytes;
        _snapshotBytes -= job.Submission.Budget.MaxSnapshotBytes;
        _resultBytes -= job.ResultBytes;
        _dependencyEdges -= job.Submission.Dependencies.Length;
        _receiptRecords -= job.Submission.Budget.MaxBatches;
        _reservedDeferredBatches -= DeferredReservation(job.Submission);
        job.Retention.Dispose();
    }

    private void DisposeJobHandle(JobRecord job)
    {
        lock (_control.Gate)
        {
            if (!job.HandleOpen)
                return;
            job.HandleOpen = false;
            _retentionHandles--;
            TryReleaseJob(job);
        }
    }

    private static JobOutputRead CopyBytes(ImmutableArray<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
            return new(JobOutputReadStatus.BufferTooSmall, source.Length);
        source.AsSpan().CopyTo(destination);
        return new(JobOutputReadStatus.Copied, source.Length);
    }

    private void ReleaseReadPin(JobRecord job)
    {
        lock (_control.Gate)
        {
            job.ReadPins--;
            TryReleaseJob(job);
        }
    }

    private sealed class JobArgumentReader(WorldPublicationAuthority owner, JobId id) : IJobArguments
    {
        public JobOutputRead TryCopy(Span<byte> destination)
        {
            JobRecord ownerJob;
            lock (owner._control.Gate)
            {
                if (!owner._jobs.TryGetValue(id, out var job) || job.Execution != JobExecutionStatus.Running)
                    return new(JobOutputReadStatus.Expired, 0);
                ownerJob = job;
                ownerJob.ReadPins = checked(ownerJob.ReadPins + 1);
            }

            try
            {
                return CopyBytes(ownerJob.Submission.Arguments, destination);
            }
            finally
            {
                owner.ReleaseReadPin(ownerJob);
            }
        }
    }

    private sealed class DependencyOutputReader(WorldPublicationAuthority owner, JobId id) : IJobDependencyOutputs
    {
        public JobOutputRead TryCopy(JobId prerequisite, Span<byte> destination)
        {
            JobRecord producer;
            lock (owner._control.Gate)
            {
                if (!owner._jobs.TryGetValue(id, out var job) || job.Execution != JobExecutionStatus.Running)
                    return new(JobOutputReadStatus.Expired, 0);
                if (!job.Submission.Dependencies.Contains(new JobDependency(prerequisite, DependencyKind.Execution)))
                    return new(JobOutputReadStatus.Undeclared, 0);

                producer = owner._jobs[prerequisite];
                producer.ReadPins = checked(producer.ReadPins + 1);
            }

            try
            {
                return CopyBytes(producer.Output, destination);
            }
            finally
            {
                owner.ReleaseReadPin(producer);
            }
        }
    }

    private sealed class JobHandle(WorldPublicationAuthority owner, JobRecord job) : IJobHandle
    {
        private JobRecord? _job = job;

        public JobId Id { get; } = job.Id;

        public JobObservation Observe()
        {
            lock (owner._control.Gate)
            {
                var current = _job ?? throw new ObjectDisposedException(nameof(JobHandle));
                return new JobObservation(current.Execution, current.Commit);
            }
        }

        public JobCancellationStatus RequestCancellation()
        {
            lock (owner._control.Gate)
            {
                return _job is { } current ? owner.Cancel(current) : JobCancellationStatus.Released;
            }
        }

        public int CopyReceipts(Span<BatchReceipt> destination)
        {
            lock (owner._control.Gate)
            {
                var current = _job ?? throw new ObjectDisposedException(nameof(JobHandle));
                int copied = 0;
                foreach (var batch in current.Batches)
                {
                    if (copied == destination.Length || batch.Receipt is not { } receipt)
                        break;
                    destination[copied++] = receipt;
                }

                return copied;
            }
        }

        public void Dispose()
        {
            lock (owner._control.Gate)
            {
                if (_job is not { } current)
                    return;
                _job = null;
                owner.DisposeJobHandle(current);
            }
        }
    }

    private sealed class JobOwner
    {
        internal JobSubmission? Submission { get; set; }
        internal IWorldStateRoot? Root { get; set; }
        internal JobRecord? Record { get; set; }
    }

    private sealed class JobRecord(JobId id, JobSubmission submission, IAdmittedJobScope scope,
        IRuntimeWorkLease retention, long descriptorBytes, long resultBytes)
    {
        internal JobId Id { get; } = id;
        internal JobSubmission Submission { get; } = submission;
        internal IAdmittedJobScope Scope { get; } = scope;
        internal IRuntimeWorkLease Retention { get; } = retention;
        internal long DescriptorBytes { get; } = descriptorBytes;
        internal long ResultBytes { get; } = resultBytes;
        internal JobExecutionStatus Execution { get; set; } = JobExecutionStatus.Waiting;
        internal JobCommitStatus Commit { get; set; } = submission.ResultKind == JobResultKind.Commandless
            ? JobCommitStatus.NotApplicable : JobCommitStatus.Pending;
        internal JobDispatchPermit? Permit { get; set; }
        internal bool CaptureConsumed { get; set; }
        internal bool CaptureOpen { get; set; }
        internal bool CancellationRequested { get; set; }
        internal bool HandleOpen { get; set; } = true;
        internal bool DependenciesReleased { get; set; }
        internal int PolicyPins { get; set; }
        internal int ReadPins { get; set; }
        internal bool CopyingCompletion { get; set; }
        internal int Consumers { get; set; }
        internal int UnsettledBatches { get; set; }
        internal LinkedListNode<JobRecord> AdmissionNode => field ??= new(this);
        internal ImmutableArray<byte> Output { get; set; } = [];
        internal List<BatchRecord> Batches { get; set; } = [];
    }
}
