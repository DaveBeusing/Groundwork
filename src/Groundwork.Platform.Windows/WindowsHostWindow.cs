using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Groundwork.Core;
using Microsoft.Win32.SafeHandles;

namespace Groundwork.Platform.Windows;

public sealed partial class WindowsHostModule
{
    private const int MessageBatchLimit = 64;
    private const nuint ModalStopTimer = 1;
    private const uint ModalStopPollMilliseconds = 25;
    [ThreadStatic] private static WindowsHostModule? s_uiHost;
    private nint _window;
    private bool _minimized;
    private int _modalDepth;

    private void RunUiThread(object? state)
    {
        var lease = (IRuntimeWorkLease)state!;
        SafeWaitHandle? wake = null;
        var wakeRetained = false;
        nint instance = 0;
        ushort atom = 0;
        s_uiHost = this;
        try
        {
            if (ObserveStop())
            {
                return;
            }

            wake = _wake.Handle.SafeWaitHandle;
            wake.DangerousAddRef(ref wakeRetained);
            instance = Win32.GetModuleHandle(null);
            if (instance == 0)
            {
                throw Win32.Error(nameof(Win32.GetModuleHandle));
            }

            atom = RegisterWindowClass(instance);
            _window = Win32.CreateWindow(0, atom, _options.Title, Win32.WindowStyle,
                Win32.UseDefault, Win32.UseDefault, _options.WindowWidth, _options.WindowHeight,
                0, 0, instance, 0);
            if (_window == 0)
            {
                throw Win32.Error(nameof(Win32.CreateWindow));
            }

            if (ObserveStop())
            {
                return;
            }

            Win32.ShowWindow(_window, Win32.ShowNormal);
            if (ObserveStop())
            {
                return;
            }

            UpdateSize(_window, _minimized, starting: false);
            _ready.TrySetResult();
            Pump(wake.DangerousGetHandle());
        }
        catch (Exception error)
        {
            FailUi(error);
        }
        finally
        {
            _ready.TrySetCanceled();
            var nativeReleased = ReleaseWindow(atom, instance);
            if (wakeRetained)
            {
                wake!.DangerousRelease();
            }

            s_uiHost = null;
            if (nativeReleased)
            {
                ReleaseOwnership(lease);
            }

            Finish(nativeReleased);
        }
    }

    private static unsafe ushort RegisterWindowClass(nint instance)
    {
        var className = $"Groundwork.Host.{Guid.NewGuid():N}";
        var cursor = Win32.LoadCursor(0, 32512); // IDC_ARROW, a shared system resource
        if (cursor == 0)
        {
            throw Win32.Error(nameof(Win32.LoadCursor));
        }

        fixed (char* name = className)
        {
            var definition = new Win32.WindowClass
            {
                Style = 8, // CS_DBLCLKS
                Procedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure,
                Instance = instance,
                Cursor = cursor,
                Background = 6, // COLOR_WINDOW + 1
                ClassName = (nint)name
            };
            var atom = Win32.RegisterClass(definition);
            return atom == 0 ? throw Win32.Error(nameof(Win32.RegisterClass)) : atom;
        }
    }

    private bool ObserveStop()
    {
        if (!StopRequested && _wake.Handle.WaitOne(TimeSpan.Zero))
        {
            // Composition reserves the UI wake for terminal host stop.
            MarkStop();
        }

        return StopRequested;
    }

    private unsafe void Pump(nint wake)
    {
        while (!StopRequested)
        {
            var result = Win32.MsgWaitForMultipleObjectsEx(
                1, &wake, Win32.Infinite, Win32.AllInput, Win32.InputAvailable);
            if (result == Win32.WaitObject)
            {
                MarkStop();
                return;
            }

            if (result == Win32.WaitFailed)
            {
                throw Win32.Error(nameof(Win32.MsgWaitForMultipleObjectsEx));
            }

            if (result != Win32.WaitObject + 1)
            {
                throw new InvalidOperationException($"Unexpected Win32 wait result {result}.");
            }

            for (var i = 0; i < MessageBatchLimit && !StopRequested; i++)
            {
                if (!Win32.PeekMessage(out var message, 0, 0, 0, Win32.RemoveMessage))
                {
                    break;
                }

                if (message.Id == Win32.Quit)
                {
                    RequestStop(HostStopReason.WindowClose);
                    return;
                }

                Win32.TranslateMessage(message);
                Win32.DispatchMessage(message);
            }
        }
    }

