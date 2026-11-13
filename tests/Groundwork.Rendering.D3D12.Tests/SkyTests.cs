using System.Numerics;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public void WarpSkyReadbackCoversScatteringStarsLimbAndRebase() => VerifySky(true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void NvidiaSkyReadbackCoversScatteringStarsLimbAndRebase() => VerifySky(false);

    [TestMethod]
    public void InvalidSkyDoesNotConsumeFrameSequence()
    {
        var admission = new RendererFrameAdmission();
        admission.MarkInitialized();
        admission.MarkRunning();
        Assert.IsTrue(admission.TrySetSurface(RenderSurfaceState.Drawable, 160, 96));
        var frame = new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() };
        Assert.AreEqual(RenderSubmissionResult.Accepted, admission.TrySubmit(frame));
        var sky = frame.Sky!.Value;
        foreach (var invalid in new[]
        {
            sky with { EyeMeters = Vector3.Zero },
            sky with { Up = sky.Forward },
            sky with { RayleighHeightMeters = float.NaN },
            sky with { AtmosphereRadiusMeters = sky.PlanetRadiusMeters },
            sky with { RayleighExtinction = Vector3.Zero },
            sky with { SunDirection = new Vector3(2, 0, 0) },
        })
        {
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame, admission.TrySubmit(frame with { Sequence = 2, Sky = invalid }));
            var view = new VolumeViewIdentity(1, 1, PlanetLocalFrame.Create(default));
            var camera = new VolumeCamera(view, default, sky.Forward, sky.Up, sky.VerticalFieldOfView,
                0.1f, 100, Vector4.One, Vector4.Zero) { Sky = invalid };
            Assert.IsFalse(D3D12Renderer.TryCreateVolumeFrame(camera, 2, out _));
            Assert.AreEqual(1UL, admission.Read().LastAcceptedSequence);
        }
    }

    private static RenderSky SkyFixture() => new(new(0, 4096, 0), -Vector3.UnitZ, Vector3.UnitY,
        1f, 4096, 4352, 72, 24, new(0.0012f, 0.0026f, 0.005f), new(0.002f),
        new(0.0014f, 0.003f, 0.006f), new(0.0024f), Vector3.UnitY, 16, 0.009f, 42);

    private static void VerifySky(bool warp)
    {
        var requireDebug = !warp || Environment.GetEnvironmentVariable("GROUNDWORK_SKY_REQUIRE_DEBUG") == "1";
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true);
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            if (requireDebug)
                Assert.IsTrue(gpu.DebugLayerEnabled);
            Assert.IsTrue(gpu.DredEnabled);
            var sky = SkyFixture();
            var noon = Read(sky);
            var dark = Read(sky with { SunDirection = -Vector3.UnitY });
            var twilight = Read(sky with { SunDirection = Vector3.Normalize(new(1, -0.04f, 0)) });
            Assert.IsGreaterThan(twilight.ColorAt(80, 24)[2], noon.ColorAt(80, 24)[2]);
            Assert.IsGreaterThan(dark.ColorAt(80, 24)[0], twilight.ColorAt(80, 24)[0]);

            var stars = Read(sky with { SunDirection = -Vector3.UnitY, SunRadiance = 0 });
            var otherStars = Read(sky with { SunDirection = -Vector3.UnitY, SunRadiance = 0, StarSeed = 99 });
            var bright = 0;
            var changed = 0;
            for (var y = 0; y < 45; y++)
            for (var x = 0; x < 160; x++)
            {
                if (stars.ColorAt(x, y)[0] > 8)
                    bright++;
                if (!stars.ColorAt(x, y).SequenceEqual(otherStars.ColorAt(x, y)))
                    changed++;
            }
            Assert.IsGreaterThan(0, bright);
            Assert.IsGreaterThan(0, changed);

            var orbit = sky with { EyeMeters = new(0, 0, 8192), SunDirection = Vector3.UnitX, VerticalFieldOfView = 1.25f };
            var limb = Read(orbit);
            var unlitLimb = Read(orbit with { SunRadiance = 0 });
            Assert.AreEqual((byte)0, unlitLimb.ColorAt(80, 48)[2]);
            Assert.IsGreaterThan(unlitLimb.ColorAt(116, 48)[2], limb.ColorAt(116, 48)[2]);

            Assert.AreEqual(1f, limb.DepthAt(116, 48));

            var surface = sky with { EyeMeters = new(0, 4096, 0), Forward = -Vector3.UnitY, Up = Vector3.UnitZ,
                SunDirection = -Vector3.UnitY };
            Assert.AreEqual((byte)0, Read(surface).ColorAt(80, 48)[0]);
            var outward = surface with { Forward = Vector3.UnitY, SunDirection = Vector3.UnitY };
            Assert.IsGreaterThan((byte)100, Read(outward).ColorAt(80, 48)[0]);

            var shell = sky with { Forward = Vector3.UnitY, Up = Vector3.UnitZ, StarSeed = 91 };
            var inside = Read(shell with { EyeMeters = new(0, sky.AtmosphereRadiusMeters - 0.01f, 0) });
            var outside = Read(shell with { EyeMeters = new(0, sky.AtmosphereRadiusMeters + 0.01f, 0) });
            for (var channel = 0; channel < 3; channel++)
                Assert.AreEqual((float)outside.ColorAt(80, 48)[channel], inside.ColorAt(80, 48)[channel], 1f);

            var frame = PlanetLocalFrame.Create(default);
            Assert.IsTrue(frame.TryRebase(new PlanetVector(32, 0, 0), out var shifted));
            var diskSky = sky with { EyeMeters = new(0, 0, 8192), SunDirection = -Vector3.UnitZ };
            var camera = new VolumeCamera(new(1, 1, frame), default, diskSky.Forward, diskSky.Up,
                diskSky.VerticalFieldOfView, 0.1f, 100, Vector4.One, Vector4.Zero) { Sky = diskSky };
            // Look into space with the sun centred, while a fogged cube covers part of the sky.
            camera = camera with
            {
                Sky = diskSky with { EyeMeters = new(0, 0, -8192) },
                Fog = new(Vector3.UnitX, 0, 8, 1),
                Position = new(0, 0, 4),
            };
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 1, out var first));
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera with { View = camera.View with { Frame = shifted } },
                2, out var second));
            var before = GpuDevice.RenderOffscreen(gpu, 1920, 1080, first with { Scene = null });
            var after = GpuDevice.RenderOffscreen(gpu, 1920, 1080, second with { Scene = null });
            var a = DiskCenter(before);
            var b = DiskCenter(after);
            Assert.AreEqual(0f, Vector2.Distance(a, b), 1f);
            Console.WriteLine($"Solar disk centroid at 1080p: {a} -> {b}.");
            var terrain = GpuDevice.RenderOffscreen(gpu, 160, 96, first);
            var noSky = GpuDevice.RenderOffscreen(gpu, 160, 96, first with { Sky = null });
            Assert.IsLessThan(1f, terrain.DepthAt(80, 48));
            Assert.AreEqual(noSky.DepthAt(80, 48), terrain.DepthAt(80, 48));
            Assert.IsTrue(noSky.ColorAt(80, 48).SequenceEqual(terrain.ColorAt(80, 48)));
            Assert.IsNull(gpu.CaptureDred());

            OffscreenFrame Read(RenderSky input) => GpuDevice.RenderOffscreen(gpu, 160, 96,
                new RenderFrame(1, Vector4.Zero) { Sky = input });
        }
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);
    }

    private static Vector2 DiskCenter(OffscreenFrame image)
    {
        var count = 0;
        var sum = Vector2.Zero;
        for (var y = 500; y < 580; y++)
        for (var x = 920; x < 1000; x++)
        {
            if (image.ColorAt(x, y)[0] < 220)
                continue;
            sum += new Vector2(x, y);
            count++;
        }
        Assert.IsGreaterThan(100, count);
        return sum / count;
    }
}
