using Groundwork.Core;

namespace Groundwork.Platform.Windows;

internal static class WindowsInputTranslation
{
    internal static InputControl Key(nuint virtualKey, nint parameters)
    {
        var extended = (parameters & (1 << 24)) != 0;
        var scan = (parameters >> 16) & 0xff;
        return virtualKey switch
        {
            >= 0x41 and <= 0x5a => InputControl.A + (int)(virtualKey - 0x41),
            >= 0x30 and <= 0x39 => InputControl.Digit0 + (int)(virtualKey - 0x30),
            >= 0x70 and <= 0x7b => InputControl.F1 + (int)(virtualKey - 0x70),
            >= 0x60 and <= 0x69 => InputControl.Numpad0 + (int)(virtualKey - 0x60),
            0x08 => InputControl.Backspace,
            0x09 => InputControl.Tab,
            0x0d => extended ? InputControl.NumpadEnter : InputControl.Enter,
            0x10 => scan == 0x36 ? InputControl.RightShift : InputControl.LeftShift,
            0x11 => extended ? InputControl.RightControl : InputControl.LeftControl,
            0x12 => extended ? InputControl.RightAlt : InputControl.LeftAlt,
            0x13 => InputControl.Pause,
            0x14 => InputControl.CapsLock,
            0x1b => InputControl.Escape,
            0x20 => InputControl.Space,
            0x21 => extended ? InputControl.PageUp : InputControl.Numpad9,
            0x22 => extended ? InputControl.PageDown : InputControl.Numpad3,
            0x23 => extended ? InputControl.End : InputControl.Numpad1,
            0x24 => extended ? InputControl.Home : InputControl.Numpad7,
            0x25 => extended ? InputControl.Left : InputControl.Numpad4,
            0x26 => extended ? InputControl.Up : InputControl.Numpad8,
            0x27 => extended ? InputControl.Right : InputControl.Numpad6,
            0x28 => extended ? InputControl.Down : InputControl.Numpad2,
            0x2c => InputControl.PrintScreen,
            0x2d => extended ? InputControl.Insert : InputControl.Numpad0,
            0x2e => extended ? InputControl.Delete : InputControl.NumpadDecimal,
            0x0c => InputControl.Numpad5,
            0x5b => InputControl.LeftSystem,
            0x5c => InputControl.RightSystem,
            0x5d => InputControl.Menu,
            0x6a => InputControl.NumpadMultiply,
            0x6b => InputControl.NumpadAdd,
            0x6d => InputControl.NumpadSubtract,
            0x6e => InputControl.NumpadDecimal,
            0x6f => InputControl.NumpadDivide,
            0x90 => InputControl.NumLock,
            0x91 => InputControl.ScrollLock,
            0xa0 => InputControl.LeftShift,
            0xa1 => InputControl.RightShift,
            0xa2 => InputControl.LeftControl,
            0xa3 => InputControl.RightControl,
            0xa4 => InputControl.LeftAlt,
            0xa5 => InputControl.RightAlt,
            0xba => InputControl.Semicolon,
            0xbb => InputControl.Equals,
            0xbc => InputControl.Comma,
            0xbd => InputControl.Minus,
            0xbe => InputControl.Period,
            0xbf => InputControl.Slash,
            0xc0 => InputControl.Grave,
            0xdb => InputControl.LeftBracket,
            0xdc or 0xe2 => InputControl.Backslash,
            0xdd => InputControl.RightBracket,
            0xde => InputControl.Apostrophe,
            _ => InputControl.None
        };
    }

    internal static InputControl MouseButton(uint message, nuint parameter) => message switch
    {
        Win32.LeftDown or Win32.LeftUp or Win32.LeftDoubleClick => InputControl.PointerPrimary,
        Win32.RightDown or Win32.RightUp or Win32.RightDoubleClick => InputControl.PointerSecondary,
        Win32.MiddleDown or Win32.MiddleUp or Win32.MiddleDoubleClick => InputControl.PointerMiddle,
        Win32.AuxiliaryDown or Win32.AuxiliaryUp or Win32.AuxiliaryDoubleClick => HighSigned(parameter) switch
        {
            1 => InputControl.PointerAuxiliary1,
            2 => InputControl.PointerAuxiliary2,
            _ => InputControl.None
        },
        _ => InputControl.None
    };

    internal static InputPoint Position(nint parameter)
        => new(unchecked((short)parameter), HighSigned(unchecked((nuint)parameter)));

    internal static int HighSigned(nuint parameter) => unchecked((short)(parameter >> 16));
}
