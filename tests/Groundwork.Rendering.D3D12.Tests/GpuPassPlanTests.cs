using System.Numerics;
using Groundwork.Rendering.Passes;
using TerraFX.Interop.DirectX;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class GpuPassPlanTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FixedOrderCoversCachedShadowPropsFollowOnAndDualTerminalRoutes(bool preview)
    {
        var shape = new GpuPassShape(163, 97, 4096, Scene: true, Sky: true, Props: true, FollowOn: true,
            ShadowPassKind.Cached, ShadowPassKind.Draw, Readback: true, Present: true, SkyAfterOpaque: preview);
        Assert.IsTrue(GpuPassPlan.TryCreate(shape, out var plan));
        Assert.IsNotNull(plan);
        var expected = preview ? new[] {
            GpuPassPlan.Clear, GpuPassPlan.FarShadow, GpuPassPlan.Terrain, GpuPassPlan.Props,
            GpuPassPlan.FollowOn, GpuPassPlan.Sky, GpuPassPlan.Copy, GpuPassPlan.Present, GpuPassPlan.Readback,
        } : new[] {
            GpuPassPlan.Clear, GpuPassPlan.FarShadow, GpuPassPlan.Sky, GpuPassPlan.Terrain,
            GpuPassPlan.Props, GpuPassPlan.FollowOn, GpuPassPlan.Copy, GpuPassPlan.Present, GpuPassPlan.Readback,
        };
        CollectionAssert.AreEqual(expected, plan.Passes.Select(p => p.Id).ToArray());
        var cached = plan.Resources.Single(r => r.Extent.Class == RenderExtentClass.FixedTexture &&
            r.ImportedRanges[0].Initialized);
        Assert.AreEqual(RenderResourceState.ShaderRead, cached.ImportedRanges[0].State);
        Assert.IsTrue(plan.Passes.Single(p => p.Id == GpuPassPlan.Terrain).Inputs.AsSpan()
            .Contains(new(cached.Id, new(0, 1), RenderResourceState.ShaderRead)));
        var readback = plan.Resources.Single(r => (r.Usage & RenderResourceUsage.Readback) != 0);
        Assert.AreEqual(163 * 97 * 8, readback.Extent.Width);
        Assert.IsFalse(readback.ImportedRanges[0].Initialized);
        var depth = plan.Resources.Single(r => r.Format == RenderResourceFormat.Depth32Float &&
            r.Extent.Class == RenderExtentClass.FrameTexture);
        var sky = plan.Passes.Single(p => p.Id == GpuPassPlan.Sky);
        Assert.AreEqual(preview,
            sky.Inputs.AsSpan().Contains(new(depth.Id, new(0, 1), RenderResourceState.DepthRead)));
        Assert.IsFalse(sky.Outputs.Any(output => output.Resource == depth.Id));
        Assert.AreEqual(2, plan.Resources.Count(r => r.Extent.Class == RenderExtentClass.FrameTexture));
    }

    [TestMethod]
    public void SkyOnlyReadsTheClearedDepthWithoutIntroducingAnotherTarget()
    {
        var shape = new GpuPassShape(163, 97, 0, Scene: false, Sky: true, Props: false, FollowOn: false,
            ShadowPassKind.Disabled, ShadowPassKind.Disabled, Readback: false, Present: true, SkyAfterOpaque: true);
        Assert.IsTrue(GpuPassPlan.TryCreate(shape, out var plan));
        Assert.IsNotNull(plan);
        CollectionAssert.AreEqual(new[] { GpuPassPlan.Clear, GpuPassPlan.Sky, GpuPassPlan.Present },
            plan.Passes.Select(pass => pass.Id).ToArray());
        Assert.HasCount(2, plan.Resources);
        Assert.AreEqual(RenderResourceState.DepthRead, plan.Passes[1].Inputs.Single().State);
        Assert.HasCount(1, plan.Passes[1].Outputs);
    }

    [TestMethod]
    public void RejectsInconsistentBranchesAndImpossiblePackedReadbackExtent()
    {
        var clear = new GpuPassShape(163, 97, 0, false, false, false, false,
            ShadowPassKind.Disabled, ShadowPassKind.Disabled, Readback: false, Present: true);
        Assert.IsTrue(GpuPassPlan.TryCreate(clear, out var plan));
        Assert.IsNotNull(plan);
        CollectionAssert.AreEqual(new[] { GpuPassPlan.Clear, GpuPassPlan.Present }, plan.Passes.Select(p => p.Id).ToArray());
        Assert.IsFalse(GpuPassPlan.TryCreate(clear with { Present = false }, out _));
        Assert.IsFalse(GpuPassPlan.TryCreate(clear with { Near = ShadowPassKind.Cached }, out _));
        Assert.IsFalse(GpuPassPlan.TryCreate(clear with { Props = true }, out _));
        Assert.IsFalse(GpuPassPlan.TryCreate(clear with { Scene = true, Sky = true, Props = true,
            TransparentPropsAfterSky = true }, out _));
        Assert.IsFalse(GpuPassPlan.TryCreate(clear with { Width = 16384, Height = 16384, Readback = true }, out _));
    }

    [TestMethod]
    public void WarpPaddedReadbackConsumesOnlyLogicalColorAndDepthRows()
    {
        var frame = new RenderFrame(1, new Vector4(0.25f, 0.5f, 0.75f, 1));
        var pixels = GpuDevice.RenderOffscreen(163, 97, frame);
        byte[] expected = [64, 128, 191, 255];
        foreach (var (x, y) in new[] { (0, 0), (162, 0), (0, 1), (162, 95), (162, 96) })
        {
            Assert.IsTrue(pixels.ColorAt(x, y).SequenceEqual(expected));
            Assert.AreEqual(1f, pixels.DepthAt(x, y));
        }
    }

    [TestMethod]
    public void WarpClosedListRejectionDoesNotSignalAndTheNextRecordingUsesTheSameAllocator()
    {
        var signals = 0;
        var hooks = new GpuRetirementTestHooks(
            SignalFailure: (_, _) => { signals++; return null; },
            RejectRecordedPlan: sequence => sequence == 1);
        using var gpu = new GpuDevice(warp: true, retirementHooks: hooks);
        var frame = new RenderFrame(1, new Vector4(0, 0.5f, 1, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => GpuDevice.RenderOffscreen(gpu, 163, 97, frame));
        Assert.IsTrue(gpu.IsIdle);
        Assert.AreEqual(0, signals);

        var pixels = GpuDevice.RenderOffscreen(gpu, 163, 97, frame with { Sequence = 2 });
        Assert.IsTrue(pixels.ColorAt(162, 96).SequenceEqual(new byte[] { 0, 128, 255, 255 }));
        Assert.AreEqual(1f, pixels.DepthAt(162, 96));
        Assert.AreEqual(1, signals);
    }

    [TestMethod]
    public unsafe void WarpTargetsCannotReuseAnOldGenerationOrCrossQueueOwners()
    {
        using var firstOwner = new GpuDevice(warp: true);
        using var secondOwner = new GpuDevice(warp: true);
        using var first = Attach(firstOwner, 163);
        using var second = Attach(secondOwner, 163);
        Assert.IsTrue(first.IsCurrent(firstOwner, 1));
        Assert.IsTrue(second.IsCurrent(secondOwner, 1));
        Assert.IsFalse(first.IsCurrent(secondOwner, 1));
        Assert.IsFalse(second.IsCurrent(firstOwner, 1));
        using var resized = Attach(firstOwner, 165);
        using var originalExtent = Attach(firstOwner, 163);
        Assert.IsTrue(originalExtent.IsCurrent(firstOwner, 3));
        Assert.IsFalse(first.IsCurrent(firstOwner, 3));
        Assert.IsFalse(resized.IsCurrent(firstOwner, 3));

        static GpuTarget Attach(GpuDevice owner, int width)
        {
            var buffer = GpuNative.CreateResource(owner.Device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
                GpuNative.Texture(width, 97, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                    D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET),
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON);
            try
            {
                return owner.Attach(buffer, width, 97);
            }
            catch
            {
                GpuNative.Release(buffer);
                throw;
            }
        }
    }
}
