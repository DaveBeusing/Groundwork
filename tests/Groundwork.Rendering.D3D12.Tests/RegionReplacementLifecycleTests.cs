using Groundwork.Core;
using Groundwork.Platform.Windows;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12RegionReplacementWarp")]
    public Task WarpRegionReplacementKeepsActiveAndStagedRegionsInCustodyDuringStop() =>
        VerifyRegionReplacementStop(useWarp: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12RegionReplacementHardware")]
    public Task HardwareRegionReplacementKeepsActiveAndStagedRegionsInCustodyDuringStop() =>
        VerifyRegionReplacementStop(useWarp: false);

    private static async Task VerifyRegionReplacementStop(bool useWarp)
    {
        Assert.IsTrue(Environment.UserInteractive, "Run this test on an interactive Windows desktop.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var title = $"Groundwork region replacement stop {Guid.NewGuid():N}";
        var targetOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var rendererOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var staged = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdPreparation = 0;
        var installations = 0;
        using var release = new ManualResetEventSlim();

        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32), runtime.UiWake, targetOwners);
        await using var renderer = D3D12Renderer.CreateWithMeshRegionReplacement(window, rendererOwners,
            CreateReadbackRegion(replacement: false), beforeMeshInstallation: gpu =>
            {
                if (Volatile.Read(ref holdPreparation) == 0)
                    return;

                staged.TrySetResult(gpu);
                if (!release.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The region replacement fixture did not release the owner.");
            }, diagnostics: QualificationDiagnostics, useWarp: useWarp, startupTimeout: TimeSpan.FromSeconds(15),
            operationTimeout: TimeSpan.FromSeconds(10), afterMeshInstallation: () => Interlocked.Increment(ref installations));

        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            var diagnostics = renderer.ReadDiagnostics();
            AssertAdapter(diagnostics.Adapter, useWarp);
            Assert.IsTrue(diagnostics.DebugLayerEnabled, "Install Windows Graphics Tools and run the diagnostic category separately.");
            Assert.IsTrue(diagnostics.DredEnabled);

            var hwnd = FindWindow(null, title);
            Assert.AreNotEqual(nint.Zero, hwnd);
            var regions = renderer.MeshRegionReplacement;
            Assert.IsNotNull(regions);
            Assert.AreEqual(new MeshReplacementSnapshot(1, 1, 0), regions.ReadMeshReplacement());
            var frame = CreateRegionFrame();
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmit(frame));
            await AwaitStatus(renderer, _ => regions.ReadMeshReplacement().LastPresentedGeneration == 1, token);

            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, regions.TryDeclareMeshGeneration(2));
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                regions.TrySubmitMeshRegion(2, CreateReadbackRegion(replacement: true)));
            await AwaitStatus(renderer, _ => regions.ReadMeshReplacement().LastPresentedGeneration == 2, token);
            Assert.AreEqual(new MeshReplacementSnapshot(2, 2, 2), regions.ReadMeshReplacement());
            Assert.AreEqual(frame.Sequence, renderer.Read().LastPresentedSequence);

            Volatile.Write(ref holdPreparation, 1);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, regions.TryDeclareMeshGeneration(3));
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                regions.TrySubmitMeshRegion(3, CreateReadbackRegion(replacement: false)));
            var gpu = await staged.Task.WaitAsync(token);
            Assert.AreEqual(2, gpu.LiveMeshBuffers);
            var stagedBytes = gpu.LiveMeshBytes;
            Assert.IsGreaterThan(0L, stagedBytes);
            Assert.IsLessThanOrEqualTo(GpuDevice.MaximumNativeMeshBytes, stagedBytes);
            Assert.AreEqual(new MeshReplacementSnapshot(3, 2, 2), regions.ReadMeshReplacement());

            await Assert.ThrowsAsync<IncompleteCleanupException>(() => renderer.StopAsync(token).AsTask());
            Assert.IsTrue(runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
            Assert.AreEqual(ShutdownSettlement.Custody, runtime.SettleStop());
            Assert.AreEqual(2, targetOwners.Active);
            Assert.AreEqual(1, rendererOwners.Active);
            Assert.IsTrue(IsWindow(hwnd));
            Assert.AreNotEqual(nint.Zero, SendMessageTimeout(hwnd, 0, 0, 0, 2, 1000, out _)); // WM_NULL
            Assert.AreEqual(2, gpu.LiveMeshBuffers);
            Assert.AreEqual(stagedBytes, gpu.LiveMeshBytes);
            Assert.AreEqual(new MeshReplacementSnapshot(3, 2, 2), regions.ReadMeshReplacement());
            Assert.AreEqual(MeshReplacementSubmissionResult.Closed, regions.TryDeclareMeshGeneration(4));

            release.Set();
            await VerifyMeshReplacementSettlement(window, renderer, runtime, targetOwners, rendererOwners, hwnd, token);
            Assert.AreEqual(0, gpu.LiveMeshBuffers);
            Assert.AreEqual(0L, gpu.LiveMeshBytes);
            Assert.AreEqual(1, Volatile.Read(ref installations));
            Assert.AreEqual(new MeshReplacementSnapshot(3, 0, 2), regions.ReadMeshReplacement());
            Assert.IsTrue(renderer.Read().IncompleteCleanup, "Late settlement preserves the incomplete-cleanup evidence.");
            Console.WriteLine($"Presented regions 1 and 2 with frame {frame.Sequence}. Staged region 3 never installed or presented.");
            Console.WriteLine($"At the stop deadline, 2 region buffers retained {stagedBytes} native bytes. Both counts fell to zero after settlement.");
        }
        finally
        {
            release.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }
}
