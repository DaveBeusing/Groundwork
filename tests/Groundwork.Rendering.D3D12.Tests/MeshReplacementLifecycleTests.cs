using Groundwork.Core;
using Groundwork.Platform.Windows;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12MeshReplacementWarp")]
    public Task WarpMeshReplacementKeepsBothBuffersInCustodyDuringStop() =>
        VerifyMeshReplacementStop(useWarp: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12MeshReplacementHardware")]
    public Task HardwareMeshReplacementKeepsBothBuffersInCustodyDuringStop() =>
        VerifyMeshReplacementStop(useWarp: false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12MeshReplacementWarp")]
    public Task WarpMeshReplacementRedrawsAndCoalescesWhileMinimized() =>
        VerifyMeshReplacementLifecycle(useWarp: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12MeshReplacementHardware")]
    public Task HardwareMeshReplacementRedrawsAndCoalescesWhileMinimized() =>
        VerifyMeshReplacementLifecycle(useWarp: false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12MeshReplacementWarp")]
    public Task WarpMeshReplacementRejectsCommitAfterCoreStop() =>
        VerifyMeshReplacementCommitVeto(MeshCommitVeto.CoreStop);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12MeshReplacementWarp")]
    public Task WarpMeshReplacementRejectsCommitAfterNativeClose() =>
        VerifyMeshReplacementCommitVeto(MeshCommitVeto.NativeClose);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12MeshReplacementWarp")]
    public Task WarpMeshReplacementRejectsCommitAfterWatchdogExpiry() =>
        VerifyMeshReplacementCommitVeto(MeshCommitVeto.WatchdogExpiry);

    private enum MeshCommitVeto { CoreStop, NativeClose, WatchdogExpiry }

    private static async Task VerifyMeshReplacementCommitVeto(MeshCommitVeto veto)
    {
        Assert.IsTrue(Environment.UserInteractive, "Run this test on an interactive Windows desktop.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        // Separate authorities keep the Core stop and target revocation checks independent.
        var runtime = CreateRuntime(timing);
        var windowRuntime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var windowContext = new EngineModuleContext(TimeProvider.System, timing, windowRuntime.Reporter, windowRuntime.StopControl);
        var title = $"Groundwork mesh commit veto {veto} {Guid.NewGuid():N}";
        var targetOwners = new LeaseTracker(windowRuntime.RuntimeOwnership);
        var rendererOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var staged = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var installations = 0;
        var expiry = veto == MeshCommitVeto.WatchdogExpiry;

        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32), windowRuntime.UiWake, targetOwners);
        await using var renderer = D3D12Renderer.CreateWithMeshReplacement(window, rendererOwners, CreateTerrainPair(), gpu =>
        {
            staged.TrySetResult(gpu);
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The commit veto fixture did not release the owner.");
        }, QualificationDiagnostics, useWarp: true, startupTimeout: TimeSpan.FromSeconds(15),
            operationTimeout: TimeSpan.FromSeconds(expiry ? 2 : 10),
            afterMeshInstallation: () => Interlocked.Increment(ref installations));

        try
        {
            var (hwnd, meshes) = await StartMeshReplacement(window, renderer, context, title, useWarp: true, token, windowContext);
            SubmitReplacement(meshes, 2, CreateTerrainPair(drawSecond: false));
            var gpu = await staged.Task.WaitAsync(token);

            if (veto == MeshCommitVeto.NativeClose)
            {
                Assert.IsTrue(PostMessage(hwnd, 0x0010, 0, 0)); // WM_CLOSE
                while (window.Read().Phase != HostWindowPhase.Closing)
                    await Task.Delay(10, token);
                Assert.IsFalse(runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
            }
            else
            {
                if (!expiry)
                    runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
                while (!runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop))
                    await Task.Delay(10, token);
                Assert.AreEqual(HostWindowPhase.Open, window.Read().Phase);
            }

            uint? expectedFaultStage = expiry ? (uint)RendererStage.MeshReplacement : null;
            Assert.AreEqual(expectedFaultStage, renderer.Read().FirstFault?.Stage);
            Assert.AreEqual(new MeshReplacementSnapshot(2, 1, 1), meshes.ReadMeshReplacement());
            Assert.AreEqual(2, gpu.LiveMeshBuffers);
            Assert.AreEqual(2, targetOwners.Active);
            Assert.AreEqual(1, rendererOwners.Active);
            Assert.IsTrue(IsWindow(hwnd));

            release.Set();
            while (renderer.Read().Lifecycle != RendererLifecycle.Stopped)
                await Task.Delay(10, token);

            await window.StopAsync(token);
            await VerifyMeshReplacementSettlement(window, renderer, runtime, targetOwners, rendererOwners, hwnd, token, expectedFaultStage);
            Assert.AreEqual(ShutdownSettlement.Released, windowRuntime.SettleStop());
            Assert.AreEqual(0, Volatile.Read(ref installations));
            Assert.AreEqual(0, gpu.LiveMeshBuffers);
            Assert.AreEqual(new MeshReplacementSnapshot(2, 0, 1), meshes.ReadMeshReplacement());
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            Console.WriteLine($"{veto} vetoed the staged mesh pair and released both runtimes.");
        }
        finally
        {
            release.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            windowRuntime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private static async Task VerifyMeshReplacementStop(bool useWarp)
    {
        Assert.IsTrue(Environment.UserInteractive, "Run this test on an interactive Windows desktop.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var title = $"Groundwork mesh replacement stop {Guid.NewGuid():N}";
        var targetOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var rendererOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var staged = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32), runtime.UiWake, targetOwners);
        await using var renderer = D3D12Renderer.CreateWithMeshReplacement(window, rendererOwners, CreateTerrainPair(), gpu =>
        {
            staged.TrySetResult(gpu);
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The replacement fixture did not release the owner.");
        }, QualificationDiagnostics, useWarp, startupTimeout: TimeSpan.FromSeconds(15), operationTimeout: TimeSpan.FromSeconds(10));

        try
        {
            var (hwnd, meshes) = await StartMeshReplacement(window, renderer, context, title, useWarp, token);
            SubmitReplacement(meshes, 2, CreateTerrainPair(drawSecond: false));
            var gpu = await staged.Task.WaitAsync(token);
            Assert.AreEqual(2, gpu.LiveMeshBuffers);
            Assert.AreEqual(new MeshReplacementSnapshot(2, 1, 1), meshes.ReadMeshReplacement());

            await Assert.ThrowsAsync<IncompleteCleanupException>(() => renderer.StopAsync(token).AsTask());
            Assert.IsTrue(runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
            Assert.AreEqual(ShutdownSettlement.Custody, runtime.SettleStop());
            Assert.AreEqual(2, targetOwners.Active, "The window and admitted replacement must remain owned.");
            Assert.AreEqual(1, rendererOwners.Active);
            Assert.IsTrue(IsWindow(hwnd));
            Assert.AreNotEqual(nint.Zero, SendMessageTimeout(hwnd, 0, 0, 0, 2, 1000, out _)); // WM_NULL
            Assert.AreEqual(2, gpu.LiveMeshBuffers);
            Assert.AreEqual(new MeshReplacementSnapshot(2, 1, 1), meshes.ReadMeshReplacement());
            Assert.AreEqual(MeshReplacementSubmissionResult.Closed, meshes.TryDeclareMeshGeneration(3));

            release.Set();
            await VerifyMeshReplacementSettlement(window, renderer, runtime, targetOwners, rendererOwners, hwnd, token);
            Assert.AreEqual(0, gpu.LiveMeshBuffers);
            Assert.AreEqual(new MeshReplacementSnapshot(2, 0, 1), meshes.ReadMeshReplacement());
            Assert.IsTrue(renderer.Read().IncompleteCleanup, "Late settlement preserves the incomplete-cleanup evidence.");
            Console.WriteLine("Mesh buffers held at the stop deadline: 2, after late settlement: 0.");
        }
        finally
        {
            release.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private static async Task VerifyMeshReplacementLifecycle(bool useWarp)
    {
        Assert.IsTrue(Environment.UserInteractive, "Run this test on an interactive Windows desktop.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var title = $"Groundwork mesh replacement lifecycle {Guid.NewGuid():N}";
        var targetOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var rendererOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var firstStaged = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var highWater = 0;

        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32), runtime.UiWake, targetOwners);
        await using var renderer = D3D12Renderer.CreateWithMeshReplacement(window, rendererOwners, CreateTerrainPair(), gpu =>
        {
            highWater = Math.Max(highWater, gpu.LiveMeshBuffers);
            firstStaged.TrySetResult(gpu);
        }, QualificationDiagnostics, useWarp, startupTimeout: TimeSpan.FromSeconds(15));

        try
        {
            var (hwnd, meshes) = await StartMeshReplacement(window, renderer, context, title, useWarp, token);
            SubmitReplacement(meshes, 2, CreateTerrainPair(drawSecond: false));
            var gpu = await firstStaged.Task.WaitAsync(token);
            await AwaitStatus(renderer, _ => meshes.ReadMeshReplacement().LastPresentedGeneration == 2, token);
            Assert.AreEqual(new MeshReplacementSnapshot(2, 2, 2), meshes.ReadMeshReplacement());
            Assert.AreEqual(1UL, renderer.Read().LastAcceptedSequence);
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);

            var initialWidth = renderer.Read().AppliedWidth;
            Assert.IsTrue(SetWindowPos(hwnd, 0, 0, 0, 480, 340, 0x0016));
            await AwaitStatus(renderer, state => state.Surface == RenderSurfaceState.Drawable &&
                state.AppliedWidth != initialWidth, token);

            Assert.IsTrue(ShowWindowAsync(hwnd, 6)); // SW_MINIMIZE
            await AwaitStatus(renderer, static state => state.Surface == RenderSurfaceState.Suspended, token);
            SubmitReplacement(meshes, 3, CreateTerrainPair(drawFirst: false));
            SubmitReplacement(meshes, 4, CreateTerrainPair());
            Assert.AreEqual(new MeshReplacementSnapshot(4, 2, 2), meshes.ReadMeshReplacement());

            Assert.IsTrue(ShowWindowAsync(hwnd, 9)); // SW_RESTORE
            await AwaitStatus(renderer, _ => meshes.ReadMeshReplacement().LastPresentedGeneration == 4, token);
            Assert.AreEqual(new MeshReplacementSnapshot(4, 4, 4), meshes.ReadMeshReplacement());
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);

            Assert.IsTrue(PostMessage(hwnd, 0x0010, 0, 0)); // WM_CLOSE
            await VerifyMeshReplacementSettlement(window, renderer, runtime, targetOwners, rendererOwners, hwnd, token);
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            Assert.AreEqual(2, highWater);
            Assert.AreEqual(0, gpu.LiveMeshBuffers);
            Console.WriteLine($"Presented generations 1, 2 and 4 using frame sequence 1. Mesh buffer high-water mark: {highWater}.");
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private static async Task<(nint Window, IRenderMeshReplacement Meshes)> StartMeshReplacement(WindowsHostModule window,
        D3D12Renderer renderer, EngineModuleContext context, string title, bool useWarp, CancellationToken token,
        EngineModuleContext? windowContext = null)
    {
        await window.InitializeAsync(windowContext ?? context, token);
        await window.StartAsync(token);
        await renderer.InitializeAsync(context, token);
        await renderer.StartAsync(token);
        AssertAdapter(renderer.ReadDiagnostics().Adapter, useWarp);

        var hwnd = FindWindow(null, title);
        Assert.AreNotEqual(nint.Zero, hwnd);
        var meshes = renderer.MeshReplacement;
        Assert.IsNotNull(meshes);
        Assert.AreEqual(new MeshReplacementSnapshot(1, 1, 0), meshes.ReadMeshReplacement());
        Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmit(CreateTerrainFrame()));
        await AwaitStatus(renderer, _ => meshes.ReadMeshReplacement().LastPresentedGeneration == 1, token);
        return (hwnd, meshes);
    }

    private static void SubmitReplacement(IRenderMeshReplacement meshes, ulong generation, RenderMeshPair pair)
    {
        Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, meshes.TryDeclareMeshGeneration(generation));
        Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, meshes.TrySubmitMeshPair(generation, pair));
    }

    private static async Task VerifyMeshReplacementSettlement(WindowsHostModule window, D3D12Renderer renderer,
        RuntimeSafetyAuthority runtime, LeaseTracker targetOwners, LeaseTracker rendererOwners, nint hwnd,
        CancellationToken token, uint? expectedFaultStage = null)
    {
        while (renderer.Read().Lifecycle != RendererLifecycle.Stopped)
            await Task.Delay(10, token);
        Assert.AreEqual(expectedFaultStage, renderer.Read().FirstFault?.Stage);
        while (window.Read().Phase != HostWindowPhase.Closed)
            await Task.Delay(10, token);

        await renderer.StopAsync(token);
        await window.StopAsync(token);
        Assert.IsFalse(IsWindow(hwnd));
        Assert.AreEqual(0, targetOwners.Active);
        Assert.AreEqual(0, rendererOwners.Active);
        Assert.AreEqual(ShutdownSettlement.Released, runtime.SettleStop());
        var diagnostics = renderer.ReadDiagnostics();
        AssertCleanDiagnostics(diagnostics, diagnosticsRequested: true);
    }
}
