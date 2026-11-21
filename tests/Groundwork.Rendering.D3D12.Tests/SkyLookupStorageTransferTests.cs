using System.Numerics;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public void WarpSkyLookupTransfersOnlySettledStorage() => VerifySkyLookupStorageTransfer(warp: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void NvidiaSkyLookupTransfersOnlySettledStorage() => VerifySkyLookupStorageTransfer(warp: false);

    private static void VerifySkyLookupStorageTransfer(bool warp)
    {
        using var session = new D3D12DeviceSession(QualificationDiagnostics, useWarp: warp);
        var frame = new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() };
        var initialLedger = new ResourceDiagnostics();
        var hideRetirement = false;
        using var first = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, session: session,
            resourceDiagnostics: initialLedger, skyLookupPrototype: true, skyLookupResourceReuse: true,
            retirementHooks: new(CompletedValue: (_, actual) => hideRetirement ? 0 : actual));
        AssertPlanetAdapter(first.Adapter, warp);
        var original = SkyLookupPixels(GpuDevice.RenderOffscreen(first, 64, 64, frame), 64, 64);
        var originalStorage = first.SkyLookupStorageHandlesForTests;
        hideRetirement = true;
        try
        {
            _ = Assert.Throws<InvalidOperationException>(() => first.DisposeAfterHealthyStop());
            Assert.AreEqual(0UL, session.ParkedSkyLookupBytes);
            Assert.AreEqual(originalStorage, first.SkyLookupStorageHandlesForTests);
        }
        finally { hideRetirement = false; }
        var firstStamp = first.DisposeAfterHealthyStop();
        Assert.AreNotEqual(0UL, firstStamp);
        Assert.AreEqual(GpuDevice.SkyLookupStorageLimit, session.ParkedSkyLookupBytes);
        Assert.IsTrue(initialLedger.TryRead(out var firstSnapshot));
        var transferred = firstSnapshot.GetBalance(D3D12ResourceOwner.Texture);
        Assert.AreEqual(0UL, transferred.LiveBytes);
        Assert.AreEqual(0UL, transferred.RetainedBytes);
        Assert.AreEqual(0UL, transferred.ReleasedBytes, "A custody transfer is not a native release.");
        AssertSkyLookupSharedOwnerDiagnostics(first);
        session.ConfirmParkedSkyLookup(firstStamp);

        var successorLedger = new ResourceDiagnostics();
        var hits = 0;
        using var successor = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, session: session,
            resourceDiagnostics: successorLedger, skyLookupPrototype: true, skyLookupResourceReuse: true,
            retirementHooks: new(AfterSkyLookupFirstAllocation: () => Assert.Fail("A retire-safe successor recreated the texture."),
                AfterSkyLookupPreparationStage: (stage, _, _, reused) =>
                {
                    if (stage != "resources-reused") return;
                    Assert.IsTrue(reused);
                    hits++;
                }));
        AssertPlanetAdapter(successor.Adapter, warp);
        var nightFrame = frame with { Sky = frame.Sky!.Value with { SunRadiance = 0 } };
        var night = SkyLookupPixels(GpuDevice.RenderOffscreen(successor, 64, 64, nightFrame), 64, 64);
        Assert.IsFalse(original.SequenceEqual(night), "Reused storage was sampled without regenerating the new owner's LUT.");
        Assert.AreEqual(1, hits);
        Assert.AreEqual(0UL, session.ParkedSkyLookupBytes);
        Assert.AreEqual(originalStorage, successor.SkyLookupStorageHandlesForTests);
        Assert.IsTrue(successorLedger.TryRead(out var secondSnapshot));
        var active = secondSnapshot.GetBalance(D3D12ResourceOwner.Texture);
        Assert.AreEqual(GpuDevice.SkyLookupStorageLimit, active.LiveBytes + active.RetainedBytes);
        var restored = SkyLookupPixels(GpuDevice.RenderOffscreen(successor, 64, 64, frame with { Sequence = 2 }), 64, 64);
        CollectionAssert.AreEqual(original, restored);
        Assert.AreEqual(2UL, successor.SkyLookupDispatchesForTests);
        var successorStamp = successor.DisposeAfterHealthyStop();
        Assert.AreNotEqual(firstStamp, successorStamp);
        session.DiscardParkedSkyLookup(firstStamp);
        Assert.AreEqual(GpuDevice.SkyLookupStorageLimit, session.ParkedSkyLookupBytes,
            "A late cleanup error must not discard a subsequent owner's parked storage.");
        AssertSkyLookupSharedOwnerDiagnostics(successor);

        using var direct = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, session: session,
            skyLookupPrototype: false, skyLookupResourceReuse: true);
        Assert.IsFalse(direct.SkyLookupResourceReuseEnabledForTests);
        Assert.AreEqual(0UL, session.ParkedSkyLookupBytes, "Direct attach must drop an unused previous lookup allocation.");
        session.Dispose();
        direct.Dispose();
        AssertCleanDiagnostics(new(direct.Adapter, direct.DebugLayerEnabled, direct.DredEnabled, direct.FinalDebugSummary, null), true);
    }

    [TestMethod]
    public void WarpSkyLookupStorageTransferDropsFailedAttachmentAndUnclaimedCustody()
    {
        using var session = new D3D12DeviceSession(QualificationDiagnostics, useWarp: true);
        var frame = new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() };
        ulong Park()
        {
            using var gpu = new GpuDevice(true, session: session, skyLookupPrototype: true, skyLookupResourceReuse: true);
            _ = GpuDevice.RenderOffscreen(gpu, 64, 64, frame);
            var stamp = gpu.DisposeAfterHealthyStop();
            Assert.AreNotEqual(0UL, stamp);
            Assert.AreEqual(GpuDevice.SkyLookupStorageLimit, session.ParkedSkyLookupBytes);
            return stamp;
        }

        var unconfirmed = Park();
        using (var earlyAttach = new GpuDevice(true, session: session, skyLookupPrototype: true, skyLookupResourceReuse: true))
        {
            session.ConfirmParkedSkyLookup(unconfirmed);
            Assert.AreEqual(0UL, session.ParkedSkyLookupBytes, "Early attach must drop unconfirmed storage.");
        }

        session.ConfirmParkedSkyLookup(Park());
        _ = Assert.Throws<InvalidOperationException>(() => new GpuDevice(true, session: session, skyLookupPrototype: true,
            skyLookupResourceReuse: true, retirementHooks: new(AfterDeviceInitialize: _ =>
                throw new InvalidOperationException("Rejected successor initialization."))));
        Assert.AreEqual(0UL, session.ParkedSkyLookupBytes);

        session.ConfirmParkedSkyLookup(Park());
        using (var aborted = new GpuDevice(true, session: session, skyLookupPrototype: true, skyLookupResourceReuse: true))
        {
            Assert.AreEqual(GpuDevice.SkyLookupStorageLimit, session.ParkedSkyLookupBytes);
            aborted.Dispose();
            Assert.AreEqual(0UL, session.ParkedSkyLookupBytes);
        }

        Park();
        session.Dispose();
        Assert.AreEqual(0UL, session.ParkedSkyLookupBytes);
    }
}