    private bool ReleaseWindow(ushort atom, nint instance)
    {
        var released = true;
        if (_window != 0)
        {
            try
            {
                BeginClosing(_window);
            }
            catch (Exception error)
            {
                RecordFailure(error, FaultClass.UnknownCritical, FaultOrigin.Cleanup);
            }

            if (!Win32.DestroyWindow(_window))
            {
                RecordFailure(Win32.Error(nameof(Win32.DestroyWindow)), FaultClass.IncompleteCleanup, FaultOrigin.Cleanup);
                released = false;
            }
        }

        if (atom != 0 && !Win32.UnregisterClass(atom, instance))
        {
            RecordFailure(Win32.Error(nameof(Win32.UnregisterClass)), FaultClass.IncompleteCleanup, FaultOrigin.Cleanup);
            released = false;
        }

        return released;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        var host = s_uiHost;
        if (host is null)
        {
            return Win32.DefWindowProc(window, message, wParam, lParam);
        }

        try
        {
            return host.HandleMessage(window, message, wParam, lParam);
        }
        catch (Exception error)
        {
            // No managed failure may cross the native callback boundary.
            try
            {
                host.FailUi(error);
            }
            catch (Exception)
            {
                host.MarkStop();
            }

            return 0;
        }
    }

    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == Win32.NonClientDestroy)
        {
            if (_window == window)
            {
                _window = 0;
                if (!StopRequested)
                {
                    RequestStop(HostStopReason.WindowClose);
                    BeginClosing(window);
                }
            }

            return Win32.DefWindowProc(window, message, wParam, lParam);
        }

        if (message == Win32.Close)
        {
            RequestStop(HostStopReason.WindowClose);
            BeginClosing(window);
            if (_modalDepth != 0)
            {
                Win32.DefWindowProc(window, Win32.CancelMode, 0, 0);
            }

            return 0;
        }

        // Native move/size and menu loops dispatch messages while the outer pump is paused.
        switch (message)
        {
            case Win32.EnterMenuLoop:
            case Win32.EnterSizeMove:
                if (_modalDepth++ == 0 && Win32.SetTimer(window, ModalStopTimer, ModalStopPollMilliseconds, 0) == 0)
                {
                    var error = Win32.Error(nameof(Win32.SetTimer));
                    Win32.DefWindowProc(window, Win32.CancelMode, 0, 0);
                    throw error;
                }

                return 0;
            case Win32.ExitMenuLoop:
            case Win32.ExitSizeMove:
                if (_modalDepth > 0 && --_modalDepth == 0)
                {
                    Win32.KillTimer(window, ModalStopTimer);
                }

                return 0;
            case Win32.Timer when wParam == ModalStopTimer:
                if (_modalDepth != 0 && ObserveStop())
                {
                    Win32.DefWindowProc(window, Win32.CancelMode, 0, 0);
                }

                return 0;
        }

        if (StopRequested)
        {
            return Win32.DefWindowProc(window, message, wParam, lParam);
        }

        var current = Read();
        switch (message)
        {
            case Win32.Size:
                _minimized = wParam == Win32.SizeMinimized;
                UpdateSize(window, _minimized, current.Phase == HostWindowPhase.Starting);
                break;
            case Win32.FocusGained:
                Publish(current.Phase, current.ClientWidth, current.ClientHeight, true);
                break;
            case Win32.FocusLost:
                ClearFocus(window);
                Publish(current.Phase, current.ClientWidth, current.ClientHeight, false);
                break;
            case Win32.KeyDown or Win32.KeyUp or Win32.SystemKeyDown or Win32.SystemKeyUp when current.Focused:
                Input.Change(InputDevice.Keyboard, WindowsInputTranslation.Key(wParam, lParam),
                    message is Win32.KeyDown or Win32.SystemKeyDown, SampleTimestamp());
                break;
            case Win32.MouseMove:
                Input.Move(WindowsInputTranslation.Position(lParam), SampleTimestamp());
                break;
            case Win32.VerticalWheel or Win32.HorizontalWheel:
                Input.Wheel(message == Win32.VerticalWheel ? InputControl.WheelVertical : InputControl.WheelHorizontal,
                    WindowsInputTranslation.HighSigned(wParam), SampleTimestamp());
                break;
            case Win32.CaptureChanged:
                Input.ClearPointerButtons(Volatile.Read(ref _lastClockTimestamp));
                break;
            default:
                if (HandleMouseButton(window, message, wParam) &&
                    message is Win32.AuxiliaryDown or Win32.AuxiliaryUp or Win32.AuxiliaryDoubleClick)
                {
                    return 1;
                }

                break;
        }

        return Win32.DefWindowProc(window, message, wParam, lParam);
    }

    private bool HandleMouseButton(nint window, uint message, nuint parameter)
    {
        var control = WindowsInputTranslation.MouseButton(message, parameter);
        if (control == InputControl.None)
        {
            return false;
        }

        var down = message is not (Win32.LeftUp or Win32.RightUp or Win32.MiddleUp or Win32.AuxiliaryUp);
        Input.Change(InputDevice.Mouse, control, down, SampleTimestamp());
        if (down)
        {
            Win32.SetCapture(window);
        }
        else if (!Input.HasPointerButton())
        {
            ReleaseCaptureIfOwned(window);
        }

        return true;
    }

    private void UpdateSize(nint window, bool minimized, bool starting)
    {
        if (!Win32.GetClientRect(window, out var rectangle))
        {
            throw Win32.Error(nameof(Win32.GetClientRect));
        }

        var phase = starting ? HostWindowPhase.Starting : minimized ? HostWindowPhase.Minimized : HostWindowPhase.Open;
        Publish(phase, minimized ? 0 : rectangle.Right - rectangle.Left,
            minimized ? 0 : rectangle.Bottom - rectangle.Top, Read().Focused);
    }

    private void BeginClosing(nint window)
    {
        var current = Read();
        if (current.Phase is HostWindowPhase.Closing or HostWindowPhase.Closed)
        {
            return;
        }

        ClearFocus(window);
        Publish(HostWindowPhase.Closing, current.ClientWidth, current.ClientHeight, false);
    }

    private void ClearFocus(nint window)
    {
        Input.ClearFocus(Volatile.Read(ref _lastClockTimestamp));
        ReleaseCaptureIfOwned(window);
    }

    private static void ReleaseCaptureIfOwned(nint window)
    {
        if (Win32.GetCapture() == window && !Win32.ReleaseCapture())
        {
            throw Win32.Error(nameof(Win32.ReleaseCapture));
        }
    }

    private void FailUi(Exception error)
    {
        var started = _ready.Task.IsCompletedSuccessfully;
        try
        {
            RequestStop(started ? HostStopReason.RuntimeFault : HostStopReason.StartupFault);
        }
        catch (Exception stopError)
        {
            RecordFailure(stopError, FaultClass.UnknownCritical, FaultOrigin.Cleanup);
        }

        RecordFailure(error, FaultClass.UnknownCritical, FaultOrigin.Primary);
        _ready.TrySetException(error);
    }
}
