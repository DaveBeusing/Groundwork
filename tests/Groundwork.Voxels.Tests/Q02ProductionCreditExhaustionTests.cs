using Groundwork.Core;
using Groundwork.Core.Tests;

namespace Groundwork.Voxels.Tests;

public sealed partial class VolumeEditTests
{
    // R09-approved WYU limits, with the production overlay, reader and journal capacities.
    private static readonly VolumeResources R09WorkCredits = new(
        50_388_576, 8_388_608, 8_388_608, 8_388_608, 1, 1, 1, 64, 1);
    private static readonly VolumeResources R09BaselineCredits = new(
        101_039_296, 16_777_216, 16_777_216, 16_777_216, 0, 0, 0, 144, 2);
    private static readonly VolumeResources R09TotalCredits = new(
        302_593_600, 50_331_648, 50_331_648, 50_331_648, 4, 4, 4, 400, 6);

    private static EditRun CreateR09CreditRun() => new(limit: 8192, qualificationLimits: new(
        R09TotalCredits, R09BaselineCredits, R09WorkCredits, R09WorkCredits, R09WorkCredits, R09WorkCredits,
        candidates: 12, readers: 12, journalRecords: 256));

    public enum FirstEditCredit { Cpu, Native, Upload, Retirement }

    [TestMethod]
    [DataRow(FirstEditCredit.Cpu, -1)]
    [DataRow(FirstEditCredit.Cpu, 0)]
    [DataRow(FirstEditCredit.Cpu, 1)]
    [DataRow(FirstEditCredit.Native, -1)]
    [DataRow(FirstEditCredit.Native, 0)]
    [DataRow(FirstEditCredit.Native, 1)]
    [DataRow(FirstEditCredit.Upload, -1)]
    [DataRow(FirstEditCredit.Upload, 0)]
    [DataRow(FirstEditCredit.Upload, 1)]
    [DataRow(FirstEditCredit.Retirement, -1)]
    [DataRow(FirstEditCredit.Retirement, 0)]
    [DataRow(FirstEditCredit.Retirement, 1)]
    public async Task Q02SealedFirstEditRespectsR09CreditBoundary(FirstEditCredit axis, int availableMinusRequired)
    {
        using var run = CreateR09CreditRun();
        await run.BuildInitial();
        using var visible = run.Coordinator.TryRead();
        Assert.IsNotNull(visible);
        var begin = await run.OpenTick();
        using var speculative = run.HoldSpeculativeCredits(R09WorkCredits);
        var intent = Brush(begin.Tick, 1, 1, VolumeBrushOperation.Remove);
        SealAndHold(run, intent);
        var original = run.World.Publication.Committed.Read();
        var before = run.Coordinator.Statistics;
        AssertCredits(R09WorkCredits + (R09WorkCredits.Retained with { LeaseSlots = 65 }), before.Used);
        Assert.AreEqual(2, before.Generations);
        Assert.AreEqual(1, before.Readers);
        Assert.IsFalse(speculative.IsCancellationRequested);

        var request = AdjustFirstEditRequest(axis, availableMinusRequired);
        var result = run.Stage(intent, out var work, request);
        using (work)
        {
            var expected = availableMinusRequired < 0 ? VolumeAdmission.Backpressure : VolumeAdmission.Accepted;
            Assert.AreEqual(expected, result);
            if (expected == VolumeAdmission.Backpressure)
            {
                Assert.IsNull(work);
                Assert.IsTrue(before == run.Coordinator.Statistics, "Denied admission changed the terrain ledger.");
                Assert.AreEqual(VolumeEditStatus.Pending, run.Status(intent));
                AssertOnlyCreditDenial(run);
                Assert.IsTrue(run.Coordinator.RejectEdit(intent.Tick, intent.Id));
            }
            else
            {
                Assert.IsNotNull(work);
                AssertCredits(before.Used + request, run.Coordinator.Statistics.Used);
                Assert.AreEqual(before.Generations + 1, run.Coordinator.Statistics.Generations);
                Assert.AreEqual(VolumeEditStatus.Building, run.Status(intent));
            }
            Assert.AreSame(original.Root, run.World.Publication.Committed.Read().Root);
            Assert.AreEqual(original.Stamp, run.World.Publication.Committed.Read().Stamp);
            Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);
            Assert.AreEqual(1UL, visible.Info.TerrainRevision);
        }

