using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class TextureStreamTests
{
    private static readonly D3D12TextureReservation Credit = new(1024, 512, 4096, 65_536, 65_536);
    private static readonly D3D12TextureUsage ResidentCredit = new(0, 0, 0, 1, 0, 0, 0, 0, 65_536);
    private static readonly D3D12TextureUsage WorkerCredit = new(1, 1, 1, 1, 1024, 512, 4096, 65_536, 65_536);
    private static readonly D3D12TextureMipPolicy MipPolicy = new(FineMip: 0, CoarseMip: 2, PromoteWithin: 10, DemoteBeyond: 20);

    [TestMethod]
    public void TierRequestsKeepHysteresisAndServiceFirstUseAheadOfOlderRefinements()
    {
        var view = View();
        var stream = Stream(descriptors: 4);
        var texture = Cook(TextureSemantic.Color);
        var first = Target(1, MaterialTextureSemantic.BaseColor, texture);
        var second = Target(2, MaterialTextureSemantic.BaseColor, texture);
        var third = Target(3, MaterialTextureSemantic.BaseColor, texture);
        Assert.IsTrue(stream.Publish(view, 1, [first, second, third]));
        Assert.AreEqual(D3D12TextureResult.Accepted, Demand(first, 30, out var coarse));
        Ready(coarse);
        Assert.AreEqual(2, Take(stream, view).RequestedMip);
        Install(stream, view, coarse);
        Assert.AreEqual(D3D12TextureResult.Coalesced, Demand(first, 15, out _));
        Assert.AreEqual(D3D12TextureResult.Accepted, Demand(first, 5, out var refine));
        Assert.AreEqual(D3D12TextureResult.Coalesced, Demand(first, 15, out _));
        Ready(refine);
        Assert.IsFalse(stream.HasReady(view, firstUse: true));
        Assert.IsTrue(stream.HasReady(view, firstUse: false));

        Assert.AreEqual(D3D12TextureResult.Accepted, Demand(second, 1, out var secondFirst));
        Assert.AreEqual(D3D12TextureResult.Accepted, Demand(third, 1, out var thirdFirst));
        Ready(thirdFirst);
        Ready(secondFirst);
        Assert.AreEqual(secondFirst, Take(stream, view).Ticket);
        Install(stream, view, secondFirst);
        Assert.IsTrue(stream.TryTake(view, firstUse: true, out var transfer));
        Assert.AreEqual(thirdFirst, transfer.Ticket);
        Install(stream, view, thirdFirst);

        Assert.IsFalse(stream.TryTake(view, firstUse: true, out _));
        Assert.IsTrue(stream.TryTake(view, firstUse: false, out transfer));
        Assert.AreEqual(refine, transfer.Ticket);
        Assert.AreEqual(0, transfer.RequestedMip);
        Assert.IsTrue(stream.MarkCopySubmitted(refine, new(8, 8, 32_768, 32_768, 0)));
        Assert.IsTrue(stream.TryInstall(refine, view, out var predecessor));
        Assert.AreEqual(coarse, predecessor);
        Assert.AreEqual(3 * Credit.NativeBytes + 32_768, stream.ReadUsage().NativeBytes);
        Assert.IsTrue(stream.Release(predecessor));
        Assert.IsTrue(stream.TryReadResidency(view, first, out var residency));
        Assert.AreEqual(new D3D12TextureResidency(0, 0, 32_768), residency);
        Assert.AreEqual(0L, stream.ReadUsage().StagingBytes);

        D3D12TextureResult Demand(D3D12TextureTarget target, double distance, out D3D12TextureTicket ticket) =>
            stream.TryRequestTier(view, target, MipPolicy, distance, Credit, Credit, out ticket);
        void Ready(D3D12TextureTicket ticket) =>
            Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, texture));
    }

    [TestMethod]
    public void DemotionOverCapUsesFallbackAndRetriesOnlyWithSettledCreditOrNewDemand()
    {
        var view = View();
        var fine = Credit with { NativeBytes = 131_072 };
        var stream = new D3D12TextureHandoff(new(1, 2, 2048, 1024, 8192, 131_072, fine.NativeBytes, 2));
        var texture = Cook(TextureSemantic.Color);
        var target = Target(1, MaterialTextureSemantic.BaseColor, texture);
        Assert.IsTrue(stream.Publish(view, 1, [target]));
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(view, target, fine, out var predecessor));
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(predecessor, texture));
        Assert.AreEqual(predecessor, Take(stream, view).Ticket);
        Install(stream, view, predecessor);

        var wakes = 0;
        stream.BindOwner(view.World, () => wakes++);
        Assert.AreEqual(D3D12TextureResult.Quota, Demand(21, out _));
        Assert.AreEqual(1, wakes);
        Assert.IsTrue(stream.TryReadResidency(view, target, out var fallback));
        Assert.AreEqual(new D3D12TextureResidency(2, null, 0), fallback);
        Assert.AreEqual(fine.NativeBytes, stream.ReadUsage().NativeBytes);
        Assert.AreEqual(D3D12TextureResult.Quota, Demand(21, out _));
        Assert.AreEqual(1, wakes);
        Assert.IsTrue(stream.Release(predecessor));
        Assert.AreEqual(D3D12TextureResult.Accepted, Demand(21, out var missing));
        Assert.IsTrue(stream.CompleteWorker(missing));
        Assert.AreEqual(D3D12TextureResult.Unavailable, Demand(21, out _));
        Assert.AreEqual(D3D12TextureResult.Unavailable, Demand(15, out _));
        Assert.AreEqual(default, stream.ReadUsage());

        Assert.AreEqual(D3D12TextureResult.Accepted, Demand(5, out var coarse));
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(coarse, texture));
        Assert.AreEqual(2, Take(stream, view).RequestedMip);
        Install(stream, view, coarse);
        Assert.AreEqual(D3D12TextureResult.Quota, Demand(5, out _));
        Assert.IsTrue(stream.TryReadResidency(view, target, out var retained));
        Assert.AreEqual(new D3D12TextureResidency(0, 2, Credit.NativeBytes), retained);
        Assert.AreEqual(Credit.NativeBytes, stream.ReadUsage().NativeBytes);

        D3D12TextureResult Demand(double distance, out D3D12TextureTicket ticket) =>
            stream.TryRequestTier(view, target, MipPolicy, distance, fine, Credit, out ticket);
    }

    [TestMethod]
    public void RapidTierChangesKeepAnObsoleteCopyChargedAcrossRemovalAndEpochChange()
    {
        var view = View();
        var stream = Stream();
        var texture = Cook(TextureSemantic.Color);
        var target = Target(1, MaterialTextureSemantic.BaseColor, texture);
        Assert.IsTrue(stream.Publish(view, 1, [target]));
        Assert.AreEqual(D3D12TextureResult.Accepted, Demand(view, 30, out var predecessor));
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(predecessor, texture));
        Take(stream, view);
        Install(stream, view, predecessor);
        Assert.AreEqual(D3D12TextureResult.Accepted, Demand(view, 5, out var pending));
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(pending, texture));
        Assert.AreEqual(pending, Take(stream, view).Ticket);
        Assert.IsTrue(stream.MarkCopySubmitted(pending));

        Assert.AreEqual(D3D12TextureResult.Coalesced, Demand(view, 25, out _));
        Assert.AreEqual(D3D12TextureResult.Coalesced, Demand(view, 15, out _));
        Assert.IsFalse(stream.TryInstall(pending, view, out _));
        Assert.IsTrue(stream.TryGetResident(view, target.Slot, out var drawable));
        Assert.AreEqual(predecessor, drawable);
        var retained = stream.ReadUsage();
        Assert.AreEqual(Credit.StagingBytes, retained.StagingBytes);
        Assert.AreEqual(2 * Credit.NativeBytes, retained.NativeBytes);

        Assert.IsTrue(stream.Publish(view, 2, []));
        var next = view with { View = view.View with { RendererEpoch = 2 } };
        Assert.IsTrue(stream.Publish(next, 1, [target]));
        Assert.AreEqual(retained, stream.ReadUsage());
        Assert.IsFalse(stream.TryGetResident(next, target.Slot, out _));
        Assert.IsFalse(stream.TryInstall(pending, next, out _));
        Assert.IsTrue(stream.Release(pending));
        Assert.IsTrue(stream.Release(predecessor));
        Assert.AreEqual(default, stream.ReadUsage());
        Assert.AreEqual(D3D12TextureResult.Accepted, Demand(next, 5, out var fresh));
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(fresh, texture));
        Assert.AreEqual(2, Take(stream, next).RequestedMip);

        D3D12TextureResult Demand(D3D12TextureView cut, double distance, out D3D12TextureTicket ticket) =>
            stream.TryRequestTier(cut, target, MipPolicy, distance, Credit, Credit, out ticket);
    }

    [TestMethod]
    public void IndependentTargetsCompleteInReverseAcrossAFullPaletteOnlyChange()
    {
        var view = View();
        var stream = Stream();
        var texture = Cook(TextureSemantic.Color);
        var first = Target(2, MaterialTextureSemantic.BaseColor, texture);
        var second = Target(1, MaterialTextureSemantic.BaseColor, texture);
        Assert.IsTrue(stream.Publish(view, 1, [first, second]));
        var a = Request(stream, view, first);
        var b = Request(stream, view, second);
        Assert.AreEqual(D3D12TextureResult.Coalesced, stream.TryRequest(view, first, Credit, out var duplicate));
        Assert.AreEqual(default, duplicate);

        var wakes = 0;
        stream.BindOwner(view.World, () =>
        {
            Assert.IsFalse(stream.Gate.IsHeldByCurrentThread);
            wakes++;
        });
        Assert.IsTrue(stream.Publish(view, 2, [second, first]));
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(b, texture));
        Assert.AreEqual(b, Take(stream, view).Ticket);
        Install(stream, view, b);
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(a, texture));
        Assert.AreEqual(a, Take(stream, view).Ticket);
        Install(stream, view, a);
        Assert.AreEqual(3, wakes);
        Assert.IsTrue(stream.TryGetResident(view, first.Slot, out var firstResident));
        Assert.AreEqual(a, firstResident);
        Assert.IsTrue(stream.TryGetDrawableResident(view, out var drawable));
        Assert.AreEqual(b, drawable);
        Assert.AreEqual(2 * Credit.NativeBytes, stream.ReadUsage().NativeBytes);
        Assert.AreEqual(0L, stream.ReadUsage().StagingBytes);
        Assert.AreEqual(2, stream.ReadPeakUsage().Workers);
    }

    [TestMethod]
    public void ExactIdLinearDataDoesNotBindAsNormal()
    {
        var view = View();
        var stream = Stream();
        var texture = Cook(TextureSemantic.Data);
        var normalTarget = Target(1, MaterialTextureSemantic.Normal, texture);
        Assert.IsTrue(stream.Publish(view, 1, [normalTarget]));
        Assert.IsTrue(normalTarget.Reference.MatchesBinding(texture.AssetId, MaterialTextureColorSpace.Linear),
            "The ID and color space match. Only the cooked semantic distinguishes this binding.");
        var ticket = Request(stream, view, normalTarget);
        Assert.AreEqual(D3D12TextureResult.Invalid, stream.TryComplete(ticket, texture));
        Assert.IsFalse(stream.HasReady(view));
        Assert.AreEqual(default, stream.ReadUsage());

        var normal = Cook(TextureSemantic.Normal);
        var next = Target(1, MaterialTextureSemantic.Normal, normal) with { Sequence = 2 };
        Assert.IsTrue(stream.Publish(view, 2, [next]));
        var valid = Request(stream, view, next);
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(valid, normal));
        Take(stream, view);
        Install(stream, view, valid);
        Assert.IsTrue(stream.TryGetResident(view, next.Slot, out _));
        Assert.IsFalse(stream.TryGetDrawableResident(view, out _));
    }

    [TestMethod]
    public void SupersededWorkerAndResidentPredecessorStayChargedUntilTheirOwnSettlement()
    {
        var view = View();
        var stream = Stream(descriptors: 2);
        var texture = Cook(TextureSemantic.Color);
        var original = Target(1, MaterialTextureSemantic.BaseColor, texture);
        Assert.IsTrue(stream.Publish(view, 1, [original]));
        var predecessor = Request(stream, view, original);
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(predecessor, texture));
        Take(stream, view);
        Install(stream, view, predecessor);

        var older = original with { Sequence = 2 };
        Assert.IsTrue(stream.Publish(view, 2, [older]));
        var draining = Request(stream, view, older);
        var current = original with { Sequence = 3 };
        Assert.IsTrue(stream.Publish(view, 3, [current]));
        Assert.IsFalse(stream.TryGetDrawableResident(view, out _));
        Assert.IsFalse(stream.IsCurrent(draining, view));
        Assert.AreEqual(new D3D12TextureUsage(1, 1, 1, 2, 1024, 512, 4096, 65_536, 131_072), stream.ReadUsage());
        Assert.AreEqual(D3D12TextureResult.Quota, stream.TryRequest(view, current, Credit, out _));

        Assert.AreEqual(D3D12TextureResult.Stale, stream.TryComplete(draining, texture));
        Assert.IsFalse(stream.CompleteWorker(draining));
        Assert.AreEqual(ResidentCredit, stream.ReadUsage());

        var replacement = Request(stream, view, current);
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(replacement, texture));
        Take(stream, view);
        Assert.IsTrue(stream.MarkCopySubmitted(replacement));
        Assert.AreEqual(new D3D12TextureUsage(1, 0, 0, 2, 0, 0, 0, 65_536, 131_072), stream.ReadUsage());
        Assert.IsTrue(stream.TryInstall(replacement, view, out var retired));
        Assert.AreEqual(predecessor, retired);
        Assert.AreEqual(2, stream.ReadUsage().Descriptors);
        Assert.AreEqual(131_072L, stream.ReadUsage().NativeBytes);
        Assert.IsTrue(stream.Release(retired));
        Assert.IsFalse(stream.Release(retired));
        Assert.AreEqual(ResidentCredit, stream.ReadUsage());
    }

    [TestMethod]
    public void OwnerCanReturnUnexecutedWorkWithoutAProducerWakeOrCreditGap()
    {
        var view = View();
        var stream = Stream();
        var texture = Cook(TextureSemantic.Color);
        var target = Target(1, MaterialTextureSemantic.BaseColor, texture);
        Assert.IsTrue(stream.Publish(view, 1, [target]));
        var wakes = 0;
        stream.BindOwner(view.World, () => wakes++);
        var ticket = Request(stream, view, target);
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, texture));
        var ready = stream.ReadUsage();
        var taken = Take(stream, view);
        Assert.AreSame(texture, taken.Texture);
        Assert.AreEqual(ready, stream.ReadUsage());
        Assert.IsFalse(stream.HasReady(view));
        Assert.IsTrue(stream.TryReturnToReady(ticket, view));
        Assert.AreEqual(ready, stream.ReadUsage());
        Assert.AreEqual(1, wakes);
        Assert.IsTrue(stream.HasReady(view));
        Take(stream, view);
        Assert.IsTrue(stream.Cancel(ticket));
        Assert.IsFalse(stream.TryReturnToReady(ticket, view));
        Assert.AreEqual(ready, stream.ReadUsage());
        Assert.IsTrue(stream.Release(ticket));
        Assert.AreEqual(default, stream.ReadUsage());
    }

    [TestMethod]
    public void SupersededTakenEntryCannotHideTheNextReadyHandoff()
    {
        var view = View();
        var stream = Stream();
        var texture = Cook(TextureSemantic.Color);
        var first = Target(1, MaterialTextureSemantic.BaseColor, texture);
        var later = Target(2, MaterialTextureSemantic.BaseColor, texture);
        Assert.IsTrue(stream.Publish(view, 1, [first, later]));
        var a = Request(stream, view, first);
        var b = Request(stream, view, later);
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(a, texture));
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(b, texture));
        Assert.AreEqual(a, Take(stream, view).Ticket);
        Assert.IsTrue(stream.Publish(view, 2, [first with { Sequence = 2 }, later]));
        Assert.IsFalse(stream.IsCurrent(a, view));
        Assert.IsTrue(stream.Release(a));

        Assert.IsTrue(stream.HasReady(view), "The current successor must re-arm the owner even with no submitted copy.");
        Assert.AreEqual(b, Take(stream, view).Ticket);
        Assert.IsFalse(stream.HasReady(view), "A taken handoff cannot keep the idle owner waking.");
        Assert.IsTrue(stream.Release(b));
    }

    [TestMethod]
    public void CloseRevokesLookupButKeepsWorkersAndCopyCredit()
    {
        var view = View();
        var stream = Stream();
        var texture = Cook(TextureSemantic.Color);
        var first = Target(1, MaterialTextureSemantic.BaseColor, texture);
        var second = Target(2, MaterialTextureSemantic.BaseColor, texture);
        Assert.IsTrue(stream.Publish(view, 1, [first, second]));
        var copying = Request(stream, view, first);
        var worker = Request(stream, view, second);
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(copying, texture));
        Take(stream, view);
        Assert.IsTrue(stream.MarkCopySubmitted(copying));

        var pending = stream.ReadUsage();
        stream.Close();
        Assert.AreEqual(pending, stream.ReadUsage());
        Assert.IsFalse(stream.HasReady(view));
        Assert.IsFalse(stream.IsCurrent(copying, view));
        Assert.IsFalse(stream.TryInstall(copying, view, out _));
        Assert.IsFalse(stream.CompleteWorker(copying));
        Assert.IsTrue(stream.Release(copying));
        Assert.AreEqual(WorkerCredit, stream.ReadUsage());
        Assert.AreEqual(D3D12TextureResult.Closed, stream.TryComplete(worker, texture));
        Assert.AreEqual(default, stream.ReadUsage());
    }

    [TestMethod]
    public void TicketGenerationAndWorldPreventForeignOrRecycledCompletion()
    {
        var view = View();
        var texture = Cook(TextureSemantic.Color);
        var target = Target(1, MaterialTextureSemantic.BaseColor, texture);
        var stream = Stream();
        var other = Stream();
        Assert.IsTrue(stream.Publish(view, 1, [target]));
        Assert.ThrowsExactly<InvalidOperationException>(() => stream.BindOwner(Guid.NewGuid(), () => { }));
        Assert.IsTrue(other.Publish(view, 1, [target]));
        var old = Request(stream, view, target);
        var foreign = Request(other, view, target);
        Assert.AreEqual(old.Index, foreign.Index);
        Assert.AreEqual(old.Generation, foreign.Generation);
        Assert.AreEqual(D3D12TextureResult.Stale, stream.TryComplete(foreign, texture));
        Assert.AreEqual(WorkerCredit, stream.ReadUsage());
        Assert.IsTrue(stream.CompleteWorker(old));
        var replacement = Request(stream, view, target);
        Assert.AreEqual(old.Index, replacement.Index);
        Assert.AreNotEqual(old.Generation, replacement.Generation);
        Assert.AreEqual(D3D12TextureResult.Stale, stream.TryComplete(old, texture));
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(replacement, texture));
        Assert.IsTrue(stream.HasReady(view));
        Assert.IsFalse(stream.HasReady(view with { TerrainRevision = 2 }));
    }

    private static D3D12TextureHandoff Stream(int descriptors = 3) =>
        new(new(3, 3, 4096, 4096, 16_384, 262_144, 262_144, descriptors));

    private static D3D12TextureView View() => new(Guid.NewGuid(), new(1, 1, PlanetLocalFrame.Create(default)), 1, 1);

    private static CookedTexture Cook(TextureSemantic semantic)
    {
        var colorSpace = semantic == TextureSemantic.Color ? TextureColorSpace.Srgb : TextureColorSpace.Linear;
        Span<byte> blocks = stackalloc byte[8];
        blocks.Clear();
        Assert.AreEqual(TextureCookError.None, TextureCooker.CookBc1(blocks, 4, 4, new(colorSpace, semantic, 1),
            new(1024, 1024, 1024, 4, 1), out var texture));
        return texture!;
    }

    private static D3D12TextureTarget Target(ushort material, MaterialTextureSemantic semantic, CookedTexture texture) =>
        new(new(material, semantic), 1, new(semantic, texture.AssetId));

    private static D3D12TextureTicket Request(D3D12TextureHandoff stream, D3D12TextureView view, D3D12TextureTarget target)
    {
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(view, target, Credit, out var ticket));
        return ticket;
    }

    private static D3D12TextureTransfer Take(D3D12TextureHandoff stream, D3D12TextureView view)
    {
        Assert.IsTrue(stream.TryTake(view, out var transfer));
        return transfer;
    }

    private static void Install(D3D12TextureHandoff stream, D3D12TextureView view, D3D12TextureTicket ticket)
    {
        Assert.IsTrue(stream.MarkCopySubmitted(ticket));
        Assert.IsTrue(stream.TryInstall(ticket, view, out var predecessor));
        Assert.AreEqual(default, predecessor);
    }
}
