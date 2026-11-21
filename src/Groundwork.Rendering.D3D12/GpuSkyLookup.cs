using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    // Diagnostic prototype only. Two fixed RGBA32F angular fields use 1 MiB
    // per owner. Preparation rejects larger native texture alignment.
    private const int SkyLookupWidth = 256;
    private const int SkyLookupHeight = 128;
    private ID3D12Resource* _skyLookupRadiance;
    private ID3D12Resource* _skyLookupTransmission;
    private ID3D12DescriptorHeap* _skyLookupDescriptors;
    private ID3D12PipelineState* _skyLookupCompute;
    internal bool SkyLookupPrototypeEnabledForTests { get; }
    internal int SkyLookupWidthForTests => SkyLookupPrototypeEnabledForTests ? SkyLookupWidth : 0;
    internal int SkyLookupHeightForTests => SkyLookupPrototypeEnabledForTests ? SkyLookupHeight : 0;
    internal ulong SkyLookupAllocatedBytesForTests { get; private set; }
    internal ulong SkyLookupDispatchesForTests { get; private set; }

    private ID3D12RootSignature* CreateSkyLookupRoot()
    {
        D3D12_DESCRIPTOR_RANGE* ranges = stackalloc D3D12_DESCRIPTOR_RANGE[2];
        ranges[0] = new() { RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_UAV, NumDescriptors = 2 };
        ranges[1] = new() { RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV, NumDescriptors = 2 };
        D3D12_ROOT_PARAMETER* parameters = stackalloc D3D12_ROOT_PARAMETER[3];
        parameters[0] = new()
        {
            ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS,
            Constants = new() { Num32BitValues = SkyConstantCount },
        };
        for (var i = 1; i < 3; i++)
            parameters[i] = new()
            {
                ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
                DescriptorTable = new() { NumDescriptorRanges = 1, pDescriptorRanges = &ranges[i - 1] },
            };
        var sampler = new D3D12_STATIC_SAMPLER_DESC
        {
            Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_LINEAR_MIP_POINT,
            AddressU = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_WRAP,
            AddressV = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            ComparisonFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_ALWAYS,
            MaxAnisotropy = 1, MaxLOD = 0,
            ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL,
        };
        var description = new D3D12_ROOT_SIGNATURE_DESC
        {
            NumParameters = 3, pParameters = parameters,
            NumStaticSamplers = 1, pStaticSamplers = &sampler,
            Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT,
        };
        ID3DBlob* blob = null;
        ID3DBlob* error = null;
        ID3D12RootSignature* root = null;
        try
        {
            Check(SerializeRootSignature(&description, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1,
                &blob, &error), "Serialize sky lookup root signature");
            Check(_device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(),
                __uuidof<ID3D12RootSignature>(), (void**)&root), "Create sky lookup root signature");
            return root;
        }
        catch { Release(root); throw; }
        finally { Release(error); Release(blob); }
    }

    private void PrepareSkyLookup()
    {
        if (_skyLookupCompute != null)
            return;
        // Partial preparation is owned immediately and released on any failure;
        // none of these resources are published into the direct session cache.
        try
        {
            var texture = Texture(SkyLookupWidth, SkyLookupHeight, DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
                D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
            var allocation = _device->GetResourceAllocationInfo(1, 1, &texture).SizeInBytes;
            if (allocation == ulong.MaxValue || allocation > 512 * 1024)
                throw new InvalidOperationException("Sky lookup native allocation exceeds its fixed two-owner resource budget.");
            _skyLookupRadiance = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, texture,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            ObserveResource(_skyLookupRadiance, D3D12ResourceOwner.Texture, allocation);
            SkyLookupAllocatedBytesForTests += allocation;
            _retirementHooks?.AfterSkyLookupFirstAllocation?.Invoke();
            _skyLookupTransmission = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, texture,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            ObserveResource(_skyLookupTransmission, D3D12ResourceOwner.Texture, allocation);
            SkyLookupAllocatedBytesForTests += allocation;
            var heapDescription = new D3D12_DESCRIPTOR_HEAP_DESC
            {
                Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
                NumDescriptors = 4, Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
            };
            fixed (ID3D12DescriptorHeap** heap = &_skyLookupDescriptors)
                Check(_device->CreateDescriptorHeap(&heapDescription, __uuidof<ID3D12DescriptorHeap>(), (void**)heap),
                    "Create sky lookup descriptors");
            var increment = _device->GetDescriptorHandleIncrementSize(heapDescription.Type);
            var handle = _skyLookupDescriptors->GetCPUDescriptorHandleForHeapStart();
            var uav = new D3D12_UNORDERED_ACCESS_VIEW_DESC
            {
                Format = texture.Format, ViewDimension = D3D12_UAV_DIMENSION.D3D12_UAV_DIMENSION_TEXTURE2D,
            };
            _device->CreateUnorderedAccessView(_skyLookupRadiance, null, &uav, handle);
            handle.ptr += increment;
            _device->CreateUnorderedAccessView(_skyLookupTransmission, null, &uav, handle);
            handle.ptr += increment;
            var srv = new D3D12_SHADER_RESOURCE_VIEW_DESC
            {
                Format = texture.Format, ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D,
                Shader4ComponentMapping = 5768, // Identity component mapping.
                Texture2D = new() { MipLevels = 1 },
            };
            _device->CreateShaderResourceView(_skyLookupRadiance, &srv, handle);
            handle.ptr += increment;
            _device->CreateShaderResourceView(_skyLookupTransmission, &srv, handle);
            var shader = LoadShader("SkyLookup.cs.dxil");
            fixed (byte* code = shader)
            {
                var pipeline = new D3D12_COMPUTE_PIPELINE_STATE_DESC
                {
                    pRootSignature = _skyRoot, CS = new() { pShaderBytecode = code, BytecodeLength = (nuint)shader.Length },
                };
                fixed (ID3D12PipelineState** state = &_skyLookupCompute)
                    Check(_device->CreateComputePipelineState(&pipeline, __uuidof<ID3D12PipelineState>(), (void**)state),
                        "Create sky lookup compute pipeline");
            }
        }
        catch { ReleaseSkyLookup(); throw; }
    }

    private void BuildSkyLookup(ID3D12GraphicsCommandList* commands, in SkyConstants constants)
    {
        PrepareSkyLookup();
        ObserveUse(_skyLookupRadiance);
        ObserveUse(_skyLookupTransmission);
        ID3D12DescriptorHeap* heap = _skyLookupDescriptors;
        commands->SetDescriptorHeaps(1, &heap);
        commands->SetPipelineState(_skyLookupCompute);
        commands->SetComputeRootSignature(_skyRoot);
        fixed (SkyConstants* data = &constants)
            commands->SetComputeRoot32BitConstants(0, SkyConstantCount, data, 0);
        commands->SetComputeRootDescriptorTable(1, heap->GetGPUDescriptorHandleForHeapStart());
        commands->Dispatch(SkyLookupWidth / 8, SkyLookupHeight / 8, 1);
        SkyLookupDispatchesForTests++;
        // The transition orders all UAV writes before sampling. Every recording
        // restores UAV state after the draw, even when admission later rejects it.
        RecordTransition(commands, _skyLookupRadiance, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        RecordTransition(commands, _skyLookupTransmission, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
    }

    private D3D12_GPU_DESCRIPTOR_HANDLE SkyLookupSrv()
    {
        var handle = _skyLookupDescriptors->GetGPUDescriptorHandleForHeapStart();
        handle.ptr += 2UL * _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        return handle;
    }

    private void FinishSkyLookup(ID3D12GraphicsCommandList* commands)
    {
        RecordTransition(commands, _skyLookupRadiance, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        RecordTransition(commands, _skyLookupTransmission, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    }

    private void ReleaseSkyLookup()
    {
        Release(_skyLookupCompute);
        Release(_skyLookupDescriptors);
        ReleaseObservedResource(_skyLookupTransmission);
        ReleaseObservedResource(_skyLookupRadiance);
        _skyLookupCompute = null;
        _skyLookupDescriptors = null;
        _skyLookupTransmission = _skyLookupRadiance = null;
        SkyLookupAllocatedBytesForTests = 0;
    }
}
