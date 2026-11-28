using System.Diagnostics;

namespace Groundwork.Rendering.D3D12;

/// <summary>A real DXGI Present call bracket in the system QPC clock domain.</summary>
/// <remarks>Use the process and native thread with the call bracket to find the raw DXGI events.
/// The window and swap-chain addresses corroborate a join and can be reused across lifetimes.
/// A recorded result identifies a call, including an unsuccessful call. Display requires separate ETW evidence.</remarks>
public readonly record struct D3D12NativePresentMarker(
    long SwapChainLifetime, ulong CallOrdinal, int ProcessId, uint NativeThreadId, ulong WindowHandle,
    ulong SwapChainAddress, ulong QueueFence, long BeforeQpc, long AfterQpc, uint SyncInterval, uint Flags, int HResult);

/// <summary>A fixed-size, opt-in buffer of real DXGI Present calls from attached renderers.</summary>
/// <remarks>Present test probes and injected failures are excluded. The buffer retains its earliest records
/// and counts every discarded record. Read after the traced renderers stop to avoid contending with them.</remarks>
public sealed class D3D12NativePresentTrace
{
    private const int MaximumCapacity = 65_536;
    private static long _lastLifetime;
    private readonly Lock _gate = new();
    private readonly D3D12NativePresentMarker[] _records;
    private int _count;
    private long _dropped;

    public D3D12NativePresentTrace(int capacity = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaximumCapacity);
        _records = new D3D12NativePresentMarker[capacity];
    }

    /// <summary>The frequency of the system QPC timestamps.</summary>
    public static long QpcFrequency => Stopwatch.Frequency;

    /// <summary>Copy retained records and return the number discarded due to capacity.</summary>
    public D3D12NativePresentMarker[] Read(out long dropped)
    {
        lock (_gate)
        {
            dropped = _dropped;
            return _records.AsSpan(0, _count).ToArray();
        }
    }

    internal static long NewSwapChainLifetime() => Interlocked.Increment(ref _lastLifetime);

    internal void Record(in D3D12NativePresentMarker marker)
    {
        lock (_gate)
        {
            if (_count == _records.Length)
                _dropped++;
            else
                _records[_count++] = marker;
        }
    }
}