        Assert.AreEqual(VolumeEditStatus.Rejected, run.Status(intent));
        AssertCredits(before.Used, run.Coordinator.Statistics.Used);
        visible.Dispose();
        speculative.Dispose();
        AssertCreditRunSettles(run);
        Console.WriteLine($"FirstEdit class credits: axis={axis}, availableMinusRequired={availableMinusRequired}, result={result}; settled");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Q02FirstEditCannotPublishUntilRetainedR09BaselineCreditsReturn(bool releasePredecessor)
    {
        using var run = CreateR09CreditRun();
        await run.BuildInitial();
        using var predecessor = run.Coordinator.TryRead();
        Assert.IsNotNull(predecessor);
        run.Hold();
        await run.StreamAt(run.Center, 1);
        var begin = await run.OpenTick();
        using var visible = run.Coordinator.TryRead();
        Assert.IsNotNull(visible);
        Assert.AreNotEqual(predecessor.Info.Generation, visible.Info.Generation);
        using var speculative = run.HoldSpeculativeCredits(R09WorkCredits);
        var intent = Brush(begin.Tick, 1, 1, VolumeBrushOperation.Remove);
        SealAndHold(run, intent);
        Assert.AreEqual(VolumeAdmission.Accepted, run.Stage(intent, out var work));
        Assert.IsNotNull(work);
        using var edit = work;
        Assert.IsTrue(edit.Execute());
        await run.CommitUnchanged();

        var later = await run.Begin();
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, later.Tick, BarrierKind.CommitResults);
        var original = run.World.Publication.Committed.Read();
        var before = run.Coordinator.Statistics;
        var twoBaselineOwners = R09WorkCredits.Retained + (R09WorkCredits.Retained with { LeaseSlots = 66 });
        Assert.IsTrue(twoBaselineOwners.Fits(R09BaselineCredits));
        Assert.IsFalse(R09WorkCredits.Retained.Fits(R09BaselineCredits - twoBaselineOwners));
        AssertCredits(twoBaselineOwners + R09WorkCredits.Retained + R09WorkCredits, before.Used);
        Assert.AreEqual(4, before.Generations);
        Assert.AreEqual(2, before.Readers);
        Assert.IsFalse(speculative.IsCancellationRequested);
        DrainEvents(run);

