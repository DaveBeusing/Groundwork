using TerraFX.Interop.DirectX;

namespace Groundwork.Rendering.D3D12;

/// <summary>Retains one native device across consecutive renderers on the same adapter.</summary>
/// <remarks>Only one renderer can use the session at a time. Dispose it after the final renderer stops.</remarks>
public sealed unsafe class D3D12DeviceSession(D3D12DiagnosticsOptions diagnostics = default, bool useWarp = false) : IDisposable
{
    private readonly Lock _gate = new();
    private ID3D12Device* _device;
    private D3D12AdapterInfo? _adapter;
    private bool _attached;
    private bool _disposed;

    internal D3D12DiagnosticsOptions Diagnostics { get; } = diagnostics;
    internal bool UseWarp { get; } = useWarp;

    internal ID3D12Device* Attach(out D3D12AdapterInfo? adapter)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attached)
                throw new InvalidOperationException("The preceding renderer still owns this device session.");
            _attached = true;
            if (_device != null)
                _device->AddRef();
            adapter = _adapter;
            return _device;
        }
    }

    internal void Retain(ID3D12Device* device, D3D12AdapterInfo adapter)
    {
        lock (_gate)
        {
            if (_disposed || _device != null)
                return;
            device->AddRef();
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
            if (_device != null)
                _device->Release();
            _device = null;
            _adapter = null;
        }
    }
}
