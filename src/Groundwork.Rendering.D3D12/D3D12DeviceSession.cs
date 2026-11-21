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
    private ID3D12RootSignature* _skyRoot;
    private ID3D12PipelineState* _skyPipeline;
    private ID3D12RootSignature* _skyLookupRoot;
    private ID3D12PipelineState* _skyLookupGraphics;
    private ID3D12PipelineState* _skyLookupCompute;
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

    // One immutable sky pipeline per device session. Each attached owner holds
    // its own COM references; command allocators, constants and fences remain
    // owner-local. A session never admits concurrent renderer owners.
    internal bool TryAcquireSkyPipeline(out ID3D12RootSignature* root, out ID3D12PipelineState* pipeline)
    {
        lock (_gate)
        {
            root = null;
            pipeline = null;
            if (_disposed || !_attached || _skyPipeline == null)
                return false;
            _skyRoot->AddRef();
            _skyPipeline->AddRef();
            root = _skyRoot;
            pipeline = _skyPipeline;
            return true;
        }
    }

    internal void RetainSkyPipeline(ID3D12RootSignature* root, ID3D12PipelineState* pipeline)
    {
        lock (_gate)
        {
            if (_disposed || !_attached || _skyPipeline != null)
                return;
            root->AddRef();
            pipeline->AddRef();
            _skyRoot = root;
            _skyPipeline = pipeline;
        }
    }

    // The lookup ABI has UAV/SRV tables and a compute PSO. Keep its complete
    // immutable triplet separate from the direct sky cache. Mutable LUTs,
    // descriptor heaps, constants and retirement fences never enter a session.
    internal bool TryAcquireSkyLookupPipelines(out ID3D12RootSignature* root,
        out ID3D12PipelineState* graphics, out ID3D12PipelineState* compute)
    {
        lock (_gate)
        {
            root = null;
            graphics = null;
            compute = null;
            if (_disposed || !_attached || _skyLookupRoot == null || _skyLookupGraphics == null || _skyLookupCompute == null)
                return false;
            _skyLookupRoot->AddRef();
            _skyLookupGraphics->AddRef();
            _skyLookupCompute->AddRef();
            root = _skyLookupRoot;
            graphics = _skyLookupGraphics;
            compute = _skyLookupCompute;
            return true;
        }
    }

    internal void RetainSkyLookupPipelines(ID3D12RootSignature* root,
        ID3D12PipelineState* graphics, ID3D12PipelineState* compute)
    {
        if (root == null || graphics == null || compute == null)
            throw new ArgumentException("The lookup cache requires a complete pipeline triplet.");
        lock (_gate)
        {
            if (_disposed || !_attached || _skyLookupRoot != null)
                return;
            root->AddRef();
            graphics->AddRef();
            compute->AddRef();
            _skyLookupRoot = root;
            _skyLookupGraphics = graphics;
            _skyLookupCompute = compute;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_skyPipeline != null)
                _skyPipeline->Release();
            if (_skyRoot != null)
                _skyRoot->Release();
            _skyPipeline = null;
            _skyRoot = null;
            if (_skyLookupCompute != null)
                _skyLookupCompute->Release();
            if (_skyLookupGraphics != null)
                _skyLookupGraphics->Release();
            if (_skyLookupRoot != null)
                _skyLookupRoot->Release();
            _skyLookupCompute = _skyLookupGraphics = null;
            _skyLookupRoot = null;
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
