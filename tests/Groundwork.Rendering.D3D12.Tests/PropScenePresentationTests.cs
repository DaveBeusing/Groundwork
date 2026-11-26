using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task WarpPropSceneRequiresResidentCellsAndARealPresent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        using var run = new VolumeRun(editable: true, openFloor: true);
        var runtime = run.Fixture.Runtime;
        var limit = TimeSpan.FromSeconds(10);
        var context = new EngineModuleContext(run.Fixture.Clock, new RuntimeTimingLimits(limit, limit, limit),
            runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork prop scene {Guid.NewGuid():N}",
            TerrainWidth, TerrainHeight, 32), runtime.UiWake, runtime.RuntimeOwnership);
        var frames = Enumerable.Range(0, 9).Select(_ =>
            new TaskCompletionSource<OffscreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics with { EnableResourceSnapshots = true }, true, TimeSpan.FromSeconds(15), null, null)
        {
            VolumePresentReadbackForTests = (sequence, _, image) =>
            {
                if (sequence < (ulong)frames.Length)
                    frames[sequence].TrySetResult(image);
            },
        };
        var mesh = RenderMesh.Create([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], [0, 1, 2]);
        var solid = new D3D12PropAsset(new(1, 1, 2, 40, 20, 24), mesh, mesh);
        var ghost = new D3D12PropAsset(new(2, 2, 2, 40, 20, 24), mesh, mesh) { Transparent = true };
        var props = new D3D12PropCells(new(2, 2, 2, 2, 4), 1, 42, 1, [solid, ghost]);
        renderer.AttachPropCells(props);
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
            using var lease = run.Terrain.TryRead();
            Assert.IsNotNull(lease);
            var pub = new PropPublication(run.Terrain.WorldIdentity, 1, lease.Info.TerrainRevision, lease.Info.Generation);
            var key = new PropCellKey(1, 0, 0);
            var preview = new PropCellKey(2, 0, 0);
            var camera = VolumeCameraAt(view, 32, 7);
            props.Publish(pub, [key]);
            Assert.IsTrue(props.TryReviseCoverage(pub, [key], [], [key], 7));
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TrySubmitVolumeTerrain(pub.CoverageGeneration, 2, camera));
            var background = await frames[2].Task.WaitAsync(token);
            Assert.IsFalse(props.HasPresentedScene(pub, 7, 2), "The terrain-only Present cannot release a pending prop scene.");

            Stage(key, 0, new(32, 32, 3), 0xff0000ff);
            Assert.IsFalse(props.HasPresentedScene(pub, 7, 2), "Staging a CPU cell cannot acknowledge a Present.");
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(pub.CoverageGeneration, 3, camera));
            var opaque = await frames[3].Task.WaitAsync(token);
            Assert.IsTrue(props.HasPresentedScene(pub, 7, 3));
            Assert.IsFalse(props.HasPresentedScene(pub, 7, 4));

            Assert.IsTrue(props.TryReviseCoverage(pub, [key, preview], [], [key, preview], 8, preview));
            Assert.IsFalse(props.HasPresentedScene(pub, 8, 3), "The new preview cannot reuse the previous scene's Present.");
            Stage(preview, 1, new(320, 32, 5), 0xffff0000);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(pub.CoverageGeneration, 4, camera));
            await frames[4].Task.WaitAsync(token);
            Assert.IsFalse(props.HasPresentedScene(pub, 8, 4), "The culled preview cannot receive a draw receipt.");

            Assert.IsTrue(props.TryReviseCoverage(pub, [key, preview], [preview], [key, preview], 9, preview));
            Stage(preview, 1, new(32, 32, 5), 0xffff0000);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(pub.CoverageGeneration, 5, camera));
            var blended = await frames[5].Task.WaitAsync(token);
            Assert.IsTrue(props.HasPresentedScene(pub, 9, 5));
            var x = renderer.Read().AppliedWidth / 2;
            var y = renderer.Read().AppliedHeight / 2;
            Assert.IsLessThan(background.DepthAt(x, y), opaque.DepthAt(x, y));
            Assert.AreEqual(opaque.DepthAt(x, y), blended.DepthAt(x, y));
            var colors = $"Background {Convert.ToHexString(background.ColorAt(x, y))}, opaque {Convert.ToHexString(opaque.ColorAt(x, y))}, blended {Convert.ToHexString(blended.ColorAt(x, y))}.";
            Assert.IsTrue(blended.ColorAt(x, y)[0] > 0 && blended.ColorAt(x, y)[0] < opaque.ColorAt(x, y)[0], colors);
            Assert.IsTrue(blended.ColorAt(x, y)[2] > opaque.ColorAt(x, y)[2] && blended.ColorAt(x, y)[2] < 255, colors);

            Assert.IsTrue(props.TryReviseCoverage(pub, [], [], [], 10));
            Assert.IsFalse(props.HasPresentedScene(pub, 10, 5));
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(pub.CoverageGeneration, 6, camera));
            var removed = await frames[6].Task.WaitAsync(token);
            Assert.IsTrue(props.HasPresentedScene(pub, 10, 6));
            Assert.IsFalse(props.HasPresentedScene(pub, 9, 6));
            Assert.IsTrue(background.ColorAt(x, y).SequenceEqual(removed.ColorAt(x, y)));
            Assert.AreEqual(background.DepthAt(x, y), removed.DepthAt(x, y));
            Console.WriteLine(FormattableString.Invariant(
                $"Present 2 terrain only, 3 opaque, 4 culled preview, 5 blended preview, 6 removed. {colors} Depth {background.DepthAt(x, y):R} -> {opaque.DepthAt(x, y):R} -> {blended.DepthAt(x, y):R} -> {removed.DepthAt(x, y):R}."));
            var skyCamera = camera with { FarPlane = 4, Sky = SkyFixture() };
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(pub.CoverageGeneration, 7, skyCamera));
            var skyBackground = await frames[7].Task.WaitAsync(token);
            Assert.IsTrue(props.TryReviseCoverage(pub, [preview], [preview], [preview], 11, preview));
            Stage(preview, 1, new(32, 32, 5), 0xffff0000);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(pub.CoverageGeneration, 8, skyCamera));
            var skyWithPreview = await frames[8].Task.WaitAsync(token);
            Assert.IsTrue(props.HasPresentedScene(pub, 11, 8));
            Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var observations));
            Assert.AreEqual(8UL, observations.FrameSequence);
            Assert.IsTrue(observations.TryGetPass(GpuPassPlan.TransparentProps, out var previewPass));
            Assert.AreEqual(2, previewPass.Barriers);
            Assert.AreEqual(4, previewPass.ResourceUses);

            var previewPixelsOverSky = 0;
            var viewport = renderer.Read();
            for (var py = 0; py < viewport.AppliedHeight; py++)
                for (var px = 0; px < viewport.AppliedWidth; px++)
                {
                    if (skyBackground.DepthAt(px, py) != 1)
                        continue;
                    Assert.AreEqual(1f, skyWithPreview.DepthAt(px, py));
                    if (!skyBackground.ColorAt(px, py).SequenceEqual(skyWithPreview.ColorAt(px, py)))
                        previewPixelsOverSky++;
                }
            Assert.IsGreaterThan(0, previewPixelsOverSky, "The preview must blend over sky without writing depth.");
            await renderer.StopAsync(token);
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }

        void Stage(PropCellKey cell, int model, PropPoint origin, uint tint)
        {
            Assert.AreEqual(PropCellResult.Accepted, props.TryRequest(cell, 1, out var request));
            Assert.AreEqual(PropCellResult.Accepted, props.TryStage(request, origin,
                [new(0, model, Vector3.Zero, Quaternion.Identity, 1, tint)]));
        }
    }
}
