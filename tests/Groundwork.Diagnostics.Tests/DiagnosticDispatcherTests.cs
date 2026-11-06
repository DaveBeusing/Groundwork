using System.Reflection;
using Groundwork.Core;

namespace Groundwork.Diagnostics.Tests;

[TestClass]
public sealed class DiagnosticDispatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly ResourceQuota Quota = new(1, 16,
        new JobQuota(4, 2, 4, 4, 2, 4, 8, 4096, 4096, 4096),
        new DiagnosticQuota(4, 4096, 12, 16384, 4, 4, 64, 1024));

    [TestMethod]
    public async Task OptionalQueueDropsOnSaturationAndReusesDrainedSlots()
    {
        var (runtime, _) = CreateRuntime(2);
        var delivered = new List<uint>();
        var dispatcher = new DiagnosticDispatcher(runtime.Diagnostics, runtime.RuntimeOwnership,
            new Sink((observation, _) =>
            {
                delivered.Add(observation.Code);
                return ValueTask.CompletedTask;
            }));
        try
        {
            Assert.AreEqual(FaultReportStatus.Recorded, Report(runtime.Reporter, 1));
            Assert.AreEqual(FaultReportStatus.Recorded, Report(runtime.Reporter, 2));
            Assert.AreEqual(FaultReportStatus.Coalesced, Report(runtime.Reporter, 3));
            Assert.AreEqual(1, await dispatcher.DrainAsync(1).WaitAsync(Timeout));
            Assert.AreEqual(FaultReportStatus.Recorded, Report(runtime.Reporter, 4));
            Assert.AreEqual(2, await dispatcher.DrainAsync(4).WaitAsync(Timeout));
            CollectionAssert.AreEqual(new uint[] { 1, 2, 4 }, delivered);
            var state = runtime.State.Read();
            Assert.AreEqual(1UL, state.DeliveryLossCount);
            Assert.IsFalse(state.Conditions.HasFlag(SafetyCondition.RequiredEvidenceLost));
        }
        finally
        {
            await StopAsync(runtime, dispatcher);
        }
    }

    [TestMethod]
    public void DrainingWorldDiagnosticsDoesNotConsumeRequiredEvidence()
    {
        var (runtime, _) = CreateRuntime(1);
        try
        {
            Assert.IsTrue(runtime.TryAllocateWorldId(out var id));
            var quota = Quota with { Diagnostics = Quota.Diagnostics with
            {
                NormalRecords = 3, NormalBytes = 3072, EmergencyRecords = 9, EmergencyBytes = 13312,
            } };
            var world = runtime.OpenWorld(id, quota, new BarrierSupervisorPolicy(4), new Root(id), new ScopePolicy());
            var context = new FaultContext(id, 1, TickPhase.CommitResults, null, null, null);
            var first = new FaultObservation(FaultClass.StaleResult, FaultSeverity.Warning, FaultSubsystem.Ecs,
                FaultOrigin.Primary, 1, 0, context, [], []);
            var second = new FaultObservation(FaultClass.WriteConflict, FaultSeverity.Warning, FaultSubsystem.Ecs,
                FaultOrigin.Primary, 2, 0, context, [], []);
            Assert.AreEqual(FaultReportStatus.Recorded, world.Reporter.TryReport(first));
            Assert.AreEqual(FaultReportStatus.Recorded, world.Reporter.TryReport(second));

            Assert.IsTrue(world.Diagnostics.TryAcquire(out var later, out var lease));
            using var retained = lease;
            Assert.AreEqual(2U, later.Code);
            Assert.IsFalse(world.Diagnostics.TryAcquire(out _, out _));
            Assert.AreEqual(1U, world.Diagnostics.ReadEmergency().Primary?.Code);
            var state = world.State.Read();
            Assert.AreEqual(2, state.PendingRequiredEvidenceRecords);
            Span<RequiredFaultEvidence> evidence = stackalloc RequiredFaultEvidence[2];
            Assert.AreEqual(FaultEvidenceReadStatus.Copied, world.Evidence.CopyPending(state.PendingFaultGeneration, evidence, out var count));
            Assert.AreEqual(2, count);
            Assert.AreEqual(FaultClass.StaleResult, evidence[0].Classification);
            Assert.AreEqual(FaultClass.WriteConflict, evidence[1].Classification);
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            Assert.AreEqual(ShutdownSettlement.Released, runtime.SettleStop());
        }
    }

    [TestMethod]
    public async Task EmergencyArrivingDuringBatchPrecedesQueuedNormalEvents()
    {
        var (runtime, _) = CreateRuntime(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<uint>();
        var dispatcher = new DiagnosticDispatcher(runtime.Diagnostics, runtime.RuntimeOwnership,
            new Sink(async (observation, _) =>
            {
                delivered.Add(observation.Code);
                if (observation.Code == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            }));
        try
        {
            Assert.AreEqual(FaultReportStatus.Recorded, Report(runtime.Reporter, 1));
            Assert.AreEqual(FaultReportStatus.Recorded, Report(runtime.Reporter, 2));
            var active = dispatcher.DrainAsync(3);
            await entered.Task.WaitAsync(Timeout);
            Assert.AreEqual(FaultReportStatus.Recorded, Report(runtime.Reporter, 11, FaultClass.UnknownCritical));
            release.TrySetResult();
            Assert.AreEqual(3, await active.WaitAsync(Timeout));
            CollectionAssert.AreEqual(new uint[] { 1, 11, 2 }, delivered);
        }
        finally
        {
            release.TrySetResult();
            await StopAsync(runtime, dispatcher);
        }
    }

    [TestMethod]
    public async Task EmergencyRacingAcquisitionPreservesNormalEventForNextBatch()
    {
        var (runtime, _) = CreateRuntime(1);
        var delivered = new List<uint>();
        var reader = new InterceptingReader(runtime.Diagnostics,
            () => Assert.AreEqual(FaultReportStatus.Recorded, Report(runtime.Reporter, 11, FaultClass.UnknownCritical)));
        var dispatcher = new DiagnosticDispatcher(reader, runtime.RuntimeOwnership, new Sink((observation, _) =>
        {
            delivered.Add(observation.Code);
            return ValueTask.CompletedTask;
        }));
        try
        {
            Assert.AreEqual(FaultReportStatus.Recorded, Report(runtime.Reporter, 1));
            Assert.AreEqual(1, await dispatcher.DrainAsync(1).WaitAsync(Timeout));
            CollectionAssert.AreEqual(new uint[] { 11 }, delivered);
            Assert.AreEqual(1, await dispatcher.DrainAsync(1).WaitAsync(Timeout));
            CollectionAssert.AreEqual(new uint[] { 11, 1 }, delivered);
        }
        finally
        {
            await StopAsync(runtime, dispatcher);
        }
    }

    [TestMethod]
    public async Task StopAfterSelectionPreventsSinkAdmission()
    {
        var (runtime, _) = CreateRuntime(1);
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new InterceptingReader(runtime.Diagnostics, () =>
        {
            selected.TrySetResult();
            release.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
        });
        int calls = 0;
        var dispatcher = new DiagnosticDispatcher(reader, runtime.RuntimeOwnership, new Sink((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.CompletedTask;
        }));
        try
        {
            Assert.AreEqual(FaultReportStatus.Recorded, Report(runtime.Reporter, 1));
            var active = dispatcher.DrainAsync(1);
            await selected.Task.WaitAsync(Timeout);
            var deadline = runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            var stopped = dispatcher.StopAsync(deadline, TimeProvider.System).AsTask();
            Assert.IsFalse(stopped.IsCompleted);
            release.TrySetResult();
            Assert.AreEqual(0, await active.WaitAsync(Timeout));
            await stopped.WaitAsync(Timeout);
            Assert.AreEqual(0, calls);
        }
        finally
        {
            release.TrySetResult();
            await StopAsync(runtime, dispatcher);
        }
    }

    [TestMethod]
    public async Task ThrowingReentrantSinkIsDisabledAndEmergencyRecordsStaySeparate()
    {
        var (runtime, _) = CreateRuntime(1);
        int calls = 0;
        DiagnosticDispatcher? dispatcher = null;
        var sink = new Sink(async (_, _) =>
        {
            calls++;
            Assert.AreEqual(0, await dispatcher!.DrainAsync(1));
            throw new InvalidOperationException("secret that must not be copied");
        });
        dispatcher = new DiagnosticDispatcher(runtime.Diagnostics, runtime.RuntimeOwnership, sink);
        try
        {
            Report(runtime.Reporter, 1);
            Report(runtime.Reporter, 11, FaultClass.UnknownCritical);
            Report(runtime.Reporter, 22, FaultClass.IncompleteCleanup, FaultOrigin.Cleanup);
            Assert.AreEqual(0, calls);
            Assert.AreEqual(0, await dispatcher.DrainAsync(4).WaitAsync(Timeout));
            Assert.IsTrue(dispatcher.SinkDisabled);
            Assert.AreEqual(0, await dispatcher.DrainAsync(4).WaitAsync(Timeout));
            Assert.AreEqual(1, calls);

            var emergency = runtime.Diagnostics.ReadEmergency();
            Assert.AreEqual(11U, emergency.Primary?.Code);
            Assert.AreEqual(22U, emergency.Cleanup?.Code);
            Assert.AreEqual(FaultOrigin.Sink, emergency.Sink?.Origin);
            Assert.AreEqual(FaultSubsystem.Diagnostics, emergency.Sink?.Subsystem);
            Assert.IsTrue(runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
            Assert.IsFalse(runtime.State.Read().Conditions.HasFlag(SafetyCondition.RequiredEvidenceLost));
        }
        finally
        {
            await StopAsync(runtime, dispatcher);
        }
    }

    [TestMethod]
    public async Task BlockedSinkKeepsRuntimeInCustodyUntilItActuallyReleases()
    {
        var (runtime, budget) = CreateRuntime(1, TimeSpan.FromMilliseconds(250));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DiagnosticDispatcher(runtime.Diagnostics, runtime.RuntimeOwnership,
            new Sink(async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
            }));
        Report(runtime.Reporter, 1);
        var active = dispatcher.DrainAsync(1);
        RuntimeDeadline deadline;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.AreEqual(FaultReportStatus.Coalesced, Report(runtime.Reporter, 3));
            Assert.AreEqual(1UL, runtime.State.Read().DeliveryLossCount);
            deadline = runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            await Assert.ThrowsExactlyAsync<IncompleteCleanupException>(() => dispatcher.StopAsync(deadline, TimeProvider.System).AsTask());
            Assert.AreEqual(ShutdownSettlement.Custody, runtime.SettleStop());
            Assert.IsNull(budget.TryReserveRuntime(Quota));
            Assert.IsFalse(active.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.AreEqual(1, await active.WaitAsync(Timeout));
        using var watchdog = new CancellationTokenSource(Timeout);
        while (runtime.SettleStop() != ShutdownSettlement.Released)
            await Task.Delay(1, watchdog.Token);
        await dispatcher.StopAsync(deadline, TimeProvider.System);
        using var available = budget.TryReserveRuntime(Quota);
        Assert.IsNotNull(available);
    }

    [TestMethod]
    public async Task StopDeadlineIsEnforcedWhileNormalLeaseReleaseWaitsOnCore()
    {
        var (runtime, _) = CreateRuntime(1, TimeSpan.FromMilliseconds(250));
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var ledger = typeof(RuntimeSafetyAuthority).GetField("_ledger", fields)!.GetValue(runtime)!;
        var queue = ledger.GetType().GetField("_normal", fields)!.GetValue(ledger)!;
        var gate = queue.GetType().GetProperty("Gate", fields)!.GetValue(queue)!;
        var sinkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSink = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returning = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DiagnosticDispatcher(runtime.Diagnostics, runtime.RuntimeOwnership,
            new Sink((_, _) =>
            {
                sinkEntered.SetResult();
                releaseSink.Task.WaitAsync(Timeout, CancellationToken.None).GetAwaiter().GetResult();
                returning.SetResult(Thread.CurrentThread);
                return ValueTask.CompletedTask;
            }));
        Assert.AreEqual(FaultReportStatus.Recorded, Report(runtime.Reporter, 1));
        var active = dispatcher.DrainAsync(1);

        var gateHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? holder = null;
        try
        {
            await sinkEntered.Task.WaitAsync(Timeout);
            holder = Task.Run(() =>
            {
                lock (gate)
                {
                    gateHeld.SetResult();
                    releaseGate.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
                }
            });
            await gateHeld.Task.WaitAsync(Timeout);
            releaseSink.SetResult();
            var worker = await returning.Task.WaitAsync(Timeout);
            // The synchronous sink has finished waiting. Its worker can now
            // block only while returning the normal lease to the held queue.
            using var watchdog = new CancellationTokenSource(Timeout);
            while ((worker.ThreadState & ThreadState.WaitSleepJoin) == 0)
                await Task.Delay(1, watchdog.Token);

            var deadline = runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            await Assert.ThrowsExactlyAsync<IncompleteCleanupException>(() =>
                Task.Run(() => dispatcher.StopAsync(deadline, TimeProvider.System).AsTask()).WaitAsync(Timeout / 2));
            Assert.IsFalse(active.IsCompleted);
        }
        finally
        {
            releaseSink.TrySetResult();
            releaseGate.TrySetResult();
            if (holder is not null)
                await holder.WaitAsync(Timeout);
            await active.WaitAsync(Timeout);
            var deadline = runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            try
            {
                await dispatcher.StopAsync(deadline, TimeProvider.System);
            }
            catch (IncompleteCleanupException)
            {
                // The original deadline has elapsed. Custody ends when cleanup finishes.
            }

            using var watchdog = new CancellationTokenSource(Timeout);
            while (runtime.SettleStop() != ShutdownSettlement.Released)
                await Task.Delay(1, watchdog.Token);
        }
    }

    [TestMethod]
    public async Task BlockedCancellationCallbackKeepsCustodyAfterSinkCompletes()
    {
        var (runtime, budget) = CreateRuntime(1, TimeSpan.FromMilliseconds(250));
        var sinkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSink = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DiagnosticDispatcher(runtime.Diagnostics, runtime.RuntimeOwnership,
            new Sink(async (observation, token) =>
            {
                _ = token.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    releaseCallback.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
                    Assert.AreEqual(1U, observation.Code);
                });
                sinkEntered.TrySetResult();
                await releaseSink.Task;
            }));
        Assert.AreEqual(FaultReportStatus.Recorded, Report(runtime.Reporter, 1));
        var active = dispatcher.DrainAsync(1);
        RuntimeDeadline deadline;
        try
        {
            await sinkEntered.Task.WaitAsync(Timeout);
            deadline = runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            var stopped = dispatcher.StopAsync(deadline, TimeProvider.System).AsTask();
            await callbackEntered.Task.WaitAsync(Timeout);
            releaseSink.TrySetResult();
            Assert.AreEqual(1, await active.WaitAsync(Timeout));
            Assert.AreEqual(FaultReportStatus.Coalesced, Report(runtime.Reporter, 2));
            await Assert.ThrowsExactlyAsync<IncompleteCleanupException>(() => stopped);
            Assert.AreEqual(ShutdownSettlement.Custody, runtime.SettleStop());
            Assert.IsNull(budget.TryReserveRuntime(Quota));
        }
        finally
        {
            releaseSink.TrySetResult();
            releaseCallback.TrySetResult();
        }

        using var watchdog = new CancellationTokenSource(Timeout);
        while (runtime.SettleStop() != ShutdownSettlement.Released)
            await Task.Delay(1, watchdog.Token);
        await dispatcher.StopAsync(deadline, TimeProvider.System);
        using var available = budget.TryReserveRuntime(Quota);
        Assert.IsNotNull(available);
    }

    [TestMethod]
    public void RuntimeNormalReservationCannotAlsoBeChargedToAWorld()
    {
        var (runtime, _) = CreateRuntime(2);
        try
        {
            Assert.IsTrue(runtime.TryAllocateWorldId(out var world));
            var quota = Quota with { Diagnostics = Quota.Diagnostics with { EmergencyRecords = 9, EmergencyBytes = 13312 } };
            var policy = new BarrierSupervisorPolicy(4);
            Assert.ThrowsExactly<InvalidOperationException>(() => runtime.OpenWorld(world, quota, policy, new Root(world), new ScopePolicy()));
            quota = quota with { Diagnostics = quota.Diagnostics with { NormalRecords = 2, NormalBytes = 2048 } };
            Assert.IsNotNull(runtime.OpenWorld(world, quota, policy, new Root(world), new ScopePolicy()));
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            Assert.AreEqual(ShutdownSettlement.Released, runtime.SettleStop());
        }
    }

    private static FaultReportStatus Report(IFaultReporter reporter, uint code,
        FaultClass classification = FaultClass.DiagnosticOnly, FaultOrigin origin = FaultOrigin.Primary)
    {
        var observation = new FaultObservation(classification, FaultSeverity.Error, FaultSubsystem.Host,
            origin, code, 0, default, [], "redacted context");
        return reporter.TryReport(observation);
    }

    private static (RuntimeSafetyAuthority Runtime, ProcessResourceBudget Budget) CreateRuntime(int records, TimeSpan? shutdown = null)
    {
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, Quota));
        using var reservation = budget.TryReserveRuntime(Quota);
        Assert.IsNotNull(reservation);
        return (RuntimeSafetyAuthority.Create(TimeProvider.System, new RuntimeTimingLimits(shutdown ?? Timeout,
            Timeout, Timeout), reservation, records), budget);
    }

    private static async Task StopAsync(RuntimeSafetyAuthority runtime, DiagnosticDispatcher dispatcher)
    {
        var deadline = runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        await dispatcher.StopAsync(deadline, TimeProvider.System);
        Assert.AreEqual(ShutdownSettlement.Released, runtime.SettleStop());
    }

    private sealed record Root(WorldId World) : IWorldStateRoot;

    private sealed class ScopePolicy : IJobScopePolicy
    {
        public JobScopeAdmission ValidateAdmission(JobSubmission submission, in CommittedWorldView world) =>
            new(JobScopeStatus.InvalidAccess, null);
        public bool CanDispatch(IAdmittedJobScope scope, in CommittedWorldView world) => false;
        public JobScopeOverlap EvaluateOverlap(IAdmittedJobScope first, IAdmittedJobScope second, in CommittedWorldView world) =>
            JobScopeOverlap.Unknown;
    }

    private sealed class Sink(Func<DiagnosticEvent, CancellationToken, ValueTask> write) : IDiagnosticSink
    {
        public ValueTask WriteAsync(DiagnosticEvent observation, CancellationToken cancellationToken) => write(observation, cancellationToken);
    }

    private sealed class InterceptingReader(IDiagnosticReader inner, Action onAcquired) : IDiagnosticReader
    {
        public bool TryAcquire(out DiagnosticEvent observation, out DiagnosticReadLease lease)
        {
            if (!inner.TryAcquire(out observation, out lease))
                return false;

            try
            {
                onAcquired();
                return true;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public DiagnosticEmergencySnapshot ReadEmergency() => inner.ReadEmergency();

        public void RecordSinkFailure(uint code, long timestamp) => inner.RecordSinkFailure(code, timestamp);
    }
}
