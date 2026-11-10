namespace Groundwork.Core.Tests;

[TestClass]
public sealed class RootChangePermitTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RevocationAndSwapHaveOneAuthoritativeOutcome(bool revokeFirst)
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var candidates = await CoreRuntimeFixture.SealAsync(world, 1, BarrierKind.CommitResults);
        var old = world.Publication.Committed.Read().Root;
        var next = new CoreRuntimeFixture.Root(old.World, 83);
        var permit = world.Publication.RootChanges.TryReserve(candidates, next);
        Assert.IsNotNull(permit);
        Assert.IsNull(world.Publication.RootChanges.TryReserve(candidates, next));
        var prepared = world.Publication.Preparation.TryPrepare(new BarrierPublicationDraft(
            candidates, next, world.State.Read().PendingFaultGeneration, [], permit)).Publication;
        Assert.IsNotNull(prepared);
        var faults = world.State.Read().PendingRequiredEvidenceRecords;
        if (revokeFirst)
            permit.Revoke();
        var result = world.Publication.Finalization.TryFinalize(prepared);
        permit.Revoke();
        Assert.AreEqual(revokeFirst ? BarrierStatus.Unchanged : BarrierStatus.Committed, result.Status);
        Assert.AreSame(revokeFirst ? old : next, world.Publication.Committed.Read().Root);
        Assert.AreEqual(revokeFirst ? RootChangeStatus.Revoked : RootChangeStatus.Committed, permit.Status);
        Assert.AreEqual(revokeFirst ? null : result.Stamp, permit.CommittedStamp);
        Assert.AreEqual(faults, world.State.Read().PendingRequiredEvidenceRecords);
        Assert.AreEqual(BarrierStatus.Stopped, world.Publication.Finalization.TryFinalize(prepared).Status);
        Assert.AreEqual(result.Stamp, world.Publication.Committed.Read().Stamp);
    }

    [TestMethod]
    public async Task PermitCannotCrossBarriersOrAuthorizeAnotherRoot()
    {
        using var fixture = new CoreRuntimeFixture();
        var world = fixture.OpenWorld();
        var begin = await CoreRuntimeFixture.SealAsync(world, 1, BarrierKind.BeginTick);
        var old = world.Publication.Committed.Read().Root;
        var next = new CoreRuntimeFixture.Root(old.World, 83);
        Assert.IsNull(world.Publication.RootChanges.TryReserve(begin, next));
        var ordinary = CoreRuntimeFixture.Prepare(world, begin, []);
        world.Publication.Finalization.TryFinalize(ordinary);
        var candidates = await CoreRuntimeFixture.SealAsync(world, 1, BarrierKind.CommitResults);
        var permit = world.Publication.RootChanges.TryReserve(candidates, next);
        Assert.IsNotNull(permit);
        Assert.AreEqual(BarrierPreparationStatus.InvalidCandidate, world.Publication.Preparation.TryPrepare(
            new BarrierPublicationDraft(candidates, new CoreRuntimeFixture.Root(old.World, 84), 0, [], permit)).Status);
        var prepared = world.Publication.Preparation.TryPrepare(new BarrierPublicationDraft(candidates, next, 0, [], permit));
        Assert.IsNotNull(prepared.Publication);
        fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        Assert.AreEqual(BarrierStatus.Stopped, world.Publication.Finalization.TryFinalize(prepared.Publication).Status);
        Assert.AreEqual(RootChangeStatus.Revoked, permit.Status);
        Assert.AreSame(old, world.Publication.Committed.Read().Root);
    }
}
