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
    private ID3D12Resource* _parkedSkyLookupFields;
    private ID3D12DescriptorHeap* _parkedSkyLookupViews;
    private Guid _previewDebugIdentity;
    private string?[]? _previewDebugNames;
    private ulong _parkedSkyLookupBytes;
    private ulong _parkedSkyLookupStamp;
    private ulong _nextSkyLookupStamp;
    private bool _parkedSkyLookupConfirmed;
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

    /// <summary>Native texture backing owned by the session between opted-in sky lookup owners.</summary>
    /// <remarks>Active or retiring LUT resources are tracked by the attached renderer. This
    /// count covers only the parked array, without descriptor or other driver backing.</remarks>
    public ulong ParkedSkyLookupBytes
    {
        get
        {
            lock (_gate)
                return _parkedSkyLookupBytes;
        }
    }
    internal readonly record struct NamedPreviewObject(D3D12_MESSAGE_ID Kind, string Name);

    private enum PreviewRole
    {
        DirectRoot,
        DirectDraw,
        LookupRoot,
        LookupDraw,
        LookupCompute,
        LookupFields,
        LookupViews,
    }

    internal (string? Queue, NamedPreviewObject[] Preview) CaptureRetainedDebugObjects()
    {
        lock (_gate)
        {
            var queue = _queue == null ? null : _queueName;
            if (_skyRoot == null && _skyLookupRoot == null && _parkedSkyLookupFields == null)
                return (queue, []);

            // Only the debug report calls this method. Device creation and
            // normal Preview timing runs do not need to name session objects.
            var preview = new List<NamedPreviewObject>((int)PreviewRole.LookupViews + 1);
            Add(PreviewRole.DirectRoot, D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LIVE_ROOTSIGNATURE, (ID3D12Object*)_skyRoot);
            Add(PreviewRole.DirectDraw, D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LIVE_PIPELINESTATE, (ID3D12Object*)_skyPipeline);
            Add(PreviewRole.LookupRoot, D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LIVE_ROOTSIGNATURE, (ID3D12Object*)_skyLookupRoot);
            Add(PreviewRole.LookupDraw, D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LIVE_PIPELINESTATE, (ID3D12Object*)_skyLookupGraphics);
            Add(PreviewRole.LookupCompute, D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LIVE_PIPELINESTATE, (ID3D12Object*)_skyLookupCompute);
            Add(PreviewRole.LookupFields, D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LIVE_RESOURCE,
                (ID3D12Object*)_parkedSkyLookupFields, _parkedSkyLookupStamp);
            Add(PreviewRole.LookupViews, D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LIVE_DESCRIPTORHEAP,
                (ID3D12Object*)_parkedSkyLookupViews, _parkedSkyLookupStamp);
            return (queue, preview.ToArray());

            void Add(PreviewRole role, D3D12_MESSAGE_ID kind, ID3D12Object* resource, ulong stamp = 0)
            {
                if (resource == null)
                    return;
                if (NamePreviewObject(role, resource, stamp) is { } name)
                    preview.Add(new(kind, name));
            }
        }
    }

    // Called under the same lock as ownership publication. Delimit the entire
    // session/role/stamp identity so a partial debug name cannot match it.
    private string? NamePreviewObject(PreviewRole role, ID3D12Object* resource, ulong stamp)
    {
        _previewDebugNames ??= new string?[(int)PreviewRole.LookupViews + 1];
        if (_previewDebugNames[(int)role] is { } existing)
            return existing;
        if (_previewDebugIdentity == Guid.Empty)
            _previewDebugIdentity = Guid.NewGuid();
        var name = $"|Groundwork retained Preview {_previewDebugIdentity:N}:{role}:{stamp}|";
        fixed (char* value = name)
            return _previewDebugNames[(int)role] = resource->SetName(value).Value >= 0 ? name : null;
    }

    internal ID3D12Device* Attach(out D3D12AdapterInfo? adapter, out ID3D12CommandQueue* queue,
        bool useParkedSkyLookup = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attached)
                throw new InvalidOperationException("The preceding renderer still owns this device session.");
            // A concurrent caller that attaches before the old owner's final
            // cleanup completes must not inherit unconfirmed storage.
            if (!useParkedSkyLookup || !_parkedSkyLookupConfirmed)
                ReleaseParkedSkyLookup();
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
    // immutable triplet separate from the direct sky cache and the optional
    // retired, exclusively owned lookup storage slot below.
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

    // Move the complete bundle only under the existing exclusive attachment.
    // The caller has verified native retirement and the array is back in UAV state.
    internal ulong TryParkSkyLookup(ID3D12Device* device, ID3D12Resource* fields,
        ID3D12DescriptorHeap* views, ulong bytes)
    {
        lock (_gate)
        {
            if (_disposed || !_attached || device == null || device != _device ||
                fields == null || views == null || bytes is 0 or > GpuDevice.SkyLookupStorageLimit ||
                _parkedSkyLookupFields != null || _nextSkyLookupStamp == ulong.MaxValue)
                return 0;
            _parkedSkyLookupFields = fields;
            _parkedSkyLookupViews = views;
            _parkedSkyLookupBytes = bytes;
            return _parkedSkyLookupStamp = ++_nextSkyLookupStamp;
        }
    }

    internal bool TryTakeSkyLookup(ID3D12Device* device, out ID3D12Resource* fields,
        out ID3D12DescriptorHeap* views, out ulong bytes)
    {
        lock (_gate)
        {
            fields = null;
            views = null;
            bytes = 0;
            if (_disposed || !_attached || device != _device || !_parkedSkyLookupConfirmed)
                return false;
            fields = _parkedSkyLookupFields;
            views = _parkedSkyLookupViews;
            bytes = _parkedSkyLookupBytes;
            ClearParkedSkyLookup();
            return true;
        }
    }

    internal void ConfirmParkedSkyLookup(ulong stamp)
    {
        lock (_gate)
        {
            if (!_disposed && !_attached && stamp != 0 && stamp == _parkedSkyLookupStamp)
                _parkedSkyLookupConfirmed = true;
        }
    }

    internal void DiscardParkedSkyLookup(ulong expectedStamp = 0)
    {
        lock (_gate)
        {
            if (expectedStamp == 0 || expectedStamp == _parkedSkyLookupStamp)
                ReleaseParkedSkyLookup();
        }
    }

    private void ReleaseParkedSkyLookup()
    {
        GpuNative.Release(_parkedSkyLookupViews);
        GpuNative.Release(_parkedSkyLookupFields);
        ClearParkedSkyLookup();
    }

    private void ClearParkedSkyLookup()
    {
        if (_previewDebugNames is { } names)
        {
            names[(int)PreviewRole.LookupFields] = null;
            names[(int)PreviewRole.LookupViews] = null;
        }
        _parkedSkyLookupFields = null;
        _parkedSkyLookupViews = null;
        _parkedSkyLookupBytes = 0;
        _parkedSkyLookupStamp = 0;
        _parkedSkyLookupConfirmed = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ReleaseParkedSkyLookup();
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
