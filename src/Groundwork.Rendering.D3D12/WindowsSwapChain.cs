using System.Runtime.InteropServices;
using Groundwork.Platform.Windows;
using Microsoft.Win32.SafeHandles;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal enum PresentResult { Presented, Occluded, Busy }

/// <summary>Render-thread-owned flip chain and exactly two back-buffer references.</summary>
internal sealed unsafe class WindowsSwapChain : IDisposable
{
    private const uint BufferCount = 2;
    private const uint LatencyFlag = (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
    private const uint PresentTest = 1;
    private const int StatusOccluded = 0x087A0001;
    private const int ErrorStillDrawing = unchecked((int)0x887A000A);
    private const DXGI_FORMAT Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;

    private readonly GpuDevice _gpu;
    private readonly GpuTarget?[] _targets = new GpuTarget?[BufferCount];
    private IDXGISwapChain3* _chain;
    private WaitHandle? _latency;
    private bool _frameReady;

    internal WindowsSwapChain(GpuDevice gpu, WindowsPresentationOperation operation, int width, int height)
    {
        _gpu = gpu;
        IDXGISwapChain1* initial = null;
        var descriptor = new DXGI_SWAP_CHAIN_DESC1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            BufferUsage = 0x20, // DXGI_USAGE_RENDER_TARGET_OUTPUT
            BufferCount = BufferCount,
            Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
            SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
            AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            Flags = LatencyFlag,
        };

        try
        {
            Check(gpu.Factory->CreateSwapChainForHwnd((IUnknown*)gpu.Queue, (HWND)operation.WindowHandle,
                &descriptor, null, null, &initial));
            IDXGISwapChain3* chain = null;
            Check(initial->QueryInterface(__uuidof<IDXGISwapChain3>(), (void**)&chain));
            _chain = chain;
            Check(gpu.Factory->MakeWindowAssociation((HWND)operation.WindowHandle, 2)); // DXGI_MWA_NO_ALT_ENTER
            Check(_chain->SetMaximumFrameLatency(1));
            var latencyHandle = (nint)_chain->GetFrameLatencyWaitableObject();
            if (latencyHandle == 0)
                throw new InvalidOperationException("DXGI did not provide a frame latency waitable handle.");

            _latency = new LatencyWaitHandle(latencyHandle);
            AttachBuffers(width, height);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (initial != null)
                initial->Release();
        }
    }

    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal GpuTarget CurrentBuffer => _targets[_chain->GetCurrentBackBufferIndex()]!;

    internal bool CanBeginFrame()
    {
        if (!_frameReady)
            _frameReady = _latency!.WaitOne(0);
        return _frameReady;
    }

    internal void Resize(int width, int height)
    {
        ReleaseBuffers();
        Check(_chain->ResizeBuffers(BufferCount, (uint)width, (uint)height, Format, LatencyFlag));
        AttachBuffers(width, height);
    }

    internal PresentResult Present(bool testOnly)
    {
        if (!testOnly)
            _frameReady = false;
        var result = _chain->Present(testOnly ? 0u : 1u, testOnly ? PresentTest : 0u);
        return result.Value switch
        {
            0 => PresentResult.Presented,
            StatusOccluded => PresentResult.Occluded,
            ErrorStillDrawing => PresentResult.Busy,
            _ => UnexpectedPresent(result),
        };
    }

    private void AttachBuffers(int width, int height)
    {
        for (uint index = 0; index < BufferCount; index++)
        {
            ID3D12Resource* buffer = null;
            Check(_chain->GetBuffer(index, __uuidof<ID3D12Resource>(), (void**)&buffer));
            try
            {
                _targets[index] = _gpu.Attach(buffer, width, height);
            }
            catch
            {
                buffer->Release();
                throw;
            }
        }

        Width = width;
        Height = height;
    }

    private void ReleaseBuffers()
    {
        for (var index = _targets.Length - 1; index >= 0; index--)
        {
            _targets[index]?.Dispose();
            _targets[index] = null;
        }
    }

    public void Dispose()
    {
        ReleaseBuffers();
        _latency?.Dispose();
        _latency = null;
        if (_chain != null)
        {
            _chain->Release();
            _chain = null;
        }
    }

    private static PresentResult UnexpectedPresent(HRESULT result)
    {
        Check(result);
        throw new GpuNativeException("Unexpected DXGI presentation status", result.Value);
    }

    private static void Check(HRESULT result) => Marshal.ThrowExceptionForHR(result.Value);

    private sealed class LatencyWaitHandle : WaitHandle
    {
        internal LatencyWaitHandle(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }
}
