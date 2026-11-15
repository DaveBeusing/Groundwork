using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
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
    public void WarpStaticTextureSamplesLinearSrgbAndBc1()
    {
        using var gpu = new GpuDevice(warp: true, enableDebugLayer: true, enableDred: true);
        VerifyStaticTextureSampling(gpu);
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
        var hooks = new GpuRetirementTestHooks(
            SignalFailure: (_, _) => { signals++; return null; },
            CompletedValue: (_, value) => sentinel ? ulong.MaxValue : hold ? 0 : value);
        using var gpu = new GpuDevice(warp: true, retirementHooks: hooks);
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

        Assert.IsTrue(gpu.TrySubmitTexture(texture, 2, requirements, out var submitted));
        Assert.AreEqual(1, signals);
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

            gpu.TexturePreparationFailureForTests = null;
            var replacement = Upload(gpu, green);
            Assert.IsTrue(gpu.SetFrameTexture(replacement, 1));
            AssertCenter(gpu, 4, 0, 255, 0);
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
        using var gpu = new GpuDevice(warp: true, enableDred: true, retirementHooks: hooks);
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

    private static CookedTexture Rgba(int width, int height, TextureColorSpace colorSpace, byte red, byte green, byte blue)
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
            new(colorSpace, TextureSemantic.Color, 1), Limits, out var texture));
        Assert.IsNotNull(texture);
        return texture;
    }
}
