using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal static unsafe partial class GpuNative
{
    [LibraryImport("dxgi.dll", EntryPoint = "CreateDXGIFactory2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int CreateFactory(uint flags, Guid* iid, void** result);

    [LibraryImport("d3d12.dll", EntryPoint = "D3D12CreateDevice")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int CreateDevice(IUnknown* adapter, D3D_FEATURE_LEVEL level, Guid* iid, void** result);

    [LibraryImport("d3d12.dll", EntryPoint = "D3D12GetDebugInterface")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int GetDebugInterface(Guid* iid, void** result);

    [LibraryImport("d3d12.dll", EntryPoint = "D3D12SerializeRootSignature")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int SerializeRootSignature(
        D3D12_ROOT_SIGNATURE_DESC* description, D3D_ROOT_SIGNATURE_VERSION version, ID3DBlob** blob, ID3DBlob** error);

    internal static void Check(HRESULT result, string operation) => Check(result.Value, operation);

    internal static void Check(int result, string operation)
    {
        if (result < 0)
            throw new GpuNativeException(operation, result);
    }

    internal static void Release<T>(T* value) where T : unmanaged
    {
        if (value != null)
            ((IUnknown*)value)->Release();
    }

    internal static ID3D12Resource* CreateResource(ID3D12Device* device, D3D12_HEAP_TYPE heap,
        D3D12_RESOURCE_DESC description, D3D12_RESOURCE_STATES state, D3D12_CLEAR_VALUE* clear = null)
    {
        var properties = new D3D12_HEAP_PROPERTIES { Type = heap, CreationNodeMask = 1, VisibleNodeMask = 1 };
        ID3D12Resource* resource = null;
        Check(device->CreateCommittedResource(&properties, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            &description, state, clear, __uuidof<ID3D12Resource>(), (void**)&resource), "CreateCommittedResource");
        return resource;
    }

    internal static D3D12_RESOURCE_DESC Buffer(ulong bytes) => new()
    {
        Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
        Width = bytes,
        Height = 1,
        DepthOrArraySize = 1,
        MipLevels = 1,
        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
        Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
    };

    internal static D3D12_RESOURCE_DESC Texture(int width, int height, DXGI_FORMAT format, D3D12_RESOURCE_FLAGS flags) => new()
    {
        Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
        Width = (ulong)width,
        Height = (uint)height,
        DepthOrArraySize = 1,
        MipLevels = 1,
        Format = format,
        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
        Flags = flags,
    };

    internal static ID3D12DescriptorHeap* CreateHeap(ID3D12Device* device, D3D12_DESCRIPTOR_HEAP_TYPE type)
    {
        var description = new D3D12_DESCRIPTOR_HEAP_DESC { Type = type, NumDescriptors = 1 };
        ID3D12DescriptorHeap* heap = null;
        Check(device->CreateDescriptorHeap(&description, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "CreateDescriptorHeap");
        return heap;
    }

    internal static void Transition(ID3D12GraphicsCommandList* commands, ID3D12Resource* resource,
        D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        var barrier = new D3D12_RESOURCE_BARRIER
        {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };
        barrier.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER
        {
            pResource = resource,
            Subresource = uint.MaxValue,
            StateBefore = before,
            StateAfter = after,
        };
        commands->ResourceBarrier(1, &barrier);
    }
}

internal sealed class GpuNativeException : InvalidOperationException
{
    internal GpuNativeException(string operation, int result) : base($"{operation} failed (0x{result:X8}).")
        => HResult = result;
}
