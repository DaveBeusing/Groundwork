using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PausedAppearanceUsesLocalIdentityAfterProtectedFirstPresent(bool warp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        using var run = new VolumeRun(openFloor: true);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork appearance {Guid.NewGuid():N}",
            TerrainWidth, TerrainHeight, 32), runtime.UiWake, runtime.RuntimeOwnership);
        var stream = new D3D12TextureHandoff(new(2, 2, 4096, 4096, 4096, 512 * 1024, 512 * 1024, 2));
        var first = new TaskCompletionSource<OffscreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var red = new TaskCompletionSource<(ulong Id, ulong Frame, ulong Generation, OffscreenFrame Pixels)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var green = new TaskCompletionSource<(ulong Id, ulong Frame, ulong Generation, OffscreenFrame Pixels)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executeFirst = new ManualResetEventSlim();
        var held = 0;
        var appearancePresents = 0;
        var copies = 0;
        var x = TerrainWidth / 2;
        var y = TerrainHeight / 2;
        var hooks = new GpuRetirementTestHooks(RejectRecordedPlan: sequence =>
        {
            if (sequence == 2 && Interlocked.Exchange(ref held, 1) == 0)
            {
                prepared.TrySetResult();
                executeFirst.Wait(token);
            }
            return false;
        });

        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics, warp, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), hooks)
        {
            BeforeTextureUploadForTests = (_, _) => Interlocked.Increment(ref copies),
            VolumePresentReadbackForTests = (sequence, _, pixels) =>
            {
                if (sequence == 2)
                    first.TrySetResult(pixels);
            },
            AppearancePresentReadbackForTests = (identity, sequence, generation, pixels) =>
            {
                Interlocked.Increment(ref appearancePresents);
                var color = pixels.ColorAt(x, y);
                if (color[0] > 0 && color[1] == 0 && color[2] == 0)
                    red.TrySetResult((identity, sequence, generation, pixels));
                if (color[0] == 0 && color[1] > 0 && color[2] == 0)
                    green.TrySetResult((identity, sequence, generation, pixels));
            },
        };
        renderer.AttachTextureHandoff(stream);
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
            using var installed = run.Terrain.TryRead();
            Assert.IsNotNull(installed);
            var generation = installed.Info.Generation;
            var cut = new D3D12TextureView(run.Terrain.WorldIdentity, installed.Info.View,
                installed.Info.TerrainRevision, generation);
            var committed = run.World.Publication.Committed.Read();
            var camera = VolumeCameraAt(view, 32, 7);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(generation, 2, camera));
            await prepared.Task.WaitAsync(token);
            var slot = new D3D12TextureSlot(1, MaterialTextureSemantic.BaseColor);
            var redTexture = MakeStaticColor(255, 0);
            var greenTexture = MakeStaticColor(0, 255);
            // Both requests become ready while the protected first draw is held.
            // The later palette supersedes the unconsumed worker result.
            Stage(1, greenTexture);
            Stage(2, redTexture);
            Assert.AreEqual(2UL, renderer.Read().LastAcceptedSequence);
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
            Assert.AreEqual(0, Volatile.Read(ref appearancePresents));
            Assert.AreEqual(0, Volatile.Read(ref copies));

            executeFirst.Set();
            var firstPixels = await first.Task.WaitAsync(token);
            var initial = await red.Task.WaitAsync(token);
            Assert.AreEqual(1UL, initial.Id);
            Assert.AreEqual(2UL, initial.Frame);
            Assert.AreEqual(generation, initial.Generation);
            Assert.AreEqual(1, Volatile.Read(ref copies));
            Assert.IsLessThan(1f, firstPixels.DepthAt(x, y));
            Assert.AreEqual(firstPixels.DepthAt(x, y), initial.Pixels.DepthAt(x, y));
            Assert.IsFalse(firstPixels.ColorAt(x, y).SequenceEqual(initial.Pixels.ColorAt(x, y)));

            Stage(3, greenTexture);
            var updated = await green.Task.WaitAsync(token);
            Assert.IsGreaterThan(initial.Id, updated.Id);
            Assert.AreEqual(2UL, updated.Frame);
            Assert.AreEqual(generation, updated.Generation);
            Assert.AreEqual(initial.Pixels.DepthAt(x, y), updated.Pixels.DepthAt(x, y));
            Assert.AreEqual(2UL, renderer.Read().LastAcceptedSequence);
            Assert.AreEqual(2UL, renderer.Read().LastPresentedSequence);
            Assert.AreSame(committed.Root, run.World.Publication.Committed.Read().Root);
            Assert.AreEqual(committed.Stamp, run.World.Publication.Committed.Read().Stamp);
            var registrations = 0;
            var receipts = 0;
            while (run.Terrain.TryReadEvent(out var entry))
            {
                if (entry.Generation != generation)
                    continue;
                if (entry.Kind == VolumeEventKind.Registered)
                {
                    Assert.AreEqual(2UL, entry.Frame);
                    registrations++;
                }
                if (entry.Kind == VolumeEventKind.Accepted)
                    receipts++;
            }
            Assert.AreEqual(1, registrations);
            Assert.AreEqual(1, receipts);

            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(generation, 3, camera));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 3, token);
            await renderer.StopAsync(token);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, warp);
            AssertCleanDiagnostics(diagnostics, diagnosticsRequested: true);

            void Stage(ulong revision, CookedTexture texture)
            {
                var target = new D3D12TextureTarget(slot, revision, new(slot.Semantic, texture.AssetId));
                var reserve = new D3D12TextureReservation(256, 256, 256, 128 * 1024, 128 * 1024);
                Assert.IsTrue(stream.Publish(cut, revision, [target]));
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(cut, target, reserve, out var ticket));
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, texture));
            }
        }
        finally
        {
            executeFirst.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }
}
