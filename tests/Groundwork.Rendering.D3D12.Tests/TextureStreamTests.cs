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
