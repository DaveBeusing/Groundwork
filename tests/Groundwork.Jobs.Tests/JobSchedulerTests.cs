using System.Collections.Immutable;
using System.Diagnostics;
using Groundwork.Core;

namespace Groundwork.Jobs.Tests;

[TestClass]
public sealed class JobSchedulerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JobHandlerId First = new(1);
    private static readonly JobHandlerId Second = new(2);
    private static readonly JobHandlerId Third = new(3);
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TwoOwnershipCreditsRunCommandlessWorkAndRejectUnfundedCapture()
    {
        await using var fixture = new Fixture(worldOperations: 2);
        var scheduler = fixture.CreateScheduler(new(2, 1), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = static (_, _) => ValueTask.CompletedTask,
        });
        var phase = await fixture.OpenPhaseAsync();
        Assert.AreEqual(JobAdmissionStatus.CapacityExceeded,
            scheduler.TrySubmit(Submission(phase, 1, First) with { InputKind = JobInputKind.Borrowed }, phase).Status);
        using var job = Admit(scheduler, phase, Submission(phase, 2, First));
        await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
        Assert.AreEqual(JobExecutionStatus.Succeeded, (await Finish(job)).Status);
        fixture.Publication.Phases.Close(phase);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ResultWriterClosesOnOwnerCancellationOrLocalStop(bool localStop)
    {
        await using var fixture = new Fixture();
        var started = Signal();
        var write = Signal();
        var observed = new TaskCompletionSource<BatchAppendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = fixture.CreateScheduler(new(2, 1), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = async (context, _) =>
            {
                started.TrySetResult();
                await write.Task;
                observed.TrySetResult(localStop
                    ? context.Results.TrySetOutput([42])
                    : context.Results.TryAppend(CreateCommand()));
            },
        });
        var phase = await fixture.OpenPhaseAsync();
        using var job = Admit(scheduler, phase, localStop ? Submission(phase, 1, First) : Structural(phase, 1, First));
        Task? stopped = null;
        try
        {
            await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
            await started.Task.WaitAsync(TestTimeout);
            if (localStop)
                stopped = scheduler.StopAsync(fixture.Ports.CreateBorrowedDrainDeadline()).AsTask();
            else
                Assert.AreEqual(JobCancellationStatus.Requested, job.RequestCancellation());
        }
        finally
        {
            write.TrySetResult();
        }

        Assert.AreEqual(BatchAppendResult.Closed, await observed.Task.WaitAsync(TestTimeout));
        Assert.AreEqual(JobExecutionStatus.Cancelled, (await Finish(job)).Status);
        if (stopped is not null)
            await stopped.WaitAsync(TestTimeout);
        fixture.Publication.Phases.Close(phase);
    }

    [TestMethod]
    public async Task CancellationCallbackKeepsInputUntilItJoinsAndReportsCleanupOnce()
    {
        await using var fixture = new Fixture(requiredEvidenceRecords: 1);
        var registered = Signal();
        var callbackEntered = Signal();
        var handlerReturned = Signal();
        using var release = new ManualResetEventSlim();
        var callbackRead = new TaskCompletionSource<ComponentReadStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exception = new InvalidOperationException("cancellation cleanup failure");
        var scheduler = fixture.CreateScheduler(new(2, 1), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = async (context, token) =>
            {
                // The invocation owns the token registration through callback completion.
                _ = token.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    if (!release.Wait(TestTimeout))
                        throw new TimeoutException("The test did not release the cancellation callback.");
                    callbackRead.TrySetResult(context.Input!.ReadEntity(new EntityId(fixture.World, 0, 1)).Status);
                    throw exception;
                });
                registered.TrySetResult();
                await callbackEntered.Task;
                handlerReturned.TrySetResult();
            },
        });
        var phase = await fixture.OpenPhaseAsync();
        using var job = Admit(scheduler, phase, Submission(phase, 1, First) with
        {
            InputKind = JobInputKind.Borrowed,
            ReadScopes = [new ResourceKey(fixture.World, new ComponentTypeId(1), new ResourceScope(ScopeKind.World, null, null))],
        });
        try
        {
            await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
            await registered.Task.WaitAsync(TestTimeout);
            Assert.AreEqual(JobCancellationStatus.Requested, job.RequestCancellation());
            await handlerReturned.Task.WaitAsync(TestTimeout);
            Assert.IsFalse(job.ExecutionCompletion.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        Assert.AreEqual(ComponentReadStatus.Present, await callbackRead.Task.WaitAsync(TestTimeout));
        var report = await Finish(job);
        Assert.AreEqual(JobExecutionStatus.Faulted, report.Status);
        Assert.IsNotNull(report.Failure);
        Assert.IsNull(report.Failure.Primary);
        Assert.HasCount(1, report.Failure.Cleanup);
        var cleanup = report.Failure.Cleanup[0].Exception.SourceException;
        Assert.IsInstanceOfType<AggregateException>(cleanup);
        Assert.AreSame(exception, ((AggregateException)cleanup).InnerException);
        var safety = fixture.Ports.State.Read();
        Assert.AreEqual(1, safety.PendingRequiredEvidenceRecords);
        Assert.IsFalse(safety.Conditions.HasFlag(SafetyCondition.RequiredEvidenceLost));
        fixture.Publication.Phases.Close(phase);
    }

    [TestMethod]
    public async Task IndependentWorkersRunTogetherAndDependentReadsSettledOutput()
    {
        await using var fixture = new Fixture();
        var producerStarted = Signal();
        var independentStarted = Signal();
        var release = Signal();
        var dependentStarted = Signal();
        var scheduler = fixture.CreateScheduler(new(8, 2), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = async (context, token) =>
            {
                producerStarted.TrySetResult();
                await release.Task.WaitAsync(token);
                Assert.AreEqual(BatchAppendResult.Accepted, context.Results.TrySetOutput([3, 5, 8]));
            },
            [Second] = (context, _) =>
            {
                var destination = new byte[3];
                Assert.AreEqual(new JobOutputRead(JobOutputReadStatus.Copied, 3),
                    context.Dependencies.TryCopy(new JobId(fixture.World, 1), destination));
                CollectionAssert.AreEqual(new byte[] { 3, 5, 8 }, destination);
                dependentStarted.TrySetResult();
                return ValueTask.CompletedTask;
            },
            [Third] = async (_, token) =>
            {
                independentStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            },
        });
        var phase = await fixture.OpenPhaseAsync();
        using var producer = Admit(scheduler, phase, Submission(phase, 1, First));
        using var dependent = Admit(scheduler, phase, Submission(phase, 2, Second) with
        {
            Dependencies = [new JobDependency(producer.Id, DependencyKind.Execution)],
        });
        using var independent = Admit(scheduler, phase, Submission(phase, 3, Third));

        try
        {
            await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
            await producerStarted.Task.WaitAsync(TestTimeout);
            await independentStarted.Task.WaitAsync(TestTimeout);
            Assert.IsFalse(dependentStarted.Task.IsCompleted);
            Assert.AreEqual(JobExecutionStatus.Waiting, dependent.Observe().Execution);
            Assert.AreEqual(2, scheduler.ReadMetrics().OccupiedWorkerSlots);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.AreEqual(JobExecutionStatus.Succeeded, (await Finish(producer)).Status);
        Assert.AreEqual(JobExecutionStatus.Succeeded, (await Finish(independent)).Status);
        await fixture.DispatchUntilAsync(dependent, phase);
        Assert.AreEqual(JobExecutionStatus.Succeeded, (await Finish(dependent)).Status);
        Assert.IsTrue(dependentStarted.Task.IsCompleted);
        fixture.Publication.Phases.Close(phase);
    }

    [TestMethod]
    public async Task BoundedQueueAndCallerDisposalDoNotLoseSchedulerOwnership()
    {
        await using var fixture = new Fixture();
        var started = Signal();
        var scheduler = fixture.CreateScheduler(new(1, 1), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            [Second] = static (_, _) => throw new AssertFailedException("A cancelled queued job must never run."),
        });
        var phase = await fixture.OpenPhaseAsync();
        using var active = Admit(scheduler, phase, Submission(phase, 1, First));
        Assert.AreEqual(JobAdmissionStatus.CapacityExceeded,
            scheduler.TrySubmit(Submission(phase, 2, Second), phase).Status);

        await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
        await started.Task.WaitAsync(TestTimeout);
        using var queued = Admit(scheduler, phase, Submission(phase, 2, Second));
        var queuedCompletion = queued.ExecutionCompletion;
        Assert.AreEqual(JobCancellationStatus.Requested, queued.RequestCancellation());
        queued.Dispose();
        Assert.AreEqual(JobCancellationStatus.Requested, active.RequestCancellation());
        Assert.AreEqual(JobExecutionStatus.Cancelled, (await Finish(active)).Status);
        await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
        Assert.AreEqual(JobExecutionStatus.Cancelled, (await queuedCompletion.WaitAsync(TestTimeout)).Status);
        Assert.AreEqual(1, scheduler.ReadMetrics().Rejected);
        fixture.Publication.Phases.Close(phase);
        await scheduler.StopAsync(fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation));
        var metrics = scheduler.ReadMetrics();
        TestContext.WriteLine(FormattableString.Invariant(
            $"cancel_observed_to_terminal_us={metrics.CancellationWaitTicks * 1_000_000d / metrics.TimestampFrequency:F2}"));
    }

    [TestMethod]
    public async Task HandlerFaultDiscardsCommandsAndInvalidatesCommitDependents()
    {
        await using var fixture = new Fixture();
        var exception = new InvalidOperationException("handler failure");
        var scheduler = fixture.CreateScheduler(new(4, 2), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = (context, _) =>
            {
                Assert.AreEqual(BatchAppendResult.Accepted, context.Results.TryAppend(CreateCommand()));
                throw exception;
            },
            [Second] = static (_, _) => throw new AssertFailedException("A failed prerequisite cannot dispatch."),
        });
        var phase = await fixture.OpenPhaseAsync();
        using var producer = Admit(scheduler, phase, Structural(phase, 1, First));
        using var dependent = Admit(scheduler, phase, Submission(phase, 2, Second) with
        {
            Dependencies = [new JobDependency(producer.Id, DependencyKind.Commit)],
        });

        await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
        var fault = await Finish(producer);
        Assert.AreEqual(JobExecutionStatus.Faulted, fault.Status);
        Assert.AreSame(exception, fault.Failure?.Primary?.Exception.SourceException);
        Assert.AreEqual(JobCommitStatus.Rejected, producer.Observe().Commit);
        await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
        Assert.AreEqual(JobExecutionStatus.DependencyFailed, (await Finish(dependent)).Status);
        Assert.AreEqual(1, fixture.Ports.State.Read().PendingRequiredEvidenceRecords);

        fixture.Publication.Phases.Close(phase);
        var candidates = await fixture.SealAsync(1, BarrierKind.CommitResults);
        Assert.IsEmpty(candidates.Batches);
    }

    [TestMethod]
    public async Task CapturedBorrowIsReleasedAndWorkerReaderExpiresBeforeBarrier()
    {
        await using var fixture = new Fixture();
        var started = Signal();
        var release = Signal();
        IJobInputReader? reader = null;
        var scheduler = fixture.CreateScheduler(new(4, 2), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = async (context, token) =>
            {
                reader = context.Input ?? throw new AssertFailedException("Expected captured input.");
                Assert.IsFalse(reader is IJobInput);
                Assert.AreEqual(context.DispatchStamp, reader.CapturedAt);
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            },
        });
        var phase = await fixture.OpenPhaseAsync();
        using var job = Admit(scheduler, phase, Submission(phase, 1, First) with
        {
            InputKind = JobInputKind.Borrowed,
            ReadScopes = [new ResourceKey(fixture.World, new ComponentTypeId(1),
                new ResourceScope(ScopeKind.World, null, null))],
        });

        try
        {
            await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
            await started.Task.WaitAsync(TestTimeout);
            fixture.Publication.Phases.Close(phase);
            var request = new BarrierRequest(phase.Stamp, 1, BarrierKind.CommitResults);
            var drain = fixture.Publication.Borrow.CloseAndDrainAsync(request,
                fixture.Ports.CreateBorrowedDrainDeadline()).AsTask();
            Assert.IsFalse(drain.IsCompleted);
            release.TrySetResult();
            Assert.AreEqual(JobExecutionStatus.Succeeded, (await Finish(job)).Status);
            Assert.AreEqual(BorrowDrainStatus.Drained, await drain.WaitAsync(TestTimeout));
            Assert.IsNotNull(reader);
            Assert.AreEqual(ComponentReadStatus.ExpiredLease, reader.ReadEntity(new EntityId(fixture.World, 0, 1)).Status);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WorkerCompletionPermutationsKeepCanonicalCandidateOrder(bool reverse)
    {
        await using var fixture = new Fixture();
        var firstStarted = Signal();
        var secondStarted = Signal();
        var firstFinish = Signal();
        var secondFinish = Signal();
        var scheduler = fixture.CreateScheduler(new(4, 2), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = async (context, token) =>
            {
                firstStarted.TrySetResult();
                await firstFinish.Task.WaitAsync(token);
                Assert.AreEqual(BatchAppendResult.Accepted, context.Results.TryAppend(CreateCommand()));
            },
            [Second] = async (context, token) =>
            {
                secondStarted.TrySetResult();
                await secondFinish.Task.WaitAsync(token);
                Assert.AreEqual(BatchAppendResult.Accepted, context.Results.TryAppend(CreateCommand()));
            },
        });
        var phase = await fixture.OpenPhaseAsync();
        using var first = Admit(scheduler, phase, Structural(phase, 1, First));
        using var second = Admit(scheduler, phase, Structural(phase, 2, Second));
        try
        {
            await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
            await firstStarted.Task.WaitAsync(TestTimeout);
            await secondStarted.Task.WaitAsync(TestTimeout);
            var firstGate = reverse ? secondFinish : firstFinish;
            var secondGate = reverse ? firstFinish : secondFinish;
            firstGate.TrySetResult();
            Assert.AreEqual(JobExecutionStatus.Succeeded, (await Finish(reverse ? second : first)).Status);
            secondGate.TrySetResult();
            Assert.AreEqual(JobExecutionStatus.Succeeded, (await Finish(reverse ? first : second)).Status);
        }
        finally
        {
            firstFinish.TrySetResult();
            secondFinish.TrySetResult();
        }

        fixture.Publication.Phases.Close(phase);
        var candidates = await fixture.SealAsync(1, BarrierKind.CommitResults);
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, candidates.Batches.Select(batch => batch.Permit.Job).ToArray());
        fixture.Publication.Finalization.TryAbort(candidates, BarrierAbortReason.Cancelled);
    }

    [TestMethod]
    public async Task IgnoredAppendFailureDoesNotPublishTheValidPrefix()
    {
        await using var fixture = new Fixture();
        var scheduler = fixture.CreateScheduler(new(2, 1), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = static (context, _) =>
            {
                var command = CreateCommand();
                Assert.AreEqual(BatchAppendResult.Accepted, context.Results.TryAppend(command));
                Assert.AreEqual(BatchAppendResult.CapacityExceeded,
                    context.Results.TryAppend(command with { CommandSequence = 2 }));
                return ValueTask.CompletedTask;
            },
        });
        var phase = await fixture.OpenPhaseAsync();
        using var job = Admit(scheduler, phase, Structural(phase, 1, First));
        await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
        Assert.AreEqual(JobExecutionStatus.Faulted, (await Finish(job)).Status);
        fixture.Publication.Phases.Close(phase);
        var candidates = await fixture.SealAsync(1, BarrierKind.CommitResults);
        Assert.IsEmpty(candidates.Batches);
    }

    [TestMethod]
    public async Task UnknownHandlerAndForwardDependencyAreRejectedBeforeExecution()
    {
        await using var fixture = new Fixture();
        var scheduler = fixture.CreateScheduler(new(2, 1), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = static (_, _) => throw new AssertFailedException("An invalid descriptor cannot run."),
        });
        var phase = await fixture.OpenPhaseAsync();
        Assert.AreEqual(JobAdmissionStatus.InvalidDescriptor,
            scheduler.TrySubmit(Submission(phase, 1, Second), phase).Status);
        Assert.AreEqual(JobAdmissionStatus.InvalidPrerequisite,
            scheduler.TrySubmit(Submission(phase, 1, First) with
            {
                Dependencies = [new JobDependency(new JobId(fixture.World, 2), DependencyKind.Execution)],
            }, phase).Status);
        await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
        Assert.AreEqual(0, scheduler.ReadMetrics().Started);
        fixture.Publication.Phases.Close(phase);
    }

    [TestMethod]
    public async Task StaleCaptureSettlesFaultWithoutCallingHandler()
    {
        await using var fixture = new Fixture();
        var called = Signal();
        var scheduler = fixture.CreateScheduler(new(2, 1), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = (_, _) =>
            {
                called.TrySetResult();
                return ValueTask.CompletedTask;
            },
        }, new InputSource(fixture.Publication.Capture, wrongStamp: true));
        var phase = await fixture.OpenPhaseAsync();
        using var job = Admit(scheduler, phase, Submission(phase, 1, First) with
        {
            InputKind = JobInputKind.OwnedSnapshot,
            Budget = new JobResultBudget(0, 0, 0, 16, 16),
        });
        await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
        Assert.AreEqual(JobExecutionStatus.Faulted, (await Finish(job)).Status);
        Assert.IsFalse(called.Task.IsCompleted);
        fixture.Publication.Phases.Close(phase);
        var candidates = await fixture.SealAsync(1, BarrierKind.CommitResults);
        Assert.IsEmpty(candidates.Batches);
    }

    [TestMethod]
    public async Task OwnedSnapshotDoesNotBlockAClosedPhaseBarrier()
    {
        await using var fixture = new Fixture();
        var started = Signal();
        var release = Signal();
        var scheduler = fixture.CreateScheduler(new(2, 1), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = async (context, token) =>
            {
                Assert.AreEqual(context.DispatchStamp, context.Input?.CapturedAt);
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                Assert.AreEqual(context.DispatchStamp, context.Input?.CapturedAt);
            },
        });
        var phase = await fixture.OpenPhaseAsync();
        using var job = Admit(scheduler, phase, Submission(phase, 1, First) with
        {
            InputKind = JobInputKind.OwnedSnapshot,
            Budget = new JobResultBudget(0, 0, 0, 16, 16),
        });

        try
        {
            await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
            await started.Task.WaitAsync(TestTimeout);
            fixture.Publication.Phases.Close(phase);
            var candidates = await fixture.SealAsync(1, BarrierKind.CommitResults);
            var prepared = fixture.Publication.Preparation.TryPrepare(new BarrierPublicationDraft(
                candidates, fixture.Publication.Committed.Read().Root, 0, []));
            Assert.IsNotNull(prepared.Publication);
            Assert.AreEqual(BarrierStatus.Unchanged, fixture.Publication.Finalization.TryFinalize(prepared.Publication).Status);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.AreEqual(JobExecutionStatus.Succeeded, (await Finish(job)).Status);
    }

    [TestMethod]
    public async Task ReportsNoOpWorkerAndCoordinatorTimingSample()
    {
        await using var fixture = new Fixture();
        var scheduler = fixture.CreateScheduler(new(4, 1), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = static (_, _) => ValueTask.CompletedTask,
        });
        var phase = await fixture.OpenPhaseAsync();
        const int count = 32;
        var sampleStart = Stopwatch.GetTimestamp();
        for (ulong ordinal = 1; ordinal <= count; ordinal++)
        {
            using var job = Admit(scheduler, phase, Submission(phase, ordinal, First));
            await fixture.DispatchUntilAsync(job, phase);
            Assert.AreEqual(JobExecutionStatus.Succeeded, (await Finish(job)).Status);
        }

        fixture.Publication.Phases.Close(phase);
        var deadline = fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        await scheduler.StopAsync(deadline);
        var metrics = scheduler.ReadMetrics();
        var elapsed = Stopwatch.GetElapsedTime(sampleStart);
        Assert.AreEqual((long)count, metrics.Succeeded);
        Assert.AreEqual(0, metrics.OccupiedWorkerSlots);
        var ticksToMicroseconds = 1_000_000d / metrics.TimestampFrequency;
        TestContext.WriteLine(FormattableString.Invariant(
            $"jobs={count}, queue_us_per_job={metrics.QueueWaitTicks * ticksToMicroseconds / count:F2}, worker_us_per_job={metrics.WorkerBusyTicks * ticksToMicroseconds / count:F2}, dispatch_us_per_job={metrics.DispatchWaitTicks * ticksToMicroseconds / count:F2}, single_slot_utilization_pct={100d * metrics.WorkerBusyTicks / metrics.TimestampFrequency / elapsed.TotalSeconds:F2}"));
    }

    [TestMethod]
    public async Task ShutdownDeadlineLeavesUncooperativeWorkInCustodyUntilItAcknowledges()
    {
        var clock = new TestClock();
        await using var fixture = new Fixture(clock);
        var started = Signal();
        var finish = Signal();
        var scheduler = fixture.CreateScheduler(new(4, 1), new Dictionary<JobHandlerId, JobHandler>
        {
            [First] = async (_, _) =>
            {
                started.TrySetResult();
                await finish.Task;
            },
        });
        var phase = await fixture.OpenPhaseAsync();
        using var handle = Admit(scheduler, phase, Submission(phase, 1, First));
        try
        {
            await scheduler.DispatchReadyAsync(phase, CancellationToken.None);
            await started.Task.WaitAsync(TestTimeout);
            var deadline = fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            clock.Advance(TimeSpan.FromSeconds(3));
            await Assert.ThrowsExactlyAsync<IncompleteCleanupException>(
                () => scheduler.StopAsync(deadline).AsTask());
            Assert.AreEqual(ShutdownSettlement.Custody, fixture.Runtime.SettleStop());
            Assert.IsFalse(handle.ExecutionCompletion.IsCompleted);
        }
        finally
        {
            finish.TrySetResult();
        }

        Assert.AreEqual(JobExecutionStatus.Cancelled, (await Finish(handle)).Status);
        handle.Dispose();
        await fixture.WaitForReleaseAsync();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<JobExecutionReport> Finish(IJobExecutionHandle handle) =>
        handle.ExecutionCompletion.WaitAsync(TestTimeout);

    private static IJobExecutionHandle Admit(JobScheduler scheduler, JobDispatchPhase phase, JobSubmission submission)
    {
        var admission = scheduler.TrySubmit(submission, phase);
        Assert.AreEqual(JobAdmissionStatus.Accepted, admission.Status);
        Assert.IsInstanceOfType<IJobExecutionHandle>(admission.Handle);
        return (IJobExecutionHandle)admission.Handle;
    }

    private static JobSubmission Submission(JobDispatchPhase phase, ulong ordinal, JobHandlerId handler) => new(
        new SourceKey(phase.Stamp.World, phase.Stamp.Tick, phase.Phase, 1, 0, ordinal),
        handler, JobInputKind.None, JobResultKind.Commandless, [], [], [], [], [],
        new JobResultBudget(0, 0, 0, 16, 0));

    private static JobSubmission Structural(JobDispatchPhase phase, ulong ordinal, JobHandlerId handler) =>
        Submission(phase, ordinal, handler) with
        {
            ResultKind = JobResultKind.Commands,
            CommandScopes = [new ResourceKey(phase.Stamp.World, default, new ResourceScope(ScopeKind.World, null, null))],
            Budget = new JobResultBudget(1, 1, 0, 0, 0),
        };

    private static JobCommand CreateCommand() => new(
        1, 1, JobCommandKind.CreateEntity, new CommandEntity(CommandEntityKind.Provisional, null, 1),
        default, null, null, []);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TimeProvider _clock;
        private JobScheduler? _scheduler;

        internal Fixture(TimeProvider? clock = null, int worldOperations = 48, int requiredEvidenceRecords = 4)
        {
            _clock = clock ?? TimeProvider.System;
            var quota = new ResourceQuota(1, 64,
                new JobQuota(16, 4, 32, 24, 8, 16, 24, 131072, 131072, 131072),
                new DiagnosticQuota(1, 16384, 12, 32768, 4, 4, 64, 1024));
            var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, quota));
            var reservation = budget.TryReserveRuntime(quota);
            Assert.IsNotNull(reservation);
            var interval = TimeSpan.FromSeconds(2);
            Runtime = RuntimeSafetyAuthority.Create(_clock, new RuntimeTimingLimits(interval, interval, interval), reservation);
            Assert.IsTrue(Runtime.TryAllocateWorldId(out var world));
            World = world;
            var worldQuota = quota with
            {
                OutstandingOperations = worldOperations,
                Diagnostics = quota.Diagnostics with
                {
                    EmergencyRecords = 9,
                    EmergencyBytes = 28672,
                    RequiredEvidenceRecords = requiredEvidenceRecords,
                },
            };
            Ports = Runtime.OpenWorld(world, worldQuota, new ContinuePolicy(), new Root(world), new ScopePolicy());
        }

        internal RuntimeSafetyAuthority Runtime { get; }
        internal WorldId World { get; }
        internal WorldSafetyPorts Ports { get; }
        internal WorldPublicationPorts Publication => Ports.Publication;

        internal JobScheduler CreateScheduler(JobSchedulerLimits limits, IReadOnlyDictionary<JobHandlerId, JobHandler> handlers,
            IJobInputSource? inputs = null)
            => _scheduler = new JobScheduler(World, limits, Publication.Execution,
                inputs ?? new InputSource(Publication.Capture), Ports.Ownership, Ports.Reporter, _clock, handlers);

        internal async Task<JobDispatchPhase> OpenPhaseAsync()
        {
            var candidates = await SealAsync(1, BarrierKind.BeginTick);
            var prepared = Publication.Preparation.TryPrepare(new BarrierPublicationDraft(
                candidates, Publication.Committed.Read().Root, 0, []));
            Assert.IsNotNull(prepared.Publication);
            var outcome = Publication.Finalization.TryFinalize(prepared.Publication);
            Assert.IsTrue(outcome.Stamp.HasValue);
            var phase = Publication.Phases.TryOpen(outcome.Stamp.GetValueOrDefault(), TickPhase.Base);
            Assert.AreEqual(JobPhaseOpenStatus.Opened, phase.Status);
            Assert.IsNotNull(phase.Phase);
            return phase.Phase;
        }

        internal async Task<CandidateSet> SealAsync(ulong tick, BarrierKind kind)
        {
            var request = new BarrierRequest(Publication.Committed.Read().Stamp, tick, kind);
            Assert.AreEqual(BorrowDrainStatus.Drained, await Publication.Borrow.CloseAndDrainAsync(request,
                Ports.CreateBorrowedDrainDeadline()));
            var result = Publication.Candidates.SealCandidates(request);
            Assert.AreEqual(CandidateSealStatus.Sealed, result.Status);
            Assert.IsNotNull(result.Candidates);
            return result.Candidates;
        }

        internal async Task DispatchUntilAsync(IJobExecutionHandle handle, JobDispatchPhase phase)
        {
            using var cancellation = new CancellationTokenSource(TestTimeout);
            while (!handle.ExecutionCompletion.IsCompleted)
            {
                await _scheduler!.DispatchReadyAsync(phase, cancellation.Token);
                await Task.Delay(1, cancellation.Token);
            }
        }

        internal async Task WaitForReleaseAsync()
        {
            using var cancellation = new CancellationTokenSource(TestTimeout);
            while (Runtime.SettleStop() != ShutdownSettlement.Released)
                await Task.Delay(1, cancellation.Token);
        }

        public async ValueTask DisposeAsync()
        {
            var deadline = Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            if (_scheduler is not null && !deadline.IsExpired(_clock))
                await _scheduler.StopAsync(deadline);
            Runtime.SettleStop();
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }

    private sealed record Root(WorldId World) : IWorldStateRoot;

    private sealed record Scope(ImmutableArray<ResourceKey> ReadScopes, ImmutableArray<ResourceKey> CommandScopes)
        : IAdmittedJobScope;

    private sealed class ScopePolicy : IJobScopePolicy
    {
        public JobScopeAdmission ValidateAdmission(JobSubmission submission, in CommittedWorldView world) =>
            new(JobScopeStatus.Authorized, new Scope(submission.ReadScopes, submission.CommandScopes));

        public bool CanDispatch(IAdmittedJobScope scope, in CommittedWorldView world) => true;

        public JobScopeOverlap EvaluateOverlap(IAdmittedJobScope first, IAdmittedJobScope second, in CommittedWorldView world)
            => JobScopeOverlap.Independent;
    }

    private sealed class ContinuePolicy : IBarrierSupervisorPolicy
    {
        public ValueTask<SupervisorAction> DecideAsync(BarrierReviewRequest request, IFaultEvidenceReader evidence,
            CancellationToken cancellationToken) => ValueTask.FromResult(SupervisorAction.ContinueAtSafeBarrier);
    }

    private sealed class InputSource(IJobCaptureAuthority capture, bool wrongStamp = false) : IJobInputSource
    {
        public JobInputCapture TryCapture(in JobInputRequest request)
        {
            var result = capture.TryAcquire(request.Permit);
            if (result.Lease is not { } lease)
                return new JobInputCapture(JobInputCaptureStatus.InvalidPermit, null);
            var stamp = lease.World.Stamp;
            if (wrongStamp)
                stamp = stamp with { Epoch = stamp.Epoch - 1 };
            if (request.Permit.InputKind == JobInputKind.OwnedSnapshot)
            {
                lease.Dispose();
                return new JobInputCapture(JobInputCaptureStatus.Captured, new Input(stamp, null));
            }

            return new JobInputCapture(JobInputCaptureStatus.Captured, new Input(stamp, lease));
        }
    }

    private sealed class Input(BarrierStamp stamp, IJobCaptureLease? lease) : IJobInput
    {
        public BarrierStamp CapturedAt => stamp;
        public ComponentRead<T> Read<T>(EntityId entity) where T : unmanaged => new(ComponentReadStatus.Missing, default, null);
        public EntityRead ReadEntity(EntityId entity) => new(ComponentReadStatus.Present, null);
        public void Dispose() => lease?.Dispose();
    }
}
