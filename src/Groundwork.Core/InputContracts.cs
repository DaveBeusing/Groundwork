using System.Collections.Immutable;

namespace Groundwork.Core;

public enum InputEventKind
{
    Pressed = 1,
    Released,
    PointerMoved,
    WheelScrolled,
    FocusCleared
}

public enum InputDevice
{
    Keyboard = 1,
    Mouse,
    Host
}

public enum InputControl
{
    None = 0,
    A = 1, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Digit0 = 30, Digit1, Digit2, Digit3, Digit4,
    Digit5, Digit6, Digit7, Digit8, Digit9,
    Escape = 50, Tab, Enter, Space, Backspace, Delete, Insert,
    Home, End, PageUp, PageDown, Up, Down, Left, Right,
    LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,
    F1 = 80, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    PointerPrimary = 100, PointerSecondary, PointerMiddle, PointerAuxiliary1, PointerAuxiliary2,
    WheelVertical, WheelHorizontal,
    Minus = 120, Equals, LeftBracket, RightBracket, Backslash, Semicolon, Apostrophe,
    Grave, Comma, Period, Slash, CapsLock, NumLock, ScrollLock, PrintScreen, Pause,
    LeftSystem, RightSystem, Menu,
    Numpad0 = 150, Numpad1, Numpad2, Numpad3, Numpad4, Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
    NumpadAdd, NumpadSubtract, NumpadMultiply, NumpadDivide, NumpadDecimal, NumpadEnter
}

public readonly record struct InputPoint(int X, int Y);

public readonly record struct InputEvent(
    InputEventKind Kind,
    InputDevice Device,
    InputControl Control,
    ulong Sequence,
    long Timestamp,
    InputPoint Position,
    int WheelDelta);

public sealed record InputSnapshot(
    ulong LastSequence,
    ImmutableArray<InputEvent> Events,
    ImmutableArray<InputControl> HeldControls,
    InputPoint? PointerPosition,
    bool TransitionLoss);

public interface IInputSource
{
    InputSnapshot Capture();
}

public enum HostWindowPhase
{
    Starting = 1,
    Open,
    Minimized,
    Closing,
    Closed
}

public readonly record struct HostWindowSnapshot(
    HostWindowPhase Phase,
    int ClientWidth,
    int ClientHeight,
    bool Focused,
    ulong Revision);

public interface IHostWindowStateSource
{
    HostWindowSnapshot Read();
}
