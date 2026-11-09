using System.Diagnostics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

unsafe sealed partial class Gpu : IDisposable
{
    private IDXGIAdapter3* _adapter;
    private ID3D12Device* _device;
    private ID3D12CommandQueue* _queue;
    private ID3D12CommandQueue* _streamQueue;
    private ID3D12Fence* _streamFence;
    private ulong _streamNext;
    private ulong _gateValue;
    private readonly List<(nint Allocator, nint Commands)> _streamCommands = [];
    private ID3D12CommandAllocator* _allocator;
    private ID3D12GraphicsCommandList* _commands;
    private ID3D12Fence* _fence;
    private ID3D12Fence* _gate;
    private ID3D12Resource* _readback;
    private HANDLE _event;
    private ulong _next;
    private bool _gatePending;

    internal string Adapter { get; private set; } = "";
    internal uint VendorId { get; private set; }
    internal ulong ReadbackBytes => AllocationSize(65536);

    internal Gpu(uint requestedVendor)
    {
        try
        {
            IDXGIFactory4* factory = null;
            Check(CreateFactory(0, __uuidof<IDXGIFactory4>(), (void**)&factory), "factory");
            try
            {
                for (uint index = 0; ; index++)
                {
                    IDXGIAdapter1* candidate = null;
                    var result = factory->EnumAdapters1(index, &candidate);
                    if (result.Value == unchecked((int)0x887A0002))
                        break;
                    Check(result, "enumerate adapter");
                    try
                    {
                        DXGI_ADAPTER_DESC1 description = default;
                        Check(candidate->GetDesc1(&description), "adapter description");
                        if (description.VendorId != requestedVendor || (description.Flags & 2) != 0)
                            continue;
                        ID3D12Device* device = null;
                        Check(CreateDevice((IUnknown*)candidate, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                            __uuidof<ID3D12Device>(), (void**)&device), "device");
                        _device = device;
                        IDXGIAdapter3* adapter = null;
                        Check(candidate->QueryInterface(__uuidof<IDXGIAdapter3>(), (void**)&adapter), "adapter3");
                        _adapter = adapter;
                        Adapter = new string(description.Description).TrimEnd((char)0);
                        VendorId = description.VendorId;
                        break;
                    }
                    finally { Release(candidate); }
                }
            }
            finally { Release(factory); }
            if (_device == null)
                throw new InvalidOperationException($"Hardware adapter vendor {requestedVendor:X} not found.");

            var queueDesc = new D3D12_COMMAND_QUEUE_DESC { Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT };
            ID3D12CommandQueue* queue = null;
            Check(_device->CreateCommandQueue(&queueDesc, __uuidof<ID3D12CommandQueue>(), (void**)&queue), "queue");
            _queue = queue;
            queue = null;
            Check(_device->CreateCommandQueue(&queueDesc, __uuidof<ID3D12CommandQueue>(), (void**)&queue), "stream queue");
            _streamQueue = queue;
            ID3D12CommandAllocator* allocator = null;
            Check(_device->CreateCommandAllocator(queueDesc.Type, __uuidof<ID3D12CommandAllocator>(), (void**)&allocator), "allocator");
            _allocator = allocator;
            ID3D12GraphicsCommandList* commands = null;
            Check(_device->CreateCommandList(0, queueDesc.Type, _allocator, null,
                __uuidof<ID3D12GraphicsCommandList>(), (void**)&commands), "command list");
            _commands = commands;
            Check(_commands->Close(), "close");
            _fence = MakeFence();
            _gate = MakeFence();
            _streamFence = MakeFence();
            _event = CreateEventW(null, false, false, null);
            if (_event == HANDLE.NULL)
                throw new InvalidOperationException("CreateEventW failed.");
            _readback = Buffer(65536, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);
        }
        catch { Dispose(); throw; }
    }

    internal ulong AllocationSize(ulong bytes)
    {
        var description = Description(bytes);
        return _device->GetResourceAllocationInfo(0, 1, &description).SizeInBytes;
    }

