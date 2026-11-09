using System.Diagnostics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_STATES;

unsafe sealed partial class Gpu
{
    internal sealed partial class Surface : IDisposable
    {
        private const uint Width = 640, Height = 480;
        private const DXGI_FORMAT Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        private readonly Gpu _gpu;
        private HWND _window;
        private HANDLE _frameReady;
        private const uint DoNotWait = 8;
        private const int StillDrawing = unchecked((int)0x887A000A);
        private IDXGISwapChain3* _chain;
        private ID3D12DescriptorHeap* _rtv;
        private ID3D12RootSignature* _root;
        private ID3D12QueryHeap* _timestamps;
        private readonly ulong _timestampFrequency;
        private ID3D12PipelineState* _pipeline;
        private readonly ID3D12Resource*[] _targets = new ID3D12Resource*[2];
        internal ulong Bytes { get; private set; }

        internal Surface(Gpu gpu, bool paced = false)
        {
            _gpu = gpu;
            IDXGIFactory4* factory = null;
            IDXGISwapChain1* initial = null;
            try
            {
                fixed (char* type = "STATIC", title = "Groundwork C00 capacity probe")
                    _window = CreateWindowExW(0, type, title, 0x10CF0000, 40, 40, (int)Width, (int)Height,
                        HWND.NULL, HMENU.NULL, HINSTANCE.NULL, null);
                if (_window == HWND.NULL)
                    throw new InvalidOperationException("CreateWindowExW failed.");
                Check(CreateFactory(0, __uuidof<IDXGIFactory4>(), (void**)&factory), "swapchain factory");
                var desc = new DXGI_SWAP_CHAIN_DESC1
                {
                    Width = Width, Height = Height, Format = Format, BufferCount = 2, BufferUsage = 0x20,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                    Flags = paced ? (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT : 0
                };
                Check(factory->CreateSwapChainForHwnd((IUnknown*)gpu._queue, _window, &desc, null, null, &initial), "create swapchain");
                fixed (IDXGISwapChain3** chain = &_chain)
                    Check(initial->QueryInterface(__uuidof<IDXGISwapChain3>(), (void**)chain), "swapchain3");
                if (paced)
                {
                    Check(_chain->SetMaximumFrameLatency(2), "set swapchain frame latency");
                    _frameReady = _chain->GetFrameLatencyWaitableObject();
                    if (_frameReady == HANDLE.NULL)
                        throw new InvalidOperationException("Swapchain did not provide a frame-latency handle.");
                }
                var heap = new D3D12_DESCRIPTOR_HEAP_DESC { Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV, NumDescriptors = 2 };
                fixed (ID3D12DescriptorHeap** rtv = &_rtv)
                    Check(gpu._device->CreateDescriptorHeap(&heap, __uuidof<ID3D12DescriptorHeap>(), (void**)rtv), "RTV heap");
                for (uint i = 0; i < 2; i++)
                {
                    fixed (ID3D12Resource** target = &_targets[i])
                        Check(_chain->GetBuffer(i, __uuidof<ID3D12Resource>(), (void**)target), "swapchain buffer");
                    var resource = _targets[i]->GetDesc();
                    Bytes += gpu._device->GetResourceAllocationInfo(0, 1, &resource).SizeInBytes;
                    gpu._device->CreateRenderTargetView(_targets[i], null, Handle(i));
                }
                var query = new D3D12_QUERY_HEAP_DESC { Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_TIMESTAMP, Count = 2 };
                fixed (ID3D12QueryHeap** timestamps = &_timestamps)
                    Check(gpu._device->CreateQueryHeap(&query, __uuidof<ID3D12QueryHeap>(), (void**)timestamps), "timestamp queries");
                ulong frequency;
                Check(gpu._queue->GetTimestampFrequency(&frequency), "timestamp frequency");
                _timestampFrequency = frequency;
                CreatePipeline();
            }
            catch { Dispose(); throw; }
            finally { Release(initial); Release(factory); }
        }

        private D3D12_CPU_DESCRIPTOR_HANDLE Handle(uint index)
        {
            var handle = _rtv->GetCPUDescriptorHandleForHeapStart();
            handle.ptr += index * _gpu._device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
            return handle;
        }

        internal (double Present, double Frame, double GpuDraw, double ReadyWait, int BusyRetries) Draw(Buffers buffers, uint vertices)
        {
            MSG message;
            while (PeekMessageW(&message, _window, 0, 0, 1))
                DispatchMessageW(&message);
            var start = Stopwatch.GetTimestamp();
            var paced = _frameReady != HANDLE.NULL;
            if (paced && WaitForSingleObject(_frameReady, 5_000) != 0)
                throw new TimeoutException("Swapchain did not become ready.");
            var readyWait = paced ? Stopwatch.GetElapsedTime(start).TotalMilliseconds : 0;
            var commands = _gpu._commands;
            Check(_gpu._allocator->Reset(), "reset draw allocator");
            Check(commands->Reset(_gpu._allocator, _pipeline), "reset draw list");
            const D3D12_QUERY_TYPE timestamp = D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP;
            commands->EndQuery(_timestamps, timestamp, 0);
            var index = _chain->GetCurrentBackBufferIndex();
            Transition(commands, _targets[index], D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);
            Transition(commands, buffers.Geometry, buffers.Pooled ? D3D12_RESOURCE_STATE_COMMON : D3D12_RESOURCE_STATE_COPY_SOURCE,
                D3D12_RESOURCE_STATE_VERTEX_AND_CONSTANT_BUFFER);
            var handle = Handle(index);
            var viewport = new D3D12_VIEWPORT { Width = Width, Height = Height, MaxDepth = 1 };
            var scissor = new RECT { right = (int)Width, bottom = (int)Height };
            var view = new D3D12_VERTEX_BUFFER_VIEW
            {
                BufferLocation = buffers.Geometry->GetGPUVirtualAddress() + 4,
                SizeInBytes = vertices * 12, StrideInBytes = 12
            };
            float* clear = stackalloc float[] { .02f, .04f, .06f, 1 };
            commands->SetGraphicsRootSignature(_root);
            commands->RSSetViewports(1, &viewport);
            commands->RSSetScissorRects(1, &scissor);
            commands->OMSetRenderTargets(1, &handle, false, null);
            commands->ClearRenderTargetView(handle, clear, 0, null);
            commands->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            commands->IASetVertexBuffers(0, 1, &view);
            commands->DrawInstanced(vertices, 1, 0, 0);
            Transition(commands, buffers.Geometry, D3D12_RESOURCE_STATE_VERTEX_AND_CONSTANT_BUFFER, D3D12_RESOURCE_STATE_COPY_SOURCE);
            Transition(commands, _targets[index], D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);
            commands->EndQuery(_timestamps, timestamp, 1);
            commands->ResolveQueryData(_timestamps, timestamp, 0, 2, _gpu._readback, 16);
            Check(commands->Close(), "close draw");
            var list = (ID3D12CommandList*)commands;
            _gpu._queue->ExecuteCommandLists(1, &list);
            var present = Stopwatch.GetTimestamp();
            var result = _chain->Present(1, paced ? DoNotWait : 0);
            var busyRetries = 0;
            while (paced && result.Value == StillDrawing && Stopwatch.GetElapsedTime(present).TotalMilliseconds < 500)
            {
                busyRetries++;
                Thread.Sleep(1);
                result = _chain->Present(1, DoNotWait);
            }
            var presentMs = Stopwatch.GetElapsedTime(present).TotalMilliseconds;
            _gpu.Wait(_gpu.Signal());
            if (result.Value != 0)
                throw new InvalidOperationException($"Present returned 0x{result.Value:X8}.");
            var frameMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var range = new D3D12_RANGE { Begin = 16, End = 32 };
            void* data = null;
            Check(_gpu._readback->Map(0, &range, &data), "map draw timestamps");
            var ticks = (ulong*)((byte*)data + 16);
            var elapsed = ticks[1] - ticks[0];
            D3D12_RANGE empty = default;
            _gpu._readback->Unmap(0, &empty);
            return (presentMs, frameMs, elapsed * 1000d / _timestampFrequency, readyWait, busyRetries);
        }

        private static void Transition(ID3D12GraphicsCommandList* commands, ID3D12Resource* resource,
            D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
        {
            var barrier = new D3D12_RESOURCE_BARRIER
            {
                Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION
            };
            barrier.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER
            {
                pResource = resource, Subresource = uint.MaxValue, StateBefore = before, StateAfter = after
            };
            commands->ResourceBarrier(1, &barrier);
        }

        private void CreatePipeline()
        {
            ID3DBlob* root = null, vs = null, ps = null;
            try
            {
                var desc = new D3D12_ROOT_SIGNATURE_DESC
                {
                    Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT
                };
                Check(Serialize(&desc, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, &root, null), "serialize root");
                fixed (ID3D12RootSignature** signature = &_root)
                    Check(_gpu._device->CreateRootSignature(0, root->GetBufferPointer(), root->GetBufferSize(),
                        __uuidof<ID3D12RootSignature>(), (void**)signature), "create root");
                vs = Compile("float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }"u8, "vs_5_0\0"u8);
                ps = Compile("float4 main() : SV_Target { return float4(0.2, 0.8, 0.4, 1); }"u8, "ps_5_0\0"u8);
                fixed (byte* position = "POSITION\0"u8)
                {
                    var element = new D3D12_INPUT_ELEMENT_DESC { SemanticName = (sbyte*)position, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT };
                    var pipeline = new D3D12_GRAPHICS_PIPELINE_STATE_DESC
                    {
                        pRootSignature = _root,
                        VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs->GetBufferPointer(), BytecodeLength = vs->GetBufferSize() },
                        PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps->GetBufferPointer(), BytecodeLength = ps->GetBufferSize() },
                        InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = &element, NumElements = 1 },
                        SampleMask = uint.MaxValue, NumRenderTargets = 1,
                        RasterizerState = new D3D12_RASTERIZER_DESC
                        {
                            FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID, CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE,
                            DepthClipEnable = true
                        },
                        PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE,
                        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }
                    };
                    pipeline.RTVFormats[0] = Format;
                    pipeline.BlendState.RenderTarget[0].RenderTargetWriteMask = 15;
                    fixed (ID3D12PipelineState** state = &_pipeline)
                        Check(_gpu._device->CreateGraphicsPipelineState(&pipeline, __uuidof<ID3D12PipelineState>(), (void**)state), "draw pipeline");
                }
            }
            finally { Release(ps); Release(vs); Release(root); }
        }

        private static ID3DBlob* Compile(ReadOnlySpan<byte> source, ReadOnlySpan<byte> profile)
        {
            ID3DBlob* blob = null;
            fixed (byte* code = source, target = profile, entry = "main\0"u8)
                Check(CompileShader(code, (nuint)source.Length, null, null, null, entry, target, 0, 0, &blob, null), "compile shader");
            return blob;
        }

        public void Dispose()
        {
            Release(_timestamps);
            Release(_pipeline);
            Release(_root);
            foreach (var target in _targets)
                Release(target);
            Release(_rtv);
            if (_frameReady != HANDLE.NULL)
                CloseHandle(_frameReady);
            Release(_chain);
            if (_window != HWND.NULL)
                DestroyWindow(_window);
        }

        [LibraryImport("d3d12.dll", EntryPoint = "D3D12SerializeRootSignature")]
        private static partial int Serialize(D3D12_ROOT_SIGNATURE_DESC* desc, D3D_ROOT_SIGNATURE_VERSION version, ID3DBlob** blob, ID3DBlob** errors);
        [LibraryImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile")]
        private static partial int CompileShader(void* source, nuint length, byte* name, void* defines, void* include,
            byte* entry, byte* profile, uint flags1, uint flags2, ID3DBlob** blob, ID3DBlob** errors);
    }
}
