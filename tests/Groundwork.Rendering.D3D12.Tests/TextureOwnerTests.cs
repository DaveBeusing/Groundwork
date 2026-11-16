using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StaticTextureRetiresAndRedrawsPausedVolumeWithoutAnotherFrame(bool warp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        using var run = new VolumeRun(openFloor: true);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork textures {Guid.NewGuid():N}",
            TerrainWidth, TerrainHeight, 32), runtime.UiWake, runtime.RuntimeOwnership);
        var stream = new D3D12TextureHandoff(new(4, 4, 4096, 4096, 4096, 512 * 1024, 512 * 1024, 4));
        var first = new TaskCompletionSource<OffscreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var red = new TaskCompletionSource<OffscreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var green = new TaskCompletionSource<OffscreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateFallback = new TaskCompletionSource<OffscreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var assigned = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var readyBurst = new ManualResetEventSlim();
        using var releaseSignal = new ManualResetEventSlim();
        var staleTicket = default(D3D12TextureTicket);
        var stoppedSignal = 0;
        var holdLatePresent = 0;
        var lateDrawSignals = 0;
        var latePublications = 0;
        var activeCut = default(D3D12TextureView);
        var highTarget = default(D3D12TextureTarget);
        var lateTarget = default(D3D12TextureTarget);
        var rejectedHead = 0;
        var takes = 0;
        var expectCopy = 0;
        var hold = 0;
        ulong heldFence = 0;
        var hooks = new GpuRetirementTestHooks(
            SignalFailure: (device, fence) =>
            {
                if (device.HasTextureCopy && Interlocked.Exchange(ref expectCopy, 0) != 0)
                {
                    Volatile.Write(ref heldFence, fence);
                    assigned.TrySetResult(fence);
                    if (Volatile.Read(ref stoppedSignal) != 0)
                        releaseSignal.Wait(token);
                }
                else if (Volatile.Read(ref holdLatePresent) != 0 &&
                    Interlocked.Increment(ref lateDrawSignals) == 2)
                {
                    Volatile.Write(ref heldFence, fence);
                    assigned.TrySetResult(fence);
                }
                return null;
            },
            CompletedValue: (_, completed) =>
            {
                var fence = Volatile.Read(ref heldFence);
                return fence != 0 && Volatile.Read(ref hold) != 0 ? Math.Min(completed, fence - 1) : completed;
            },
            RejectRecordedPlan: sequence =>
            {
                if (sequence == 4 && Interlocked.Increment(ref latePublications) == 1)
                    Assert.IsTrue(stream.Publish(activeCut, 3, [highTarget, lateTarget]));
                return false;
            });
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics, warp, TimeSpan.FromSeconds(15), null, hooks)
        {
            BeforeTextureUploadForTests = (ticket, wake) =>
            {
                Interlocked.Increment(ref takes);
                if (ticket != staleTicket)
                    return;
                readyBurst.Wait(token);
                wake.WaitOne(0);
                Assert.IsTrue(stream.Cancel(ticket));
                Interlocked.Increment(ref rejectedHead);
            },
            VolumePresentReadbackForTests = (sequence, _, pixels) =>
            {
                if (sequence == 4)
                    lateFallback.TrySetResult(pixels);
                if (sequence != 2)
                    return;
                first.TrySetResult(pixels);
                var color = pixels.ColorAt(TerrainWidth / 2, TerrainHeight / 2);
                if (color[0] > 0 && color[1] == 0 && color[2] == 0)
                    red.TrySetResult(pixels);
                if (color[1] > 0 && color[0] == 0 && color[2] == 0)
                    green.TrySetResult(pixels);
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
            var cut = new D3D12TextureView(run.Terrain.WorldIdentity, installed.Info.View,
                installed.Info.TerrainRevision, installed.Info.Generation);
            activeCut = cut;
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(installed.Info.Generation, 2, VolumeCameraAt(view, 32, 7)));
            var fallback = await first.Task.WaitAsync(token);
            var x = TerrainWidth / 2;
            var y = TerrainHeight / 2;
            Assert.IsLessThan(1f, fallback.DepthAt(x, y));

            var firstTexture = MakeStaticColor(255, 0);
            var replacement = MakeStaticColor(0, 255);
            var slot = new D3D12TextureSlot(2, MaterialTextureSemantic.BaseColor);
            var staleSlot = new D3D12TextureSlot(3, MaterialTextureSemantic.Emissive);
            var firstTarget = new D3D12TextureTarget(slot, 1, new(slot.Semantic, firstTexture.AssetId));
            var nextTarget = new D3D12TextureTarget(slot, 2, new(slot.Semantic, replacement.AssetId));
            highTarget = nextTarget;
            var staleTarget = new D3D12TextureTarget(staleSlot, 1, new(staleSlot.Semantic, firstTexture.AssetId));
            var reserve = new D3D12TextureReservation(256, 256, 256, 128 * 1024, 128 * 1024);
            Assert.IsTrue(stream.Publish(cut, 1, [staleTarget, firstTarget]));
            Volatile.Write(ref hold, 1);
            Volatile.Write(ref expectCopy, 1);
            Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(cut, staleTarget, reserve, out staleTicket));
            Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(staleTicket, firstTexture));
            Stage(firstTarget, firstTexture);
            readyBurst.Set();
            await assigned.Task.WaitAsync(token);
            Assert.AreEqual(1, Volatile.Read(ref rejectedHead));
            Assert.IsFalse(stream.TryGetResident(cut, slot, out _));
            Assert.IsFalse(red.Task.IsCompleted);
            Assert.IsGreaterThan(0L, stream.ReadUsage().StagingBytes);
            // A precharged successor waits behind the held copy without consuming
            // another turn, then settles without any added producer or frame event.
            Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(cut, staleTarget, reserve, out var blocked));
            Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(blocked, firstTexture));
            var beforePoll = Volatile.Read(ref takes);
            await Task.Delay(40, token);
            Assert.AreEqual(beforePoll, Volatile.Read(ref takes));

            // No camera, producer, frame, or explicit owner wake follows this release.
            Volatile.Write(ref hold, 0);
            var redFrame = await red.Task.WaitAsync(token);
            Assert.AreEqual(fallback.DepthAt(x, y), redFrame.DepthAt(x, y));
            while (!stream.TryGetResident(cut, staleSlot, out var renewed) || renewed != blocked)
                await Task.Delay(10, token);
            Assert.AreEqual(0L, stream.ReadUsage().StagingBytes);
            Assert.IsTrue(stream.TryGetResident(cut, slot, out var previous));

            Assert.IsTrue(stream.Publish(cut, 2, [nextTarget]));
            Stage(nextTarget, replacement);
            var greenFrame = await green.Task.WaitAsync(token);
            Assert.AreEqual(redFrame.DepthAt(x, y), greenFrame.DepthAt(x, y));
            Assert.IsTrue(stream.TryGetResident(cut, slot, out var current));
            Assert.AreNotEqual(previous, current);
            Assert.AreEqual(2UL, renderer.Read().LastPresentedSequence);
            Assert.AreEqual(1, stream.ReadUsage().Descriptors);
            var copied = renderer.ReadTextureDiagnostics();
            Assert.AreEqual(12UL, copied.UploadedBytes);
            Assert.AreEqual(4UL, copied.LastCopyBytes);
            Assert.IsGreaterThan(0L, copied.NativeHighWaterBytes);
            Assert.AreEqual(RenderSubmissionResult.Accepted,
                renderer.TrySubmitVolumeCamera(installed.Info.Generation, 3, VolumeCameraAt(view, 32, 7)));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 3, token);
            Assert.AreEqual(copied, renderer.ReadTextureDiagnostics());

            // Declaring a lower-priority-number BaseColor after Close changes the
            // draw selection even though the captured higher slot is still resident.
            var lowerSlot = new D3D12TextureSlot(1, MaterialTextureSemantic.BaseColor);
            lateTarget = new(lowerSlot, 1, new(lowerSlot.Semantic, firstTexture.AssetId));
            assigned = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref heldFence, 0);
            Volatile.Write(ref hold, 1);
            Volatile.Write(ref holdLatePresent, 1);
            var untexturedColor = new Vector3(0.05f, 0.17f, 0.61f);
            var material = new PbrMaterialDescriptor(Vector3.Zero, 0, 0.5f, 1, untexturedColor, 1);
            Assert.AreEqual(RenderSubmissionResult.Accepted,
                renderer.TrySubmitVolumeCamera(installed.Info.Generation, 4,
                    VolumeCameraAt(view, 32, 7) with { TerrainMaterial = material }));
            await assigned.Task.WaitAsync(token);
            Assert.AreEqual(1, Volatile.Read(ref latePublications));
            Assert.AreEqual(2, Volatile.Read(ref lateDrawSignals));
            Assert.AreEqual(1, stream.ReadUsage().Descriptors);
            Assert.IsTrue(stream.TryGetResident(cut, slot, out var retained));
            Assert.AreEqual(current, retained);
            Assert.IsFalse(lateFallback.Task.IsCompleted);
            Volatile.Write(ref holdLatePresent, 0);
            Volatile.Write(ref hold, 0);
            var reRecorded = await lateFallback.Task.WaitAsync(token);
            AssertColor(reRecorded.ColorAt(x, y), untexturedColor, 255, "stale binding uses untextured PBR on the same Present");
            Assert.AreEqual(fallback.DepthAt(x, y), reRecorded.DepthAt(x, y));
            Assert.AreEqual(copied.UploadedBytes, renderer.ReadTextureDiagnostics().UploadedBytes);

            assigned = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref stoppedSignal, 1);
            Volatile.Write(ref expectCopy, 1);
            Stage(lateTarget, firstTexture);
            await assigned.Task.WaitAsync(token);
            using var stopWait = new CancellationTokenSource();
            var stop = Task.Run(() => renderer.StopAsync(stopWait.Token).AsTask(), token);
            using (var stopTransition = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                await AwaitStatus(renderer, state => state.Lifecycle == RendererLifecycle.Stopping, stopTransition.Token);
            stopWait.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => stop.WaitAsync(TimeSpan.FromSeconds(3), token));
            Assert.IsGreaterThan(0L, stream.ReadUsage().StagingBytes);
            Assert.AreEqual(D3D12TextureResult.Closed, stream.TryRequest(cut, lateTarget, reserve, out _));
            releaseSignal.Set();
            await AwaitStatus(renderer, state => state.Lifecycle == RendererLifecycle.Stopped, token);
            await renderer.StopAsync(token);
            Assert.AreEqual(default(D3D12TextureUsage), stream.ReadUsage());
            Assert.AreEqual(0L, renderer.ReadTextureDiagnostics().ResidentAndPendingBytes);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, warp);
            AssertCleanDiagnostics(diagnostics, diagnosticsRequested: true);

            void Stage(D3D12TextureTarget target, CookedTexture texture)
            {
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(cut, target, reserve, out var ticket));
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, texture));
            }
        }
        finally
        {
            readyBurst.Set();
            releaseSignal.Set();
            Volatile.Write(ref hold, 0);
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private static CookedTexture MakeStaticColor(byte red, byte green)
    {
        Span<byte> source = stackalloc byte[22];
        source.Clear();
        source[2] = 2;
        source[12] = 1;
        source[14] = 1;
        source[16] = 32;
        source[17] = 0x28;
        source[19] = green;
        source[20] = red;
        source[21] = byte.MaxValue;
        var limits = new TextureLimits(256, 256, 256, 4, 1);
        Assert.AreEqual(TextureCookError.None, TextureCooker.CookTga32(source,
            new(TextureColorSpace.Srgb, TextureSemantic.Color, 1), limits, out var texture));
        return texture!;
    }
}
