using System.Reflection;

namespace Groundwork.Core.Tests;

[TestClass]
public sealed class DiagnosticIngressTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectDiagnosticLeaseRetainsScopeThroughCustody(bool worldScoped)
    {
        var quota = CoreRuntimeFixture.Quota with { Diagnostics = CoreRuntimeFixture.Quota.Diagnostics with
        {
            NormalRecords = 2,
        } };
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, quota));
        using var reservation = budget.TryReserveRuntime(quota);
        Assert.IsNotNull(reservation);
        var shutdown = TimeSpan.FromMilliseconds(100);
        var runtime = RuntimeSafetyAuthority.Create(TimeProvider.System,
            new RuntimeTimingLimits(shutdown, Timeout, Timeout), reservation, 1);
        var reader = runtime.Diagnostics;
        var reporter = runtime.Reporter;
        WorldId? world = null;
        if (worldScoped)
        {
            Assert.IsTrue(runtime.TryAllocateWorldId(out var id));
            world = id;
            var worldQuota = quota with { Diagnostics = quota.Diagnostics with
            {
                NormalRecords = 1, NormalBytes = 8192, EmergencyRecords = 9, EmergencyBytes = 28672,
            } };
            var scope = runtime.OpenWorld(id, worldQuota, new StopPolicy(), new CoreRuntimeFixture.Root(id), new ScopePolicy());
            reader = scope.Diagnostics;
            reporter = scope.Reporter;
        }

        Assert.AreEqual(FaultReportStatus.Recorded, Report(reporter, world, FaultClass.DiagnosticOnly, 1));
        Assert.IsTrue(reader.TryAcquire(out var observation, out var lease));
        using (lease)
        {
            Assert.AreEqual(1U, observation.Code);
            if (world is { } id)
                Report(reporter, id, FaultClass.UnsafePartialCommit, 2);
            else
                runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);

            using var watchdog = new CancellationTokenSource(Timeout);
            ShutdownSettlement settlement;
            do
            {
                settlement = world is { } current ? runtime.SettleWorld(current) : runtime.SettleStop();
                Assert.AreNotEqual(ShutdownSettlement.Released, settlement);
                if (settlement != ShutdownSettlement.Custody)
                    await Task.Delay(1, watchdog.Token);
            } while (settlement != ShutdownSettlement.Custody);

            Assert.IsFalse(reader.TryAcquire(out _, out _));
            Assert.IsNull(budget.TryReserveRuntime(quota));
            if (worldScoped)
                Assert.IsFalse(runtime.TryAllocateWorldId(out _));
        }

        // Reclamation is driven by release, with no additional settlement call.
        using var releaseWatchdog = new CancellationTokenSource(Timeout);
        if (worldScoped)
        {
            WorldId next;
            while (!runtime.TryAllocateWorldId(out next))
                await Task.Delay(1, releaseWatchdog.Token);
            Assert.IsTrue(runtime.TryAbandonWorldId(next));
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            while (runtime.SettleStop() != ShutdownSettlement.Released)
                await Task.Delay(1, releaseWatchdog.Token);
        }
        else
        {
            ProcessRuntimeReservation? next;
            while ((next = budget.TryReserveRuntime(quota)) is null)
                await Task.Delay(1, releaseWatchdog.Token);
            next.Dispose();
        }
    }

    [TestMethod]
    public async Task OptionalDeliveryContentionDoesNotFenceRequiredEvidence()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var id = world.Publication.Committed.Read().Stamp.World;
        Assert.AreEqual(FaultReportStatus.Recorded, Report(world.Reporter, id, FaultClass.DiagnosticOnly, 1));

        // Hold the actual optional queue lock to make the producer and reader race deterministic.
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var ledger = world.Reporter.GetType().GetFields(fields).Single(field => field.FieldType.Name == "FaultLedger")
            .GetValue(world.Reporter)!;
        var delivery = ledger.GetType().GetField("_normal", fields)!.GetValue(ledger)!;
        var gate = delivery.GetType().GetProperty("Gate", fields)!.GetValue(delivery)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = Task.Run(() =>
        {
            lock (gate)
            {
                entered.SetResult();
                release.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
            }
        });

        try
        {
            await entered.Task.WaitAsync(Timeout);
            await Task.Run(() =>
            {
                Assert.AreEqual(FaultReportStatus.Coalesced, Report(world.Reporter, id, FaultClass.DiagnosticOnly, 2));
                Assert.AreEqual(FaultReportStatus.Recorded, Report(world.Reporter, id, FaultClass.StaleResult, 3));
                Assert.AreEqual(FaultReportStatus.Recorded, Report(world.Reporter, id, FaultClass.WriteConflict, 4));
                world.Diagnostics.RecordSinkFailure(5, 0);
                var emergency = world.Diagnostics.ReadEmergency();
                Assert.AreEqual(3U, emergency.Primary?.Code);
                Assert.AreEqual(5U, emergency.Sink?.Code);
                var safety = world.State.Read();
                Assert.AreEqual(2, safety.PendingRequiredEvidenceRecords);
                Assert.AreEqual(2UL, safety.DeliveryLossCount);
                Assert.AreEqual(SafetyCondition.None, safety.Conditions);
            }).WaitAsync(Timeout);
        }
        finally
        {
            release.TrySetResult();
            await held.WaitAsync(Timeout);
        }

        Assert.IsTrue(world.Diagnostics.TryAcquire(out var observation, out var lease));
        using (lease)
            Assert.AreEqual(1U, observation.Code);
    }

    private static FaultReportStatus Report(IFaultReporter reporter, WorldId? world, FaultClass classification, uint code)
    {
        var observation = new FaultObservation(classification, FaultSeverity.Warning, FaultSubsystem.Ecs,
            FaultOrigin.Primary, code, 0, new FaultContext(world, null, null, null, null, null), [], []);
        return reporter.TryReport(observation);
    }

    private sealed class StopPolicy : IBarrierSupervisorPolicy
    {
        public ValueTask<SupervisorAction> DecideAsync(BarrierReviewRequest request, IFaultEvidenceReader evidence,
            CancellationToken cancellationToken) => ValueTask.FromResult(SupervisorAction.StopWorld);
    }

    private sealed class ScopePolicy : IJobScopePolicy
    {
        public JobScopeAdmission ValidateAdmission(JobSubmission submission, in CommittedWorldView world) =>
            new(JobScopeStatus.InvalidAccess, null);
        public bool CanDispatch(IAdmittedJobScope scope, in CommittedWorldView world) => false;
        public JobScopeOverlap EvaluateOverlap(IAdmittedJobScope first, IAdmittedJobScope second, in CommittedWorldView world) =>
            JobScopeOverlap.Unknown;
    }
}
