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

            Assert.IsTrue(world.Diagnostics.TryRead(out var later));
            Assert.AreEqual(2U, later.Code);
            Assert.IsFalse(world.Diagnostics.TryRead(out _));
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
            Assert.AreEqual(FaultReportStatus.Coalesced, Report(runtime.Reporter, 3, FaultClass.DiagnosticOnly, (FaultOrigin)99));
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
}
