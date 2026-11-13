using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Groundwork.Rendering.D3D12.GpuNative;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice : IDisposable
{
    private const int AdapterNotFound = unchecked((int)0x887A0002);
    private readonly Frame[] _frames = [new(), new()];
    private IDXGIFactory4* _factory;
    private ID3D12Device* _device;
    private ID3D12CommandQueue* _queue;
    private ID3D12Fence* _fence;
    private ID3D12RootSignature* _root;
    private ID3D12PipelineState* _pipeline;
    private ID3D12Resource* _cubeGeometry;
    private ID3D12Resource* _depth;
    private ID3D12DescriptorHeap* _depthHeap;
    private int _depthWidth;
    private int _depthHeight;
    private int _nextFrame;
    private readonly D3D12_HEAP_TYPE _meshUploadHeap;
    private GpuRetirement _retirement;
    private readonly GpuRetirementTestHooks? _retirementHooks;
    private D3D12DeviceSession? _session;

    internal GpuDevice(bool warp, bool enableDebugLayer = false, bool enableDred = false, RenderMeshPair? meshes = null,
        GpuRetirementTestHooks? retirementHooks = null, RenderMeshRegion? region = null, D3D12DeviceSession? session = null)
    {
        if (meshes is not null && region is not null)
            throw new ArgumentException("Only one initial mesh set may be supplied.", nameof(region));

        _retirementHooks = retirementHooks;
        try
        {
            fixed (IDXGIFactory4** factory = &_factory)
                Check(CreateFactory(0, __uuidof<IDXGIFactory4>(), (void**)factory), "CreateDXGIFactory2");
            lock (DiagnosticStartup)
            {
                // Reserve an in-flight open before releasing the lock. An existing
                // device for the same adapter can be returned by D3D12CreateDevice.
                if (Volatile.Read(ref _activeDevices) == 0)
                {
                    if (!Volatile.Read(ref _openedDevice))
                        _debugLayerEnabled |= enableDebugLayer && !_debugLayerEnabled && TryEnableDebugLayer();
                    _dredEnabled |= enableDred && !_dredEnabled && TryEnableDred();
                }
                DebugLayerEnabled = enableDebugLayer && _debugLayerEnabled;
                DredEnabled = enableDred && _dredEnabled;
                CountDeviceReference();
            }
            var reused = false;
            try
            {
                if (session is not null)
                {
                    _device = session.Attach(out var adapter, out _queue);
                    _session = session;
                    reused = _device != null;
                    if (adapter is not null)
                        Adapter = adapter;
                }
                if (_device == null)
                    _device = OpenDevice(warp);
            }
            catch
            {
                UncountDeviceReference();
                throw;
            }
            if (reused)
                Check(_device->GetDeviceRemovedReason(), "Reattach session device");
            if (DebugLayerEnabled)
                ConfigureDebugQueue();
            if (_queue == null)
            {
                var queueDescription = new D3D12_COMMAND_QUEUE_DESC();
                fixed (ID3D12CommandQueue** queue = &_queue)
                    Check(_device->CreateCommandQueue(&queueDescription, __uuidof<ID3D12CommandQueue>(), (void**)queue), "CreateCommandQueue");
            }
            fixed (ID3D12Fence** fence = &_fence)
                Check(_device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, __uuidof<ID3D12Fence>(), (void**)fence), "CreateFence");
            var uploadOptions = new D3D12_FEATURE_DATA_D3D12_OPTIONS16();
            var supported = _device->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS16,
                &uploadOptions, (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS16)).Value >= 0 &&
                uploadOptions.GPUUploadHeapSupported;
            _meshUploadHeap = supported ? D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_GPU_UPLOAD : D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD;
            CreatePipeline();
            if (region is not null)
                ActiveMeshGeometry = PrepareMeshGeometry(region);
            else if (meshes is not null)
                ActiveMeshGeometry = PrepareMeshGeometry(meshes);
            else
                CreateCubeGeometry();
            foreach (var frame in _frames)
                frame.Initialize(_device);
            _session?.Retain(_device, _queue, Adapter);
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    internal IDXGIFactory4* Factory => _factory;
    internal ID3D12Device* Device => _device;
    internal ID3D12CommandQueue* Queue => _queue;
    internal int RemovedReason => _device == null ? 0 : _device->GetDeviceRemovedReason().Value;
    internal bool IsRemoved => RemovedReason < 0;
    internal bool IsIdle => _fence != null && _retirement.IsIdle(ReadCompletedValue());

    private ulong ReadCompletedValue()
    {
        var completed = _fence->GetCompletedValue();
        if (_retirementHooks?.CompletedValue is not { } observe)
            return completed;

        // A test may hide completion or inject the sentinel, but it cannot
        // fabricate successful retirement or hide an actual removal sentinel.
        var observed = observe(this, completed);
        return completed == ulong.MaxValue || observed == ulong.MaxValue
            ? ulong.MaxValue : Math.Min(completed, observed);
    }

    internal GpuTarget Attach(ID3D12Resource* backBuffer, int width, int height)
    {
        if (_depthWidth != width || _depthHeight != height)
        {
            if (!IsIdle)
                throw new InvalidOperationException("Cannot replace depth while GPU work is outstanding.");

            Release(_depth);
            _depth = null;
            _depthWidth = 0;
            _depthHeight = 0;
            if (_depthHeap == null)
                _depthHeap = CreateHeap(_device, D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_DSV);
            _depth = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
                Texture(width, height, DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT, D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL),
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE);
            _device->CreateDepthStencilView(_depth, null, _depthHeap->GetCPUDescriptorHandleForHeapStart());
            _depthWidth = width;
            _depthHeight = height;
        }

        return new GpuTarget(_device, backBuffer, width, height);
    }

    internal bool TryRender(GpuTarget target, in RenderFrame frame) => TryRender(target, frame, null, null);

    private bool TryRender(GpuTarget target, in RenderFrame frame, RenderScene? subsequent, Readback? readback)
    {
        var completed = ReadCompletedValue();
        if (completed == ulong.MaxValue)
            throw new GpuNativeException("Read GPU retirement fence after device removal", RemovedReason);
        var context = _frames[_nextFrame];
        if (!_retirement.CanReuse(completed, context.Retirement))
            return false;
        Check(context.Allocator->Reset(), "Reset allocator");
        Check(context.Commands->Reset(context.Allocator, _pipeline), "Reset command list");
        try
        {
            Record(context.Commands, target, frame, subsequent, readback);
            Check(context.Commands->Close(), "Close command list");
        }
        catch
        {
            InvalidateShadowCache();
            throw;
        }
        var commands = (ID3D12CommandList*)context.Commands;
        var next = BeginQueueWork();
        _queue->ExecuteCommandLists(1, &commands);
        Check(SignalQueueWork(next), "Signal queue");
        context.Retirement = next;
        _nextFrame = (_nextFrame + 1) % _frames.Length;
        return true;
    }

    internal ulong BeginQueueWork() => _retirement.BeginSubmission();

    internal int? InjectedPresentFailure() => _retirementHooks?.PresentFailure?.Invoke(this);

    internal HRESULT SignalQueueWork(ulong value)
    {
        if (_retirementHooks?.SignalFailure?.Invoke(this, value) is int failure)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(failure, 0);
            return new HRESULT(failure);
        }

        var result = _queue->Signal(_fence, value);
        if (result.Value >= 0)
            _retirement.CompleteSignal(value);
        return result;
    }

    public void Dispose()
    {
        if (_fence != null && !IsIdle && !IsRemoved)
            throw new InvalidOperationException("GPU resources are still owned by an unretired submission.");
        ReleaseResources();
    }

    private ID3D12Device* OpenDevice(bool warp)
    {
        if (warp)
        {
            IDXGIAdapter1* adapter = null;
            Check(_factory->EnumWarpAdapter(__uuidof<IDXGIAdapter1>(), (void**)&adapter), "EnumWarpAdapter");
            try
            {
                DXGI_ADAPTER_DESC1 description;
                Check(adapter->GetDesc1(&description), "GetDesc1");
                var device = TryOpenDevice(adapter, description);
                return device != null ? device : throw new PlatformNotSupportedException("WARP lacks feature level 11_0 or shader model 6.0.");
            }
            finally
            {
                Release(adapter);
            }
        }

        for (uint index = 0; ; index++)
        {
            IDXGIAdapter1* adapter = null;
            var result = _factory->EnumAdapters1(index, &adapter);
            if (result.Value == AdapterNotFound)
                break;
            Check(result, "EnumAdapters1");
            try
            {
                DXGI_ADAPTER_DESC1 description;
                Check(adapter->GetDesc1(&description), "GetDesc1");
                if ((description.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) == 0)
                {
                    var device = TryOpenDevice(adapter, description);
                    if (device != null)
                        return device;
                }
            }
            finally
            {
                Release(adapter);
            }
        }

        throw new PlatformNotSupportedException("No hardware adapter supports D3D12 feature level 11_0 and shader model 6.0.");
    }

    private ID3D12Device* TryOpenDevice(IDXGIAdapter1* adapter, DXGI_ADAPTER_DESC1 description)
    {
        ID3D12Device* device = null;
        if (CreateDevice((IUnknown*)adapter, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, __uuidof<ID3D12Device>(), (void**)&device) < 0)
            return null;

        Volatile.Write(ref _openedDevice, true);
        var shader = new D3D12_FEATURE_DATA_SHADER_MODEL { HighestShaderModel = D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_0 };
        try
        {
            if (device->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_SHADER_MODEL, &shader,
                (uint)sizeof(D3D12_FEATURE_DATA_SHADER_MODEL)).Value < 0 ||
                shader.HighestShaderModel < D3D_SHADER_MODEL.D3D_SHADER_MODEL_6_0)
                return null;

            Adapter = DescribeAdapter(adapter, description);
            var selected = device;
            device = null;
            return selected;
        }
        finally
        {
            Release(device);
        }
    }

    private void ReleaseResources()
    {
        foreach (var frame in _frames)
            frame.Dispose();
        Release(_depth);
        Release(_depthHeap);
        ActiveMeshGeometry?.Dispose();
        ActiveMeshGeometry = null;
        Release(_cubeGeometry);
        Release(_skyPipeline);
        Release(_skyRoot);
        _skyPipeline = null;
        _skyRoot = null;
        _presentReadback?.Dispose();
        _presentReadback = null;
        ReleaseShadows();
        Release(_normalPipeline);
        _normalPipeline = null;
        Release(_pipeline);
        Release(_root);
        Release(_fence);
        Release(_queue);
        _depth = _cubeGeometry = null;
        _depthHeap = null;
        _pipeline = null;
        _root = null;
        _fence = null;
        _queue = null;
        try
        {
            FinishDebugReport();
        }
        finally
        {
            if (_device != null)
            {
                Release(_device);
                UncountDeviceReference();
            }
            Release(_factory);
            _device = null;
            _factory = null;
            _session?.Detach();
            _session = null;
        }
    }

    private sealed class Frame : IDisposable
    {
        internal ID3D12CommandAllocator* Allocator;
        internal ID3D12GraphicsCommandList* Commands;
        internal ulong Retirement;

        internal void Initialize(ID3D12Device* device)
        {
            fixed (ID3D12CommandAllocator** allocator = &Allocator)
                Check(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
                    __uuidof<ID3D12CommandAllocator>(), (void**)allocator), "CreateCommandAllocator");
            fixed (ID3D12GraphicsCommandList** commands = &Commands)
                Check(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, Allocator,
                    null, __uuidof<ID3D12GraphicsCommandList>(), (void**)commands), "CreateCommandList");
            Check(Commands->Close(), "Close initial command list");
        }

        public void Dispose()
        {
            Release(Commands);
            Release(Allocator);
            Commands = null;
            Allocator = null;
        }
    }
}

internal sealed unsafe class GpuTarget : IDisposable
{
    private ID3D12DescriptorHeap* _heap;
    private ID3D12Resource* _buffer;
    internal int Width { get; }
    internal int Height { get; }
    internal ID3D12Resource* Buffer => _buffer;
    internal D3D12_CPU_DESCRIPTOR_HANDLE Rtv => _heap->GetCPUDescriptorHandleForHeapStart();

    internal GpuTarget(ID3D12Device* device, ID3D12Resource* buffer, int width, int height)
    {
        _heap = CreateHeap(device, D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        device->CreateRenderTargetView(buffer, null, Rtv);
        _buffer = buffer;
        Width = width;
        Height = height;
    }

    public void Dispose()
    {
        Release(_heap);
        Release(_buffer);
        _heap = null;
        _buffer = null;
    }
}
