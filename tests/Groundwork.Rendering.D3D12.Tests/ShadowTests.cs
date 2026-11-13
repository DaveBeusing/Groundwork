using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public void WarpShadowReadbackCoversDirectionSplitRebaseAndReplacement() => VerifyShadows(true, false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void ShadowReadbackWithDebugLayerAndDred(bool warp) => VerifyShadows(warp, true);

    private static void VerifyShadows(bool warp, bool debug)
    {
        var origin = PlanetLocalFrame.Create(default);
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, region: Region(origin, true));
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DredEnabled);
            if (debug)
                Assert.IsTrue(gpu.DebugLayerEnabled);
            var positive = Vector3.Normalize(new Vector3(1, 0, 1));
            var negative = Vector3.Normalize(new Vector3(-1, 0, 1));
            var left = Read(origin, positive, 0);
            var right = Read(origin, negative, 0);
            Assert.IsGreaterThan(left.ColorAt(65, 48)[0] + 80, left.ColorAt(95, 48)[0]);
            Assert.IsGreaterThan(right.ColorAt(95, 48)[0] + 80, right.ColorAt(65, 48)[0]);

            var fogged = Read(origin, positive, 0, fog: true);
            Assert.IsGreaterThan((byte)40, fogged.ColorAt(65, 48)[2]);
            Assert.IsGreaterThan(left.ColorAt(95, 48)[2], fogged.ColorAt(95, 48)[2]);

            // Move the same receiver through 22, 24 and 26 m of positive view depth.
            foreach (var depth in new[] { 21.9, 22.1, 24.0, 25.9, 26.1 })
            {
                var image = Read(origin, positive, depth - 24);
                Assert.IsGreaterThan(image.ColorAt(65, 48)[0] + 80, image.ColorAt(95, 48)[0], $"Depth {depth}");
            }

            Assert.IsTrue(origin.TryRebase(new PlanetVector(32, -16, 8), out var shifted));
            Replace(Region(shifted, true));
            var rebased = Read(shifted, positive, 0);
            for (var x = 60; x <= 100; x++)
                Assert.AreEqual((float)left.ColorAt(x, 48)[0], rebased.ColorAt(x, 48)[0], 1f, $"x={x}");

            // Only the replacement's buffers are available to either depth pass.
            Replace(Region(shifted, false));
            var edited = Read(shifted, positive, 0);
            var lowSun = Vector3.Normalize(new Vector3(1, 0, 0.10f));
            var lowLit = Read(shifted, lowSun, 0, shadows: false);
            var lowShadowed = Read(shifted, lowSun, 0);
            for (var x = 55; x <= 105; x++)
                Assert.AreEqual((float)lowLit.ColorAt(x, 48)[0], lowShadowed.ColorAt(x, 48)[0], 1f, $"Low sun x={x}");
            Assert.IsGreaterThan(left.ColorAt(65, 48)[0] + 80, edited.ColorAt(65, 48)[0]);
            Assert.IsNull(gpu.CaptureDred());
        }
        Assert.AreEqual(0, gpu.LiveMeshBuffers);
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);

        void Replace(RenderMeshRegion region)
        {
            var old = gpu.ActiveMeshGeometry;
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(region);
            old?.Dispose();
        }

        OffscreenFrame Read(PlanetLocalFrame local, Vector3 sun, double eyeZ, bool fog = false, bool shadows = true)
        {
            var camera = new VolumeCamera(new(1, 1, local), new(0, 0, eyeZ), -Vector3.UnitZ, Vector3.UnitY,
                0.65f, 0.1f, 100, Vector4.One, Vector4.Zero)
            {
                Sunlight = new(sun, new(0.85f), new(0.12f)), TerrainShadows = shadows,
                Fog = fog ? new(Vector3.UnitZ, 0, 64, 1) : null,
            };
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 1, out var frame));
            return GpuDevice.RenderOffscreen(gpu, 160, 96, frame);
        }

        static RenderMeshRegion Region(PlanetLocalFrame local, bool caster)
        {
            var translation = local.ToLocal(default);
            var floor = new RenderMeshInstance(Quad(8, 5, -24), translation);
            return new(caster ? [floor, new(Quad(1, 1, -21.5f), translation)] : [floor]);
        }

        static RenderMesh Quad(float x, float y, float z)
        {
            var normal = PackedTerrainNormal.Encode(Vector3.UnitZ);
            return RenderMesh.CreateWithTerrainNormals([new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z)],
                [0, 1, 2, 0, 2, 3], [], [normal, normal, normal, normal]);
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TerrainEditFirstPresentContainsNewShadowWithDebugLayerAndDred(bool warp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        using var run = new VolumeRun(editable: true, openFloor: true);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork shadow edit {Guid.NewGuid():N}",
            TerrainWidth, TerrainHeight, 32), runtime.UiWake, runtime.RuntimeOwnership);
        var frames = Enumerable.Range(0, 7)
            .Select(_ => new TaskCompletionSource<(ulong Generation, OffscreenFrame Pixels)>(
                TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics, warp, TimeSpan.FromSeconds(15), null, null)
        {
            VolumePresentReadbackForTests = (sequence, generation, pixels) =>
                frames[checked((int)sequence)].TrySetResult((generation, pixels)),
        };
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);

            var view = run.Terrain.View;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 1, Vector4.Zero));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            using var previous = run.Terrain.TryRead();
            Assert.IsNotNull(previous);
            var beforeCamera = Camera(view);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(previous.Info.Generation, 2, beforeCamera));
            var before = await Read(2, previous.Info.Generation);
            Assert.AreEqual(RenderSubmissionResult.Accepted,
                renderer.TrySubmitVolumeCamera(previous.Info.Generation, 3, beforeCamera with { TerrainShadows = false }));
            var beforeLit = await Read(3, previous.Info.Generation);

            var next = await run.CutFloor(renderer, token,
                new VolumeBrushIntent(1, 1, 1, new(32, 30, -1), new(35, 34, 2), VolumeBrushOperation.Add, 1),
                maskSequence: 4);
            using var edited = run.Terrain.TryRead();
            Assert.IsNotNull(edited);
            Assert.AreEqual(previous.Info.TerrainRevision + 1, edited.Info.TerrainRevision);
            Assert.AreNotEqual(previous.Info.Generation, edited.Info.Generation);
            var afterCamera = Camera(next);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(edited.Info.Generation, 5, afterCamera));
            var after = await Read(5, edited.Info.Generation);
            Assert.AreEqual(RenderSubmissionResult.Accepted,
                renderer.TrySubmitVolumeCamera(edited.Info.Generation, 6, afterCamera with { TerrainShadows = false }));
            var afterLit = await Read(6, edited.Info.Generation);

            var newlyShadowed = 0;
            var status = renderer.Read();
            for (var y = 0; y < status.AppliedHeight; y++)
            for (var x = 0; x < status.AppliedWidth; x++)
            {
                if (before.DepthAt(x, y) < 1 &&
                    Math.Abs(before.DepthAt(x, y) - after.DepthAt(x, y)) <= 0.000001f &&
                    beforeLit.ColorAt(x, y).SequenceEqual(afterLit.ColorAt(x, y)) &&
                    Math.Abs(beforeLit.ColorAt(x, y)[0] - before.ColorAt(x, y)[0]) <= 1 &&
                    before.ColorAt(x, y)[0] - after.ColorAt(x, y)[0] > 4)
                    newlyShadowed++;
            }
            Assert.IsGreaterThan(0, newlyShadowed, "The first presented edit frame must darken unchanged receivers.");
            await renderer.StopAsync(token);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, warp);
            AssertCleanDiagnostics(diagnostics, diagnosticsRequested: true);
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }

        static VolumeCamera Camera(VolumeViewIdentity identity) => VolumeCameraAt(identity, 32, 7) with
        {
            Sunlight = new(Vector3.Normalize(new Vector3(1, 0.25f, 0.6f)), Vector3.One, new(0.12f)),
            TerrainShadows = true,
        };

        async Task<OffscreenFrame> Read(ulong sequence, ulong generation)
        {
            var capture = await frames[checked((int)sequence)].Task.WaitAsync(token);
            Assert.AreEqual(sequence, renderer.Read().LastPresentedSequence);
            Assert.AreEqual(generation, capture.Generation);
            return capture.Pixels;
        }
    }

    [TestMethod]
    public void ShadowCascadeFittingHandlesClippedBandsAndCameraTurns()
    {
        var scene = new RenderScene(Matrix4x4.Identity, Vector4.One, Matrix4x4.Identity, 0.7f, 28, 100)
            { ShadowOrigin = new(-4096, 512, -16) };
        var basis = ShadowBasis.Create(Vector3.Normalize(new(0.02f, 1, 0.03f)));
        Assert.IsFalse(basis.Fit(scene, 16f / 9, 0, 26).Valid);
        var straight = basis.Fit(scene, 16f / 9, 22, 64);
        var turned = basis.Fit(scene with { View = Matrix4x4.CreateRotationY(1.4f) }, 16f / 9, 22, 64);
        Assert.IsTrue(straight.Valid && turned.Valid);
        Assert.AreEqual(straight.Area.Z, turned.Area.Z);
        Assert.IsFalse(basis.Fit(scene with { FarPlane = 20, NearPlane = 0.1f }, 1, 22, 64).Valid);
    }
}
