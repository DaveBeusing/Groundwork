using System.Numerics;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public void WarpDistanceFogReadbackUsesViewDepthAndPreservesRebase()
        => VerifyDistanceFog(warp: true, requireDebug: false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void DistanceFogReadbackWithDebugLayerAndDred(bool warp)
        => VerifyDistanceFog(warp, requireDebug: true);

    [TestMethod]
    public void WideDistanceFogBandUsesNormalShaderConstants()
    {
        var start = float.MaxValue * 0.125f;
        var end = float.MaxValue;
        var fog = new RenderFog(Vector3.One, start, end, 1f);
        var view = new VolumeViewIdentity(1, 1, PlanetLocalFrame.Create(default));
        Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(FogCamera(view) with { Fog = fog }, 1, out _));

        Assert.IsTrue(float.IsSubnormal(1f / (end - start)));
        var constants = GpuDevice.GetFogDepthConstants(fog);
        Assert.IsTrue(float.IsNormal(constants.Y));
        Assert.AreEqual(0f, BlendAt(start), 0.000001f);
        Assert.AreEqual(0.5f, BlendAt(start + (end - start) * 0.5f), 0.000001f);
        Assert.AreEqual(1f, BlendAt(end), 0.000001f);

        float BlendAt(float depth) => (depth * constants.Z - constants.X) * constants.Y;
    }

    [TestMethod]
    public void InvalidDistanceFogLeavesTheAcceptedSequenceUnchanged()
    {
        var admission = new RendererFrameAdmission();
        admission.MarkInitialized();
        admission.MarkRunning();
        Assert.IsTrue(admission.TrySetSurface(RenderSurfaceState.Drawable, TerrainWidth, TerrainHeight));
        var view = new VolumeViewIdentity(1, 1, PlanetLocalFrame.Create(default));
        var camera = FogCamera(view);
        Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 1, out var frame));
        Assert.AreEqual(RenderSubmissionResult.Accepted, admission.TrySubmit(frame));
        var fog = camera.Fog!.Value;
        RenderFog[] invalid =
        [
            fog with { Color = new(float.NaN, 0, 0) },
            fog with { Color = new(1.01f, 0, 0) },
            fog with { StartDepthMeters = -1 },
            fog with { StartDepthMeters = float.PositiveInfinity },
            fog with { EndDepthMeters = float.PositiveInfinity },
            fog with { EndDepthMeters = fog.StartDepthMeters },
            fog with { EndDepthMeters = 2, MaxOpacity = 0 },
            fog with { StartDepthMeters = 0, EndDepthMeters = float.Epsilon },
            fog with { MaxOpacity = float.NaN },
            fog with { MaxOpacity = 1.01f },
        ];
        foreach (var value in invalid)
        {
            Assert.IsFalse(D3D12Renderer.TryCreateVolumeFrame(camera with { Fog = value }, 2, out _));
            var bad = frame with { Sequence = 2, Scene = frame.Scene!.Value with { Fog = value } };
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame, admission.TrySubmit(bad));
            Assert.AreEqual(1UL, admission.Read().LastAcceptedSequence);
        }
        Assert.IsTrue(admission.TryTakeLatest(out var pending));
        Assert.AreEqual(frame, pending);
    }

    private static void VerifyDistanceFog(bool warp, bool requireDebug)
    {
        var origin = PlanetLocalFrame.Create(default);
        var view = new VolumeViewIdentity(1, 1, origin);
        var camera = FogCamera(view);
        // The middle-depth panel is off axis, where radial distance differs from view depth.
        var panels = RenderMesh.Create(
            [.. Panel(-0.5f, 4), .. Panel(0.5f, 16), .. Panel(0, 32)],
            [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 8, 9, 10, 8, 10, 11],
            Enumerable.Repeat(0x800000ffu, 12).ToArray());
        var region = new RenderMeshRegion([new(panels, Vector3.Zero)]);
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, region: region);
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DredEnabled);
            if (requireDebug)
                Assert.IsTrue(gpu.DebugLayerEnabled);

            var baseline = Read(camera with { Fog = null });
            var zero = Read(camera with { Fog = camera.Fog!.Value with { MaxOpacity = 0 } });
            var fogged = Read(camera);
            AssertPixel(40, [255, 0, 0, 128]);
            AssertPixel(120, [153, 0, 102, 128]);
            AssertPixel(80, [51, 0, 204, 128]);

            Assert.IsTrue(origin.TryRebase(new PlanetVector(32, 0, 0), out var shifted));
            var rebased = new RenderMeshRegion([new(panels, shifted.ToLocal(default))]);
            var old = gpu.ActiveMeshGeometry;
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(rebased);
            old?.Dispose();
            var shiftedPixels = Read(camera with { View = view with { Frame = shifted } });
            foreach (var x in new[] { 40, 80, 120 })
            {
                for (var i = 0; i < 4; i++)
                    Assert.AreEqual((float)fogged.ColorAt(x, 48)[i], shiftedPixels.ColorAt(x, 48)[i], 1f);
                Assert.AreEqual(fogged.DepthAt(x, 48), shiftedPixels.DepthAt(x, 48), 0.00001f);
            }

            Assert.AreEqual(1, gpu.LiveMeshBuffers);
            Assert.IsNull(gpu.CaptureDred());

            OffscreenFrame Read(VolumeCamera input)
            {
                Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(input, 1, out var frame));
                return GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            }

            void AssertPixel(int x, ReadOnlySpan<byte> expected)
            {
                const int y = 48;
                var baselineColor = baseline.ColorAt(x, y);
                Assert.IsTrue(baselineColor.SequenceEqual(new byte[] { 255, 0, 0, 128 }));
                Assert.IsTrue(zero.ColorAt(x, y).SequenceEqual(baselineColor));
                var actual = fogged.ColorAt(x, y);
                for (var i = 0; i < actual.Length; i++)
                    Assert.AreEqual((float)expected[i], actual[i], 1f, $"Channel {i} at {x}.");
                Assert.AreEqual(baseline.DepthAt(x, y), fogged.DepthAt(x, y));
                Assert.AreEqual(baseline.DepthAt(x, y), zero.DepthAt(x, y));
            }
        }
        Assert.AreEqual(0, gpu.LiveMeshBuffers);
        Assert.AreEqual(0L, gpu.LiveMeshBytes);
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null),
            diagnosticsRequested: true);

        static Vector3[] Panel(float center, float depth)
        {
            var scale = depth * TerrainWidth / TerrainHeight;
            var left = (center - 0.16f) * scale;
            var right = (center + 0.16f) * scale;
            var halfHeight = depth * 0.3f;
            return [new(left, -halfHeight, -depth), new(right, -halfHeight, -depth),
                new(right, halfHeight, -depth), new(left, halfHeight, -depth)];
        }
    }

    private static VolumeCamera FogCamera(VolumeViewIdentity view)
        => new(view, default, -Vector3.UnitZ, Vector3.UnitY, MathF.PI / 2, 0.1f, 100f, Vector4.One, Vector4.Zero)
        {
            Fog = new(Vector3.UnitZ, 8, 24, 0.8f),
        };
}