    internal Buffers Allocate(ulong bytes, uint marker, ReadOnlySpan<byte> payload = default)
    {
        var upload = Buffer(bytes, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
        ID3D12Resource* geometry = null;
        try
        {
            geometry = Buffer(bytes, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);
            void* mapped = null;
            D3D12_RANGE empty = default;
            Check(upload->Map(0, &empty, &mapped), "map upload");
            new Span<uint>(mapped, checked((int)bytes / sizeof(uint))).Fill(marker);
            payload.CopyTo(new Span<byte>((byte*)mapped + 4, checked((int)bytes - 8)));
            upload->Unmap(0, null);
            return new Buffers(geometry, upload, bytes, marker,
                payload.IsEmpty ? marker : MemoryMarshal.Read<uint>(payload),
                payload.IsEmpty ? marker : MemoryMarshal.Read<uint>(payload[^4..]));
        }
        catch { Release(geometry); Release(upload); throw; }
    }

    internal void CopyAndVerify(Buffers buffers)
    {
        Check(_allocator->Reset(), "reset allocator");
        Check(_commands->Reset(_allocator, null), "reset command list");
        RecordCopy(_commands, buffers);
        _commands->CopyBufferRegion(_readback, 0, buffers.Geometry, 0, 4);
        _commands->CopyBufferRegion(_readback, 4, buffers.Geometry, buffers.Bytes - 4, 4);
        _commands->CopyBufferRegion(_readback, 8, buffers.Geometry, 4, 4);
        _commands->CopyBufferRegion(_readback, 12, buffers.Geometry, buffers.Bytes - 8, 4);
        Check(_commands->Close(), "close copy");
        ID3D12CommandList* command = (ID3D12CommandList*)_commands;
        _queue->ExecuteCommandLists(1, &command);
        Wait(Signal());
        void* mapped = null;
        var range = new D3D12_RANGE { End = 16 };
        Check(_readback->Map(0, &range, &mapped), "map readback");
        var words = (uint*)mapped;
        var valid = words[0] == buffers.Marker && words[1] == buffers.Marker &&
            words[2] == buffers.FirstWord && words[3] == buffers.LastWord;
        D3D12_RANGE empty = default;
        _readback->Unmap(0, &empty);
        if (!valid)
            throw new InvalidOperationException("GPU payload marker mismatch.");
        buffers.ConfirmCopy();
    }

    internal void EnqueueSpeculative(Buffers buffers)
    {
        if (!_gatePending)
        {
            Check(_streamQueue->Wait(_gate, ++_gateValue), "hold speculative streaming");
            _gatePending = true;
        }

        ID3D12CommandAllocator* allocator = null;
        ID3D12GraphicsCommandList* commands = null;
        var type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT;
        Check(_device->CreateCommandAllocator(type, __uuidof<ID3D12CommandAllocator>(), (void**)&allocator), "stream allocator");
        try
        {
            Check(_device->CreateCommandList(0, type, allocator, null,
                __uuidof<ID3D12GraphicsCommandList>(), (void**)&commands), "stream list");
            RecordCopy(commands, buffers);
            Check(commands->Close(), "close stream list");
            _streamCommands.Add(((nint)allocator, (nint)commands));
            ID3D12CommandList* command = (ID3D12CommandList*)commands;
            allocator = null;
            commands = null;
            _streamQueue->ExecuteCommandLists(1, &command);
            Check(_streamQueue->Signal(_streamFence, ++_streamNext), "stream signal");
        }
        finally { Release(commands); Release(allocator); }
    }

    internal bool StreamingComplete
    {
        get
        {
            var value = _streamFence->GetCompletedValue();
            if (value == ulong.MaxValue)
                throw new InvalidOperationException("Streaming GPU device removed.");
            return value >= _streamNext;
        }
    }

    private static void RecordCopy(ID3D12GraphicsCommandList* commands, Buffers buffers)
    {
        commands->CopyBufferRegion(buffers.Geometry, 0, buffers.Upload, 0, buffers.Bytes);
        TransitionCopy(commands, buffers, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
    }

    private static void TransitionCopy(ID3D12GraphicsCommandList* commands, Buffers buffers,
        D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        var barrier = new D3D12_RESOURCE_BARRIER { Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION };
        barrier.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER
        {
            pResource = buffers.Geometry,
            Subresource = uint.MaxValue,
            StateBefore = before,
            StateAfter = after
        };
        commands->ResourceBarrier(1, &barrier);
    }

    internal ulong BlockRetirement(Buffers old, Buffers cancelled)
    {
        Check(_queue->Wait(_streamFence, _streamNext), "retire after streaming copies");
        _gatePending = true;
        Check(_allocator->Reset(), "reset retirement allocator");
        Check(_commands->Reset(_allocator, null), "reset retirement list");
        _commands->CopyBufferRegion(_readback, 0, old.Geometry, 0, 4);
        _commands->CopyBufferRegion(_readback, 4, cancelled.Geometry, 0, 4);
        Check(_commands->Close(), "close retirement list");
        ID3D12CommandList* command = (ID3D12CommandList*)_commands;
        _queue->ExecuteCommandLists(1, &command);
        return Signal();
    }

    internal void VerifyRetiredMarkers(uint oldMarker, uint cancelledMarker)
    {
        void* mapped = null;
        var range = new D3D12_RANGE { End = 8 };
        Check(_readback->Map(0, &range, &mapped), "retirement readback");
        var valid = ((uint*)mapped)[0] == oldMarker && ((uint*)mapped)[1] == cancelledMarker;
        D3D12_RANGE empty = default;
        _readback->Unmap(0, &empty);
        if (!valid)
            throw new InvalidOperationException("Retirement marker mismatch.");
    }

    internal bool IsComplete(ulong fence)
    {
        var value = _fence->GetCompletedValue();
        if (value == ulong.MaxValue)
            throw new InvalidOperationException("GPU device removed.");
        return value >= fence;
    }

    internal void OpenRetirement()
    {
        Check(_gate->Signal(_gateValue), "open retirement gate");
        _gatePending = false;
    }

    internal void Wait(ulong fence) => Wait(fence, false);

    private FenceWaitClock Wait(ulong fence, bool capture)
    {
        if (IsComplete(fence))
            return default;
        var registrationStart = capture ? Stopwatch.GetTimestamp() : 0;
        Check(_fence->SetEventOnCompletion(fence, _event), "wait registration");
        var waitStart = capture ? Stopwatch.GetTimestamp() : 0;
        var result = WaitForSingleObject(_event, 10_000);
        var waitReturn = capture ? Stopwatch.GetTimestamp() : 0;
        if (result != 0 || !IsComplete(fence))
            throw new TimeoutException("GPU fence did not settle.");
        return new(registrationStart, waitStart, waitReturn);
    }

    internal readonly record struct FenceWaitClock(long RegistrationStart, long WaitStart, long WaitReturn);

    internal void Drain()
    {
        if (_gatePending)
            OpenRetirement();
        if (_streamNext > 0)
            Check(_queue->Wait(_streamFence, _streamNext), "drain streaming queue");
        Wait(Signal());
        ReleaseStreamCommands();
    }

    private void ReleaseStreamCommands()
    {
        foreach (var (allocator, commands) in _streamCommands)
        {
            Release((ID3D12GraphicsCommandList*)commands);
            Release((ID3D12CommandAllocator*)allocator);
        }
        _streamCommands.Clear();
    }

    internal (ulong Local, ulong NonLocal, ulong LocalBudget, ulong NonLocalBudget) Usage()
    {
        DXGI_QUERY_VIDEO_MEMORY_INFO local = default, nonLocal = default;
        Check(_adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local), "local memory");
        Check(_adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &nonLocal), "nonlocal memory");
        return (local.CurrentUsage, nonLocal.CurrentUsage, local.Budget, nonLocal.Budget);
    }

