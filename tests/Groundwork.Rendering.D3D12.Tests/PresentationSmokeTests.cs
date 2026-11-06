using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Groundwork.Core;
using Groundwork.Platform.Windows;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed partial class PresentationSmokeTests
{
    private static readonly D3D12DiagnosticsOptions QualificationDiagnostics = new(EnableDebugLayer: true, EnableDred: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12Hardware")]
    public void HardwareReadbackContainsCubeColorAndDepth() => VerifyDiagnosticReadback(useWarp: false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12WarpDebugLayer")]
    public void WarpReadbackWithOptionalDebugLayerReportsCleanTeardown() => VerifyDiagnosticReadback(useWarp: true);

    private static void VerifyDiagnosticReadback(bool useWarp)
    {
        var gpu = new GpuDevice(warp: useWarp, enableDebugLayer: true, enableDred: true);
        using (gpu)
        {
            AssertAdapter(gpu.Adapter, useWarp);
            var near = new RenderScene(Matrix4x4.CreateTranslation(0, 0, -2), new Vector4(1, 0, 0, 1),
                Matrix4x4.Identity, 1, 0.2f, 10);
            var far = near with { World = Matrix4x4.CreateTranslation(0, 0, -4), Tint = new Vector4(0, 1, 0, 1) };
            var pixels = GpuDevice.RenderOffscreen(gpu, 160, 96, new RenderFrame(1, new Vector4(0, 0, 0, 1), near), far);
            CollectionAssert.AreEqual(new byte[] { 255, 0, 0, 255 }, pixels.ColorAt(80, 48).ToArray());
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 255 }, pixels.ColorAt(0, 0).ToArray());
            var expectedDepth = near.FarPlane / (near.FarPlane - near.NearPlane) * (1 - near.NearPlane / 1.5f);
            Assert.AreEqual(expectedDepth, pixels.DepthAt(80, 48), 0.00001f);
            Assert.AreEqual(1f, pixels.DepthAt(0, 0));
            Assert.IsNull(gpu.CaptureDred());
        }

        AssertCleanDiagnostics(new D3D12DiagnosticsSnapshot(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled,
            gpu.FinalDebugSummary, null), diagnosticsRequested: true);
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(false)]
    [DataRow(true)]
    public Task WarpSwapChainPresentsThroughResizeMinimizeRestoreAndNativeClose(bool minimizeDuringStartup) =>
        VerifySwapChainLifecycle(useWarp: true, minimizeDuringStartup);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12Hardware")]
    public Task HardwareSwapChainPresentsThroughResizeMinimizeRestoreAndNativeClose() =>
        VerifySwapChainLifecycle(useWarp: false, minimizeDuringStartup: false);

    private static async Task VerifySwapChainLifecycle(bool useWarp, bool minimizeDuringStartup)
    {
        Assert.IsTrue(Environment.UserInteractive, "Run this test on an interactive Windows desktop.");
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = watchdog.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var title = $"Groundwork D3D12 {Guid.NewGuid():N}";

        var targetOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var rendererOwners = new LeaseTracker(runtime.RuntimeOwnership);
        using var gate = new StartupOperationGate(targetOwners);
        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32),
            runtime.UiWake, minimizeDuringStartup ? gate : targetOwners);
        await using var renderer = useWarp
            ? new D3D12Renderer(window, rendererOwners, useWarp: true, startupTimeout: TimeSpan.FromSeconds(15))
            : new D3D12Renderer(QualificationDiagnostics, window, rendererOwners, startupTimeout: TimeSpan.FromSeconds(15));
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            var hwnd = FindWindow(null, title);
            Assert.AreNotEqual(nint.Zero, hwnd);
            var starting = renderer.StartAsync(token).AsTask();
            if (minimizeDuringStartup)
            {
                try
                {
                    await gate.Entered.Task.WaitAsync(token);
                    Assert.IsTrue(ShowWindowAsync(hwnd, 6));
                    while (window.Read().Phase != HostWindowPhase.Minimized)
                        await Task.Delay(10, token);
                }
                finally { gate.Release.Set(); }
            }
            await starting;
            AssertAdapter(renderer.ReadDiagnostics().Adapter, useWarp);
            if (minimizeDuringStartup)
            {
                Assert.AreEqual(RenderSurfaceState.Suspended, renderer.Read().Surface);
                Assert.IsTrue(ShowWindowAsync(hwnd, 9));
            }

            var frame = new RenderFrame(1, new Vector4(0.1f, 0.2f, 0.3f, 1), new RenderScene(
                Matrix4x4.CreateRotationY(0.3f), new Vector4(1, 0.5f, 0.25f, 1),
                Matrix4x4.CreateLookAt(new Vector3(1, 1, 3), Vector3.Zero, Vector3.UnitY), 1, 0.1f, 10));
            var submit = Stopwatch.GetTimestamp();
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmit(frame));
            var submitDuration = Stopwatch.GetElapsedTime(submit);
            var first = await AwaitStatus(renderer, static state => state.LastPresentedSequence == 1, token);
            Console.WriteLine($"Submit {submitDuration.TotalMilliseconds:F3} ms, first present observed after {Stopwatch.GetElapsedTime(submit).TotalMilliseconds:F3} ms.");

            Assert.IsTrue(SetWindowPos(hwnd, 0, 0, 0, 480, 340, 0x0016));
            await AwaitStatus(renderer, state => state.Surface == RenderSurfaceState.Drawable &&
                state.AppliedWidth != first.AppliedWidth, token);
            Assert.IsTrue(ShowWindowAsync(hwnd, 6)); // SW_MINIMIZE
            await AwaitStatus(renderer, static state => state.Surface == RenderSurfaceState.Suspended, token);

            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmit(frame with { Sequence = 2 }));
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
            Assert.IsTrue(ShowWindowAsync(hwnd, 9)); // SW_RESTORE
            await AwaitStatus(renderer, static state => state.LastPresentedSequence == 2, token);

            var close = Stopwatch.GetTimestamp();
            Assert.IsTrue(PostMessage(hwnd, 0x0010, 0, 0)); // WM_CLOSE
            await AwaitStatus(renderer, static state => state.Lifecycle == RendererLifecycle.Stopped, token);
            await renderer.StopAsync(token);
            await window.StopAsync(token);
            Assert.IsFalse(IsWindow(hwnd));
            Assert.AreEqual(RenderSubmissionResult.Closed, renderer.TrySubmit(frame with { Sequence = 3 }));
            Assert.IsNull(renderer.Read().FirstFault);
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            Assert.AreEqual(0, targetOwners.Active);
            Assert.AreEqual(0, rendererOwners.Active);
            Assert.AreEqual(ShutdownSettlement.Released, runtime.SettleStop());
            Console.WriteLine($"Native close to settled stop {Stopwatch.GetElapsedTime(close).TotalMilliseconds:F3} ms.");
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: !useWarp);
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
    public Task BlockedResizeStopsCoreAndRetainsOwnershipUntilTheCallReturns(bool rejectDiagnostics) =>
        VerifyBlockedResize(useWarp: true, rejectDiagnostics);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12Hardware")]
    public Task HardwareBlockedResizeStopsCoreAndRetainsOwnershipUntilTheCallReturns() =>
        VerifyBlockedResize(useWarp: false, rejectDiagnostics: false);

    private static async Task VerifyBlockedResize(bool useWarp, bool rejectDiagnostics)
    {
        Assert.IsTrue(Environment.UserInteractive, "Run this test on an interactive Windows desktop.");
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = watchdog.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var runtime = CreateRuntime(timing);
        var reporter = new TrackingReporter(runtime.Reporter, rejectDiagnostics);
        var context = new EngineModuleContext(TimeProvider.System, timing, reporter, runtime.StopControl);
        var title = $"Groundwork blocked resize {Guid.NewGuid():N}";
        var targetOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var rendererOwners = new LeaseTracker(runtime.RuntimeOwnership);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resizeReturned = false;
        using var release = new ManualResetEventSlim();

        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32), runtime.UiWake, targetOwners);
        await using var renderer = new D3D12Renderer(window, rendererOwners, TimeSpan.FromSeconds(1), (chain, width, height) =>
        {
            // Stall the already-admitted resize, then let it complete against the native swap chain.
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("The resize fixture did not release the driver call.");
            chain.Resize(width, height);
            Volatile.Write(ref resizeReturned, true);
        }, useWarp, diagnostics: useWarp ? default : QualificationDiagnostics);

        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            AssertAdapter(renderer.ReadDiagnostics().Adapter, useWarp);
            var hwnd = FindWindow(null, title);
            Assert.AreNotEqual(nint.Zero, hwnd);
            Assert.IsTrue(SetWindowPos(hwnd, 0, 0, 0, 480, 340, 0x0016));
            await entered.Task.WaitAsync(token);
            var blocked = Stopwatch.GetTimestamp();

            // No caller stop or native close precedes this assertion.
            while (!runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop))
                await Task.Delay(10, token);
            Console.WriteLine($"Blocked resize to observed Core stop {Stopwatch.GetElapsedTime(blocked).TotalMilliseconds:F3} ms.");

            var stopping = renderer.Read();
            Assert.AreEqual(RendererLifecycle.Stopping, stopping.Lifecycle);
            Assert.AreEqual((uint?)RendererStage.Resize, stopping.FirstFault?.Stage);
            Assert.AreEqual(RenderSubmissionResult.Closed, renderer.TrySubmit(new RenderFrame(1, Vector4.One)));
            await Assert.ThrowsAsync<IncompleteCleanupException>(() => renderer.StopAsync(token).AsTask());
            Assert.AreEqual(ShutdownSettlement.Custody, runtime.SettleStop());
            Assert.AreEqual(2, targetOwners.Active, "The window and its admitted operation must both retain ownership.");
            Assert.AreEqual(1, rendererOwners.Active, "The renderer must retain its device and swap-chain ownership.");
            Assert.IsTrue(IsWindow(hwnd));
            Assert.AreNotEqual(nint.Zero, SendMessageTimeout(hwnd, 0, 0, 0, 2, 1000, out _)); // WM_NULL
            Assert.IsTrue(renderer.Read().IncompleteCleanup);

            var unblocked = Stopwatch.GetTimestamp();
            release.Set();
            while (renderer.Read().Lifecycle != RendererLifecycle.Stopped || window.Read().Phase != HostWindowPhase.Closed)
                await Task.Delay(10, token);
            await renderer.StopAsync(token);
            await window.StopAsync(token);
            Assert.IsFalse(IsWindow(hwnd));
            Assert.IsTrue(Volatile.Read(ref resizeReturned));
            Assert.AreEqual(0, targetOwners.Active);
            Assert.AreEqual(0, rendererOwners.Active);
            Assert.AreEqual(ShutdownSettlement.Released, runtime.SettleStop());
            Assert.AreEqual(1, reporter.Primary);
            Assert.AreEqual(1, reporter.Cleanup);
            Console.WriteLine($"Late driver release to settled stop {Stopwatch.GetElapsedTime(unblocked).TotalMilliseconds:F3} ms.");
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: !useWarp);
        }
        finally
        {
            release.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private static void AssertAdapter(D3D12AdapterInfo? adapter, bool useWarp)
    {
        Assert.IsNotNull(adapter);
        Assert.AreEqual(useWarp, adapter.IsSoftware);
        Assert.IsFalse(string.IsNullOrWhiteSpace(adapter.Description));
        Console.WriteLine($"DXGI adapter {adapter.Description}, vendor 0x{adapter.VendorId:X4}, device 0x{adapter.DeviceId:X4}, driver {adapter.DriverVersion ?? "unavailable"}, software {adapter.IsSoftware}.");
    }

    private static void AssertCleanDiagnostics(D3D12DiagnosticsSnapshot diagnostics, bool diagnosticsRequested)
    {
        Console.WriteLine($"Debug layer enabled {diagnostics.DebugLayerEnabled}, DRED enabled {diagnostics.DredEnabled}.");
        Assert.AreEqual(diagnosticsRequested, diagnostics.DredEnabled);
        Assert.IsNull(diagnostics.DeviceRemoval);
        if (!diagnosticsRequested)
            Assert.IsFalse(diagnostics.DebugLayerEnabled);

        if (!diagnostics.DebugLayerEnabled)
        {
            Assert.IsNull(diagnostics.DebugMessages);
            return;
        }

        var messages = diagnostics.DebugMessages;
        Assert.IsNotNull(messages);
        Console.WriteLine($"Debug warnings {messages.Warnings}, errors {messages.Errors}, corruption {messages.Corruption}, discarded {messages.Discarded}, live-object report HRESULT {messages.LiveObjectReportResult:X8}, live child objects {messages.LiveChildObjects}.");
        Assert.AreEqual(0UL, messages.Errors);
        Assert.AreEqual(0UL, messages.Corruption);
        Assert.AreEqual(0UL, messages.Discarded);
        Assert.AreEqual(0, messages.LiveObjectReportResult);
        Assert.AreEqual(0, messages.LiveChildObjects);
    }

    private static RuntimeSafetyAuthority CreateRuntime(RuntimeTimingLimits timing)
    {
        var quota = new ResourceQuota(1, 8, new JobQuota(2, 1, 2, 2, 2, 2, 2, 4096, 4096, 4096),
            new DiagnosticQuota(4, 8192, 12, 32768, 4, 4, 64, 1024));
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, quota));
        using var reservation = budget.TryReserveRuntime(quota);
        Assert.IsNotNull(reservation);
        return RuntimeSafetyAuthority.Create(TimeProvider.System, timing, reservation);
    }

    private sealed class TrackingReporter(IFaultReporter inner, bool reject) : IFaultReporter
    {
        private int _primary;
        private int _cleanup;
        internal int Primary => Volatile.Read(ref _primary);
        internal int Cleanup => Volatile.Read(ref _cleanup);

        public FaultReportStatus TryReport(scoped in FaultObservation observation)
        {
            if (observation.Origin == FaultOrigin.Primary)
                Interlocked.Increment(ref _primary);
            if (observation.Origin == FaultOrigin.Cleanup)
                Interlocked.Increment(ref _cleanup);
            return reject ? FaultReportStatus.EvidenceLost : inner.TryReport(observation);
        }
    }

    private sealed class LeaseTracker(IRuntimeOwnership inner) : IRuntimeOwnership
    {
        private int _active;
        internal int Active => Volatile.Read(ref _active);

        public IRuntimeWorkLease? TryTrack(object retainedOwner)
        {
            var lease = inner.TryTrack(retainedOwner);
            if (lease is null)
                return null;
            Interlocked.Increment(ref _active);
            return new TrackedLease(this, lease);
        }

        private sealed class TrackedLease(LeaseTracker tracker, IRuntimeWorkLease inner) : IRuntimeWorkLease
        {
            public void Dispose()
            {
                inner.Dispose();
                Interlocked.Decrement(ref tracker._active);
            }
        }
    }

    private sealed class StartupOperationGate(IRuntimeOwnership inner) : IRuntimeOwnership, IDisposable
    {
        private int _calls;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();

        public IRuntimeWorkLease? TryTrack(object retainedOwner)
        {
            if (Interlocked.Increment(ref _calls) == 2)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The startup fixture did not release native admission.");
            }
            return inner.TryTrack(retainedOwner);
        }

        public void Dispose() => Release.Dispose();
    }

    private static async Task<RendererSnapshot> AwaitStatus(D3D12Renderer renderer,
        Func<RendererSnapshot, bool> reached, CancellationToken token)
    {
        while (true)
        {
            var state = renderer.Read();
            Assert.IsNull(state.FirstFault, $"Renderer fault: {state.FirstFault}");
            if (reached(state))
            {
                return state;
            }

            await Task.Delay(10, token);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? className, string title);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint hwnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
}
