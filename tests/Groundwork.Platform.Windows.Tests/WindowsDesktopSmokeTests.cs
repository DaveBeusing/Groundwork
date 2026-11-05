using System.Diagnostics;
using System.Runtime.InteropServices;
using Groundwork.Core;

namespace Groundwork.Platform.Windows.Tests;

[TestClass]
public sealed partial class WindowsDesktopSmokeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task OpensResizesChangesFocusMinimizesAndClosesNativeWindow()
    {
        RequireInteractiveDesktop();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = watchdog.Token;
        using var process = Process.GetCurrentProcess();
        var handleBaseline = process.HandleCount;
        var clock = new UiClock();
        await using var fixture = new HostFixture(clock: clock);
        await fixture.InitializeAsync(token);
        await fixture.Host.StartAsync(token);

        var window = FindWindow(null, fixture.Options.Title);
        Assert.AreNotEqual(nint.Zero, window);
        var className = ReadClassName(window);
        var opened = await AwaitSnapshot(fixture.Host, state => state.Phase == HostWindowPhase.Open, token);
        Assert.IsGreaterThan(0, opened.ClientWidth);
        Assert.IsGreaterThan(0, opened.ClientHeight);

        Assert.IsTrue(SetWindowPos(window, 0, 0, 0, 540, 410, 0x0016)); // no move, z-order or activation
        var resized = await AwaitSnapshot(fixture.Host,
            state => state.ClientWidth != opened.ClientWidth && state.ClientHeight != opened.ClientHeight, token);
        Assert.IsGreaterThan(opened.Revision, resized.Revision);

        await Task.Run(() => Focus(window, window), token).WaitAsync(token);
        await AwaitSnapshot(fixture.Host, state => state.Focused, token);
        fixture.Host.Capture();
        Assert.IsTrue(PostMessage(window, Win32.KeyDown, 0x41, 0x001e0001));
        await AwaitCondition(() => fixture.Host.Capture().HeldControls.Contains(InputControl.A), token);

        clock.AllowUiSamples = false;
        try
        {
            await Task.Run(() => Focus(window, 0), token).WaitAsync(token);
            await AwaitSnapshot(fixture.Host, state => !state.Focused, token);
        }
        finally
        {
            clock.AllowUiSamples = true;
        }
        var cleared = fixture.Host.Capture();
        Assert.IsTrue(cleared.HeldControls.IsEmpty);
        Assert.IsNull(cleared.PointerPosition);
        Assert.IsTrue(cleared.Events.Any(e => e.Kind == InputEventKind.FocusCleared));
        await Task.Run(() => Focus(window, window), token).WaitAsync(token);
        await AwaitSnapshot(fixture.Host, state => state.Focused, token);

        Assert.IsTrue(ShowWindowAsync(window, 6)); // SW_MINIMIZE
        var minimized = await AwaitSnapshot(fixture.Host, state => state.Phase == HostWindowPhase.Minimized, token);
        Assert.AreEqual(0, minimized.ClientWidth);
        Assert.AreEqual(0, minimized.ClientHeight);
        Assert.IsTrue(ShowWindowAsync(window, 9)); // SW_RESTORE
        await AwaitSnapshot(fixture.Host, state => state.Phase == HostWindowPhase.Open, token);

        clock.AllowUiSamples = false;
        Assert.IsTrue(PostMessage(window, Win32.Close, 0, 0));
        await AwaitCondition(() => fixture.Runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop), token);
        await fixture.Host.StopAsync(token);
        Assert.AreEqual(HostWindowPhase.Closed, fixture.Host.Read().Phase);
        Assert.IsNull(fixture.Host.Failure);
        Assert.IsFalse(IsWindow(window));
        Assert.AreEqual(nint.Zero, FindWindow(null, fixture.Options.Title));
        Assert.IsFalse(IsClassRegistered(className));
        var wake = fixture.Runtime.UiWake.Handle.SafeWaitHandle;
        Assert.AreEqual(ShutdownSettlement.Released, fixture.Runtime.SettleStop());
        Assert.IsTrue(wake.IsClosed);
        process.Refresh();
        TestContext.WriteLine($"Process handles before startup: {handleBaseline}; after close and runtime release: {process.HandleCount}.");
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task RetainedRuntimeWakeStopsAnIdleNativePump()
    {
        RequireInteractiveDesktop();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = watchdog.Token;
        await using var fixture = new HostFixture();
        await fixture.InitializeAsync(token);
        await fixture.Host.StartAsync(token);
        var window = FindWindow(null, fixture.Options.Title);
        Assert.AreNotEqual(nint.Zero, window);

        fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        // Await the UI-owned final snapshot without calling the module's own stop signal.
        await AwaitSnapshot(fixture.Host, state => state.Phase == HostWindowPhase.Closed, token);
        await fixture.Host.StopAsync(token);
        Assert.IsFalse(IsWindow(window));
        Assert.IsNull(fixture.Host.Failure);
        Assert.AreEqual(ShutdownSettlement.Released, fixture.Runtime.SettleStop());
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(0xf008, 0, 2)] // SC_SIZE bottom-right / GUI_INMOVESIZE
    [DataRow(0xf100, 32, 8)] // SC_KEYMENU with space / GUI_SYSTEMMENUMODE
    public async Task RetainedRuntimeWakeExitsNativeModalLoop(int command, int parameter, int flag)
    {
        RequireInteractiveDesktop();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = watchdog.Token;
        await using var fixture = new HostFixture();
        await fixture.InitializeAsync(token);
        await fixture.Host.StartAsync(token);
        var window = FindWindow(null, fixture.Options.Title);
        Assert.AreNotEqual(nint.Zero, window);
        var thread = GetWindowThreadProcessId(window, 0);
        await Task.Run(() => Focus(window, window), token).WaitAsync(token);

        Assert.IsTrue(PostMessage(window, 0x0112, (nuint)command, parameter)); // WM_SYSCOMMAND
        try
        {
            await AwaitCondition(() => HasGuiFlag(thread, (uint)flag), token);
            fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            await AwaitSnapshot(fixture.Host, state => state.Phase == HostWindowPhase.Closed, token);
            await fixture.Host.StopAsync(token);
            Assert.IsFalse(IsWindow(window));
            Assert.IsNull(fixture.Host.Failure);
            Assert.AreEqual(ShutdownSettlement.Released, fixture.Runtime.SettleStop());
        }
        finally
        {
            PostMessage(window, Win32.CancelMode, 0, 0);
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task CancelledStopWaitLeavesOwnerFreeToFinish()
    {
        RequireInteractiveDesktop();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = watchdog.Token;
        using var ownership = new PausedRelease();
        await using var fixture = new HostFixture(ownership: ownership);
        ownership.Inner = fixture.Runtime.RuntimeOwnership;
        await fixture.InitializeAsync(token);
        await fixture.Host.StartAsync(token);

        fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        try
        {
            await ownership.Releasing.Task.WaitAsync(token);
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => fixture.Host.StopAsync(new CancellationToken(true)).AsTask());
        }
        finally
        {
            ownership.Resume.Set();
        }

        await fixture.Host.StopAsync(token);
        Assert.IsNull(fixture.Host.Failure);
        Assert.AreEqual(ShutdownSettlement.Released, fixture.Runtime.SettleStop());
    }

    private sealed class PausedRelease : IRuntimeOwnership, IDisposable
    {
        internal IRuntimeOwnership Inner { get; set; } = null!;
        internal TaskCompletionSource Releasing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Resume { get; } = new();

        public IRuntimeWorkLease? TryTrack(object retainedOwner) => Inner.TryTrack(retainedOwner) is { } lease
            ? new Lease(this, lease)
            : null;

        public void Dispose() => Resume.Dispose();

        private sealed class Lease(PausedRelease owner, IRuntimeWorkLease inner) : IRuntimeWorkLease
        {
            public void Dispose()
            {
                owner.Releasing.TrySetResult();
                owner.Resume.Wait(TimeSpan.FromSeconds(5));
                inner.Dispose();
            }
        }
    }

    private static unsafe bool HasGuiFlag(uint thread, uint flag)
    {
        var info = new GuiThreadInfo { Size = (uint)sizeof(GuiThreadInfo) };
        return GetGUIThreadInfo(thread, &info) && (info.Flags & flag) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal uint Size;
        internal uint Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal Win32.Rectangle CaretRect;
    }

    private sealed class UiClock : TimeProvider
    {
        internal volatile bool AllowUiSamples = true;

        public override long GetTimestamp() => AllowUiSamples || Thread.CurrentThread.Name != "Groundwork Win32 host"
            ? TimeProvider.System.GetTimestamp()
            : throw new InvalidOperationException("The UI clock is unavailable during the lifecycle transition.");
    }

    private static void RequireInteractiveDesktop()
    {
        if (!Environment.UserInteractive)
        {
            Assert.Inconclusive("This smoke test needs an unlocked interactive Windows desktop.");
        }

        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0)
        {
            Assert.Inconclusive("The current session cannot open the interactive input desktop.");
        }

        Assert.IsTrue(CloseDesktop(desktop));
    }

    private static async Task<HostWindowSnapshot> AwaitSnapshot(
        WindowsHostModule host, Func<HostWindowSnapshot, bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = host.Read();
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AwaitCondition(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Focus(nint window, nint target)
    {
        Win32.PeekMessage(out _, 0, 0, 0, 0); // Ensure the caller has a message queue.
        var thread = GetWindowThreadProcessId(window, 0);
        var caller = GetCurrentThreadId();
        if (!AttachThreadInput(caller, thread, true))
        {
            throw Win32.Error(nameof(AttachThreadInput));
        }

        bool detached;
        try
        {
            SetFocus(target);
        }
        finally
        {
            detached = AttachThreadInput(caller, thread, false);
        }

        if (!detached)
        {
            throw Win32.Error(nameof(AttachThreadInput));
        }
    }

    private static unsafe bool IsClassRegistered(string className)
    {
        Win32.WindowClass definition;
        return GetClassInfo(Win32.GetModuleHandle(null), className, &definition);
    }

    private static unsafe string ReadClassName(nint window)
    {
        Span<char> buffer = stackalloc char[256];
        fixed (char* data = buffer)
        {
            var count = GetClassName(window, data, buffer.Length);
            return count == 0 ? throw Win32.Error(nameof(GetClassName)) : new string(buffer[..count]);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? className, string title);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint window, int command);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    private static unsafe partial int GetClassName(nint window, char* className, int count);

    [LibraryImport("user32.dll", EntryPoint = "GetClassInfoW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetClassInfo(nint instance, string className, Win32.WindowClass* definition);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, nint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetGUIThreadInfo(uint thread, GuiThreadInfo* info);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint caller, uint target, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint window);
}