    private ulong Signal()
    {
        Check(_queue->Signal(_fence, ++_next), "signal");
        return _next;
    }

    private ID3D12Fence* MakeFence()
    {
        ID3D12Fence* fence = null;
        Check(_device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE,
            __uuidof<ID3D12Fence>(), (void**)&fence), "fence");
        return fence;
    }

    private ID3D12Resource* Buffer(ulong bytes, D3D12_HEAP_TYPE type, D3D12_RESOURCE_STATES state)
    {
        var heap = new D3D12_HEAP_PROPERTIES { Type = type, CreationNodeMask = 1, VisibleNodeMask = 1 };
        var description = Description(bytes);
        ID3D12Resource* buffer = null;
        Check(_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, &description,
            state, null, __uuidof<ID3D12Resource>(), (void**)&buffer), "allocate buffer");
        return buffer;
    }

    private static D3D12_RESOURCE_DESC Description(ulong bytes) => new()
    {
        Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
        Width = bytes, Height = 1, DepthOrArraySize = 1, MipLevels = 1,
        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR
    };

    public void Dispose()
    {
        if (_gatePending)
            _gate->Signal(_gateValue);
        if (_event != HANDLE.NULL)
            CloseHandle(_event);
        Release(_readback);
        Release(_commands);
        Release(_allocator);
        foreach (var (allocator, commands) in _streamCommands)
        {
            Release((ID3D12GraphicsCommandList*)commands);
            Release((ID3D12CommandAllocator*)allocator);
        }
        Release(_streamFence);
        Release(_streamQueue);
        Release(_gate);
        Release(_fence);
        Release(_queue);
        Release(_device);
        Release(_adapter);
    }

    internal sealed class Buffers(ID3D12Resource* geometry, ID3D12Resource* upload, ulong bytes, uint marker, uint firstWord, uint lastWord,
        Action? copied = null, Action? returnLease = null) : IDisposable
    {
        internal ID3D12Resource* Geometry { get; } = geometry;
        internal ID3D12Resource* Upload { get; private set; } = upload;
        internal ulong Bytes { get; } = bytes;
        internal uint Marker { get; } = marker;
        internal uint FirstWord { get; } = firstWord;
        internal uint LastWord { get; } = lastWord;
        internal bool Pooled => returnLease is not null;
        internal void ConfirmCopy() => copied?.Invoke();

        internal void ReleaseUpload()
        {
            if (returnLease is null)
                Release(Upload);
            Upload = null;
        }

        public void Dispose()
        {
            ReleaseUpload();
            if (returnLease is null)
                Release(Geometry);
            else
                returnLease();
        }
    }

    private static void Check(HRESULT result, string action)
    {
        if (result.Value < 0)
            throw new InvalidOperationException($"{action} failed with 0x{result.Value:X8}.");
    }

    private static void Release<T>(T* pointer) where T : unmanaged
    {
        if (pointer != null) ((IUnknown*)pointer)->Release();
    }

    [LibraryImport("dxgi.dll", EntryPoint = "CreateDXGIFactory2")]
    private static partial int CreateFactory(uint flags, Guid* iid, void** result);

    [LibraryImport("d3d12.dll", EntryPoint = "D3D12CreateDevice")]
    private static partial int CreateDevice(IUnknown* adapter, D3D_FEATURE_LEVEL level, Guid* iid, void** result);
}
