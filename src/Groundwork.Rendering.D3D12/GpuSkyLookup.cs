using System.Diagnostics;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    // Diagnostic prototype only. Two fixed RGBA32F view-ray fields share one
    // array. Preparation rejects native allocations above 1 MiB.
    internal const ulong SkyLookupStorageLimit = 1024 * 1024;
    private const int SkyLookupWidth = 256;
    private const int SkyLookupHeight = 128;
    private const ushort SkyLookupFields = 2;
    private ID3D12Resource* _skyLookupFields;
    private ID3D12DescriptorHeap* _skyLookupDescriptors;
    private ID3D12PipelineState* _skyLookupCompute;
    private bool _skyLookupFallbackMaskForTests;
    private bool _skyLookupPrepared;
    internal bool SkyLookupPrototypeEnabledForTests { get; }
    internal bool SkyLookupResourceReuseEnabledForTests { get; }
    internal int SkyLookupWidthForTests => SkyLookupPrototypeEnabledForTests ? SkyLookupWidth : 0;
    internal int SkyLookupHeightForTests => SkyLookupPrototypeEnabledForTests ? SkyLookupHeight : 0;
    internal ulong SkyLookupAllocatedBytesForTests { get; private set; }
    internal ulong SkyLookupDispatchesForTests { get; private set; }
    internal (nint Root, nint Graphics, nint Compute) SkyLookupPipelineHandlesForTests =>
        ((nint)_skyRoot, (nint)_skyPipeline, (nint)_skyLookupCompute);
    internal (nint Fields, nint Views) SkyLookupStorageHandlesForTests => ((nint)_skyLookupFields, (nint)_skyLookupDescriptors);

    internal void SetSkyLookupFallbackMaskForTests(bool enabled)
    {
        if (!SkyLookupPrototypeEnabledForTests || !IsIdle)
            throw new InvalidOperationException("Lookup fallback capture requires an idle prototype device.");
        _skyLookupFallbackMaskForTests = enabled;
    }

    private ID3D12RootSignature* CreateSkyLookupRoot()
    {
        D3D12_DESCRIPTOR_RANGE* ranges = stackalloc D3D12_DESCRIPTOR_RANGE[2];
        ranges[0] = new() { RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_UAV, NumDescriptors = 1 };
        ranges[1] = new() { RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV, NumDescriptors = 1 };
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
            AddressU = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
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
        if (_skyLookupPrepared)
            return;
        var observed = _retirementHooks?.AfterSkyLookupPreparationStage;
        var preparationStarted = observed is null ? 0 : Stopwatch.GetTimestamp();
        // Partial preparation is owned immediately and released on any failure.
        try
        {
            if (SkyLookupResourceReuseEnabledForTests && _session is { } session &&
                session.TryTakeSkyLookup(_device, out _skyLookupFields, out _skyLookupDescriptors, out var bytes))
            {
                ObserveResource(_skyLookupFields, D3D12ResourceOwner.Texture, bytes);
                SkyLookupAllocatedBytesForTests = bytes;
                observed?.Invoke("resources-reused", preparationStarted, Stopwatch.GetTimestamp(), true);
            }
            else
                CreateSkyLookupStorage(observed, preparationStarted);
            var computeStarted = observed is null ? 0 : Stopwatch.GetTimestamp();
            var reusedCompute = _skyLookupCompute != null;
            if (!reusedCompute)
            {
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
            observed?.Invoke("compute-pipeline", computeStarted, Stopwatch.GetTimestamp(), reusedCompute);
            // Publish only after all three immutable objects exist.
            _session?.RetainSkyLookupPipelines(_skyRoot, _skyPipeline, _skyLookupCompute);
            _skyLookupPrepared = true;
            observed?.Invoke("ready", preparationStarted, Stopwatch.GetTimestamp(), reusedCompute);
        }
        catch { ReleaseSkyLookup(); throw; }
    }

    private void CreateSkyLookupStorage(Action<string, long, long, bool>? observed, long preparationStarted)
    {
        var texture = Texture(SkyLookupWidth, SkyLookupHeight, DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
            D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        texture.DepthOrArraySize = SkyLookupFields;
        var allocation = _device->GetResourceAllocationInfo(1, 1, &texture).SizeInBytes;
        if (allocation is 0 or > SkyLookupStorageLimit)
            throw new InvalidOperationException("Sky lookup native allocation exceeds its fixed two-owner resource budget.");
        _skyLookupFields = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, texture,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        ObserveResource(_skyLookupFields, D3D12ResourceOwner.Texture, allocation);
        SkyLookupAllocatedBytesForTests = allocation;
        _retirementHooks?.AfterSkyLookupFirstAllocation?.Invoke();
        observed?.Invoke("textures", preparationStarted, Stopwatch.GetTimestamp(), false);
        var descriptorsStarted = observed is null ? 0 : Stopwatch.GetTimestamp();
        var heapDescription = new D3D12_DESCRIPTOR_HEAP_DESC
        {
            Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV,
            NumDescriptors = 2, Flags = D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE,
        };
        fixed (ID3D12DescriptorHeap** heap = &_skyLookupDescriptors)
            Check(_device->CreateDescriptorHeap(&heapDescription, __uuidof<ID3D12DescriptorHeap>(), (void**)heap),
                "Create sky lookup descriptors");
        var increment = _device->GetDescriptorHandleIncrementSize(heapDescription.Type);
        var handle = _skyLookupDescriptors->GetCPUDescriptorHandleForHeapStart();
        var uav = new D3D12_UNORDERED_ACCESS_VIEW_DESC
        {
            Format = texture.Format, ViewDimension = D3D12_UAV_DIMENSION.D3D12_UAV_DIMENSION_TEXTURE2DARRAY,
            Texture2DArray = new() { ArraySize = SkyLookupFields },
        };
        _device->CreateUnorderedAccessView(_skyLookupFields, null, &uav, handle);
        handle.ptr += increment;
        var srv = new D3D12_SHADER_RESOURCE_VIEW_DESC
        {
            Format = texture.Format, ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2DARRAY,
            Shader4ComponentMapping = 5768, // Identity component mapping.
            Texture2DArray = new() { ArraySize = SkyLookupFields, MipLevels = 1 },
        };
        _device->CreateShaderResourceView(_skyLookupFields, &srv, handle);
        observed?.Invoke("descriptors", descriptorsStarted, Stopwatch.GetTimestamp(), false);
    }

    private ulong TryParkSkyLookupStorage()
    {
        if (!_skyLookupPrepared || _session is not { } session)
            return 0;
        var stamp = session.TryParkSkyLookup(_device, _skyLookupFields, _skyLookupDescriptors, SkyLookupAllocatedBytesForTests);
        if (stamp == 0)
            return 0;
        var fields = (nint)_skyLookupFields;
        _skyLookupFields = null;
        _skyLookupDescriptors = null;
        SkyLookupAllocatedBytesForTests = 0;
        _resourceDiagnostics?.ForgetTransferred(fields);
        return stamp;
    }

    private void BuildSkyLookup(ID3D12GraphicsCommandList* commands, in SkyConstants constants,
        ref ShadowTimingLayout timingLayout)
    {
        PrepareSkyLookup();
        ObserveUse(_skyLookupFields);
        ID3D12DescriptorHeap* heap = _skyLookupDescriptors;
        commands->SetDescriptorHeaps(1, &heap);
        commands->SetPipelineState(_skyLookupCompute);
        commands->SetComputeRootSignature(_skyRoot);
        fixed (SkyConstants* data = &constants)
            commands->SetComputeRoot32BitConstants(0, SkyConstantCount, data, 0);
        commands->SetComputeRootDescriptorTable(1, heap->GetGPUDescriptorHandleForHeapStart());
        commands->Dispatch(SkyLookupWidth / 8, SkyLookupHeight / 8, 1);
        if (_retirementHooks?.AfterSkyLookupGpuTimingRetired is not null && _terrainTiming is { MeasuresSky: true } timing)
            timingLayout.SkyLookupComputeEnd = timing.Stamp(commands, ref timingLayout);
        MarkSkyLookupRecordSplitForTests();
        SkyLookupDispatchesForTests++;
        // The transition orders all UAV writes before sampling. Every recording
        // restores UAV state after the draw, even when admission later rejects it.
        RecordTransition(commands, _skyLookupFields, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
    }

    private D3D12_GPU_DESCRIPTOR_HANDLE SkyLookupSrv()
    {
        var handle = _skyLookupDescriptors->GetGPUDescriptorHandleForHeapStart();
        handle.ptr += _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        return handle;
    }

    private void FinishSkyLookup(ID3D12GraphicsCommandList* commands)
    {
        RecordTransition(commands, _skyLookupFields, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    }

    private void ReleaseSkyLookup()
    {
        Release(_skyLookupCompute);
        Release(_skyLookupDescriptors);
        ReleaseObservedResource(_skyLookupFields);
        _skyLookupCompute = null;
        _skyLookupDescriptors = null;
        _skyLookupFields = null;
        SkyLookupAllocatedBytesForTests = 0;
        _skyLookupPrepared = false;
    }
}
