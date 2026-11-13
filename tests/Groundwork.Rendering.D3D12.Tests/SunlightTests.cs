using System.Numerics;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private static Vector3 DecodeTerrainNormal(uint bits) => Vector3.Normalize(new Vector3(
        (int)(bits << 22) >> 22, (int)(bits << 12) >> 22, (int)(bits << 2) >> 22));

    [TestMethod]
    public void SunlightValidationPreservesAcceptedSequence()
    {
        var admission = new RendererFrameAdmission();
        admission.MarkInitialized();
        admission.MarkRunning();
        Assert.IsTrue(admission.TrySetSurface(RenderSurfaceState.Drawable, 160, 96));
        var view = new VolumeViewIdentity(1, 1, PlanetLocalFrame.Create(default));
        var camera = new VolumeCamera(view, default, -Vector3.UnitZ, Vector3.UnitY, 1, 0.1f, 100, Vector4.One, Vector4.Zero)
        { Sunlight = new(Vector3.UnitZ, Vector3.One, new(0.2f)) };
        Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 1, out var frame));
        Assert.AreEqual(RenderSubmissionResult.Accepted, admission.TrySubmit(frame));
        var sun = camera.Sunlight!.Value;
        foreach (var bad in new[]
        {
            sun with { Direction = Vector3.Zero },
            sun with { Direction = Vector3.One },
            sun with { Direction = new(float.NaN, 0, 0) },
            sun with { Direct = new(-1) },
            sun with { Ambient = new(float.PositiveInfinity) },
            sun with { Ambient = new(5) },
        })
        {
            Assert.IsFalse(D3D12Renderer.TryCreateVolumeFrame(camera with { Sunlight = bad }, 2, out _));
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame, admission.TrySubmit(frame with
            { Sequence = 2, Scene = frame.Scene!.Value with { Sunlight = bad } }));
            Assert.AreEqual(1UL, admission.Read().LastAcceptedSequence);
        }
        Assert.IsTrue(admission.TryTakeLatest(out var pending));
        Assert.AreEqual(frame, pending);
    }

    [TestMethod]
    public void PackedSunlightNormalRoundTripsAndRejectsInvalidInput()
    {
        foreach (var invalid in new[] { Vector3.Zero, new(float.NaN), new(float.PositiveInfinity) })
            Assert.AreEqual(0u, PackedTerrainNormal.Encode(invalid));
        for (var x = -5; x <= 5; x++)
        for (var y = -5; y <= 5; y++)
        for (var z = -5; z <= 5; z++)
        {
            var input = new Vector3(x, y, z);
            if (input != Vector3.Zero)
                Assert.IsGreaterThan(0.99999f,
                    Vector3.Dot(Vector3.Normalize(input), DecodeTerrainNormal(PackedTerrainNormal.Encode(input))));
        }
    }

    [TestMethod]
    public void WarpSunlightReadbackPreservesFogAndRebase() => VerifySunlight(true, false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void SunlightReadbackWithDebugLayerAndDred(bool warp) => VerifySunlight(warp, true);

    private static void VerifySunlight(bool warp, bool debug)
    {
        var leftNormal = PackedTerrainNormal.Encode(Vector3.UnitZ);
        var rightNormal = PackedTerrainNormal.Encode(-Vector3.UnitZ);
        Vector3[] positions = [new(-2, -1, -4), new(0, -1, -4), new(-2, 1, -4),
            new(0, -1, -4), new(2, -1, -4), new(2, 1, -4)];
        var mesh = RenderMesh.CreateWithTerrainNormals(positions, [0, 1, 2, 3, 4, 5],
            [0x80ffffff, 0x80ffffff, 0x80ffffff, 0x80ffffff, 0x80ffffff, 0x80ffffff],
            [leftNormal, leftNormal, leftNormal, rightNormal, rightNormal, rightNormal]);
        var origin = PlanetLocalFrame.Create(default);
        var view = new VolumeViewIdentity(1, 1, origin);
        var camera = new VolumeCamera(view, default, -Vector3.UnitZ, Vector3.UnitY, 1,
            0.1f, 100, Vector4.One, Vector4.Zero)
        { Sunlight = new(Vector3.UnitZ, new(0.6f), new(0.2f)) };
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, region: Region(origin));
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DredEnabled);
            if (debug)
                Assert.IsTrue(gpu.DebugLayerEnabled);
            var baseline = Read(camera with { Sunlight = null });
            var lit = Read(camera);
            Assert.AreEqual((byte)255, baseline.ColorAt(46, 48)[0]);
            Assert.AreEqual((byte)204, lit.ColorAt(46, 48)[0]);
            Assert.AreEqual((byte)51, lit.ColorAt(113, 48)[0]);
            Assert.AreEqual((byte)128, lit.ColorAt(46, 48)[3]);
            Assert.AreEqual(baseline.DepthAt(46, 48), lit.DepthAt(46, 48));
            var fogged = camera with { Fog = new(Vector3.UnitZ, 0, 8, 1) };
            var fog = Read(fogged);
            Assert.AreEqual(102f, fog.ColorAt(46, 48)[0], 1f);
            Assert.AreEqual(26f, fog.ColorAt(113, 48)[0], 1f);
            Assert.IsTrue(origin.TryRebase(new PlanetVector(32, 0, 0), out var shifted));
            var old = gpu.ActiveMeshGeometry;
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(Region(shifted));
            old?.Dispose();
            var rebased = Read(fogged with { View = view with { Frame = shifted } });
            foreach (var x in new[] { 46, 113 })
            {
                Assert.IsTrue(fog.ColorAt(x, 48).SequenceEqual(rebased.ColorAt(x, 48)));
                Assert.AreEqual(fog.DepthAt(x, 48), rebased.DepthAt(x, 48));
            }
            Assert.IsNull(gpu.CaptureDred());
        }
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);

        RenderMeshRegion Region(PlanetLocalFrame local) => new([new(mesh, local.ToLocal(default))]);

        OffscreenFrame Read(VolumeCamera input)
        {
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(input, 1, out var frame));
            return GpuDevice.RenderOffscreen(gpu, 160, 96, frame);
        }
    }
}
