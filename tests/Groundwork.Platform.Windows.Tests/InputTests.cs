using Groundwork.Core;

namespace Groundwork.Platform.Windows.Tests;

[TestClass]
public sealed class InputTests
{
    [TestMethod]
    [DataRow(0x41, 0, InputControl.A)]
    [DataRow(0x39, 0, InputControl.Digit9)]
    [DataRow(0x7b, 0, InputControl.F12)]
    [DataRow(0x10, 0x360000, InputControl.RightShift)]
    [DataRow(0x11, 0, InputControl.LeftControl)]
    [DataRow(0x11, 0x1000000, InputControl.RightControl)]
    [DataRow(0x12, 0x1000000, InputControl.RightAlt)]
    [DataRow(0x0d, 0x1000000, InputControl.NumpadEnter)]
    [DataRow(0x24, 0, InputControl.Numpad7)]
    [DataRow(0x24, 0x1000000, InputControl.Home)]
    [DataRow(0xde, 0, InputControl.Apostrophe)]
    [DataRow(0xff, 0, InputControl.None)]
    public void NormalizesWindowsVirtualKeys(int nativeKey, int parameters, InputControl expected)
        => Assert.AreEqual(expected, WindowsInputTranslation.Key((nuint)nativeKey, parameters));

    [TestMethod]
    public void NormalizesSignedMouseCoordinatesWheelAndAuxiliaryButtons()
    {
        Assert.AreEqual(new InputPoint(-19, -7), WindowsInputTranslation.Position(unchecked((nint)0xfff9ffed)));
        Assert.AreEqual(-120, WindowsInputTranslation.HighSigned(0xff880000));
        Assert.AreEqual(InputControl.PointerAuxiliary1, WindowsInputTranslation.MouseButton(Win32.AuxiliaryDown, 1u << 16));
        Assert.AreEqual(InputControl.PointerAuxiliary2, WindowsInputTranslation.MouseButton(Win32.AuxiliaryUp, 2u << 16));
        Assert.AreEqual(InputControl.None, WindowsInputTranslation.MouseButton(Win32.AuxiliaryDown, 3u << 16));
    }

    [TestMethod]
    public void CaptureOrdersTransitionsAndKeepsUniqueSortedHeldState()
    {
        var buffer = new WindowsInputBuffer(8);
        buffer.Change(InputDevice.Keyboard, InputControl.Z, true, 10);
        buffer.Change(InputDevice.Keyboard, InputControl.A, true, 10);
        buffer.Change(InputDevice.Keyboard, InputControl.A, true, 10);
        buffer.Move(new InputPoint(-5, 12), 10);
        buffer.Wheel(InputControl.WheelHorizontal, -120, 11);

        var first = buffer.Capture();
        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3, 4 }, first.Events.Select(e => e.Sequence).ToArray());
        CollectionAssert.AreEqual(new[] { InputControl.A, InputControl.Z }, first.HeldControls.ToArray());
        Assert.AreEqual(new InputPoint(-5, 12), first.PointerPosition);
        Assert.AreEqual(-120, first.Events[3].WheelDelta);
        Assert.AreEqual(InputControl.WheelHorizontal, first.Events[3].Control);

        var second = buffer.Capture();
        Assert.IsTrue(second.Events.IsEmpty);
        Assert.AreEqual(4UL, second.LastSequence);
        Assert.HasCount(2, second.HeldControls);
        Assert.IsFalse(second.TransitionLoss);
    }

    [TestMethod]
    public void FocusLossClearsStateEvenWhenTransitionBufferIsFull()
    {
        var buffer = new WindowsInputBuffer(1);
        buffer.Change(InputDevice.Keyboard, InputControl.A, true, 11);
        buffer.Change(InputDevice.Mouse, InputControl.PointerPrimary, true, 11);
        buffer.Move(new InputPoint(2, 3), 12);
        buffer.ClearFocus(13);

        var snapshot = buffer.Capture();
        Assert.AreEqual(4UL, snapshot.LastSequence);
        Assert.HasCount(1, snapshot.Events);
        Assert.IsTrue(snapshot.HeldControls.IsEmpty);
        Assert.IsNull(snapshot.PointerPosition);
        Assert.IsTrue(snapshot.TransitionLoss);
        Assert.IsFalse(buffer.Capture().TransitionLoss);

        buffer.ClearFocus(14);
        var marker = buffer.Capture().Events[0];
        Assert.AreEqual(new InputEvent(InputEventKind.FocusCleared, InputDevice.Host, InputControl.None,
            5, 14, default, 0), marker);
    }

    [TestMethod]
    public void CaptureLossReleasesOnlyMouseButtons()
    {
        var buffer = new WindowsInputBuffer(8);
        buffer.Change(InputDevice.Keyboard, InputControl.A, true, 1);
        buffer.Change(InputDevice.Mouse, InputControl.PointerSecondary, true, 2);
        buffer.Capture();

        buffer.ClearPointerButtons(3);
        var snapshot = buffer.Capture();
        CollectionAssert.AreEqual(new[] { InputControl.A }, snapshot.HeldControls.ToArray());
        Assert.AreEqual(new InputEvent(InputEventKind.Released, InputDevice.Mouse,
            InputControl.PointerSecondary, 3, 3, default, 0), snapshot.Events[0]);
    }
}
