using TerraFX.Interop.DirectX;

namespace Groundwork.Rendering.D3D12;

/// <summary>Retains native resources across consecutive renderers on the same adapter.</summary>
/// <remarks>Only one renderer can use the session at a time. Each renderer must settle its submissions before detaching.</remarks>
public sealed unsafe class D3D12DeviceSession : IDisposable
{
    private readonly Lock _gate = new();
    private readonly string? _queueName;
    private ID3D12Device* _device;
    private ID3D12CommandQueue* _queue;
    private D3D12AdapterInfo? _adapter;
    private bool _attached;
    private bool _disposed;

    public D3D12DeviceSession(D3D12DiagnosticsOptions diagnostics = default, bool useWarp = false)
        : this(diagnostics, useWarp, reuseDirectQueue: false) { }

    /// <summary>Optionally retains the direct queue along with the device. Dispose the session to release its references.</summary>
    public D3D12DeviceSession(D3D12DiagnosticsOptions diagnostics, bool useWarp, bool reuseDirectQueue)
    {
        Diagnostics = diagnostics;
        UseWarp = useWarp;
        if (reuseDirectQueue)
            _queueName = $"Groundwork retained direct queue {Guid.NewGuid():N}";
    }

    internal D3D12DiagnosticsOptions Diagnostics { get; }
    internal bool UseWarp { get; }
    internal string? RetainedQueueName
    {
        get
        {
            lock (_gate)
                return _queue == null ? null : _queueName;
        }
    }

    internal ID3D12Device* Attach(out D3D12AdapterInfo? adapter, out ID3D12CommandQueue* queue)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attached)
                throw new InvalidOperationException("The preceding renderer still owns this device session.");
            _attached = true;
            if (_device != null)
                _device->AddRef();
            if (_queue != null)
                _queue->AddRef();
            adapter = _adapter;
            queue = _queue;
            return _device;
        }
    }

    internal void Retain(ID3D12Device* device, ID3D12CommandQueue* queue, D3D12AdapterInfo adapter)
    {
        lock (_gate)
        {
            if (_disposed || _device != null)
                return;
            if (_queueName is not null)
            {
                fixed (char* name = _queueName)
                    GpuNative.Check(queue->SetName(name), "Name retained direct queue");
                queue->AddRef();
                _queue = queue;
            }
            device->AddRef();
            GpuDevice.CountDeviceReference();
            _device = device;
            _adapter = adapter;
        }
    }

    internal void Detach()
    {
        lock (_gate)
            _attached = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_queue != null)
                _queue->Release();
            if (_device != null)
            {
                _device->Release();
                GpuDevice.UncountDeviceReference();
            }
            _queue = null;
            _device = null;
            _adapter = null;
        }
    }
}
