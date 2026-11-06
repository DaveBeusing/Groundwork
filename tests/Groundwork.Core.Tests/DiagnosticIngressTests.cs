using System.Reflection;

namespace Groundwork.Core.Tests;

[TestClass]
public sealed class DiagnosticIngressTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

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

    private static FaultReportStatus Report(IFaultReporter reporter, WorldId world, FaultClass classification, uint code)
    {
        var observation = new FaultObservation(classification, FaultSeverity.Warning, FaultSubsystem.Ecs,
            FaultOrigin.Primary, code, 0, new FaultContext(world, null, null, null, null, null), [], []);
        return reporter.TryReport(observation);
    }
}
