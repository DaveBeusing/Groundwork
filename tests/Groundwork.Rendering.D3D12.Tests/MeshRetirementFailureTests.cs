using Groundwork.Core;
using Groundwork.Platform.Windows;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12MeshRetirementFaults")]
    public Task WarpFailedPostPresentSignalAndFenceSentinelRetainMeshUntilConfirmedRemoval() =>
        VerifyWarpRetirementFailure(injectSignalFailure: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12MeshRetirementFaults")]
    public Task WarpConfirmedRemovalSettlesMeshWithAnUnobservedSuccessfulSignal() =>
        VerifyWarpRetirementFailure(injectSignalFailure: false);

    private static async Task VerifyWarpRetirementFailure(bool injectSignalFailure)
    {
        Assert.IsTrue(Environment.UserInteractive, "Run this test on an interactive Windows desktop.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var releaseSignal = new ManualResetEventSlim();
        using var permitRemoval = new ManualResetEventSlim();
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var title = $"Groundwork WARP retirement fault {Guid.NewGuid():N}";
        var targetOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var rendererOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var signalReached = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirementHeld = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        const int signalError = unchecked((int)0x80004005); // E_FAIL
        var armed = false;
        var armedSignals = 0;
        var installations = 0;
        var heldFence = 0UL;
        var confirmedRemoval = 0;

        var hooks = new GpuRetirementTestHooks(SignalFailure: (gpu, value) =>
        {
            // After the first frame has presented, the next frame signals its draw and then its Present.
            if (!Volatile.Read(ref armed) || ++armedSignals != 2)
                return null;

            heldFence = value;
            signalReached.TrySetResult(gpu);
            if (!injectSignalFailure)
                return null; // Execute the real post-Present queue signal.

            if (!releaseSignal.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The failed-signal fixture did not release the owner.");
            return signalError;
        }, CompletedValue: (gpu, completed) =>
        {
            if (heldFence == 0)
                return completed;

            if (!retirementHeld.Task.IsCompleted)
                retirementHeld.TrySetResult(gpu.RemovedReason);

            if (permitRemoval.IsSet && confirmedRemoval == 0)
                confirmedRemoval = RemoveWarpRetirementDevice(gpu);

            // A sentinel alone cannot prove removal. The independent successful-signal case
            // instead keeps the observed fence behind the real post-Present signal.
            return injectSignalFailure || confirmedRemoval < 0 ? ulong.MaxValue : Math.Min(completed, heldFence - 1);
        });

        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32), runtime.UiWake, targetOwners);
        await using var renderer = D3D12Renderer.CreateWithMeshReplacement(window, rendererOwners, CreateTerrainPair(), _ => { },
            QualificationDiagnostics, useWarp: true, startupTimeout: TimeSpan.FromSeconds(15),
            operationTimeout: TimeSpan.FromSeconds(15), afterMeshInstallation: () => Interlocked.Increment(ref installations),
            retirementHooks: hooks);

        try
        {
            var (hwnd, meshes) = await StartMeshReplacement(window, renderer, context, title, useWarp: true, token);
            Volatile.Write(ref armed, true);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmit(CreateTerrainFrame() with { Sequence = 2 }));
            var gpu = await signalReached.Task.WaitAsync(token);
            if (!injectSignalFailure)
                Assert.AreEqual(0, await retirementHeld.Task.WaitAsync(token));

            SubmitReplacement(meshes, 2, CreateTerrainPair(drawSecond: false));
            Assert.AreEqual(new MeshReplacementSnapshot(2, 1, 1), meshes.ReadMeshReplacement());
            Assert.AreEqual(1, gpu.LiveMeshBuffers);

            if (injectSignalFailure)
            {
                Assert.AreEqual(2, targetOwners.Active, "The window and admitted Present are still owned.");
                Assert.AreEqual(1, rendererOwners.Active);
                releaseSignal.Set();
                Assert.AreEqual(0, await retirementHeld.Task.WaitAsync(token), "The fence sentinel is synthetic.");
                await Assert.ThrowsAsync<IncompleteCleanupException>(() => renderer.StopAsync(token).AsTask());
                Assert.AreEqual(new RenderFaultSummary((uint)RendererStage.Present, signalError, null, 2), renderer.Read().FirstFault);
                Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
                Assert.IsTrue(runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
                Assert.AreEqual(ShutdownSettlement.Custody, runtime.SettleStop());
                Assert.AreEqual(1, targetOwners.Active, "The admitted Present has returned, but the window remains owned.");
                Assert.AreEqual(1, rendererOwners.Active);
                Assert.IsTrue(IsWindow(hwnd));
                Assert.AreNotEqual(nint.Zero, SendMessageTimeout(hwnd, 0, 0, 0, 2, 1000, out _)); // WM_NULL
                Assert.AreEqual(1, gpu.LiveMeshBuffers);
                Assert.IsNull(renderer.ReadDiagnostics().DeviceRemoval);
                Assert.AreEqual(new MeshReplacementSnapshot(2, 1, 1), meshes.ReadMeshReplacement());
                Assert.AreEqual(MeshReplacementSubmissionResult.Closed, meshes.TryDeclareMeshGeneration(3));
            }
            else
            {
                Assert.AreEqual(2UL, renderer.Read().LastPresentedSequence,
                    "The second frame's real post-Present signal returned successfully.");
                Assert.AreEqual(RendererLifecycle.Running, renderer.Read().Lifecycle);
                Assert.IsFalse(runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
            }

            permitRemoval.Set();
            while (renderer.Read().Lifecycle != RendererLifecycle.Stopped)
                await Task.Delay(10, token);
            var stopped = renderer.Read();
            while (window.Read().Phase != HostWindowPhase.Closed)
                await Task.Delay(10, token);
            await renderer.StopAsync(token);
            await window.StopAsync(token);

            Assert.AreEqual(0, Volatile.Read(ref installations));
            Assert.AreEqual(0, gpu.LiveMeshBuffers);
            Assert.AreEqual(new MeshReplacementSnapshot(2, 0, 1), meshes.ReadMeshReplacement());
            Assert.AreEqual(0, targetOwners.Active);
            Assert.AreEqual(0, rendererOwners.Active);
            Assert.IsFalse(IsWindow(hwnd));
            Assert.AreEqual(ShutdownSettlement.Released, runtime.SettleStop());
            Assert.AreEqual(injectSignalFailure, stopped.IncompleteCleanup);

            var diagnostics = renderer.ReadDiagnostics();
            Console.WriteLine($"Debug layer enabled {diagnostics.DebugLayerEnabled}, DRED enabled {diagnostics.DredEnabled}.");
            if (diagnostics.DebugMessages is { } debug)
                Console.WriteLine($"Removal debug report HRESULT 0x{debug.LiveObjectReportResult:X8}, live child objects {debug.LiveChildObjects}.");
            Assert.IsTrue(diagnostics.DredEnabled);
            var removal = diagnostics.DeviceRemoval;
            Assert.IsNotNull(removal);
            Assert.IsLessThan(0, removal.Reason);
            Assert.AreEqual(confirmedRemoval, removal.Reason);
            Assert.AreEqual(0, removal.BreadcrumbResult);
            Assert.AreEqual(0, removal.PageFaultResult);
            Assert.IsLessThanOrEqualTo(16, removal.Breadcrumbs.Count);
            Assert.IsLessThanOrEqualTo(16, removal.ExistingAllocationTypes.Count);
            Assert.IsLessThanOrEqualTo(16, removal.RecentlyFreedAllocationTypes.Count);
            if (!injectSignalFailure)
            {
                Assert.AreEqual((uint)RendererStage.Retire, stopped.FirstFault?.Stage);
                Assert.AreEqual(removal.Reason, stopped.FirstFault?.DeviceRemovalReason);
            }

            Console.WriteLine($"WARP retirement fault: injected signal failure={injectSignalFailure}, " +
                $"confirmed device removal=0x{removal.Reason:X8}, DRED breadcrumb result=0x{removal.BreadcrumbResult:X8} " +
                $"({removal.Breadcrumbs.Count} records), page fault result=0x{removal.PageFaultResult:X8}. " +
                "Retained generation 1 until removal, then released all mesh and runtime ownership.");
        }
        finally
        {
            permitRemoval.Set();
            releaseSignal.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private static unsafe int RemoveWarpRetirementDevice(GpuDevice gpu)
    {
        ID3D12Device5* removable = null;
        Assert.AreEqual(0, gpu.Device->QueryInterface(__uuidof<ID3D12Device5>(), (void**)&removable).Value);
        try
        {
            removable->RemoveDevice();
            var reason = gpu.RemovedReason;
            Assert.IsLessThan(0, reason);
            return reason;
        }
        finally
        {
            removable->Release();
        }
    }
}
