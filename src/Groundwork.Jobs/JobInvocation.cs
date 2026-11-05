using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Groundwork.Core;

namespace Groundwork.Jobs;

public readonly record struct JobSchedulerLimits(int QueuedJobs, int Workers);

public readonly record struct JobSchedulerMetrics(
    long Accepted, long Rejected, int Queued, int OccupiedWorkerSlots, long Started,
    long Succeeded, long Cancelled, long Faulted, long QueueWaitTicks, long WorkerBusyTicks,
    long CancellationWaitTicks, long DispatchWaitTicks, long TimestampFrequency);

public readonly record struct JobExecutionReport(JobExecutionStatus Status, RuntimeFailure? Failure);

public interface IJobExecutionHandle : IJobHandle
{
    Task<JobExecutionReport> ExecutionCompletion { get; }
}

public sealed partial class JobScheduler
{
    private void Capture(Work work)
    {
        var permit = work.Permit!;
        if (permit.InputKind == JobInputKind.None || IsCancellationRequested(work, permit))
            return;

        try
        {
            var request = new JobInputRequest(permit);
            var result = _inputs.TryCapture(in request);
            work.Input = result.Input;
            if (result.Status == JobInputCaptureStatus.Closed && work.Input is null && IsCancellationRequested(work, permit))
                return;
            if (result.Status == JobInputCaptureStatus.CapacityExceeded && work.Input is null)
            {
                // Core's policy pin does not transfer to capture. An unrelated
                // owner can take that credit after authorization.
                work.CaptureBackpressure = new RuntimeFailure(
                    CaptureFailure(FaultClass.Cancelled, Context(permit),
                        new InvalidOperationException("Job input capture exceeded the available capacity.")), []);
                work.Stop.TrySetResult();
                return;
            }
            if (result.Status != JobInputCaptureStatus.Captured || work.Input is null ||
                work.Input.CapturedAt != permit.DispatchStamp)
            {
                work.InitialClass = result.Status is JobInputCaptureStatus.InvalidAccess or JobInputCaptureStatus.InvalidPermit
                    ? FaultClass.InvalidAccess : FaultClass.RecoverableJobFault;
                work.InitialFailure = new InvalidOperationException($"Job input capture failed or returned the wrong stamp ({result.Status}).");
            }
        }
        catch (Exception exception)
        {
            work.InitialFailure = exception;
        }
    }

    private async Task RunAsync(Work work)
    {
        var permit = work.Permit!;
        var start = TimeProvider.System.GetTimestamp();
        var status = JobExecutionStatus.Faulted;
        RuntimeFailure? failure = null;
        bool release = false;
        lock (_gate)
            Increment(ref _started);

        try
        {
            var result = await ExecuteAsync(work, permit).ConfigureAwait(false);
            status = result.Status;
            failure = result.Failure;
            release = result.Release;
        }
        catch (Exception exception)
        {
            // An infrastructure exception leaves settlement unproved. Keep the ownership lease.
            var context = Context(permit);
            failure = new RuntimeFailure(CaptureFailure(FaultClass.IncompleteCleanup, context, exception), []);
            Report(FaultClass.IncompleteCleanup, FaultOrigin.Cleanup, context);
        }
        finally
        {
            var elapsed = Elapsed(start);
            var cancellationElapsed = work.CancellationObservedAt is { } cancellation ? Elapsed(cancellation) : 0;
            work.Entry.Finish(status, failure);
            if (release)
                work.Retention!.Dispose();
            lock (_gate)
            {
                if (release)
                    _running.Remove(work.Entry.Id);
                CountTerminal(status);
                Add(ref _workerTicks, elapsed);
                Add(ref _cancellationTicks, cancellationElapsed);
                CheckQuiesced();
            }
        }
    }

