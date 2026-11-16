using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Rendering.Materials;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Mat02MaterialChangeRetainsMeshAndFirstEditPresentUsesCommittedGeneration(bool warp) =>
        await VerifyMat02Volume(warp, failPipelineCreation: false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Mat02PipelineAllocationFailurePresentsProtectedTerrainWithLegacyColor(bool warp) =>
        await VerifyMat02Volume(warp, failPipelineCreation: true);

    private static async Task VerifyMat02Volume(bool warp, bool failPipelineCreation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        using var run = new VolumeRun(editable: true);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork material edit {Guid.NewGuid():N}",
            TerrainWidth, TerrainHeight, 32), runtime.UiWake, runtime.RuntimeOwnership);
        var frames = Enumerable.Range(0, 6)
            .Select(_ => new TaskCompletionSource<(ulong Generation, OffscreenFrame Pixels)>(
                TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var packedVertices = 0;
        var pbrAttempts = 0;
        var hooks = failPipelineCreation ? new GpuRetirementTestHooks(PbrPipelineCreationFailure: shadows =>
        {
            Assert.IsFalse(shadows);
            Interlocked.Increment(ref pbrAttempts);
            return unchecked((int)0x8007000E);
        }) : null;
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics with { EnableResourceSnapshots = true }, warp, TimeSpan.FromSeconds(15),
            null, hooks, (position, material) =>
            {
                Interlocked.Increment(ref packedVertices);
                return TestVolumeColor(position, material);
            })
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
            using var first = run.Terrain.TryRead();
            Assert.IsNotNull(first);
            var generation = first.Info.Generation;
            var red = new Vector3(0.24f, 0, 0);
            var camera = VolumeCameraAt(view, 32, 4) with { TerrainMaterial = Emissive(red) };
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TrySubmitVolumeTerrain(generation, 2, camera));
            var before = await Read(2, generation);
            var status = renderer.Read();
            var x = status.AppliedWidth / 2;
            var y = status.AppliedHeight / 2;
            Assert.IsLessThan(1f, before.DepthAt(x, y));
            AssertTerrainColor(before, red, "original material");

            var root = run.World.Publication.Committed.Read().Root;
            var vertices = Volatile.Read(ref packedVertices);
            Assert.IsGreaterThan(0, vertices);
            var baseline = TerrainBalance(2);
            var usage = run.Terrain.Statistics;
            while (run.Terrain.TryReadEvent(out _)) { }

            var green = new Vector3(0, 0.43f, 0);
            Assert.AreEqual(RenderSubmissionResult.Accepted,
                renderer.TrySubmitVolumeCamera(generation, 3, camera with { TerrainMaterial = Emissive(green) }));
            var appearance = await Read(3, generation);
            AssertTerrainColor(appearance, green, "material-only frame");
            Assert.AreEqual(before.DepthAt(x, y), appearance.DepthAt(x, y));
            Assert.AreEqual(vertices, Volatile.Read(ref packedVertices), "A material-only frame must reuse the packed mesh.");
            Assert.AreEqual(baseline, TerrainBalance(3), "No new native terrain upload or release.");
            Assert.AreSame(root, run.World.Publication.Committed.Read().Root);
            Assert.AreEqual(usage.Used, run.Terrain.Statistics.Used);
            Assert.AreEqual(usage.Generations, run.Terrain.Statistics.Generations);
            var registered = false;
            while (run.Terrain.TryReadEvent(out var row))
            {
                Assert.IsFalse(row.Kind is VolumeEventKind.Admitted or VolumeEventKind.WorkReady or
                    VolumeEventKind.NativeReady or VolumeEventKind.Commit, $"Unexpected terrain work: {row.Kind}");
                registered |= row.Kind == VolumeEventKind.Registered && row.Generation == generation && row.Frame == 3;
            }
            Assert.IsTrue(registered);

            var next = await run.CutFloor(renderer, token, maskSequence: 4);
            using var edited = run.Terrain.TryRead();
            Assert.IsNotNull(edited);
            Assert.AreNotEqual(generation, edited.Info.Generation);
            Assert.AreEqual(first.Info.TerrainRevision + 1, edited.Info.TerrainRevision);
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame, renderer.TrySubmitVolumeCamera(generation, 5, camera));
            var blue = new Vector3(0, 0, 0.6f);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TrySubmitVolumeTerrain(edited.Info.Generation,
                5, VolumeCameraAt(next, 32, 4) with { TerrainMaterial = Emissive(blue) }));
            var after = await Read(5, edited.Info.Generation);
            Assert.IsLessThan(1f, after.DepthAt(x, y));
            Assert.IsGreaterThan(before.DepthAt(x, y), after.DepthAt(x, y));
            AssertTerrainColor(after, blue, "first present of edited terrain");
            Assert.AreEqual(failPipelineCreation ? 1 : 0, Volatile.Read(ref pbrAttempts));

            void AssertTerrainColor(OffscreenFrame frame, Vector3 emissive, string name)
            {
                var pixel = frame.ColorAt(x, y);
                if (failPipelineCreation)
                {
                    Assert.AreEqual((byte)0xcc, pixel[0], name);
                    Assert.AreEqual((byte)255, pixel[3], name);
                    return;
                }

                AssertColor(pixel, emissive, 255, name);
            }

            await renderer.StopAsync(token);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, warp);
            AssertCleanDiagnostics(diagnostics, diagnosticsRequested: true);
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }

        static PbrMaterialDescriptor Emissive(Vector3 color) => new(Vector3.Zero, 0, 0.5f, 1, color, 1);

        async Task<OffscreenFrame> Read(int sequence, ulong generation)
        {
            var capture = await frames[sequence].Task.WaitAsync(token);
            Assert.AreEqual((ulong)sequence, renderer.Read().LastPresentedSequence);
            Assert.AreEqual(generation, capture.Generation);
            return capture.Pixels;
        }

        D3D12ResourceBalance TerrainBalance(ulong sequence)
        {
            D3D12ResourceSnapshot snapshot = default;
            Assert.IsTrue(SpinWait.SpinUntil(() => renderer.TryReadResourceDiagnostics(out snapshot) &&
                snapshot.RetiredPresentSequence == sequence, TimeSpan.FromSeconds(2)));
            return snapshot.GetBalance(D3D12ResourceOwner.Terrain);
        }
    }
}
