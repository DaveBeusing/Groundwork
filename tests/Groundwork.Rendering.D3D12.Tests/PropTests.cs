using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public void WarpPropInstancingReadbackSurvivesRebaseAndSaturation() => VerifyProps(true, false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void PropInstancingReadbackWithDebugLayerAndDred(bool warp) => VerifyProps(warp, true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task EditedTerrainFirstPresentSuppressesObsoleteProps(bool warp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        using var run = new VolumeRun(editable: true, openFloor: true);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork props {Guid.NewGuid():N}",
            TerrainWidth, TerrainHeight, 32), runtime.UiWake, runtime.RuntimeOwnership);
        var before = new TaskCompletionSource<OffscreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var after = new TaskCompletionSource<OffscreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics, warp, TimeSpan.FromSeconds(15), null, null)
        {
            VolumePresentReadbackForTests = (sequence, _, pixels) =>
            {
                if (sequence == 2)
                    before.TrySetResult(pixels);
                if (sequence == 4)
                    after.TrySetResult(pixels);
            },
        };
        var mesh = RenderMesh.Create([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], [0, 1, 2]);
        var props = new D3D12PropCells(new(1, 1, 1, 1, 2), 1, 42, 1,
            [new(new(1, 1, 2, 40, 20, 24), mesh, mesh)]);
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
            using var original = run.Terrain.TryRead();
            Assert.IsNotNull(original);
            var key = new PropCellKey(0, 0, 0);
            props.Publish(new(run.Terrain.WorldIdentity, 1, original.Info.TerrainRevision, original.Info.Generation), [key]);
            Assert.AreEqual(PropCellResult.Accepted, props.TryRequest(key, 1, out var request));
            Assert.AreEqual(PropCellResult.Accepted, props.TryStage(request, new(32, 32, 3),
                [new(0, 0, Vector3.Zero, Quaternion.Identity, 1, 0xffff00ff)]));
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(original.Info.Generation, 2, VolumeCameraAt(view, 32, 7)));
            var first = await before.Task.WaitAsync(token);
            Assert.AreEqual(1, props.ReadStatistics().Draws);

            var next = await run.CutFloor(renderer, token, maskSequence: 3);
            using var edited = run.Terrain.TryRead();
            Assert.IsNotNull(edited);
            Assert.AreNotEqual(original.Info.TerrainRevision, edited.Info.TerrainRevision);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(edited.Info.Generation, 4, VolumeCameraAt(next, 32, 7)));
            var editFrame = await after.Task.WaitAsync(token);
            Assert.AreEqual(4UL, renderer.Read().LastPresentedSequence);
            Assert.AreEqual(0, props.ReadStatistics().Draws);
            var x = renderer.Read().AppliedWidth / 2;
            var y = renderer.Read().AppliedHeight / 2;
            Assert.AreEqual((byte)255, first.ColorAt(x, y)[0]);
            Assert.AreEqual((byte)255, first.ColorAt(x, y)[2]);
            Assert.IsFalse(first.ColorAt(x, y).SequenceEqual(editFrame.ColorAt(x, y)));

            await renderer.StopAsync(token);
            Assert.AreEqual(0L, props.ReadStatistics().NativeBytes);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, warp);
            AssertCleanDiagnostics(diagnostics, diagnosticsRequested: true);
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void PropUploadBudgetDefersWholeAssets(bool warp)
    {
        var vertices = new Vector3[70_000];
        vertices[0] = new(-1, -1, 0);
        vertices[1] = new(1, -1, 0);
        vertices[2] = new(0, 1, 0);
        var mesh = RenderMesh.Create(vertices, [0, 1, 2]);
        var props = new D3D12PropCells(new(1, 1, 1, 1, 2), 1, 42, 1,
            [new(new(1, 1, 2, 40, 20, 24), mesh, mesh)]);
        var view = new VolumeViewIdentity(1, 1, PlanetLocalFrame.Create(default));
        var key = new PropCellKey(0, 0, 0);
        props.Publish(new(Guid.NewGuid(), 1, 1, 1), [key]);
        Assert.AreEqual(PropCellResult.Accepted, props.TryRequest(key, 1, out var request));
        Assert.AreEqual(PropCellResult.Accepted, props.TryStage(request, new(0, 0, -5),
            [new(0, 0, Vector3.Zero, Quaternion.Identity, 1, uint.MaxValue)]));
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true,
            region: new([new(RenderMesh.Create([], []), Vector3.Zero)]));
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DebugLayerEnabled && gpu.DredEnabled);
            gpu.PropStream = props;
            gpu.PropTerrain = new(1, 1, view, default, 1, "props");
            var camera = new VolumeCamera(view, default, -Vector3.UnitZ, Vector3.UnitY,
                MathF.PI / 4, 0.1f, 100, Vector4.One, Vector4.Zero);
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 1, out var frame));
            var unloaded = GpuDevice.RenderOffscreen(gpu, 64, 64, frame);
            var first = props.ReadStatistics();
            Assert.AreEqual(70_000 * 16 + 12, first.PayloadBytes);
            Assert.AreEqual(1, first.DeferredCells);
            Assert.AreEqual(1f, unloaded.DepthAt(32, 32));
            var loaded = GpuDevice.RenderOffscreen(gpu, 64, 64, frame with { Sequence = 2 });
            var second = props.ReadStatistics();
            Assert.AreEqual(first.PayloadBytes + PropInstanceData.ByteSize, second.PayloadBytes);
            Assert.AreEqual(1, second.Draws);
            Assert.AreEqual(0, second.DeferredCells);
            Assert.IsLessThanOrEqualTo(D3D12PropCells.MaximumUploadBytes, second.UploadBytes);
            Assert.IsLessThan(1f, loaded.DepthAt(32, 32));
            Assert.IsNull(gpu.CaptureDred());
        }
        Assert.AreEqual(0L, props.ReadStatistics().NativeBytes);
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);
    }

    private static void VerifyProps(bool warp, bool requireDebug)
    {
        var mesh = RenderMesh.Create([new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(0, 0.5f, 0)], [0, 1, 2]);
        var props = new D3D12PropCells(new(3, 2, 4, 2, 2), 2, 42, 1,
            [new(new(1, 1, 1, 40, 20, 24), mesh, mesh)]);
        var key = new PropCellKey(0, 0, 0);
        var second = new PropCellKey(1, 0, 0);
        var third = new PropCellKey(2, 0, 0);
        var publication = new PropPublication(Guid.Parse("11111111-3333-5555-7777-999999999999"), 1, 1, 1);
        props.Publish(publication, [key, second, third]);
        Assert.AreEqual(PropCellResult.Accepted, props.TryRequest(key, 2, out var initial));
        Assert.AreEqual(PropCellResult.Accepted, props.TryStage(initial, default,
        [
            new(0, 0, new(-1, 0, -5), Quaternion.Identity, 1, 0xff0000ff),
            new(1, 0, new(1, 0, -5), Quaternion.Identity, 1, 0xff00ff00),
        ]));
        Assert.AreEqual(PropCellResult.Accepted, props.TryRequest(second, 2, out var held));
        Assert.AreEqual(PropCellResult.Quota, props.TryRequest(third, 1, out _));
        Assert.AreEqual(PropCellResult.Accepted, props.TryStage(held, default,
            [new(0, 0, new(0, 0, -5), Quaternion.Identity, 1, uint.MaxValue)]));

        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true,
            region: new([new(RenderMesh.Create([], []), Vector3.Zero)]));
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DredEnabled);
            if (requireDebug)
                Assert.IsTrue(gpu.DebugLayerEnabled);
            gpu.PropStream = props;
            var frame = PlanetLocalFrame.Create(default);
            var first = Draw(frame, 1);
            var initialStats = props.ReadStatistics();
            Assert.AreEqual(1, initialStats.Draws);
            Assert.AreEqual(2, initialStats.Instances);
            Assert.AreEqual(1, initialStats.DeferredCells);
            Assert.IsLessThanOrEqualTo(D3D12PropCells.MaximumUploadBytes, initialStats.UploadBytes);
            Assert.IsGreaterThan((byte)240, first.ColorAt(57, 48)[0]);
            Assert.IsGreaterThan((byte)240, first.ColorAt(103, 48)[1]);
            Assert.IsLessThan(1f, first.DepthAt(57, 48));

            Assert.IsTrue(frame.TryRebase(new PlanetVector(32, -16, 8), out var shifted));
            var rebased = Draw(shifted, 2);
            Assert.AreEqual(0, props.ReadStatistics().PayloadBytes);
            Assert.IsTrue(first.ColorAt(57, 48).SequenceEqual(rebased.ColorAt(57, 48)));
            Assert.IsTrue(first.ColorAt(103, 48).SequenceEqual(rebased.ColorAt(103, 48)));
            Assert.AreEqual(first.DepthAt(57, 48), rebased.DepthAt(57, 48), 0.000001f);

            var edited = Draw(shifted, 3, revision: 2, generation: 2);
            Assert.AreEqual(0, props.ReadStatistics().Draws);
            Assert.AreEqual(1f, edited.DepthAt(57, 48));
            props.Publish(publication with { TerrainRevision = 2, CoverageGeneration = 2 }, [key]);
            Assert.AreEqual(PropCellResult.Stale, props.TryStage(held, default, []));
            Assert.IsNull(gpu.CaptureDred());
        }
        Assert.AreEqual(0L, props.ReadStatistics().NativeBytes);
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), gpu.DebugLayerEnabled);

        OffscreenFrame Draw(PlanetLocalFrame origin, ulong sequence, ulong revision = 1, ulong generation = 1)
        {
            var view = new VolumeViewIdentity(1, sequence, origin);
            gpu.PropTerrain = new(generation, revision, view, default, 1, "props");
            var camera = new VolumeCamera(view, default, -Vector3.UnitZ, Vector3.UnitY,
                MathF.PI / 4, 0.1f, 100, Vector4.One, Vector4.Zero);
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, sequence, out var frame));
            return GpuDevice.RenderOffscreen(gpu, 160, 96, frame);
        }
    }
}