        Assert.IsNull(run.Coordinator.TryPreparePublication(edit.Generation, candidates, []));
        Assert.IsTrue(before == run.Coordinator.Statistics, "Denied publication changed the terrain ledger.");
        Assert.AreEqual(VolumeEditStatus.Building, run.Status(intent));
        Assert.AreSame(original.Root, run.World.Publication.Committed.Read().Root);
        Assert.AreEqual(original.Stamp, run.World.Publication.Committed.Read().Stamp);
        Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);
        AssertOnlyCreditDenial(run);
        run.FinishUnchanged(candidates);

        if (releasePredecessor)
        {
            predecessor.Dispose();
            var returned = R09WorkCredits.Retained with { LeaseSlots = 65 };
            AssertCredits(before.Used - returned, run.Coordinator.Statistics.Used);
            Assert.AreEqual(before.Generations - 1, run.Coordinator.Statistics.Generations);
            var retry = await run.Begin();
            Assert.AreEqual(later.Tick + 1, retry.Tick);
            await run.Commit(edit, edit: false);
            Assert.AreEqual(VolumeEditStatus.Committed, run.Status(intent));
            var published = (VolumeWorldRoot)run.World.Publication.Committed.Read().Root;
            Assert.AreEqual(2UL, published.Terrain.TerrainRevision);
            Assert.AreEqual(intent, published.CommittedEdit);
            Assert.AreEqual(1UL, visible.Info.TerrainRevision);
            Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);

            var consumed = await run.OpenTick();
            Assert.AreEqual(retry.Tick + 1, consumed.Tick);
            Assert.IsFalse(run.Coordinator.Statistics.MovementHeld);
            await run.CommitUnchanged();
            Console.WriteLine($"Baseline credits: denied at {later.Tick}, returned={FormatCredits(returned)}, committed at {retry.Tick}, opened at {consumed.Tick}");
        }
        else
        {
            Assert.IsTrue(run.Coordinator.RejectEdit(intent.Tick, intent.Id));
            Assert.AreEqual(VolumeEditStatus.Rejected, run.Status(intent));
            AssertCredits(before.Used - R09WorkCredits.Retained, run.Coordinator.Statistics.Used);
            Assert.AreEqual(before.Generations - 1, run.Coordinator.Statistics.Generations);
            Assert.AreSame(original.Root, run.World.Publication.Committed.Read().Root);
            Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);
            predecessor.Dispose();
            Console.WriteLine($"Baseline credits: denied at {later.Tick}, edit rejected and original root retained");
        }

        visible.Dispose();
        speculative.Dispose();
        AssertCreditRunSettles(run);
    }

    private static VolumeResources AdjustFirstEditRequest(FirstEditCredit axis, int availableMinusRequired) => axis switch
    {
        FirstEditCredit.Cpu => R09WorkCredits with { CpuBytes = R09WorkCredits.CpuBytes - availableMinusRequired },
        FirstEditCredit.Native => R09WorkCredits with { NativeBytes = R09WorkCredits.NativeBytes - availableMinusRequired },
        FirstEditCredit.Upload => R09WorkCredits with { UploadBytes = R09WorkCredits.UploadBytes - availableMinusRequired },
        FirstEditCredit.Retirement => R09WorkCredits with { RetirementBytes = R09WorkCredits.RetirementBytes - availableMinusRequired },
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    private static void SealAndHold(EditRun run, VolumeBrushIntent intent)
    {
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(intent.Tick, run.Source.Descriptor, [intent]));
        run.Hold();
        DrainEvents(run);
    }

    private static void AssertOnlyCreditDenial(EditRun run)
    {
        Assert.IsTrue(run.Coordinator.TryReadEvent(out var entry));
        Assert.AreEqual(VolumeEventKind.Backpressure, entry.Kind);
        Assert.AreEqual(0UL, entry.Generation);
        Assert.IsFalse(run.Coordinator.TryReadEvent(out _));
    }

    private static void DrainEvents(EditRun run)
    {
        while (run.Coordinator.TryReadEvent(out _))
        {
        }
    }

    private static void AssertCreditRunSettles(EditRun run)
    {
        run.Coordinator.Stop();
        var settled = run.Coordinator.Statistics;
        AssertCredits(default, settled.Used);
        Assert.AreEqual(0, settled.Readers);
        Assert.AreEqual(0, settled.Generations);
        Assert.IsTrue(settled.Stopped);
        Assert.IsTrue(settled.Peak.Fits(R09TotalCredits));
    }

    private static void AssertCredits(VolumeResources expected, VolumeResources actual) =>
        Assert.IsTrue(expected == actual, $"Expected {FormatCredits(expected)}, actual {FormatCredits(actual)}");

    private static string FormatCredits(VolumeResources value) =>
        $"cpu={value.CpuBytes}, native={value.NativeBytes}, upload={value.UploadBytes}, retirement={value.RetirementBytes}, " +
        $"jobs={value.DensityJobs}/{value.MeshJobs}/{value.QueueSlots}, leases={value.LeaseSlots}, retirements={value.RetirementSlots}";
}
