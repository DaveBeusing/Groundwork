using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    private const int MaterialOutOfMemory = unchecked((int)0x8007000E);
    private GpuTerrainMaterialSet? _frameTerrainMaterials;
    private D3D12TextureHandoff? _frameMaterialStream;
    private ID3D12Resource* _materialConstants;
    private byte* _mappedMaterialConstants;
    private long _materialAllocationBytes;
    private D3D12TextureHandoff? _materialAllocationOwner;
    private GpuTerrainMaterialSet?[]? _uploadedMaterialSets;

    private ulong FrameTerrainMaterialAddress { get; set; }

    internal bool TryGetTerrainMap(D3D12TextureTarget target, GpuTextureIdentity identity, ulong epoch, out GpuTerrainMap map)
    {
        if (identity.RendererEpoch == epoch && FindTexture(identity) is { Ready: true } entry)
        {
            map = new(target, identity, entry.Import);
            return true;
        }

        map = default;
        return false;
    }

    internal void SelectTerrainMaterials(GpuTerrainMaterialSet? materials, D3D12TextureHandoff? stream)
    {
        _frameTerrainMaterials = materials;
        _frameMaterialStream = stream;
        FrameTerrainMaterialAddress = 0;
    }

    private bool PrepareFrameTerrainMaterials(bool scene, bool shadows)
    {
        FrameTerrainMaterialAddress = 0;
        if (!scene || ActiveMeshGeometry is not { HasTerrainAppearance: true } || _frameTerrainMaterials is not { } materials ||
            _frameMaterialStream is not { } stream || !TryPrepareTerrainAppearance(shadows) || !TryAllocateMaterialConstants(stream))
            return false;

        // The selected frame context was checked for retirement before writing its constant slice.
        var offset = _nextFrame * GpuTerrainMaterialSet.ConstantStride;
        if (!ReferenceEquals(_uploadedMaterialSets![_nextFrame], materials))
        {
            materials.Payload.CopyTo(new Span<byte>(_mappedMaterialConstants + offset, GpuTerrainMaterialSet.PayloadBytes));
            _uploadedMaterialSets[_nextFrame] = materials;
        }

        FrameTerrainMaterialAddress = _materialConstants->GetGPUVirtualAddress() + (uint)offset;
        return true;
    }

    private bool TryAllocateMaterialConstants(D3D12TextureHandoff stream)
    {
        if (_materialConstants != null)
            return ReferenceEquals(_materialAllocationOwner, stream);

        var desc = Buffer((ulong)(_frames.Length * GpuTerrainMaterialSet.ConstantStride));
        var allocation = _device->GetResourceAllocationInfo(1, 1, &desc).SizeInBytes;
        if (allocation is 0 or > long.MaxValue || !stream.TryReserveMaterialBacking((long)allocation))
            return false;

        ID3D12Resource* resource = null;
        var installed = false;
        try
        {
            var uploaded = new GpuTerrainMaterialSet?[_frames.Length];
            resource = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD, desc,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
            D3D12_RANGE read = default;
            void* mapped = null;
            Check(resource->Map(0, &read, &mapped), "Map terrain material constants");
            ObserveResource(resource, D3D12ResourceOwner.Texture, allocation);

            _materialConstants = resource;
            _mappedMaterialConstants = (byte*)mapped;
            _materialAllocationBytes = (long)allocation;
            _materialAllocationOwner = stream;
            _uploadedMaterialSets = uploaded;
            var native = checked(TextureStatistics.NativeBytes + (long)allocation);
            TextureStatistics = TextureStatistics with
            {
                NativeBytes = native,
                NativePeakBytes = Math.Max(TextureStatistics.NativePeakBytes, checked(native + TextureStatistics.StagingBytes)),
            };
            installed = true;
            return true;
        }
        catch (GpuNativeException error) when (error.HResult == MaterialOutOfMemory && !IsRemoved)
        {
            return false;
        }
        finally
        {
            if (!installed)
            {
                Release(resource);
                stream.ReleaseMaterialBacking((long)allocation);
            }
        }
    }

    private void ObserveFrameTerrainMaterials()
    {
        ObserveUse(_materialConstants);
        foreach (ref readonly var map in _frameTerrainMaterials!.Maps)
            if (FindTexture(map.Identity) is { Ready: true } entry)
                ObserveUse(entry.Resource);
    }

    private void ReleaseTerrainMaterialConstants()
    {
        if (_materialConstants == null)
            return;

        _materialConstants->Unmap(0, null);
        ReleaseObservedResource(_materialConstants);
        TextureStatistics = TextureStatistics with { NativeBytes = TextureStatistics.NativeBytes - _materialAllocationBytes };
        _materialAllocationOwner!.ReleaseMaterialBacking(_materialAllocationBytes);
        _materialConstants = null;
        _mappedMaterialConstants = null;
        _materialAllocationBytes = 0;
        _materialAllocationOwner = null;
        _uploadedMaterialSets = null;
        SelectTerrainMaterials(null, null);
    }
}
