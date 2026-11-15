using Groundwork.Rendering.Passes;
using Groundwork.Rendering.Textures;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal readonly record struct GpuTextureRequirements(int PayloadBytes, int StagingBytes,
    long TextureAllocationBytes, long StagingAllocationBytes);

internal readonly record struct GpuTextureIdentity(long Device, ulong RendererEpoch, int Slot, ulong Generation);

internal readonly record struct GpuFrameTextureImport(RenderResourceFormat Format, int Width, int Height);

internal readonly record struct GpuTextureStatistics(long NativeBytes, long StagingBytes, long NativePeakBytes,
    ulong UploadedBytes);

internal enum GpuTexturePreparationStep { Pipeline, Destination }

internal sealed unsafe partial class GpuDevice
{
    internal const int MaxTextureSlots = 32;
    internal const int MaxTextureCopyBytes = 2 * 1024 * 1024;
    private const uint ShadowDescriptors = 2;
    // Slot two is permanently reserved for the untextured fallback.
    private const uint FirstTextureDescriptor = ShadowDescriptors + 1;
    private const int TextureOutOfMemory = unchecked((int)0x8007000E);
    private const int TextureUnsupported = unchecked((int)0x887A0004);
    private static long _nextTextureDeviceId;
    private long _textureDeviceId;
    private ulong _textureGeneration;
    private TextureEntry?[]? _textureSlots;
    private int _pendingTextureSlot = -1;
    private Frame? _textureCopyContext;
    private ulong _textureCopyFence;
    private ID3D12Resource* _textureStaging;
    private ID3D12RootSignature* _textureRoot;
    private ID3D12PipelineState* _texturePipeline;
    private ID3D12PipelineState* _textureNormalPipeline;
    private ID3D12PipelineState* _textureShadowPipeline;
    private ID3D12PipelineState* _textureShadowNormalPipeline;
    private GpuTextureIdentity _frameTexture;

    internal GpuTextureStatistics TextureStatistics { get; private set; }
    internal bool HasTextureCopy => _pendingTextureSlot >= 0;
    internal Func<GpuTexturePreparationStep, int?>? TexturePreparationFailureForTests;

    internal bool TryGetTextureRequirements(CookedTexture texture, out GpuTextureRequirements requirements) =>
        TryDescribeTexture(texture, out requirements, out _, out _, out _, out _);

    private bool TryDescribeTexture(CookedTexture texture, out GpuTextureRequirements requirements,
        out D3D12_RESOURCE_DESC description, out D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint,
        out uint rows, out GpuFrameTextureImport import)
    {
        ArgumentNullException.ThrowIfNull(texture);
        requirements = default;
        description = default;
        footprint = default;
        rows = 0;
        import = default;
        if (_device == null || texture.Mips.IsEmpty)
            return false;

        var (format, portableFormat) = (texture.Format, texture.ColorSpace) switch
        {
            (TextureFormat.Rgba8, TextureColorSpace.Linear) => (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, RenderResourceFormat.Rgba8Unorm),
            (TextureFormat.Rgba8, TextureColorSpace.Srgb) => (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, RenderResourceFormat.Rgba8UnormSrgb),
            (TextureFormat.Bc1, TextureColorSpace.Linear) => (DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM, RenderResourceFormat.Bc1Unorm),
            (TextureFormat.Bc1, TextureColorSpace.Srgb) => (DXGI_FORMAT.DXGI_FORMAT_BC1_UNORM_SRGB, RenderResourceFormat.Bc1UnormSrgb),
            _ => default,
        };
        var mip = texture.Mips[0];
        if (format == DXGI_FORMAT.DXGI_FORMAT_UNKNOWN || mip.Width is < 1 or > RenderPlanLimits.TextureDimension ||
            mip.Height is < 1 or > RenderPlanLimits.TextureDimension || mip.Offset != 0 ||
            texture.Semantic is not (TextureSemantic.Color or TextureSemantic.Data or TextureSemantic.Normal) ||
            texture.ColorSpace == TextureColorSpace.Srgb && texture.Semantic != TextureSemantic.Color)
            return false;
        var bc1 = texture.Format == TextureFormat.Bc1;
        if (bc1 && !IsLegalBc1LevelZero(mip.Width, mip.Height, false) && !SupportsUnalignedBlockTexture())
            return false;
        var expectedRows = bc1 ? (mip.Height + 3) / 4 : mip.Height;
        var expectedRowBytes = bc1 ? ((mip.Width + 3) / 4) * 8 : mip.Width * 4;
        if (mip.RowBytes != expectedRowBytes || mip.ByteLength != (long)expectedRows * expectedRowBytes ||
            mip.ByteLength > texture.Payload.Length || mip.ByteLength > MaxTextureCopyBytes)
            return false;

        var support = new D3D12_FEATURE_DATA_FORMAT_SUPPORT { Format = format };
        const D3D12_FORMAT_SUPPORT1 required =
            D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_TEXTURE2D |
            D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_SHADER_SAMPLE;
        if (_device->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_FORMAT_SUPPORT, &support, (uint)sizeof(D3D12_FEATURE_DATA_FORMAT_SUPPORT)).Value < 0 ||
            (support.Support1 & required) != required)
            return false;

