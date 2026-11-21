using Groundwork.Rendering.Passes;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Groundwork.Rendering.D3D12.GpuNative;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal enum GpuFrameSubmitResult { Busy, Rejected, Submitted }

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
    private ulong _depthGeneration;
    private readonly (GpuPassShape Shape, ValidatedRenderPlan? Plan)[] _passPlans = new (GpuPassShape, ValidatedRenderPlan?)[8];
    private int _nextPassPlan;
    private int _nextFrame;
    private readonly D3D12_HEAP_TYPE _meshUploadHeap;
    private GpuRetirement _retirement;
    private readonly GpuRetirementTestHooks? _retirementHooks;
    private readonly ResourceDiagnostics? _resourceDiagnostics;
    private volatile bool _observedFenceSentinel;
    private D3D12DeviceSession? _session;

    internal GpuDevice(bool warp, bool enableDebugLayer = false, bool enableDred = false, RenderMeshPair? meshes = null,
        GpuRetirementTestHooks? retirementHooks = null, RenderMeshRegion? region = null, D3D12DeviceSession? session = null,
        ResourceDiagnostics? resourceDiagnostics = null, bool? skyLookupPrototype = null)
    {
        if (meshes is not null && region is not null)
            throw new ArgumentException("Only one initial mesh set may be supplied.", nameof(region));

        _retirementHooks = retirementHooks;
        _resourceDiagnostics = resourceDiagnostics;
        SkyLookupPrototypeEnabledForTests = skyLookupPrototype ??
            Environment.GetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_PROTOTYPE") == "1";
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
            _retirementHooks?.AfterDeviceInitialize?.Invoke(this);
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
        if (_retirementHooks?.CompletedValue is { } observe)
        {
            // A test may hide completion or inject the sentinel, but it cannot
            // fabricate successful retirement or hide an actual removal sentinel.
            var observed = observe(this, completed);
            completed = completed == ulong.MaxValue || observed == ulong.MaxValue
                ? ulong.MaxValue : Math.Min(completed, observed);
        }
        if (completed == ulong.MaxValue)
            _observedFenceSentinel = true;
        _resourceDiagnostics?.Complete(completed);
        DrainRetiredSkyTimingForTests(completed);
        return completed;
    }

    internal GpuTarget Attach(ID3D12Resource* backBuffer, int width, int height)
    {
        RequireColorTarget(backBuffer, width, height);
        if (_depthWidth != width || _depthHeight != height)
        {
            if (!IsIdle)
                throw new InvalidOperationException("Cannot replace depth while GPU work is outstanding.");

            ReleaseObservedResource(_depth);
            _depth = null;
            _depthWidth = 0;
            _depthHeight = 0;
            if (_depthHeap == null)
                _depthHeap = CreateDepthAttachmentHeap();
            var clear = new D3D12_CLEAR_VALUE { Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT };
            clear.DepthStencil.Depth = 1;
            _depth = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
                Texture(width, height, DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT, D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL),
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE, &clear);
            ObserveResource(_depth, D3D12ResourceOwner.Depth);
            CreateDepthAttachmentViews();
            _depthWidth = width;
            _depthHeight = height;
            _depthGeneration = checked(_depthGeneration + 1);
        }

        return new GpuTarget(this, backBuffer, width, height, _depthGeneration);
    }

    private void RequireColorTarget(ID3D12Resource* target, int width, int height)
    {
        if (target == null || width is < 1 or > RenderPlanLimits.TextureDimension ||
            height is < 1 or > RenderPlanLimits.TextureDimension)
            throw new ArgumentException("A bounded native color target is required.", nameof(target));
        var description = target->GetDesc();
        if (description.Dimension != D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D ||
            description.Width != (ulong)width || description.Height != (uint)height ||
            description.Format != DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM || description.MipLevels != 1 ||
            description.DepthOrArraySize != 1 || description.SampleDesc.Count != 1 ||
            (description.Flags & D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET) == 0)
            throw new ArgumentException("The native target does not match the fixed color attachment.", nameof(target));

        ID3D12Device* device = null;
        try
        {
            Check(target->GetDevice(__uuidof<ID3D12Device>(), (void**)&device), "Resolve color target device");
            if (device != _device)
                throw new ArgumentException("The color target belongs to another device.", nameof(target));
        }
        finally
        {
            Release(device);
        }
    }

    internal GpuFrameSubmitResult TryRender(GpuTarget target, in RenderFrame frame, D3D12Renderer owner) =>
        TryRender(target, frame, null, null, owner);

    private GpuFrameSubmitResult TryRender(GpuTarget target, in RenderFrame frame, RenderScene? subsequent,
        Readback? readback, D3D12Renderer? owner)
    {
        var completed = ReadCompletedValue();
        if (completed == ulong.MaxValue)
            throw new GpuNativeException("Read GPU retirement fence after device removal", RemovedReason);
        var context = _frames[_nextFrame];
        if (!_retirement.CanReuse(completed, context.Retirement)
            || _terrainTiming is not null && !_retirement.IsIdle(completed))
            return GpuFrameSubmitResult.Busy;

        if (_device == null || _queue == null || _depth == null || !target.IsCurrent(this, _depthGeneration)
            || target.Width != _depthWidth || target.Height != _depthHeight
            || readback is not null && !readback.Matches(this, target)
            || frame.Scene.HasValue && (ActiveMeshGeometry is { } mesh ? !mesh.HasLiveBinding(this) : _cubeGeometry == null)
            || _retirementHooks?.RejectPreparedPlan?.Invoke(frame.Sequence) == true)
            return GpuFrameSubmitResult.Rejected;

        var shadows = frame.Scene is { } scene ? PrepareShadows(target, scene) : default;
        var terrainAppearance = PrepareFrameTerrainMaterials(frame.Scene is { World.IsIdentity: true }, shadows.Constants.Enabled);
        var shape = new GpuPassShape(target.Width, target.Height,
            frame.Scene.HasValue ? ActiveMeshGeometry?.PackedBytes ?? checked((int)(VertexCount * VertexStride)) : 0,
            frame.Scene.HasValue, frame.Sky.HasValue,
            frame.Scene is { PropOrigin: not null } && PropTerrain.HasValue && PropStream is not null,
            frame.Scene.HasValue && subsequent.HasValue, shadows.NearKind, shadows.FarKind,
            readback is not null, owner is not null, frame.Scene.HasValue && !terrainAppearance ? FrameTextureImport() : default,
            terrainAppearance, terrainAppearance ? _frameTerrainMaterials!.Imports : default);
        if (GetPassPlan(shape) is not { } plan)
        {
            if (!terrainAppearance)
                return GpuFrameSubmitResult.Rejected;
            shape = shape with { MaterialConstants = false, MaterialMaps = default };
            if (GetPassPlan(shape) is not { } fallback)
                return GpuFrameSubmitResult.Rejected;
            terrainAppearance = false;
            FrameTerrainMaterialAddress = 0;
            plan = fallback;
        }

        var terrainPbr = !terrainAppearance && TryPrepareTerrainPbr(frame.Scene, shadows.Constants.Enabled);
        var submitted = false;
        D3D12PropCells? deferredProps = null;
        try
        {
            const int maxRecordings = 3;
            for (var attempt = 0; attempt < maxRecordings; attempt++)
            {
                Check(context.Allocator->Reset(), "Reset allocator");
                Check(context.Commands->Reset(context.Allocator, _pipeline), "Reset command list");
                Record(context.Commands, target, frame, subsequent, readback, shadows, plan, terrainPbr,
                    terrainAppearance, out var timingLayout);
                Check(context.Commands->Close(), "Close command list");
                if (attempt == 0 && _retirementHooks?.RejectRecordedPlan?.Invoke(frame.Sequence) == true)
                    return GpuFrameSubmitResult.Rejected;

                _retirementHooks?.BeforeValidateRecordedBindings?.Invoke(frame.Sequence, attempt);

                // Hold the recorded prop cut through Execute and Signal. Check the
                // volume authority immediately before the native submission.
                var propGate = _propFrameStream?.Gate;
                var textureGate = owner?.RecordedTextureGate;
                propGate?.Enter();
                textureGate?.Enter();
                bool textureIsCurrent;
                try
                {
                    textureIsCurrent = owner is null || owner.RecordedTextureIsCurrent();
                    if (textureIsCurrent && RecordedPropsAreCurrent())
                    {
                        if (owner is not null && !owner.AuthorizeRecordedFrame(frame))
                            return GpuFrameSubmitResult.Rejected;
                        textureIsCurrent = owner is null || owner.RecordedTextureIsCurrent();
                        if (textureIsCurrent && RecordedPropsAreCurrent())
                        {
                            // The texture view is sealed for this submission. Only
                            // this owner can release its exact native binding.
                            textureGate?.Exit();
                            textureGate = null;
                            var commands = (ID3D12CommandList*)context.Commands;
                            var next = BeginQueueWork();
                            _resourceDiagnostics?.BeginDraw(frame.Sequence, next);
                            _retirementHooks?.BeforeExecute?.Invoke(frame.Sequence);
                            _queue->ExecuteCommandLists(1, &commands);
                            Check(SignalQueueWork(next), "Signal queue");
                            _terrainTiming?.Publish(frame.Sequence, frame.Scene.HasValue, timingLayout);
                            _resourceDiagnostics?.Signal(next, draw: true);
                            context.Retirement = next;
                            _nextFrame = (_nextFrame + 1) % _frames.Length;
                            PublishRecordedProps(deferredProps);
                            PublishShadows(shadows);
                            submitted = true;
                            PublishSkyQualificationForTests(frame.Sequence, next);
                            return GpuFrameSubmitResult.Submitted;
                        }
                    }
                }
                finally
                {
                    textureGate?.Exit();
                    propGate?.Exit();
                }

                // Drop the stale prop cut and keep current material bindings for the retry.
                // A stale texture cut falls back to numeric terrain materials for protected content.
                deferredProps ??= _propFrameStream;
                AbortRecordedProps();
                if (!textureIsCurrent)
                {
                    owner!.ClearFrameTexture(this);
                    terrainAppearance = false;
                    shape = shape with { Texture = default, MaterialConstants = false, MaterialMaps = default };
                }
                if (attempt == maxRecordings - 1 || GetPassPlan(shape with { Props = false }) is not { } terrainPlan)
                    return GpuFrameSubmitResult.Rejected;
                plan = terrainPlan;
                terrainPbr = !terrainAppearance && TryPrepareTerrainPbr(frame.Scene, shadows.Constants.Enabled);
            }
            return GpuFrameSubmitResult.Rejected;
        }
        finally
        {
            AbortRecordedProps();
            if (!submitted)
                InvalidateShadowCache();
        }
    }

    private ValidatedRenderPlan? GetPassPlan(in GpuPassShape shape)
    {
        foreach (var entry in _passPlans.AsSpan())
            if (entry.Plan is not null && entry.Shape == shape)
                return entry.Plan;

        if (!GpuPassPlan.TryCreate(shape, out var plan))
            return null;

        // Descriptions have only symbolic slots. The actual bindings are checked separately
        // on every use, including after resize or a new target reusing the same extent.
        _passPlans[_nextPassPlan] = (shape, plan);
        _nextPassPlan = (_nextPassPlan + 1) % _passPlans.Length;
        return plan;
    }

    internal ulong BeginQueueWork() => _retirement.BeginSubmission();

    internal int? InjectedPresentFailure() => _retirementHooks?.PresentFailure?.Invoke(this);

    internal void ObserveNativePresentForTests(ulong fence, int result) =>
        _retirementHooks?.AfterNativePresent?.Invoke(fence, result);

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

    internal void WakeOnQueueRetirement(WaitHandle wake)
    {
        // The renderer holds the wake event until native work and its fence settle.
        // Keep the existing bounded retirement poll if registration fails.
        _ = _fence->SetEventOnCompletion(_retirement.Submitted, (HANDLE)wake.SafeWaitHandle.DangerousGetHandle());
    }

    public void Dispose()
    {
        if (_fence != null && !IsIdle && !IsRemoved)
            throw new InvalidOperationException("GPU resources are still owned by an unretired submission.");
        if (_resourceDiagnostics is not null && IsRemoved)
            _resourceDiagnostics.NoteCustody(D3D12CustodyStatus.ConfirmedRemoval);
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
        ReleaseObservedResource(_depth);
        Release(_depthHeap);
        ActiveMeshGeometry?.Dispose();
        ActiveMeshGeometry = null;
        ReleaseObservedResource(_cubeGeometry);
        ReleaseSkyLookup();
        Release(_skyPipeline);
        Release(_skyRoot);
        _skyPipeline = null;
        _skyRoot = null;
        _presentReadback?.Dispose();
        _presentReadback = null;
        ReleaseProps();
        ReleaseTerrainMaterialConstants();
        ReleaseTextures();
        ReleaseShadows();
        Release(_normalPipeline);
        _normalPipeline = null;
        ReleasePbrPipeline();
        ReleaseTerrainAppearancePipelines();
        _terrainTiming?.Dispose();
        _terrainTiming = null;
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
    private readonly GpuDevice _owner;
    private readonly ulong _depthGeneration;
    private ID3D12DescriptorHeap* _heap;
    private ID3D12Resource* _buffer;
    internal int Width { get; }
    internal int Height { get; }
    internal ID3D12Resource* Buffer => _buffer;
    internal D3D12_CPU_DESCRIPTOR_HANDLE Rtv => _heap->GetCPUDescriptorHandleForHeapStart();

    internal GpuTarget(GpuDevice owner, ID3D12Resource* buffer, int width, int height, ulong depthGeneration)
    {
        _owner = owner;
        _depthGeneration = depthGeneration;
        _heap = CreateHeap(owner.Device, D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        owner.Device->CreateRenderTargetView(buffer, null, Rtv);
        _buffer = buffer;
        owner.ObserveResource(buffer, D3D12ResourceOwner.Presentation);
        Width = width;
        Height = height;
    }

    internal bool IsCurrent(GpuDevice owner, ulong depthGeneration) =>
        _buffer != null && _heap != null && ReferenceEquals(owner, _owner) && depthGeneration == _depthGeneration;

    public void Dispose()
    {
        Release(_heap);
        _owner.ReleaseObservedResource(_buffer);
        _heap = null;
        _buffer = null;
    }
}
