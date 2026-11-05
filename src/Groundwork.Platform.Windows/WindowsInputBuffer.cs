using System.Collections.Immutable;
using Groundwork.Core;

namespace Groundwork.Platform.Windows;

internal sealed class WindowsInputBuffer(int capacity) : IInputSource
{
    private readonly Lock _gate = new();
    private readonly InputEvent[] _events = new InputEvent[capacity];
    private readonly bool[] _held = new bool[(int)InputControl.NumpadEnter + 1];
    private int _count;
    private ulong _sequence;
    private InputPoint? _position;
    private bool _transitionLoss;

    public InputSnapshot Capture()
    {
        lock (_gate)
        {
            var events = ImmutableArray.Create(_events, 0, _count);
            var held = ImmutableArray.CreateBuilder<InputControl>();
            for (var i = 1; i < _held.Length; i++)
            {
                if (_held[i])
                {
                    held.Add((InputControl)i);
                }
            }

            var snapshot = new InputSnapshot(_sequence, events, held.ToImmutable(), _position, _transitionLoss);
            _count = 0;
            _transitionLoss = false;
            return snapshot;
        }
    }

    internal void Change(InputDevice device, InputControl control, bool down, long timestamp)
    {
        if (control == InputControl.None)
        {
            return;
        }

        lock (_gate)
        {
            if (_held[(int)control] == down)
            {
                return;
            }

            _held[(int)control] = down;
            Append(down ? InputEventKind.Pressed : InputEventKind.Released, device, control, timestamp);
        }
    }

    internal void Move(InputPoint position, long timestamp)
    {
        lock (_gate)
        {
            _position = position;
            Append(InputEventKind.PointerMoved, InputDevice.Mouse, InputControl.None, timestamp, position);
        }
    }

    internal void Wheel(InputControl axis, int delta, long timestamp)
    {
        lock (_gate)
        {
            Append(InputEventKind.WheelScrolled, InputDevice.Mouse, axis, timestamp, delta: delta);
        }
    }

    internal void ClearFocus(long timestamp)
    {
        lock (_gate)
        {
            Array.Clear(_held);
            _position = null;
            Append(InputEventKind.FocusCleared, InputDevice.Host, InputControl.None, timestamp);
        }
    }

    internal void ClearPointerButtons(long timestamp)
    {
        lock (_gate)
        {
            for (var i = (int)InputControl.PointerPrimary; i <= (int)InputControl.PointerAuxiliary2; i++)
            {
                if (_held[i])
                {
                    _held[i] = false;
                    Append(InputEventKind.Released, InputDevice.Mouse, (InputControl)i, timestamp);
                }
            }
        }
    }

    internal bool HasPointerButton()
    {
        lock (_gate)
        {
            for (var i = (int)InputControl.PointerPrimary; i <= (int)InputControl.PointerAuxiliary2; i++)
            {
                if (_held[i])
                {
                    return true;
                }
            }

            return false;
        }
    }

    private void Append(InputEventKind kind, InputDevice device, InputControl control, long timestamp,
        InputPoint position = default, int delta = 0)
    {
        var sequence = checked(++_sequence);
        if (_count == _events.Length)
        {
            _transitionLoss = true;
            return;
        }

        _events[_count++] = new InputEvent(kind, device, control, sequence, timestamp, position, delta);
    }
}