        var desc = Texture(mip.Width, mip.Height, format, D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE);
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout = default;
        uint rowCount = 0;
        ulong rowBytes = 0;
        ulong stagingBytes = 0;
        _device->GetCopyableFootprints(&desc, 0, 1, 0, &layout, &rowCount, &rowBytes, &stagingBytes);
        if (stagingBytes is 0 or > MaxTextureCopyBytes || layout.Offset != 0 || rowCount != expectedRows ||
            rowBytes != (ulong)expectedRowBytes || layout.Footprint.RowPitch < rowBytes ||
            (rowCount - 1UL) * layout.Footprint.RowPitch + rowBytes > stagingBytes)
            return false;
        var stageDesc = Buffer(stagingBytes);
        var textureAllocation = _device->GetResourceAllocationInfo(1, 1, &desc).SizeInBytes;
        var stagingAllocation = _device->GetResourceAllocationInfo(1, 1, &stageDesc).SizeInBytes;
        if (textureAllocation is 0 or > long.MaxValue || stagingAllocation is 0 or > long.MaxValue)
            return false;

        requirements = new(mip.ByteLength, (int)stagingBytes, (long)textureAllocation, (long)stagingAllocation);
        footprint = layout;
        description = desc;
        rows = rowCount;
        import = new(portableFormat, mip.Width, mip.Height);
        return true;
    }

    private bool SupportsUnalignedBlockTexture()
    {
        var options = new D3D12_FEATURE_DATA_D3D12_OPTIONS8();
        return _device->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS8,
            &options, (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS8)).Value >= 0 &&
            options.UnalignedBlockTexturesSupported;
    }

    internal static bool IsLegalBc1LevelZero(int width, int height, bool supportsUnaligned) =>
        width > 0 && height > 0 && (supportsUnaligned || ((width | height) & 3) == 0);

    internal bool TrySubmitTexture(CookedTexture texture, ulong rendererEpoch, in GpuTextureRequirements reserved,
        out GpuTextureIdentity identity, Func<bool>? authorize = null)
    {
        identity = default;
        if (rendererEpoch == 0 || HasTextureCopy || !IsIdle ||
            !TryDescribeTexture(texture, out var actual, out var description, out var footprint, out var rows, out var import) ||
            reserved != actual)
            return false;

        var slots = _textureSlots ??= new TextureEntry[MaxTextureSlots];
        var slot = Array.IndexOf(slots, null);
        if (slot < 0)
            return false;
        if (_textureDeviceId == 0)
            _textureDeviceId = Interlocked.Increment(ref _nextTextureDeviceId);
        var candidate = new GpuTextureIdentity(_textureDeviceId, rendererEpoch, slot, checked(++_textureGeneration));
        ID3D12Resource* staging = null;
        ID3D12Resource* destination = null;
        var descriptorWritten = false;
        try
        {
            Frame context;
            try
            {
                EnsureTexturePipeline();
                context = EnsureTextureCopyContext();
                staging = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD, Buffer((ulong)actual.StagingBytes),
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
                ObserveTextureAllocationPeak(actual.StagingAllocationBytes);
                Check(TexturePreparationFailureForTests?.Invoke(GpuTexturePreparationStep.Destination) ?? 0,
                    "Prepare texture destination");
                destination = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, description,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);
                ObserveTextureAllocationPeak(actual.StagingAllocationBytes + actual.TextureAllocationBytes);
                FillTextureUpload(staging, texture, footprint.Footprint.RowPitch, rows);
            }
            catch (GpuNativeException error) when (error.HResult is (TextureOutOfMemory or TextureUnsupported) &&
                !IsRemoved && IsIdle)
            {
                // Allocation and feature exhaustion here affect only the optional ticket.
                return false;
            }
            WriteTextureSrv(destination, description.Format, (uint)slot + FirstTextureDescriptor);
            descriptorWritten = true;
            var entry = new TextureEntry(candidate, destination, import, actual.TextureAllocationBytes);

            Check(context.Allocator->Reset(), "Reset texture copy allocator");
            Check(context.Commands->Reset(context.Allocator, null), "Reset texture copy command list");
            var source = new D3D12_TEXTURE_COPY_LOCATION
            {
                pResource = staging,
                Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
            };
            source.PlacedFootprint = footprint;
            var target = new D3D12_TEXTURE_COPY_LOCATION
            {
                pResource = destination,
                Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
            };
            context.Commands->CopyTextureRegion(&target, 0, 0, 0, &source, null);
            Transition(context.Commands, destination, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            Check(context.Commands->Close(), "Close texture copy command list");
            if (authorize?.Invoke() == false)
                return false;

            var retire = BeginQueueWork();
            slots[slot] = entry;
            _pendingTextureSlot = slot;
            _textureStaging = staging;
            destination = staging = null;
            var nativeBytes = TextureStatistics.NativeBytes + actual.TextureAllocationBytes;
            TextureStatistics = new(nativeBytes, actual.StagingAllocationBytes,
                TextureStatistics.NativePeakBytes,
                TextureStatistics.UploadedBytes + (ulong)actual.PayloadBytes);
            var commands = (ID3D12CommandList*)context.Commands;
            _queue->ExecuteCommandLists(1, &commands);
            Check(SignalQueueWork(retire), "Signal texture copy");
            _textureCopyFence = retire;
            identity = candidate;
            return true;
        }
        finally
        {
            if (descriptorWritten && destination != null)
                WriteTextureSrv(null, description.Format, (uint)slot + FirstTextureDescriptor);
            Release(destination);
            Release(staging);
        }
    }

    private void ObserveTextureAllocationPeak(long candidateBytes)
    {
        TextureStatistics = TextureStatistics with
        {
            NativePeakBytes = Math.Max(TextureStatistics.NativePeakBytes, TextureStatistics.NativeBytes + candidateBytes)
        };
    }

    private Frame EnsureTextureCopyContext()
    {
        if (_textureCopyContext is { } context)
            return context;

        var created = new Frame();
        try
        {
            created.Initialize(_device);
            _textureCopyContext = created;
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    private static void FillTextureUpload(ID3D12Resource* staging, CookedTexture texture, uint rowPitch, uint rows)
    {
        void* mapped = null;
        var unread = default(D3D12_RANGE);
        Check(staging->Map(0, &unread, &mapped), "Map texture upload");
        var mip = texture.Mips[0];
        try
        {
            for (var row = 0; row < rows; row++)
                texture.Payload.Slice(row * mip.RowBytes, mip.RowBytes)
                    .CopyTo(new Span<byte>((byte*)mapped + row * rowPitch, mip.RowBytes));
        }
        finally
        {
            var written = new D3D12_RANGE { End = (nuint)((rows - 1UL) * rowPitch + (uint)mip.RowBytes) };
            staging->Unmap(0, &written);
        }
    }

    internal bool TryRetireTextureCopy(out GpuTextureIdentity identity)
    {
        identity = default;
        if (!HasTextureCopy || _textureCopyFence == 0)
            return false;
        var completed = ReadCompletedValue();
        if (completed == ulong.MaxValue)
            throw new GpuNativeException("Read texture copy fence after device removal", RemovedReason);
        if (!_retirement.CanReuse(completed, _textureCopyFence))
            return false;
        var entry = _textureSlots![_pendingTextureSlot]!;
        identity = entry.Identity;
        entry.Ready = true;
        Release(_textureStaging);
        _textureStaging = null;
        _pendingTextureSlot = -1;
        _textureCopyFence = 0;
        TextureStatistics = TextureStatistics with { StagingBytes = 0 };
        return true;
    }

    internal bool TryReleaseTexture(GpuTextureIdentity identity)
    {
        if (FindTexture(identity) is not { Ready: true } entry || !IsIdle)
            return false;
        // The shared queue includes all accepted draws and the distinct post-Present signal.
        WriteTextureSrv(null, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, (uint)identity.Slot + FirstTextureDescriptor);
        Release(entry.Resource);
        _textureSlots![identity.Slot] = null;
        TextureStatistics = TextureStatistics with { NativeBytes = TextureStatistics.NativeBytes - entry.AllocationBytes };
        if (_frameTexture == identity)
            _frameTexture = default;
        return true;
    }

    internal bool SetFrameTexture(GpuTextureIdentity binding, ulong rendererEpoch)
    {
        var canBind = binding.RendererEpoch == rendererEpoch && FindTexture(binding) is { Ready: true };
        _frameTexture = canBind ? binding : default;
        return canBind;
    }

    private GpuFrameTextureImport FrameTextureImport() =>
        FindTexture(_frameTexture) is { Ready: true } entry ? entry.Import : default;

    private TextureEntry? FindTexture(GpuTextureIdentity binding) =>
        binding.Device != 0 && binding.Device == _textureDeviceId && _textureSlots is { } slots &&
        (uint)binding.Slot < (uint)slots.Length && slots[binding.Slot] is { } entry && entry.Identity == binding
            ? entry : null;

    private void EnsureTexturePipeline()
    {
        if (_textureRoot != null)
            return;
        EnsureFrameSrv();
        try
        {
            _textureRoot = CreateFrameRoot(60, texture: true);
            _texturePipeline = CreateFramePipeline(_textureRoot, sky: false, texture: true);
            Check(TexturePreparationFailureForTests?.Invoke(GpuTexturePreparationStep.Pipeline) ?? 0,
                "Prepare texture pipeline");
            _textureNormalPipeline = CreateFramePipeline(_textureRoot, sky: false, normals: true, texture: true);
            _textureShadowPipeline = CreateFramePipeline(_textureRoot, sky: false, shadow: true, texture: true);
            _textureShadowNormalPipeline = CreateFramePipeline(_textureRoot, sky: false, normals: true, shadow: true, texture: true);
        }
        catch
        {
            ReleaseTexturePipelines();
            throw;
        }
    }

    private void EnsureFrameSrv()
    {
        if (_shadowSrv != null)
            return;
        var description = new D3D12_DESCRIPTOR_HEAP_DESC
        {
            Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
            NumDescriptors = FirstTextureDescriptor + MaxTextureSlots,
            Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
        };
        ID3D12DescriptorHeap* heap = null;
        Check(_device->CreateDescriptorHeap(&description, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "Create frame SRV heap");
        _shadowSrv = heap;
        for (uint i = 0; i < ShadowDescriptors; i++)
            WriteTextureSrv(null, DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT, i);
        WriteTextureSrv(null, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, ShadowDescriptors);
    }

    private void WriteTextureSrv(ID3D12Resource* texture, DXGI_FORMAT format, uint index)
    {
        var view = new D3D12_SHADER_RESOURCE_VIEW_DESC
        {
            Format = format,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D,
            Shader4ComponentMapping = 5768, // Identity component mapping.
        };
        view.Texture2D.MipLevels = 1;
        var handle = _shadowSrv->GetCPUDescriptorHandleForHeapStart();
        handle.ptr += index * _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        _device->CreateShaderResourceView(texture, &view, handle);
    }

    private D3D12_GPU_DESCRIPTOR_HANDLE TextureGpuDescriptor(int index)
    {
        var handle = _shadowSrv->GetGPUDescriptorHandleForHeapStart();
        handle.ptr += ((uint)index + FirstTextureDescriptor) *
            _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        return handle;
    }

    private void ReleaseTextures()
    {
        _textureCopyContext?.Dispose();
        _textureCopyContext = null;
        Release(_textureStaging);
        _textureStaging = null;
        if (_textureSlots is { } slots)
            foreach (var slot in slots)
                if (slot is not null)
                    Release(slot.Resource);
        _textureSlots = null;
        _pendingTextureSlot = -1;
        _textureCopyFence = 0;
        _frameTexture = default;
        ReleaseTexturePipelines();
        TextureStatistics = TextureStatistics with { NativeBytes = 0, StagingBytes = 0 };
    }

    private void ReleaseTexturePipelines()
    {
        Release(_textureShadowNormalPipeline);
        Release(_textureShadowPipeline);
        Release(_textureNormalPipeline);
        Release(_texturePipeline);
        Release(_textureRoot);
        _textureShadowNormalPipeline = _textureShadowPipeline = _textureNormalPipeline = _texturePipeline = null;
        _textureRoot = null;
    }

    private sealed class TextureEntry(GpuTextureIdentity identity, ID3D12Resource* resource,
        GpuFrameTextureImport import, long allocationBytes)
    {
        internal readonly GpuTextureIdentity Identity = identity;
        internal readonly ID3D12Resource* Resource = resource;
        internal readonly GpuFrameTextureImport Import = import;
        internal readonly long AllocationBytes = allocationBytes;
        internal bool Ready;
    }
}
