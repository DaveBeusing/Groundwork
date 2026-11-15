using System.Diagnostics;
using System.Numerics;
using Groundwork.Core;
using Groundwork.Core.Tests;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RejectedRegistrationRecoveryPreservesTheRootAndCannotRevokeASupersedingView(bool supersede)
    {
        using var run = new VolumeRun();
        var view = run.Terrain.View;
        var authority = run.Terrain.TryBindRenderer(view.RendererEpoch);
        Assert.IsNotNull(authority);
        Assert.IsTrue(run.Terrain.ConfirmMask(view, authority, 1));
        using var work = run.Stage(VolumeWorkClass.Movement, 0, 1, cut: false);
        Assert.IsTrue(work.Execute());
        using var reader = run.Terrain.TryAcquirePreparedGeometry(authority, work.Generation, out _, out _);
        Assert.IsNotNull(reader);
        Assert.IsTrue(run.Terrain.ConfirmPreparedGeometry(authority, reader.Info));
        await run.BeginHeldTick();
        var tick = run.World.Publication.Committed.Read().Stamp.Tick;
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, tick, BarrierKind.CommitResults);
        var prepared = run.Terrain.TryPreparePublication(work.Generation, candidates, [], null, authority);
        Assert.IsNotNull(prepared);
        var committed = run.World.Publication.Finalization.TryFinalize(prepared);
        Assert.AreEqual(BarrierStatus.Committed, committed.Status);
        CoreRuntimeFixture.PublishObserver(run.World, committed.Stamp!.Value, (int)tick);
        var root = run.World.Publication.Committed.Read().Root;
        using var registration = run.Terrain.TryRegisterPreparedFrame(authority, reader, 2);
        Assert.IsNotNull(registration);
        Assert.IsTrue(run.Terrain.TryRequestFrameRecovery(authority, registration));
        Assert.IsFalse(run.Terrain.TryRequestFrameRecovery(authority, registration));
        Assert.IsFalse(run.Terrain.AcknowledgeFrame(registration));
        registration.Dispose();
        Assert.AreEqual(0, run.Terrain.Statistics.Readers);
        Assert.AreEqual(view, run.Terrain.View);
        var expected = supersede ? run.Terrain.ChangeView(view.Frame, view.RendererEpoch) :
            view with { View = view.View + 1 };

        var next = await run.BeginHeldTick();
        Assert.IsFalse(run.Terrain.BeginTick(next));
        Assert.IsFalse(run.Terrain.BeginTick(next));
        Assert.AreEqual(expected, run.Terrain.View);
        Assert.AreSame(root, run.World.Publication.Committed.Read().Root);
        Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
        Assert.IsTrue(run.Terrain.CanRequestMask(expected));
        await run.FinishHeldTick();
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RejectedFirstContentExpiresWithoutAnotherFrameAndRecoversTheSameRevision(bool warp)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        using var run = new VolumeRun(readers: 2);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        var rejection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejected = 0;
        var hooks = new GpuRetirementTestHooks(
            SignalFailure: (gpu, _) =>
            {
                device.TrySetResult(gpu);
                return null;
            },
            RejectPreparedPlan: sequence =>
            {
                if (sequence != 2)
                    return false;
                Interlocked.Increment(ref rejected);
                rejection.TrySetResult();
                return true;
            });
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork pass recovery {Guid.NewGuid():N}", 363, 281, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1, QualificationDiagnostics,
            warp, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(2), hooks);
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, warp);
            Assert.IsTrue(diagnostics.DebugLayerEnabled);
            Assert.IsTrue(diagnostics.DredEnabled);
            var view = run.Terrain.View;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 1, Vector4.Zero));
            await AwaitStatus(renderer, s => s.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            var root = (VolumeWorldRoot)run.World.Publication.Committed.Read().Root;
            var generation = root.Terrain.Generation;
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(generation, 2, VolumeCameraAt(view, 32, 4)));
            await rejection.Task.WaitAsync(token);
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame,
                renderer.TrySubmitVolumeCamera(generation, 3, VolumeCameraAt(view, 32, 5)));
            await AwaitStatus(renderer, _ => run.Terrain.Statistics.Readers == 0, token);
            Assert.AreEqual(0, (await device.Task.WaitAsync(token)).LiveMeshBuffers);
            Assert.IsLessThan(FirstContentRetry.MaximumRejections, Volatile.Read(ref rejected),
                "Recovery must result from the deadline even when eligible retry wakes do not reach the attempt limit.");
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
            Assert.AreEqual(view, run.Terrain.View);

            var begin = await run.BeginHeldTick();
            Assert.IsFalse(run.Terrain.BeginTick(begin));
            var recovered = run.Terrain.View;
            Assert.AreEqual(view with { View = view.View + 1 }, recovered);
            Assert.AreSame(root, run.World.Publication.Committed.Read().Root);
            Assert.IsFalse(run.Terrain.BeginTick(begin));
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            await run.FinishHeldTick();

            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(recovered, 3, Vector4.Zero));
            await AwaitStatus(renderer, s => s.LastPresentedSequence == 3, token);
            using (var work = run.Stage(VolumeWorkClass.Recovery, 1, 1, cut: false))
            {
                Assert.IsTrue(work.Execute(token));
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TryStageVolumeTerrain(work.Generation));
                await AwaitStatus(renderer, _ => renderer.IsVolumeTerrainReady(work.Generation), token);
                var cut = await run.BeginHeldTick();
                var candidates = await CoreRuntimeFixture.SealAsync(run.World, cut.Tick, BarrierKind.CommitResults);
                var prepared = renderer.TryPrepareVolumePublication(work.Generation, candidates, [], out var result);
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, result);
                Assert.IsNotNull(prepared);
                var committed = run.World.Publication.Finalization.TryFinalize(prepared);
                Assert.AreEqual(BarrierStatus.Committed, committed.Status);
                CoreRuntimeFixture.PublishObserver(run.World, committed.Stamp!.Value, (int)cut.Tick);
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                    renderer.TrySubmitVolumeTerrain(work.Generation, 4, VolumeCameraAt(recovered, 32, 4)));
            }
            await AwaitStatus(renderer, s => s.LastPresentedSequence == 4, token);
            var eligible = await run.BeginHeldTick();
            Assert.IsTrue(run.Terrain.BeginTick(eligible));
            Assert.IsFalse(run.Terrain.BeginTick(eligible));
            Assert.IsFalse(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual(1UL, ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Terrain.TerrainRevision);
            await renderer.StopAsync(token);
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FirstContentRetrySettlesAfterClosedListExpiryOrSupersedingMask(bool supersede)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        using var run = new VolumeRun(readers: 1);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interleaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryTimestamp = 0L;
        var attempts = 0;
        var selectedNextMask = 0;
        var signals = 0;
        var view = run.Terrain.View;
        var next = view;
        var hooks = new GpuRetirementTestHooks(
            SignalFailure: (gpu, _) =>
            {
                device.TrySetResult(gpu);
                Interlocked.Increment(ref signals);
                return null;
            },
            RejectPreparedPlan: sequence =>
            {
                if (sequence != 2 || Interlocked.Increment(ref attempts) != 1)
                    return false;
                rejected.TrySetResult();
                return true;
            },
            RejectRecordedPlan: sequence =>
            {
                if (sequence == 2 && !supersede)
                {
                    Interlocked.Exchange(ref retryTimestamp, 2 * Stopwatch.Frequency);
                    interleaved.TrySetResult();
                }
                return false;
            });
        var title = $"Groundwork pass retry cut {Guid.NewGuid():N}";
        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 361, 281, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1, QualificationDiagnostics,
            true, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(2), hooks)
        {
            FirstContentTimestampForTests = () => Interlocked.Read(ref retryTimestamp),
            BeforeRetrySelectionForTests = owner =>
            {
                if (!supersede || Interlocked.Exchange(ref selectedNextMask, 1) != 0)
                    return;
                next = run.Terrain.ChangeView(view.Frame, view.RendererEpoch);
                Assert.AreEqual(RenderSubmissionResult.Accepted, owner.TrySubmitVolumeMask(next, 3, Vector4.Zero));
                interleaved.TrySetResult();
            },
        };
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 1, Vector4.Zero));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            var beforeSignals = Volatile.Read(ref signals);
            var root = (VolumeWorldRoot)run.World.Publication.Committed.Read().Root;
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(root.Terrain.Generation, 2, VolumeCameraAt(view, 32, 4)));
            await rejected.Task.WaitAsync(token);
            Assert.AreEqual(1, run.Terrain.Statistics.Readers);

            // Resize supplies an eligible wake. The injected interleavings happen later:
            // either a newer mask wins selection or the old retry expires after Close.
            var hwnd = FindWindow(null, title);
            Assert.AreNotEqual(nint.Zero, hwnd);
            Assert.IsTrue(SetWindowPos(hwnd, 0, 0, 0, 421, 325, 0x0016));
            await interleaved.Task.WaitAsync(token);
            if (supersede)
                await AwaitStatus(renderer, state => state.LastPresentedSequence == 3, token);
            await AwaitStatus(renderer, _ => run.Terrain.Statistics.Readers == 0, token);
            Assert.AreEqual(0, (await device.Task.WaitAsync(token)).LiveMeshBuffers);
            Assert.AreEqual(supersede ? 3UL : 1UL, renderer.Read().LastPresentedSequence);
            Assert.AreEqual(beforeSignals + (supersede ? 2 : 0), Volatile.Read(ref signals),
                "Only an eligible replacement mask may signal draw and Present work.");

            var stamp = await run.BeginHeldTick();
            Assert.IsFalse(run.Terrain.BeginTick(stamp));
            Assert.AreEqual(supersede ? next : view with { View = view.View + 1 }, run.Terrain.View);
            Assert.AreSame(root, run.World.Publication.Committed.Read().Root);
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            await run.FinishHeldTick();
            await renderer.StopAsync(token);
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }
}