    private async Task<InvocationResult> ExecuteAsync(Work work, JobDispatchPermit permit)
    {
        using var cancellation = new CancellationTokenSource();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = WatchCancellationAsync(work, permit, cancellation, ended.Task);
        JobResults? results = null;
        var input = work.Input is { } captured ? new InvocationInput(captured) : null;
        var context = Context(permit);
        Exception? primary = work.InitialFailure;
        var classification = work.InitialClass;
        var cleanup = ImmutableArray.CreateBuilder<OperationalFailure>();
        bool inputReleased = true;
        bool cancelled = false;
        try
        {
            if (primary is null && !IsCancellationRequested(work, permit))
            {
                results = new JobResults(permit, work.Entry.Kind, work.Stop.Task);
                var execution = new JobExecutionContext(permit.Job, permit.DispatchStamp,
                    permit.Arguments, input, permit.DependencyOutputs, results);
                await work.Entry.Handler(execution, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (
            exception.CancellationToken == cancellation.Token && cancellation.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            primary = exception;
        }
        finally
        {
            results?.Close();
            ended.TrySetResult();
            foreach (var exception in await watch.ConfigureAwait(false))
                cleanup.Add(CaptureFailure(FaultClass.RecoverableJobFault, context, exception));

            // Cancellation callbacks may still use the invocation's read-only
            // input. Their completion is part of the borrowed ownership period.
            input?.Close();
            if (work.Input is { } owned)
            {
                try
                {
                    owned.Dispose();
                    work.Input = null;
                }
                catch (Exception exception)
                {
                    inputReleased = false;
                    cleanup.Add(CaptureFailure(FaultClass.IncompleteCleanup, context, exception));
                }
            }
        }

        cancelled |= IsCancellationRequested(work, permit);
        ExecutionSettlementStatus settlement;
        JobExecutionStatus status;
        if (primary is null && cleanup.Count == 0 && !cancelled)
        {
            var completion = results!.Seal(out var resultFailure);
            settlement = resultFailure is null ? _authority.TrySettle(completion) : ExecutionSettlementStatus.InvalidCompletion;
            if (settlement != ExecutionSettlementStatus.InvalidCompletion)
            {
                status = settlement == ExecutionSettlementStatus.Settled ? JobExecutionStatus.Succeeded : JobExecutionStatus.Cancelled;
                return new InvocationResult(status, null, true);
            }

            primary = new InvalidOperationException(resultFailure is { } rejection
                ? $"The job produced an invalid result ({rejection})." : "Core rejected the job's sealed result.");
            classification = FaultClass.InvalidAccess;
        }

        results?.Discard();
        if (primary is null && cleanup.Count == 0)
        {
            settlement = _authority.TrySettle(new JobExecutionCompletion(permit, JobExecutionStatus.Cancelled, [], []));
            if (settlement != ExecutionSettlementStatus.InvalidCompletion)
                return new InvocationResult(JobExecutionStatus.Cancelled, work.CaptureBackpressure, true);

            primary = new InvalidOperationException("Core could not acknowledge cancellation of the running job.");
            classification = FaultClass.IncompleteCleanup;
        }

        // Core records the cause of faulted execution as its atomic primary
        // observation. When cleanup alone failed, that observation already
        // represents the first cleanup error. The operational report below
        // continues to distinguish the original and cleanup exceptions.
        var fault = new FaultObservation(primary is null ? cleanup[0].Classification : classification,
            FaultSeverity.Error, FaultSubsystem.Jobs, FaultOrigin.Primary, primary is null ? 2U : 1U,
            TimeProvider.System.GetTimestamp(), context, [], []);
        settlement = _authority.TrySettleFault(permit, in fault);

        for (int index = primary is null ? 1 : 0; index < cleanup.Count; index++)
            Report(cleanup[index].Classification, FaultOrigin.Cleanup, context);

        var settled = settlement != ExecutionSettlementStatus.InvalidCompletion;
        if (!settled)
            Report(FaultClass.IncompleteCleanup, FaultOrigin.Cleanup, context);
        var failure = new RuntimeFailure(primary is null ? null : CaptureFailure(classification, context, primary),
            cleanup.ToImmutable());
        return new InvocationResult(JobExecutionStatus.Faulted, failure, settled && inputReleased);
    }

    private async Task<ImmutableArray<Exception>> WatchCancellationAsync(Work work, JobDispatchPermit permit,
        CancellationTokenSource cancellation, Task invocationEnded)
    {
        var signal = await Task.WhenAny(permit.CancellationRequested, work.Stop.Task, invocationEnded).ConfigureAwait(false);
        if (ReferenceEquals(signal, invocationEnded))
            return [];

        work.CancellationObservedAt = TimeProvider.System.GetTimestamp();
        var failures = ImmutableArray.CreateBuilder<Exception>();
        try
        {
            // The permit's own signal already follows Core's authoritative
            // intent. Only local scheduler stop needs to record it here.
            if (!permit.CancellationRequested.IsCompleted)
                _authority.RequestCancellation(permit.Job);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            // Token callbacks execute independently of the phase coordinator and scheduler locks.
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        return failures.ToImmutable();
    }

    private RuntimeFailure FailWithoutPermit(Entry entry, JobDispatchPhase phase, Exception exception)
    {
        var context = new FaultContext(World, entry.Source.Tick, phase.Phase, entry.Id, entry.Source, null);
        Report(FaultClass.InvalidAccess, FaultOrigin.Primary, context);
        return new RuntimeFailure(CaptureFailure(FaultClass.InvalidAccess, context, exception), []);
    }

    private void Report(FaultClass classification, FaultOrigin origin, FaultContext context)
    {
        var observation = new FaultObservation(classification, FaultSeverity.Error, FaultSubsystem.Jobs,
            origin, 1, TimeProvider.System.GetTimestamp(), context, [], []);
        _faults.TryReport(in observation);
    }

    private static OperationalFailure CaptureFailure(FaultClass classification, FaultContext context, Exception exception)
        => new(classification, FaultSubsystem.Jobs, context, ExceptionDispatchInfo.Capture(exception));

    private static FaultContext Context(JobDispatchPermit permit) =>
        new(permit.Job.World, permit.Source.Tick, permit.Source.Phase, permit.Job, permit.Source, null);

    private static bool IsCancellationRequested(Work work, JobDispatchPermit permit) =>
        work.Stop.Task.IsCompleted || permit.CancellationRequested.IsCompleted;

    private readonly record struct InvocationResult(JobExecutionStatus Status, RuntimeFailure? Failure, bool Release);

    private sealed class Work(Entry entry)
    {
        internal Entry Entry { get; } = entry;
        internal TaskCompletionSource Stop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IRuntimeWorkLease? Retention { get; set; }
        // The custody object keeps an unfinished async frame reachable even if
        // its handler is waiting on a task without any other external owner.
        internal Task? Invocation { get; set; }
        internal JobDispatchPermit? Permit { get; set; }
        internal IJobInput? Input { get; set; }
        internal Exception? InitialFailure { get; set; }
        internal RuntimeFailure? CaptureBackpressure { get; set; }
        internal FaultClass InitialClass { get; set; } = FaultClass.RecoverableJobFault;
        internal long? CancellationObservedAt { get; set; }
    }

    private sealed class InvocationInput(IJobInput input) : IJobInputReader
    {
        private IJobInput? _input = input;
        public BarrierStamp CapturedAt => (Volatile.Read(ref _input) ??
            throw new ObjectDisposedException(nameof(InvocationInput))).CapturedAt;
        public ComponentRead<T> Read<T>(EntityId entity) where T : unmanaged =>
            Volatile.Read(ref _input)?.Read<T>(entity) ?? new ComponentRead<T>(ComponentReadStatus.ExpiredLease, default, null);
        public EntityRead ReadEntity(EntityId entity) =>
            Volatile.Read(ref _input)?.ReadEntity(entity) ?? new EntityRead(ComponentReadStatus.ExpiredLease, null);
        internal void Close() => Interlocked.Exchange(ref _input, null);
    }

    private sealed class Entry(IJobHandle handle, SourceKey source, JobResultKind kind, bool needsCapture,
        JobHandler handler, long admittedAt)
        : IJobExecutionHandle
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<JobExecutionReport> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _callerOwns = true;
        private bool _schedulerOwns = true;

        public JobId Id { get; } = handle.Id;
        public Task<JobExecutionReport> ExecutionCompletion => _completion.Task;
        internal SourceKey Source { get; } = source;
        internal JobResultKind Kind { get; } = kind;
        internal bool NeedsCapture { get; } = needsCapture;
        internal JobHandler Handler { get; } = handler;
        internal long AdmittedAt { get; } = admittedAt;

        public JobObservation Observe()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(!_callerOwns, this);
                return handle.Observe();
            }
        }

        internal JobObservation ObserveOwned()
        {
            lock (_gate)
                return handle.Observe();
        }

        public JobCancellationStatus RequestCancellation()
        {
            lock (_gate)
                return _callerOwns ? handle.RequestCancellation() : JobCancellationStatus.Released;
        }

        internal void CancelOwned()
        {
            lock (_gate)
                handle.RequestCancellation();
        }

        public int CopyReceipts(Span<BatchReceipt> destination)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(!_callerOwns, this);
                return handle.CopyReceipts(destination);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (!_callerOwns)
                    return;
                _callerOwns = false;
                if (!_schedulerOwns)
                    handle.Dispose();
            }
        }

        internal void Finish(JobExecutionStatus status, RuntimeFailure? failure)
        {
            lock (_gate)
            {
                if (!_schedulerOwns)
                    return;
                _schedulerOwns = false;
                _completion.TrySetResult(new JobExecutionReport(status, failure));
                if (!_callerOwns)
                    handle.Dispose();
            }
        }
    }
}
