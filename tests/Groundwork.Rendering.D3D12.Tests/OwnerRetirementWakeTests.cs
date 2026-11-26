using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WarpOwnerShutdownArmsCompletedFenceOrRechecksCompletionBeforeWaiting(bool completeBeforeArm)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var run = new VolumeRun();
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        var nativeCompleted = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdObservation = 0;
        var beforeArm = 0;
        var afterArm = 0;
        var fenceWokeOwner = false;
        var hooks = new GpuRetirementTestHooks(CompletedValue: (_, actual) =>
        {
            if (Volatile.Read(ref holdObservation) != 0 && actual >= 2 && actual != ulong.MaxValue)
                nativeCompleted.TrySetResult(actual);
            return Volatile.Read(ref holdObservation) != 0 && actual != ulong.MaxValue ? 0 : actual;
        }, AfterSuccessfulPresent: (_, _) => Volatile.Write(ref holdObservation, 1));
        await using var window = new WindowsHostModule(new WindowsHostOptions(
            $"Groundwork shutdown wake {Guid.NewGuid():N}", 360, 280, 32), runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics, true, TimeSpan.FromSeconds(15), null, hooks)
        {
            TwilightPreviewForTests = true,
            OwnerRetirementWaitForTests = (armed, wake) =>
            {
                if (!armed)
                {
                    Interlocked.Increment(ref beforeArm);
                    // The native fence is already complete. Clear the old stop
                    // wake so the test observes the newly registered event.
                    wake.Reset();
                    if (completeBeforeArm)
                        Volatile.Write(ref holdObservation, 0);
                    return;
                }
                Interlocked.Increment(ref afterArm);
                fenceWokeOwner = wake.WaitOne(TimeSpan.FromSeconds(5));
                Volatile.Write(ref holdObservation, 0);
            },
        };
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            Assert.AreEqual(RenderSubmissionResult.Accepted,
                renderer.TrySubmitVolumeMask(run.Terrain.View, 1, Vector4.Zero));
            // Volume presentation acknowledgement follows retirement, which this
            // fixture deliberately holds. Observe the real post-Present fence
            // instead, then request stop while retirement is still pending.
            Assert.IsGreaterThanOrEqualTo(2UL, await nativeCompleted.Task.WaitAsync(token));
            await renderer.StopAsync(token);
            Assert.AreEqual(1, beforeArm);
            Assert.AreEqual(completeBeforeArm ? 0 : 1, afterArm);
            if (!completeBeforeArm)
                Assert.IsTrue(fenceWokeOwner, "The completed queue fence did not signal the shutdown owner wake.");
            Assert.AreEqual(RendererLifecycle.Stopped, renderer.Read().Lifecycle);
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            Assert.IsNull(renderer.Read().FirstFault, renderer.Read().FirstFault?.ToString());
            Assert.IsNull(renderer.ReadDiagnostics().DeviceRemoval);
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
            await window.StopAsync(token);
        }
        finally
        {
            Volatile.Write(ref holdObservation, 0);
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task WarpOwnerShutdownWithNoSubmissionNeedsNoRetirementWait()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var run = new VolumeRun();
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        var waits = 0;
        await using var window = new WindowsHostModule(new WindowsHostOptions(
            $"Groundwork idle shutdown {Guid.NewGuid():N}", 360, 280, 32), runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics, true, TimeSpan.FromSeconds(15), null, null)
        {
            OwnerRetirementWaitForTests = (_, _) => Interlocked.Increment(ref waits),
        };
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            await renderer.StopAsync(token);
            Assert.AreEqual(0, waits);
            Assert.AreEqual(RendererLifecycle.Stopped, renderer.Read().Lifecycle);
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
            await window.StopAsync(token);
        }
        finally { runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation); }
    }
}
