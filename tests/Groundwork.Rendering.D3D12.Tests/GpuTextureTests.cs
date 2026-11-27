using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Passes;
using Groundwork.Rendering.Textures;
using Groundwork.Voxels;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed unsafe class GpuTextureTests
{
    private static readonly TextureLimits Limits = new(4_000_000, 4_000_000, 4_000_000, 16_384, 16);
    private static readonly RenderScene Scene = new(Matrix4x4.CreateTranslation(0, 0, -3), Vector4.One,
        Matrix4x4.Identity, 1, 0.2f, 20);

    [TestMethod]
    public void TextureCopyQualificationDisposesAnUnsubmittedTicketAndRearms()
    {
        var qualification = new D3D12TextureCopyQualification();
        var ticket = new D3D12TextureTicket(1, 0, 1);
        Assert.ThrowsExactly<ArgumentException>(() => qualification.Arm(default));
        using var hold = qualification.Arm(ticket);
        Assert.AreSame(hold, qualification.Find(ticket));
        Assert.IsNull(qualification.Find(new(2, 0, 1)));
        Assert.ThrowsExactly<InvalidOperationException>(() => qualification.Arm(ticket));
        Assert.ThrowsExactly<InvalidOperationException>(() => hold.Release());
        hold.Dispose();
        Assert.IsTrue(hold.Submitted.IsCanceled);
        using var next = qualification.Arm(new(1, 0, 2));
        Assert.IsFalse(next.Submitted.IsCompleted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WarpTextureCopyQualificationWaitsOnTheExactAuthorizedNativeCopy(bool watchdog) =>
        VerifyTextureCopyQualification(warp: true, watchdog);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void HardwareTextureCopyQualificationWaitsOnTheExactAuthorizedNativeCopy() =>
        VerifyTextureCopyQualification(warp: false, watchdog: false);

    private static void VerifyTextureCopyQualification(bool warp, bool watchdog)
    {
        var diagnostics = new ResourceDiagnostics();
        using var gpu = new GpuDevice(warp: warp, enableDebugLayer: true, enableDred: true,
            resourceDiagnostics: diagnostics);
        Assert.IsTrue(gpu.DebugLayerEnabled && gpu.DredEnabled);
        Assert.AreEqual(warp, gpu.Adapter.IsSoftware);
        if (!warp)
            Assert.AreEqual(0x10deU, gpu.Adapter.VendorId);

        var texture = Rgba(512, 1024, TextureColorSpace.Linear, 255, 0, 0);
        Assert.IsTrue(gpu.TryGetTextureRequirements(texture, 0, out var requirements));
        var qualification = new D3D12TextureCopyQualification();
        var ticket = new D3D12TextureTicket(1, 0, 1);
        using var hold = qualification.Arm(ticket);
        Assert.IsFalse(gpu.TrySubmitTexture(texture, 1, requirements, out _,
            authorize: static () => false, qualification: qualification.Find(ticket)));
        Assert.IsFalse(hold.Submitted.IsCompleted);

        Assert.IsTrue(gpu.TrySubmitTexture(texture, 1, requirements, out var uploaded,
            qualification: qualification.Find(ticket)));
        Assert.IsFalse(hold.Submitted.IsCompleted);
        Assert.IsFalse(hold.TryEnqueue(gpu), "The armed ticket must never overwrite its original native wait.");
        gpu.ConfirmTextureQualificationSubmission(hold);
        Assert.IsTrue(hold.Submitted.IsCompletedSuccessfully);
        var submitted = hold.Submitted.GetAwaiter().GetResult();
        Assert.AreEqual(ticket, submitted.Ticket);
        Assert.IsTrue(submitted.WaitQueuedAt > 0 && submitted.SubmittedAt >= submitted.WaitQueuedAt);
        Assert.IsFalse(gpu.TryRetireTextureCopy(out _));
        Assert.IsTrue(diagnostics.TryRead(out var pending));
        Assert.AreEqual(submitted.CopyFence, pending.TextureCopyFence);
        Assert.IsLessThan(submitted.CopyFence, pending.CompletedFence);
        Assert.AreEqual((ulong)requirements.TextureAllocationBytes, pending.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes);
        Assert.AreEqual((ulong)requirements.StagingAllocationBytes, pending.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes);

        if (!watchdog)
            Assert.IsGreaterThanOrEqualTo(submitted.SubmittedAt, hold.Release());
        Assert.AreEqual(uploaded, AwaitTexture(gpu));
        if (watchdog)
            Assert.ThrowsExactly<InvalidOperationException>(() => hold.Release());
        Assert.IsTrue(gpu.TryReleaseTexture(uploaded));
        Assert.IsTrue(diagnostics.TryRead(out var retired));
        Assert.AreEqual(0UL, retired.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes);
        Assert.AreEqual(0UL, retired.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes);
    }

    [TestMethod]
    public void WarpStaticTextureSamplesLinearSrgbAndBc1()
    {
        using var gpu = new GpuDevice(warp: true, enableDebugLayer: true, enableDred: true);
        VerifyStaticTextureSampling(gpu);
    }

    [TestMethod]
    public void WarpMipTiersShrinkBackingAndChooseLegalBc1SourceMip() =>
        VerifyPhysicalMipTiers(warp: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void HardwareMipTiersShrinkBackingAndChooseLegalBc1SourceMip() =>
        VerifyPhysicalMipTiers(warp: false);

    private static void VerifyPhysicalMipTiers(bool warp)
    {
        Action<GpuDevice>? onDrawSignal = null;
        var hooks = new GpuRetirementTestHooks(SignalFailure: (device, _) =>
        {
            if (!device.HasTextureCopy)
                onDrawSignal?.Invoke(device);
            return null;
        });
        using var copyWake = new AutoResetEvent(false);
        var gpu = new GpuDevice(warp: warp, enableDebugLayer: true, enableDred: true, retirementHooks: hooks);
        using (gpu)
        {
            Assert.IsTrue(gpu.DebugLayerEnabled);
            Assert.IsTrue(gpu.DredEnabled);
            Assert.AreEqual(warp, gpu.Adapter.IsSoftware);
            if (!warp)
                Assert.AreEqual(0x10deU, gpu.Adapter.VendorId);

            var texture = Rgba(512, 512, TextureColorSpace.Linear, 255, 0, 0, mipCount: 3);
            Assert.IsTrue(gpu.TryGetTextureRequirements(texture, 0, out var fine));
            Assert.IsTrue(gpu.TryGetTextureRequirements(texture, 2, out var coarse));
            Assert.IsLessThan(fine.TextureAllocationBytes, coarse.TextureAllocationBytes);
            Assert.IsLessThan(fine.PayloadBytes, coarse.PayloadBytes);
            var predecessor = Upload(gpu, texture);
            Assert.IsTrue(gpu.SetFrameTexture(predecessor, 1));
            var heldPredecessor = false;
            onDrawSignal = device =>
            {
                heldPredecessor = true;
                Assert.IsFalse(device.TryReleaseTexture(predecessor));
                Assert.IsFalse(device.TrySubmitTexture(texture, 1, coarse, out _));
                Assert.AreEqual(fine.TextureAllocationBytes, device.TextureStatistics.NativeBytes);
            };
            AssertCenter(gpu, 1, 255, 0, 0);
            onDrawSignal = null;
            Assert.IsTrue(heldPredecessor);

            Assert.IsTrue(gpu.TrySubmitTexture(texture, 1, coarse, out var replacement));
            gpu.WakeOnQueueRetirement(copyWake);
            Assert.IsTrue(copyWake.WaitOne(TimeSpan.FromSeconds(3)), "The texture copy fence did not wake the owner.");
            Assert.AreEqual(fine.TextureAllocationBytes + coarse.TextureAllocationBytes, gpu.TextureStatistics.NativeBytes);
            Assert.AreEqual(replacement, AwaitTexture(gpu));
            Assert.IsTrue(gpu.SetFrameTexture(replacement, 1));
            Assert.IsTrue(gpu.TryReleaseTexture(predecessor));
            Assert.AreEqual(coarse.TextureAllocationBytes, gpu.TextureStatistics.NativeBytes);
            Assert.AreEqual(0L, gpu.TextureStatistics.StagingBytes);
            Assert.AreEqual((ulong)(fine.PayloadBytes + coarse.PayloadBytes), gpu.TextureStatistics.UploadedBytes);
            Assert.AreEqual(fine.TextureAllocationBytes +
                Math.Max(fine.StagingAllocationBytes, coarse.TextureAllocationBytes + coarse.StagingAllocationBytes),
                gpu.TextureStatistics.NativePeakBytes);
            AssertCenter(gpu, 2, 255, 0, 0);
            Assert.IsTrue(gpu.TryReleaseTexture(replacement));

            gpu.DisableUnalignedBlockTexturesForTests = true;
            var blocks = new byte[56];
            for (var index = 0; index < 4; index++)
                BinaryPrimitives.WriteUInt16LittleEndian(blocks.AsSpan(index * 8), 0xf800);
            BinaryPrimitives.WriteUInt16LittleEndian(blocks.AsSpan(32), 0x07e0);
            BinaryPrimitives.WriteUInt16LittleEndian(blocks.AsSpan(40), 0x001f);
            Assert.AreEqual(TextureCookError.None, TextureCooker.CookBc1(blocks, 8, 8,
                new(TextureColorSpace.Srgb, TextureSemantic.Color, 4), Limits, out var bc1));
            Assert.IsNotNull(bc1);
            Assert.IsFalse(gpu.TryGetTextureRequirements(bc1, 2, out _));
            Assert.IsTrue(gpu.TryGetTextureRequirements(bc1, 1, out var legal));
            var credit = new D3D12TextureReservation(512, 0, 1024, legal.StagingAllocationBytes, legal.TextureAllocationBytes);
            Assert.IsFalse(gpu.TryGetTextureTierRequirements(bc1, 2, credit with { NativeBytes = 1 }, out _));
            Assert.IsTrue(gpu.TryGetTextureTierRequirements(bc1, 2, credit, out var selected));
            Assert.AreEqual(legal, selected);
            Assert.IsFalse(gpu.TrySubmitTexture(bc1, 1, selected with { SourceMip = 2 }, out _));
            var bc1Predecessor = Upload(gpu, bc1);
            Assert.IsTrue(gpu.SetFrameTexture(bc1Predecessor, 1));
            heldPredecessor = false;
            onDrawSignal = device =>
            {
                heldPredecessor = true;
                Assert.IsFalse(device.TryReleaseTexture(bc1Predecessor));
                Assert.IsFalse(device.TrySubmitTexture(bc1, 1, selected, out _));
            };
            AssertCenter(gpu, 3, 255, 0, 0);
            onDrawSignal = null;
            Assert.IsTrue(heldPredecessor);
            Assert.IsTrue(gpu.TrySubmitTexture(bc1, 1, selected, out var bc1Binding));
            Assert.AreNotEqual(bc1Predecessor.Slot, bc1Binding.Slot);
            Assert.AreEqual(bc1Binding, AwaitTexture(gpu));
            Assert.IsTrue(gpu.TryReleaseTexture(bc1Predecessor));
            Assert.IsTrue(gpu.SetFrameTexture(bc1Binding, 1));
            AssertCenter(gpu, 4, 0, 255, 0);

            // The coarser logical request resolves to the same physical tier on this adapter.
            var view = new D3D12TextureView(new Guid("69d64fe4-e581-4cb8-846c-38e543a103cb"),
                new(1, 1, PlanetLocalFrame.Create(default)), 1, 1);
            var target = new D3D12TextureTarget(new(1, MaterialTextureSemantic.BaseColor), 1,
                new(MaterialTextureSemantic.BaseColor, bc1.AssetId));
            var oneDescriptor = new D3D12TextureHandoff(new(1, 1, credit.SourceBytes, 1, credit.PayloadBytes,
                credit.StagingBytes, credit.NativeBytes, 1));
            Assert.IsTrue(oneDescriptor.Publish(view, 1, [target]));
            Assert.AreEqual(D3D12TextureResult.Accepted, oneDescriptor.TryRequest(view, target, credit, out var resident));
            Assert.AreEqual(D3D12TextureResult.Accepted, oneDescriptor.TryComplete(resident, bc1));
            Assert.IsTrue(oneDescriptor.TryTake(view, out var transfer));
            Assert.AreEqual(resident, transfer.Ticket);
            Assert.IsTrue(oneDescriptor.MarkCopySubmitted(resident, selected));
            Assert.IsTrue(oneDescriptor.TryInstall(resident, view, out _));
            var trafficBefore = gpu.TextureStatistics.UploadedBytes;
            Assert.AreEqual(D3D12TextureResult.Quota,
                oneDescriptor.TryRequestTier(view, target, new(1, 2, 10, 20), 25, credit, credit, out _));
            Assert.IsTrue(oneDescriptor.TryGetDrawableResident(view, out var kept));
            Assert.AreEqual(resident, kept);
            Assert.IsFalse(oneDescriptor.HasReady(view));
            Assert.IsTrue(oneDescriptor.TryReadResidency(view, target, out var residency));
            Assert.AreEqual(new D3D12TextureResidency(2, selected.SourceMip, selected.TextureAllocationBytes), residency);
            AssertCenter(gpu, 5, 0, 255, 0);
            Assert.AreEqual(trafficBefore, gpu.TextureStatistics.UploadedBytes);

            Assert.IsTrue(gpu.TryReleaseTexture(bc1Binding));
            Assert.AreEqual(0L, gpu.TextureStatistics.NativeBytes);
            Assert.AreEqual(0L, gpu.TextureStatistics.StagingBytes);
            Assert.IsNull(gpu.CaptureDred());
        }
        Assert.IsNotNull(gpu.FinalDebugSummary);
        Assert.AreEqual(0UL, gpu.FinalDebugSummary.Errors);
        Assert.AreEqual(0, gpu.FinalDebugSummary.LiveChildObjects);
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void HardwareStaticTextureSamplesWithDebugLayerAndDred()
    {
        var gpu = new GpuDevice(warp: false, enableDebugLayer: true, enableDred: true);
        using (gpu)
        {
            Assert.IsFalse(gpu.Adapter.IsSoftware);
            Assert.AreEqual(0x10deU, gpu.Adapter.VendorId);
            Assert.IsTrue(gpu.DebugLayerEnabled);
            Assert.IsTrue(gpu.DredEnabled);
            VerifyStaticTextureSampling(gpu);
            Assert.IsNull(gpu.CaptureDred());
        }
        Assert.AreEqual(0L, gpu.TextureStatistics.NativeBytes);
        Assert.AreEqual(0L, gpu.TextureStatistics.StagingBytes);
        Assert.IsNotNull(gpu.FinalDebugSummary);
        Assert.AreEqual(0UL, gpu.FinalDebugSummary.Errors);
        Assert.AreEqual(0, gpu.FinalDebugSummary.LiveChildObjects);
    }

    [TestMethod]
    public void WarpTextureCopyPreflightsPaddedBytesAndPreservesResourceUntilTrustedFence()
    {
        var signals = 0;
        var hold = true;
        var sentinel = false;
        var diagnostics = new ResourceDiagnostics();
        D3D12ResourceSnapshot beforeSignal = default;
        D3D12ResourceSnapshot beforeDraw = default;
        var hooks = new GpuRetirementTestHooks(
            SignalFailure: (device, _) =>
            {
                signals++;
                if (device.HasTextureCopy)
                    Assert.IsTrue(diagnostics.TryRead(out beforeSignal));
                return null;
            },
            CompletedValue: (_, value) => sentinel ? ulong.MaxValue : hold ? 0 : value,
            BeforeExecute: _ => Assert.IsTrue(diagnostics.TryRead(out beforeDraw)));
        using var gpu = new GpuDevice(warp: true, retirementHooks: hooks, resourceDiagnostics: diagnostics);
        var oversizedFootprint = Rgba(1, 8200, TextureColorSpace.Linear, 255, 0, 0);
        Assert.IsFalse(gpu.TryGetTextureRequirements(oversizedFootprint, out _));
        var texture = Rgba(3, 2, TextureColorSpace.Linear, 255, 0, 0);
        Assert.IsTrue(gpu.TryGetTextureRequirements(texture, out var requirements));
        Assert.AreEqual(24, requirements.PayloadBytes);
        Assert.IsGreaterThan(requirements.PayloadBytes, requirements.StagingBytes);
        Assert.IsGreaterThanOrEqualTo(requirements.StagingBytes, requirements.StagingAllocationBytes);
        Assert.IsFalse(gpu.TrySubmitTexture(texture, 2, requirements with { StagingAllocationBytes = 0 }, out _));
        Assert.IsFalse(gpu.TrySubmitTexture(texture, 2, requirements, out _, authorize: static () => false));
        Assert.AreEqual(0, signals);
        Assert.AreEqual(new GpuTextureStatistics(0, 0,
            requirements.StagingAllocationBytes + requirements.TextureAllocationBytes, 0), gpu.TextureStatistics);
        Assert.IsTrue(diagnostics.TryRead(out var aborted));
        Assert.AreEqual(0UL, aborted.SubmittedFence);
        Assert.AreEqual(1UL, aborted.GetBalance(D3D12ResourceOwner.Texture).Released);
        Assert.AreEqual(1UL, aborted.GetBalance(D3D12ResourceOwner.TextureStaging).Released);

        Assert.IsTrue(gpu.TrySubmitTexture(texture, 2, requirements, out var submitted));
        Assert.AreEqual(1, signals);
        Assert.AreEqual(1UL, beforeSignal.UnassignedFence);
        Assert.AreEqual(0UL, beforeSignal.UnassignedFrameSequence);
        Assert.AreEqual(0UL, beforeSignal.TextureCopyFence);
        Assert.AreEqual((ulong)requirements.TextureAllocationBytes,
            beforeSignal.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes);
        Assert.AreEqual((ulong)requirements.StagingAllocationBytes,
            beforeSignal.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes);
        Assert.IsTrue(diagnostics.TryRead(out var assigned));
        Assert.AreEqual(1UL, assigned.TextureCopyFence);
        Assert.AreEqual(assigned.TextureCopyFence, assigned.SubmittedFence);
        Assert.AreEqual(0UL, assigned.CompletedFence);
        Assert.AreEqual(0UL, assigned.DrawFence);
        Assert.AreEqual(requirements.StagingAllocationBytes, gpu.TextureStatistics.StagingBytes);
        Assert.AreEqual(requirements.TextureAllocationBytes, gpu.TextureStatistics.NativeBytes);
        Assert.AreEqual(requirements.StagingAllocationBytes + requirements.TextureAllocationBytes, gpu.TextureStatistics.NativePeakBytes);
        Assert.IsFalse(gpu.SetFrameTexture(submitted, 2));
        Assert.IsFalse(gpu.TryReleaseTexture(submitted));
        Assert.IsFalse(gpu.TryRetireTextureCopy(out _));
        Assert.IsFalse(gpu.TrySubmitTexture(texture, 2, requirements, out _));

        sentinel = true;
        Assert.ThrowsExactly<GpuNativeException>(() => gpu.TryRetireTextureCopy(out _));
        Assert.IsFalse(gpu.IsRemoved);
        Assert.AreEqual(requirements.StagingAllocationBytes, gpu.TextureStatistics.StagingBytes);
        sentinel = false;
        hold = false;
        Assert.AreEqual(submitted, AwaitTexture(gpu));
        Assert.AreEqual(0L, gpu.TextureStatistics.StagingBytes);
        Assert.IsTrue(diagnostics.TryRead(out var retired));
        Assert.AreEqual(retired.SubmittedFence, retired.CompletedFence);
        Assert.AreEqual(2UL, retired.GetBalance(D3D12ResourceOwner.TextureStaging).Released);
        Assert.AreEqual(1, retired.GetBalance(D3D12ResourceOwner.Texture).Live);
        Assert.IsTrue(gpu.SetFrameTexture(submitted, 2));
        AssertCenter(gpu, 2, 255, 0, 0);
        Assert.AreEqual(1, beforeDraw.GetBalance(D3D12ResourceOwner.Texture).Retained);
        Assert.IsTrue(diagnostics.TryRead(out var rendered));
        Assert.AreEqual(1UL, rendered.TextureCopyFence);
        Assert.AreEqual(2UL, rendered.DrawFence);
        Assert.IsTrue(rendered.TryGetPass(GpuPassPlan.Terrain, out var terrain));
        Assert.AreEqual(4, terrain.ResourceUses);
        Assert.IsTrue(gpu.TryReleaseTexture(submitted));
        Assert.AreEqual(0L, gpu.TextureStatistics.NativeBytes);
    }

    [TestMethod]
    public void WarpHealthyTexturePreparationFailuresKeepTerrainAndPredecessorDrawable()
    {
        const int unsupported = unchecked((int)0x887A0004);
        const int outOfMemory = unchecked((int)0x8007000E);
        var signals = 0;
        var hooks = new GpuRetirementTestHooks(SignalFailure: (_, _) => { signals++; return null; });
        var gpu = new GpuDevice(warp: true, enableDebugLayer: true, enableDred: true, retirementHooks: hooks);
        using (gpu)
        {
            var red = Rgba(4, 4, TextureColorSpace.Linear, 255, 0, 0);
            Assert.IsTrue(gpu.TryGetTextureRequirements(red, out var requirements));
            gpu.TexturePreparationFailureForTests = step =>
                step == GpuTexturePreparationStep.Pipeline ? unsupported : null;
            Assert.IsFalse(gpu.TrySubmitTexture(red, 1, requirements, out var unsubmitted));
            Assert.AreEqual(default, unsubmitted);
            Assert.IsFalse(gpu.HasTextureCopy);
            Assert.IsTrue(gpu.IsIdle);
            Assert.AreEqual(0, signals);
            Assert.AreEqual(default, gpu.TextureStatistics);
            AssertCenter(gpu, 1, 255, 255, 255);

            gpu.TexturePreparationFailureForTests = null;
            var predecessor = Upload(gpu, red);
            Assert.IsTrue(gpu.SetFrameTexture(predecessor, 1));
            AssertCenter(gpu, 2, 255, 0, 0);

            var before = gpu.TextureStatistics;
            var beforeSignals = signals;
            var green = Rgba(4, 4, TextureColorSpace.Linear, 0, 255, 0);
            gpu.TexturePreparationFailureForTests = step =>
                step == GpuTexturePreparationStep.Destination ? outOfMemory : null;
            Assert.IsFalse(gpu.TrySubmitTexture(green, 1, requirements, out unsubmitted));
            Assert.AreEqual(default, unsubmitted);
            Assert.IsFalse(gpu.HasTextureCopy);
            Assert.IsTrue(gpu.IsIdle);
            Assert.AreEqual(beforeSignals, signals);
            Assert.AreEqual(before, gpu.TextureStatistics);
            AssertCenter(gpu, 3, 255, 0, 0);

            beforeSignals = signals;
            gpu.TexturePreparationFailureForTests = step =>
                step == GpuTexturePreparationStep.Close ? outOfMemory : null;
            Assert.IsFalse(gpu.TrySubmitTexture(green, 1, requirements, out unsubmitted));
            Assert.AreEqual(default, unsubmitted);
            Assert.IsFalse(gpu.HasTextureCopy);
            Assert.IsTrue(gpu.IsIdle);
            Assert.AreEqual(beforeSignals, signals);
            Assert.AreEqual(before.NativeBytes, gpu.TextureStatistics.NativeBytes);
            Assert.AreEqual(before.UploadedBytes, gpu.TextureStatistics.UploadedBytes);
            Assert.AreEqual(0L, gpu.TextureStatistics.StagingBytes);
            AssertCenter(gpu, 4, 255, 0, 0);

            gpu.TexturePreparationFailureForTests = null;
            var replacement = Upload(gpu, green);
            Assert.IsTrue(gpu.SetFrameTexture(replacement, 1));
            AssertCenter(gpu, 5, 0, 255, 0);
            VerifyCombinedShadowAndTexture(gpu);
            Assert.IsTrue(gpu.TryReleaseTexture(predecessor));
            Assert.IsTrue(gpu.TryReleaseTexture(replacement));
        }
        if (gpu.DebugLayerEnabled)
        {
            Assert.IsNotNull(gpu.FinalDebugSummary);
            Assert.AreEqual(0UL, gpu.FinalDebugSummary.Errors);
            Assert.AreEqual(0, gpu.FinalDebugSummary.LiveChildObjects);
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12WarpDred")]
    public void WarpTextureUnassignedSignalRetainsUploadAndDestinationUntilConfirmedRemoval()
    {
        const int signalFailure = unchecked((int)0x8007000E);
        var hooks = new GpuRetirementTestHooks(SignalFailure: (_, _) => signalFailure);
        var diagnostics = new ResourceDiagnostics();
        using var gpu = new GpuDevice(warp: true, enableDred: true, retirementHooks: hooks, resourceDiagnostics: diagnostics);
        var texture = Rgba(4, 4, TextureColorSpace.Linear, 128, 64, 0);
        Assert.IsTrue(gpu.TryGetTextureRequirements(texture, out var requirements));
        var failure = Assert.ThrowsExactly<GpuNativeException>(() => gpu.TrySubmitTexture(texture, 1, requirements, out _));
        Assert.AreEqual(signalFailure, failure.HResult);
        Assert.IsTrue(gpu.HasTextureCopy);
        Assert.IsFalse(gpu.IsIdle);
        Assert.IsFalse(gpu.IsRemoved);
        Assert.IsFalse(gpu.TryRetireTextureCopy(out _));
        Assert.AreEqual(requirements.StagingAllocationBytes, gpu.TextureStatistics.StagingBytes);
        Assert.AreEqual(requirements.TextureAllocationBytes, gpu.TextureStatistics.NativeBytes);
        Assert.IsTrue(diagnostics.TryRead(out var unassigned));
        Assert.AreEqual(1UL, unassigned.UnassignedFence);
        Assert.AreEqual(0UL, unassigned.SubmittedFence);
        Assert.AreEqual(0UL, unassigned.TextureCopyFence);
        Assert.AreEqual(1, unassigned.GetBalance(D3D12ResourceOwner.Texture).Retained);
        Assert.AreEqual(1, unassigned.GetBalance(D3D12ResourceOwner.TextureStaging).Retained);
        Assert.ThrowsExactly<InvalidOperationException>(gpu.Dispose);

        ID3D12Device5* removable = null;
        Assert.AreEqual(0, gpu.Device->QueryInterface(__uuidof<ID3D12Device5>(), (void**)&removable).Value);
        try
        {
            removable->RemoveDevice();
        }
        finally
        {
            removable->Release();
        }
        Assert.IsTrue(gpu.IsRemoved);
        gpu.Dispose();
        Assert.AreEqual(0L, gpu.TextureStatistics.StagingBytes);
        Assert.AreEqual(0L, gpu.TextureStatistics.NativeBytes);
        Assert.IsTrue(diagnostics.TryRead(out var removed));
        Assert.AreEqual(1UL, removed.GetBalance(D3D12ResourceOwner.Texture).Released);
        Assert.AreEqual(1UL, removed.GetBalance(D3D12ResourceOwner.TextureStaging).Released);
    }

    [TestMethod]
    public void StaticTexturePlanDeclaresOnlyTheInitializedLevelZeroAndRejectsInvalidFormat()
    {
        Assert.IsTrue(GpuDevice.IsLegalBc1LevelZero(4, 12, false));
        Assert.IsFalse(GpuDevice.IsLegalBc1LevelZero(5, 12, false));
        Assert.IsFalse(GpuDevice.IsLegalBc1LevelZero(8, 11, false));
        Assert.IsTrue(GpuDevice.IsLegalBc1LevelZero(5, 11, true));
        var shape = new GpuPassShape(32, 32, 0, Scene: true, Sky: false, Props: false, FollowOn: false,
            ShadowPassKind.Disabled, ShadowPassKind.Disabled, Readback: true, Present: false,
            new(RenderResourceFormat.Bc1UnormSrgb, 4, 8));
        Assert.IsTrue(GpuPassPlan.TryCreate(shape, out var plan));
        Assert.IsNotNull(plan);
        var import = plan.Resources.Single(r => r.Admission == RenderResourceAdmission.OptionalVisual);
        Assert.AreEqual(RenderResourceFormat.Bc1UnormSrgb, import.Format);
        Assert.AreEqual(1, import.MipLevels);
        Assert.IsTrue(import.ImportedRanges.Single().Initialized);
        var terrain = plan.Passes.Single(pass => pass.Id == GpuPassPlan.Terrain);
        Assert.IsTrue(terrain.Inputs.Any(input => input.Resource == import.Id &&
            input.State == RenderResourceState.ShaderRead && input.Range == new RenderSubresourceRange(0, 1)));
        Assert.IsFalse(GpuPassPlan.TryCreate(shape with
        {
            Texture = new(RenderResourceFormat.Depth32Float, 4, 8),
        }, out _));
    }

    private static void VerifyStaticTextureSampling(GpuDevice gpu)
    {
        AssertCenter(gpu, 1, 255, 255, 255);
        var linear = Rgba(3, 2, TextureColorSpace.Linear, 128, 64, 0);
        var first = Upload(gpu, linear);
        Assert.IsFalse(gpu.SetFrameTexture(first, 7));
        Assert.IsTrue(gpu.SetFrameTexture(first, 1));
        AssertCenter(gpu, 2, 128, 64, 0);
        var warm = gpu.TextureStatistics.UploadedBytes;
        AssertCenter(gpu, 3, 128, 64, 0);
        var farther = new RenderFrame(4, Vector4.Zero, Scene);
        var nearer = Scene with
        {
            World = Matrix4x4.CreateTranslation(0, 0, -2),
            Tint = new(0, 1, 0, 1)
        };
        var textured = GpuDevice.RenderOffscreen(gpu, 64, 64, farther);
        var followOn = GpuDevice.RenderOffscreen(gpu, 64, 64, farther, nearer);
        Assert.IsTrue(followOn.ColorAt(32, 32).SequenceEqual(new byte[] { 0, 255, 0, 255 }));
        Assert.IsLessThan(textured.DepthAt(32, 32), followOn.DepthAt(32, 32));
        Assert.AreEqual(warm, gpu.TextureStatistics.UploadedBytes);

        var srgb = Rgba(3, 2, TextureColorSpace.Srgb, 128, 64, 0);
        var second = Upload(gpu, srgb);
        Assert.IsTrue(gpu.SetFrameTexture(second, 1));
        AssertCenter(gpu, 4, 55, 13, 0);
        Assert.IsTrue(gpu.TryReleaseTexture(first));
        Assert.IsFalse(gpu.SetFrameTexture(first, 1));
        AssertCenter(gpu, 5, 255, 255, 255);

        var imported = TextureCooker.CookBc1([0xe0, 0x07, 0, 0, 0, 0, 0, 0], 4, 4,
            new(TextureColorSpace.Linear, TextureSemantic.Color, 1), Limits, out var bc1);
        Assert.AreEqual(TextureCookError.None, imported);
        Assert.IsNotNull(bc1);
        var third = Upload(gpu, bc1);
        Assert.AreEqual(first.Slot, third.Slot);
        Assert.AreNotEqual(first.Generation, third.Generation);
        Assert.IsFalse(gpu.SetFrameTexture(first, 1));
        Assert.IsFalse(gpu.SetFrameTexture(third with { Device = third.Device + 1 }, 1));
        Assert.IsTrue(gpu.SetFrameTexture(third, 1));
        AssertCenter(gpu, 6, 0, 255, 0);
        VerifyCombinedShadowAndTexture(gpu);
        Assert.IsTrue(gpu.TryReleaseTexture(second));
        Assert.IsTrue(gpu.TryReleaseTexture(third));
        Assert.AreEqual(0L, gpu.TextureStatistics.NativeBytes);
    }

    private static void VerifyCombinedShadowAndTexture(GpuDevice gpu)
    {
        var floor = new RenderMeshInstance(Quad(8, 5, -24), Vector3.Zero);
        var caster = new RenderMeshInstance(Quad(1, 1, -21.5f), Vector3.Zero);
        gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(new RenderMeshRegion([floor, caster]));
        var camera = new VolumeCamera(new(1, 1, PlanetLocalFrame.Create(default)), default,
            -Vector3.UnitZ, Vector3.UnitY, 0.65f, 0.1f, 100, Vector4.One, Vector4.Zero)
        {
            Sunlight = new(Vector3.Normalize(new(1, 0, 1)), new(0.85f), new(0.12f)),
            TerrainShadows = true,
        };
        Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 7, out var frame));
        var image = GpuDevice.RenderOffscreen(gpu, 160, 96, frame);
        Assert.AreEqual(2UL, gpu.RecordedShadowPasses);
        Assert.AreEqual((byte)0, image.ColorAt(65, 48)[0]);
        Assert.AreEqual((byte)0, image.ColorAt(95, 48)[2]);
        Assert.IsGreaterThan(image.ColorAt(65, 48)[1] + 80, image.ColorAt(95, 48)[1]);
        Assert.IsLessThan(1f, image.DepthAt(95, 48));

        static RenderMesh Quad(float x, float y, float z)
        {
            var normal = PackedTerrainNormal.Encode(Vector3.UnitZ);
            return RenderMesh.CreateWithTerrainNormals([new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z)],
                [0, 1, 2, 0, 2, 3], [], [normal, normal, normal, normal]);
        }
    }

    private static GpuTextureIdentity Upload(GpuDevice gpu, CookedTexture texture)
    {
        Assert.IsTrue(gpu.TryGetTextureRequirements(texture, out var requirements));
        Assert.IsTrue(gpu.TrySubmitTexture(texture, 1, requirements, out var submitted));
        Assert.IsFalse(gpu.SetFrameTexture(submitted, 1));
        Assert.AreEqual(submitted, AwaitTexture(gpu));
        return submitted;
    }

    private static GpuTextureIdentity AwaitTexture(GpuDevice gpu)
    {
        var started = Stopwatch.GetTimestamp();
        GpuTextureIdentity identity;
        while (!gpu.TryRetireTextureCopy(out identity))
        {
            Assert.IsLessThan(TimeSpan.FromSeconds(10), Stopwatch.GetElapsedTime(started));
            Thread.Sleep(1);
        }
        return identity;
    }

    private static void AssertCenter(GpuDevice gpu, ulong sequence, int red, int green, int blue)
    {
        var readback = GpuDevice.RenderOffscreen(gpu, 64, 64, new RenderFrame(sequence, Vector4.Zero, Scene));
        var center = readback.ColorAt(32, 32);
        Assert.AreEqual(red, center[0], 1);
        Assert.AreEqual(green, center[1], 1);
        Assert.AreEqual(blue, center[2], 1);
        Assert.AreEqual((byte)255, center[3]);
        Assert.IsLessThan(1f, readback.DepthAt(32, 32));
    }

    private static CookedTexture Rgba(int width, int height, TextureColorSpace colorSpace, byte red, byte green, byte blue,
        int mipCount = 1)
    {
        var tga = new byte[18 + width * height * 4];
        tga[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(14), (ushort)height);
        tga[16] = 32;
        tga[17] = 0x28;
        for (var offset = 18; offset < tga.Length; offset += 4)
        {
            tga[offset] = blue;
            tga[offset + 1] = green;
            tga[offset + 2] = red;
            tga[offset + 3] = 255;
        }
        Assert.AreEqual(TextureCookError.None, TextureCooker.CookTga32(tga,
            new(colorSpace, TextureSemantic.Color, mipCount), Limits, out var texture));
        Assert.IsNotNull(texture);
        return texture;
    }
}
