using Groundwork.Core;
using Groundwork.Platform.Windows;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12MeshRetirementFaults")]
    [DataRow(0, DisplayName = "Hold draw and post-Present retirement")]
    [DataRow(1, DisplayName = "Hold post-Present retirement after draw retirement")]
    public async Task WarpMeshReplacementWaitsForRetirement(int completedThrough)
    {
        Assert.IsTrue(Environment.UserInteractive, "Run this test on an interactive Windows desktop.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var title = $"Groundwork mesh retirement delay {Guid.NewGuid():N}";
        var targetOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var rendererOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var observed = new TaskCompletionSource<(GpuDevice Device, ulong NativeCompleted)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = 1;
        var replacementQueued = 0;
        var stagedWhileHeld = 0;
        var heldPollsAfterSubmission = 0;

        // On the first frame, fence 1 covers the draw and fence 2 covers Present.
        // Delay the owner's observation only. Native execution and signaling still run.
        var retirementHooks = new GpuRetirementTestHooks(CompletedValue: (gpu, completed) =>
        {
            if (Volatile.Read(ref holding) == 0 || completed == ulong.MaxValue)
                return completed;
            // Keep the fence held through an owner retry after submission.
            if (completed >= 2 && Volatile.Read(ref replacementQueued) != 0 && ++heldPollsAfterSubmission >= 2)
                observed.TrySetResult((gpu, completed));
            return Math.Min(completed, (ulong)completedThrough);
        });

        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32), runtime.UiWake, targetOwners);
        await using var renderer = D3D12Renderer.CreateWithMeshReplacement(window, rendererOwners, CreateTerrainPair(), _ =>
        {
            if (Volatile.Read(ref holding) != 0)
                Interlocked.Increment(ref stagedWhileHeld);
        }, QualificationDiagnostics, useWarp: true, startupTimeout: TimeSpan.FromSeconds(15),
            operationTimeout: TimeSpan.FromSeconds(10), retirementHooks: retirementHooks);

        try
        {
            var (hwnd, meshes) = await StartMeshReplacement(window, renderer, context, title, useWarp: true, token);
            SubmitReplacement(meshes, 2, CreateTerrainPair(drawSecond: false));
            Volatile.Write(ref replacementQueued, 1);

            var (gpu, nativeCompleted) = await observed.Task.WaitAsync(token);
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
            Assert.AreEqual(new MeshReplacementSnapshot(2, 1, 1), meshes.ReadMeshReplacement());
            Assert.AreEqual(1, gpu.LiveMeshBuffers);
            Assert.AreEqual(0, Volatile.Read(ref stagedWhileHeld));
            Console.WriteLine($"Native fence {nativeCompleted}; observed fence capped at {completedThrough}. Generation 1 still owns the only mesh buffer.");

            Volatile.Write(ref holding, 0);
            await AwaitStatus(renderer, _ => meshes.ReadMeshReplacement().LastPresentedGeneration == 2, token);
            Assert.AreEqual(new MeshReplacementSnapshot(2, 2, 2), meshes.ReadMeshReplacement());
            Assert.AreEqual(1UL, renderer.Read().LastAcceptedSequence);
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
            Assert.AreEqual(0, Volatile.Read(ref stagedWhileHeld));

            Assert.IsTrue(PostMessage(hwnd, 0x0010, 0, 0)); // WM_CLOSE
            await VerifyMeshReplacementSettlement(window, renderer, runtime, targetOwners, rendererOwners, hwnd, token);
            Assert.AreEqual(new MeshReplacementSnapshot(2, 0, 2), meshes.ReadMeshReplacement());
            Assert.AreEqual(0, gpu.LiveMeshBuffers);
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
        }
        finally
        {
            Volatile.Write(ref holding, 0);
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }
}
