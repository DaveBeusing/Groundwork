using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Groundwork.Platform.Windows;

internal static partial class Win32
{
    internal const uint Close = 0x0010;
    internal const uint CancelMode = 0x001f;
    internal const uint Quit = 0x0012;
    internal const uint Size = 0x0005;
    internal const uint FocusGained = 0x0007;
    internal const uint FocusLost = 0x0008;
    internal const uint NonClientDestroy = 0x0082;
    internal const uint KeyDown = 0x0100;
    internal const uint KeyUp = 0x0101;
    internal const uint SystemKeyDown = 0x0104;
    internal const uint SystemKeyUp = 0x0105;
    internal const uint Timer = 0x0113;
    internal const uint MouseMove = 0x0200;
    internal const uint LeftDown = 0x0201;
    internal const uint LeftUp = 0x0202;
    internal const uint LeftDoubleClick = 0x0203;
    internal const uint RightDown = 0x0204;
    internal const uint RightUp = 0x0205;
    internal const uint RightDoubleClick = 0x0206;
    internal const uint MiddleDown = 0x0207;
    internal const uint MiddleUp = 0x0208;
    internal const uint MiddleDoubleClick = 0x0209;
    internal const uint VerticalWheel = 0x020a;
    internal const uint AuxiliaryDown = 0x020b;
    internal const uint AuxiliaryUp = 0x020c;
    internal const uint AuxiliaryDoubleClick = 0x020d;
    internal const uint HorizontalWheel = 0x020e;
    internal const uint EnterMenuLoop = 0x0211;
    internal const uint ExitMenuLoop = 0x0212;
    internal const uint CaptureChanged = 0x0215;
    internal const uint EnterSizeMove = 0x0231;
    internal const uint ExitSizeMove = 0x0232;
    internal const nuint SizeMinimized = 1;

    internal const uint WindowStyle = 0x00cf0000; // WS_OVERLAPPEDWINDOW
    internal const int UseDefault = unchecked((int)0x80000000);
    internal const int ShowNormal = 1;
    internal const uint RemoveMessage = 1;
    internal const uint Infinite = uint.MaxValue;
    internal const uint AllInput = 0x04ff;
    internal const uint InputAvailable = 4;
    internal const uint WaitObject = 0;
    internal const uint WaitFailed = uint.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowClass
    {
        internal uint Style;
        internal nint Procedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal nint MenuName;
        internal nint ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X;
        internal int Y;
        internal uint Private;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint GetModuleHandle(string? module);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    internal static partial ushort RegisterClass(in WindowClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterClass(nint atom, nint instance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindow(uint extendedStyle, nint atom, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    internal static partial nint LoadCursor(nint instance, nint cursor);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint window, out Rectangle rectangle);

    [LibraryImport("user32.dll")]
    internal static partial nint SetCapture(nint window);

    [LibraryImport("user32.dll")]
    internal static partial nint GetCapture();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool KillTimer(nint window, nuint id);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static unsafe partial uint MsgWaitForMultipleObjectsEx(
        uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(in Message message);

    internal static Win32Exception Error(string operation)
    {
        var code = Marshal.GetLastPInvokeError();
        return new Win32Exception(code, $"{operation} failed with Win32 error {code}.");
    }
}
