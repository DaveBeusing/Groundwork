using System.Runtime.InteropServices;
using Groundwork.Core;

namespace Groundwork.Platform.Windows.Tests;

public sealed partial class WindowsDesktopSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task PresentationTargetTracksTheLatestDrawableExtentAndMinimize()
    {
        RequireInteractiveDesktop();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = watchdog.Token;
        await using var fixture = new HostFixture();
        Assert.IsNull(fixture.Host.TryAcquirePresentationTarget());
        await fixture.InitializeAsync(token);
        Assert.IsNull(fixture.Host.TryAcquirePresentationTarget());
        await fixture.Host.StartAsync(token);
        using var target = fixture.Host.TryAcquirePresentationTarget();
        Assert.IsNotNull(target);
        using var operation = target.TryBeginOperation();
        Assert.IsNotNull(operation);
        var window = operation.WindowHandle;
        var initial = operation.Snapshot;

        const uint resizeFlags = 0x0016; // No move, z-order or activation.
        Assert.IsTrue(SetWindowPos(window, 0, 0, 0, 480, 360, resizeFlags));
        Assert.IsTrue(SetWindowPos(window, 0, 0, 0, 550, 410, resizeFlags));
        var resized = await target.WaitForChangeAsync(initial.Revision, token);
        Assert.IsTrue(resized.ClientWidth > initial.ClientWidth && resized.ClientHeight > initial.ClientHeight);
        Assert.AreEqual(initial, operation.Snapshot, "The admitted operation uses its own coherent snapshot.");

        Assert.IsTrue(ShowWindowAsync(window, 6)); // SW_MINIMIZE
        var minimized = await AwaitTargetPhase(target, HostWindowPhase.Minimized, token);
        Assert.AreEqual(0, minimized.ClientWidth);
        Assert.AreEqual(0, minimized.ClientHeight);
        Assert.IsTrue(ShowWindowAsync(window, 9)); // SW_RESTORE
        var restored = await AwaitTargetPhase(target, HostWindowPhase.Open, token);
        Assert.AreEqual(resized.ClientWidth, restored.ClientWidth);
        Assert.AreEqual(resized.ClientHeight, restored.ClientHeight);
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PresentationCloseKeepsThePumpResponsiveThroughDriverAndLeaseRelease(bool nativeClose)
    {
        RequireInteractiveDesktop();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = watchdog.Token;
        await using var fixture = new HostFixture();
        await fixture.InitializeAsync(token);
        await fixture.Host.StartAsync(token);
        using var target = fixture.Host.TryAcquirePresentationTarget();
        Assert.IsNotNull(target);

        var enteredDriver = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaveDriver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = Task.Run(async () =>
        {
            using var operation = target.TryBeginOperation();
            enteredDriver.TrySetResult(operation?.WindowHandle ?? nint.Zero);
            await leaveDriver.Task.WaitAsync(token);
        }, token);

        nint window;
        try
        {
            window = await enteredDriver.Task.WaitAsync(token);
            Assert.AreNotEqual(nint.Zero, window);
            if (nativeClose)
            {
                Assert.IsTrue(PostMessage(window, Win32.Close, 0, 0));
            }
            else
            {
                fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            }

            Assert.AreEqual(PresentationTargetRevocation.Closing, await target.Revoked.WaitAsync(token));
            Assert.IsNull(target.TryBeginOperation());
            Assert.IsNull(fixture.Host.TryAcquirePresentationTarget());
            Assert.IsTrue(IsWindow(window));
            Assert.IsFalse(driver.IsCompleted);
            AssertPumpResponsive(window);
        }
        finally
        {
            leaveDriver.TrySetResult();
            await driver.WaitAsync(token);
        }

        Assert.IsTrue(IsWindow(window), "Driver return does not acknowledge release of the swap chain.");
        target.Dispose();
        await fixture.Host.StopAsync(token);
        Assert.IsFalse(IsWindow(window));
        Assert.IsNull(target.TryBeginOperation());
        Assert.IsNull(fixture.Host.Failure);
        Assert.AreEqual(ShutdownSettlement.Released, fixture.Runtime.SettleStop());
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task PresentationStopTimeoutRetainsTheWindowUntilLateAcknowledgment()
    {
        RequireInteractiveDesktop();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = watchdog.Token;
        var fixture = new HostFixture(shutdown: TimeSpan.FromMilliseconds(200));
        bool reportedTimeout = false;
        try
        {
            await fixture.InitializeAsync(token);
            await fixture.Host.StartAsync(token);
            using var target = fixture.Host.TryAcquirePresentationTarget();
            Assert.IsNotNull(target);
            using var operation = target.TryBeginOperation();
            Assert.IsNotNull(operation);
            var window = operation.WindowHandle;

            await Assert.ThrowsAsync<IncompleteCleanupException>(() => fixture.Host.StopAsync(token).AsTask());
            reportedTimeout = true;
            Assert.AreEqual(PresentationTargetRevocation.Closing, await target.Revoked.WaitAsync(token));
            Assert.AreEqual(ShutdownSettlement.Custody, fixture.Runtime.SettleStop());
            AssertPumpResponsive(window);
            Assert.IsTrue(IsWindow(window));

            operation.Dispose();
            Assert.IsTrue(IsWindow(window));
            target.Dispose();
            await AwaitSnapshot(fixture.Host, state => state.Phase == HostWindowPhase.Closed, token);
            Assert.IsFalse(IsWindow(window));
            await AwaitCondition(() => fixture.Runtime.SettleStop() == ShutdownSettlement.Released, token);
            await Assert.ThrowsAsync<IncompleteCleanupException>(() => fixture.Host.StopAsync(token).AsTask());
        }
        finally
        {
            try
            {
                await fixture.DisposeAsync();
            }
            catch (IncompleteCleanupException) when (reportedTimeout)
            {
                // Stop keeps the first timeout visible after late release.
            }
        }
    }

    private static void AssertPumpResponsive(nint window)
    {
        const uint nullMessage = 0;
        const uint abortIfHung = 2;
        Assert.AreNotEqual(nint.Zero, SendMessageTimeout(window, nullMessage, 0, 0, abortIfHung, 1000, out _));
    }

    private static async Task<HostWindowSnapshot> AwaitTargetPhase(WindowsPresentationTargetLease target,
        HostWindowPhase phase, CancellationToken cancellationToken)
    {
        var snapshot = target.Read();
        while (snapshot.Phase != phase)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsFalse(target.Revoked.IsCompleted);
            snapshot = await target.WaitForChangeAsync(snapshot.Revision, cancellationToken);
        }

        return snapshot;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
}
