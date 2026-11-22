using System.Numerics;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Passes;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    private const uint VertexCount = 36;
    private const uint VertexStride = 3 * sizeof(float) + sizeof(uint);
    private const uint NormalVertexStride = VertexStride + sizeof(uint);
    private const uint FrameConstantCount = 40;
    private const uint ShadowConstantCount = 20;
    // The shadow descriptor table takes the 64th DWORD of the PBR shadow root signature.
    private const uint PbrConstantCount = FrameConstantCount + ShadowConstantCount + 3;
    private const int PipelineOutOfMemory = unchecked((int)0x8007000E);
    private ID3D12PipelineState* _normalPipeline;
    private ID3D12RootSignature* _pbrRoot;
    private ID3D12RootSignature* _pbrShadowRoot;
    private ID3D12PipelineState* _pbrPipeline;
    private ID3D12PipelineState* _pbrShadowPipeline;
    private bool _pbrUnavailable;
    private bool _pbrShadowUnavailable;
    private ID3D12RootSignature* _terrainAppearanceRoot;
    private ID3D12PipelineState* _terrainAppearancePipeline;
    private ID3D12PipelineState* _terrainAppearanceShadowPipeline;
    private bool _terrainAppearanceUnavailable;
    private bool _terrainAppearanceShadowUnavailable;

    private void CreatePipeline()
    {
        _root = CreateFrameRoot(FrameConstantCount);
        _pipeline = CreateFramePipeline(_root, sky: false);
        _normalPipeline = CreateFramePipeline(_root, sky: false, normals: true);
    }

    private bool TryPrepareTerrainPbr(RenderScene? scene, bool shadows)
    {
        if (scene?.TerrainMaterial is null || ActiveMeshGeometry is not { HasPackedNormals: true } ||
            FindTexture(_frameTexture) is not null)
            return false;
        return shadows
            ? TryPreparePbrVariant(shadows, ref _pbrShadowRoot, ref _pbrShadowPipeline, ref _pbrShadowUnavailable)
            : TryPreparePbrVariant(shadows, ref _pbrRoot, ref _pbrPipeline, ref _pbrUnavailable);
    }

    private bool TryPrepareTerrainAppearance(bool shadows)
    {
        if (ActiveMeshGeometry is not { HasTerrainAppearance: true })
            return false;
        return shadows
            ? TryPreparePbrVariant(shadows, ref _terrainAppearanceRoot, ref _terrainAppearanceShadowPipeline,
                ref _terrainAppearanceShadowUnavailable, terrainAppearance: true)
            : TryPreparePbrVariant(shadows, ref _terrainAppearanceRoot, ref _terrainAppearancePipeline,
                ref _terrainAppearanceUnavailable, terrainAppearance: true);
    }

    private bool TryPreparePbrVariant(bool shadows, ref ID3D12RootSignature* root, ref ID3D12PipelineState* pipeline,
        ref bool unavailable, bool terrainAppearance = false)
    {
        if (pipeline != null)
            return true;
        if (unavailable)
            return false;

        ID3D12RootSignature* preparedRoot = null;
        try
        {
            if (terrainAppearance)
                EnsureFrameSrv();
            if (root == null)
                preparedRoot = terrainAppearance
                    ? CreateFrameRoot(FrameConstantCount + ShadowConstantCount, terrainAppearance: true)
                    : CreateFrameRoot(PbrConstantCount, shadow: shadows);
            if (!terrainAppearance)
                Check(_retirementHooks?.PbrPipelineCreationFailure?.Invoke(shadows) ?? 0, "Prepare terrain PBR pipeline");
            pipeline = CreateFramePipeline(preparedRoot == null ? root : preparedRoot, sky: false, normals: true,
                shadow: shadows, terrainPbr: true, terrainAppearance: terrainAppearance);
            if (preparedRoot != null)
            {
                root = preparedRoot;
                preparedRoot = null;
            }
            return true;
        }
        catch (GpuNativeException error) when ((error.HResult == PipelineOutOfMemory ||
            terrainAppearance && error.HResult == TextureUnsupported) && !IsRemoved)
        {
            // This optional appearance can fall back without consuming a terrain retry. Cache the
            // failed variant for this device owner so later material-only frames do not retry it.
            unavailable = true;
            return false;
        }
        finally
        {
            Release(preparedRoot);
        }
    }

    private void ReleasePbrPipeline()
    {
        Release(_pbrShadowPipeline);
        Release(_pbrPipeline);
        Release(_pbrShadowRoot);
        Release(_pbrRoot);
        _pbrShadowPipeline = _pbrPipeline = null;
        _pbrShadowRoot = _pbrRoot = null;
    }

    private void ReleaseTerrainAppearancePipelines()
    {
        Release(_terrainAppearanceShadowPipeline);
        Release(_terrainAppearancePipeline);
        Release(_terrainAppearanceRoot);
        _terrainAppearanceShadowPipeline = _terrainAppearancePipeline = null;
        _terrainAppearanceRoot = null;
    }

    private ID3D12RootSignature* CreateFrameRoot(uint count, bool shadow = false, bool props = false,
        bool texture = false, bool terrainAppearance = false)
    {
        var rootParameter = new D3D12_ROOT_PARAMETER
        {
            ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS,
            ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL,
        };
        rootParameter.Constants = new D3D12_ROOT_CONSTANTS { Num32BitValues = count };
        D3D12_ROOT_PARAMETER* parameters = stackalloc D3D12_ROOT_PARAMETER[3];
        parameters[0] = rootParameter;
        var range = new D3D12_DESCRIPTOR_RANGE
        {
            RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
            NumDescriptors = 2,
        };
        parameters[1] = new D3D12_ROOT_PARAMETER
        {
            ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
            ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL,
            DescriptorTable = new() { NumDescriptorRanges = 1, pDescriptorRanges = &range },
        };
        var textureRange = new D3D12_DESCRIPTOR_RANGE
        {
            RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
            NumDescriptors = 1,
            BaseShaderRegister = 4,
        };
        if (texture)
            parameters[2] = new D3D12_ROOT_PARAMETER
            {
                ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
                ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL,
                DescriptorTable = new() { NumDescriptorRanges = 1, pDescriptorRanges = &textureRange },
            };
        D3D12_DESCRIPTOR_RANGE* terrainRanges = stackalloc D3D12_DESCRIPTOR_RANGE[2];
        if (terrainAppearance)
        {
            terrainRanges[0] = range;
            terrainRanges[1] = textureRange with
            {
                NumDescriptors = MaxTextureSlots,
                OffsetInDescriptorsFromTableStart = FirstTextureDescriptor,
            };
            parameters[1].DescriptorTable = new() { NumDescriptorRanges = 2, pDescriptorRanges = terrainRanges };
            parameters[2] = new()
            {
                ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_CBV,
                ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL,
                Descriptor = new() { ShaderRegister = 1 },
            };
        }
        if (props)
        {
            for (uint i = 1; i <= 2; i++)
                parameters[i] = new D3D12_ROOT_PARAMETER
                {
                    ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_SRV,
                    ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_VERTEX,
                    Descriptor = new() { ShaderRegister = i + 1 },
                };
        }
        var shadowSampler = new D3D12_STATIC_SAMPLER_DESC
        {
            Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_POINT,
            AddressU = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressV = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            ComparisonFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_ALWAYS,
            MaxAnisotropy = 1,
            MaxLOD = float.MaxValue,
            ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL,
        };
        D3D12_STATIC_SAMPLER_DESC* samplers = stackalloc D3D12_STATIC_SAMPLER_DESC[2];
        samplers[0] = shadowSampler;
        samplers[1] = shadowSampler;
        samplers[1].ShaderRegister = 1;
        samplers[1].AddressU = samplers[1].AddressV = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_WRAP;
        samplers[1].MaxLOD = 0;
        var rootDescription = new D3D12_ROOT_SIGNATURE_DESC
        {
            NumParameters = props || texture || terrainAppearance ? 3u : shadow ? 2u : 1u,
            pParameters = parameters,
            NumStaticSamplers = texture || terrainAppearance ? 2u : shadow ? 1u : 0u,
            pStaticSamplers = texture || shadow || terrainAppearance ? samplers : null,
            Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT,
        };
        ID3DBlob* rootBlob = null;
        ID3DBlob* error = null;
        try
        {
            Check(SerializeRootSignature(&rootDescription, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1,
                &rootBlob, &error), "Serialize root signature");
            ID3D12RootSignature* root = null;
            var created = _device->CreateRootSignature(0, rootBlob->GetBufferPointer(), rootBlob->GetBufferSize(),
                __uuidof<ID3D12RootSignature>(), (void**)&root);
            if (created.Value < 0)
                Release(root);
            Check(created, "Create root signature");
            return root;
        }
        finally
        {
            Release(error);
            Release(rootBlob);
        }

    }

    private ID3D12PipelineState* CreateFramePipeline(ID3D12RootSignature* root, bool sky, bool normals = false,
        bool shadow = false, bool depthOnly = false, bool props = false, bool texture = false,
        bool terrainPbr = false, bool terrainAppearance = false, bool skyLookup = false, bool translucent = false)
    {
        var vertexShader = LoadShader(terrainAppearance ? "TerrainAppearance.vs.dxil" : props ? "Props.vs.dxil" :
            depthOnly ? "ShadowDepth.vs.dxil" : sky ? "Sky.vs.dxil" : normals ? "TerrainNormals.vs.dxil" : "Cube.vs.dxil");
        var pixelShader = depthOnly ? [] : LoadShader(sky ? skyLookup ? "SkyLookup.ps.dxil" : "Sky.ps.dxil" :
            terrainAppearance ? shadow ? "TerrainAppearanceShadow.ps.dxil" : "TerrainAppearance.ps.dxil" :
            terrainPbr ? shadow ? "PbrShadowTerrain.ps.dxil" : "PbrTerrain.ps.dxil" :
            texture ? shadow ? "TextureShadowTerrain.ps.dxil" : "TextureTerrain.ps.dxil" :
            shadow ? "ShadowTerrain.ps.dxil" : "Cube.ps.dxil");
        fixed (byte* vs = vertexShader, ps = pixelShader, position = "POSITION\0"u8, color = "COLOR\0"u8,
            normal = "NORMAL\0"u8, texcoord = "TEXCOORD\0"u8)
        {
            D3D12_INPUT_ELEMENT_DESC* elements = stackalloc D3D12_INPUT_ELEMENT_DESC[5]
            {
                new()
                {
                    SemanticName = (sbyte*)position,
                    Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
                },
                new()
                {
                    SemanticName = (sbyte*)color,
                    Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                    AlignedByteOffset = 3 * sizeof(float),
                },
                default,
                default,
                default,
            };
            if (terrainAppearance)
            {
                elements[3] = new()
                {
                    SemanticName = (sbyte*)texcoord,
                    Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
                    AlignedByteOffset = NormalVertexStride,
                };
                elements[4] = new()
                {
                    SemanticName = (sbyte*)texcoord,
                    SemanticIndex = 1,
                    Format = DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
                    AlignedByteOffset = NormalVertexStride + 3 * sizeof(float),
                };
            }
            elements[2] = new D3D12_INPUT_ELEMENT_DESC
            {
                SemanticName = (sbyte*)normal,
                Format = DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
                AlignedByteOffset = VertexStride,
            };
            var description = new D3D12_GRAPHICS_PIPELINE_STATE_DESC
            {
                pRootSignature = root,
                VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs, BytecodeLength = (nuint)vertexShader.Length },
                PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps, BytecodeLength = (nuint)pixelShader.Length },
                InputLayout = sky ? default : new D3D12_INPUT_LAYOUT_DESC
                {
                    pInputElementDescs = elements,
                    NumElements = terrainAppearance ? 5u : depthOnly ? 1u : normals ? 3u : 2u,
                },
                SampleMask = uint.MaxValue,
                RasterizerState = new D3D12_RASTERIZER_DESC
                {
                    FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID,
                    CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE,
                    DepthClipEnable = true,
                },
                DepthStencilState = new D3D12_DEPTH_STENCIL_DESC
                {
                    DepthEnable = true,
                    DepthWriteMask = sky || translucent ? D3D12_DEPTH_WRITE_MASK.D3D12_DEPTH_WRITE_MASK_ZERO : D3D12_DEPTH_WRITE_MASK.D3D12_DEPTH_WRITE_MASK_ALL,
                    DepthFunc = sky ? D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_LESS_EQUAL :
                        D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_LESS,
                },
                PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE,
                NumRenderTargets = depthOnly ? 0u : 1u,
                DSVFormat = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            };
            if (!depthOnly)
                description.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
            description.BlendState.RenderTarget[0] = new D3D12_RENDER_TARGET_BLEND_DESC
            {
                BlendEnable = translucent,
                SrcBlend = translucent ? D3D12_BLEND.D3D12_BLEND_BLEND_FACTOR : D3D12_BLEND.D3D12_BLEND_ONE,
                DestBlend = translucent ? D3D12_BLEND.D3D12_BLEND_INV_BLEND_FACTOR : D3D12_BLEND.D3D12_BLEND_ZERO,
                BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD,
                SrcBlendAlpha = translucent ? D3D12_BLEND.D3D12_BLEND_ZERO : D3D12_BLEND.D3D12_BLEND_ONE,
                DestBlendAlpha = translucent ? D3D12_BLEND.D3D12_BLEND_ONE : D3D12_BLEND.D3D12_BLEND_ZERO,
                BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD,
                LogicOp = D3D12_LOGIC_OP.D3D12_LOGIC_OP_NOOP,
                RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL,
            };
            ID3D12PipelineState* pipeline = null;
            var created = _device->CreateGraphicsPipelineState(&description,
                __uuidof<ID3D12PipelineState>(), (void**)&pipeline);
            if (created.Value < 0)
                Release(pipeline);
            Check(created, "Create graphics pipeline");
            return pipeline;
        }
    }

    private void CreateCubeGeometry()
    {
        _cubeGeometry = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
            Buffer(VertexCount * VertexStride), D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
        ObserveResource(_cubeGeometry, D3D12ResourceOwner.Terrain);
        var unread = new D3D12_RANGE();
        void* mapped = null;
        Check(_cubeGeometry->Map(0, &unread, &mapped), "Map cube vertices");
        try
        {
            ReadOnlySpan<byte> indices =
            [
                0, 1, 3, 0, 3, 2,
                4, 6, 7, 4, 7, 5,
                0, 4, 5, 0, 5, 1,
                2, 3, 7, 2, 7, 6,
                0, 2, 6, 0, 6, 4,
                1, 5, 7, 1, 7, 3,
            ];
            var vertices = new Span<GpuVertex>(mapped, (int)VertexCount);
            for (int index = 0; index < indices.Length; index++)
            {
                byte corner = indices[index];
                var position = new Vector3(
                    (corner & 1) == 0 ? -0.5f : 0.5f,
                    (corner & 2) == 0 ? -0.5f : 0.5f,
                    (corner & 4) == 0 ? -0.5f : 0.5f);
                vertices[index] = new GpuVertex(position, uint.MaxValue);
            }
        }
        finally
        {
            _cubeGeometry->Unmap(0, null);
        }
    }

    private static byte[] LoadShader(string name)
    {
        using var stream = typeof(GpuDevice).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded shader {name}.");
        var shader = new byte[checked((int)stream.Length)];
        stream.ReadExactly(shader);
        return shader;
    }

    private ID3D12DescriptorHeap* CreateDepthAttachmentHeap()
    {
        // Two CPU descriptors reference the same depth resource: normal writes
        // and the read-only background pass. No extra depth target is created.
        var description = new D3D12_DESCRIPTOR_HEAP_DESC
        {
            Type = D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_DSV,
            NumDescriptors = 2,
        };
        ID3D12DescriptorHeap* heap = null;
        Check(_device->CreateDescriptorHeap(&description, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap),
            "Create depth attachment descriptors");
        return heap;
    }

    private void CreateDepthAttachmentViews()
    {
        var view = _depthHeap->GetCPUDescriptorHandleForHeapStart();
        _device->CreateDepthStencilView(_depth, null, view);
        view.ptr += _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_DSV);
        var readOnly = new D3D12_DEPTH_STENCIL_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT,
            ViewDimension = D3D12_DSV_DIMENSION.D3D12_DSV_DIMENSION_TEXTURE2D,
            Flags = D3D12_DSV_FLAGS.D3D12_DSV_FLAG_READ_ONLY_DEPTH,
        };
        _device->CreateDepthStencilView(_depth, &readOnly, view);
    }

    private void Record(ID3D12GraphicsCommandList* commands, GpuTarget target, in RenderFrame frame,
        RenderScene? subsequent, Readback? readback, in ShadowWork shadows, ValidatedRenderPlan plan,
        bool terrainPbr, bool terrainAppearance, out ShadowTimingLayout timingLayout)
    {
        timingLayout = default;
        DeferredPropDraw? deferredProps = null;
        var shadowTiming = _terrainTiming is { MeasuresShadows: true } timing ? timing : null;
        ResetSkyRecordMeasurementForTests();
        shadowTiming?.Stamp(commands, ref timingLayout);
        var rtv = target.Rtv;
        var dsv = _depthHeap->GetCPUDescriptorHandleForHeapStart();
        var hasTransparentPass = frame.Sky.HasValue && PropStream is { HasTransparentAssets: true };
        _resourceDiagnostics?.BeginRecording();
        foreach (var pass in plan.Passes.AsSpan())
        {
            _resourceDiagnostics?.BeginPass(pass.Id);
            if (pass.Id == GpuPassPlan.Clear)
            {
                ObserveUse(target.Buffer);
                ObserveUse(_depth);
                RecordTransition(commands, target.Buffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
                var clear = frame.ClearColor;
                commands->ClearRenderTargetView(rtv, (float*)&clear, 0, null);
                commands->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAGS.D3D12_CLEAR_FLAG_DEPTH, 1, 0, 0, null);
            }
            else if (pass.Id == GpuPassPlan.NearShadow || pass.Id == GpuPassPlan.FarShadow)
            {
                var near = pass.Id == GpuPassPlan.NearShadow;
                if (shadowTiming is not null)
                {
                    var start = shadowTiming.Stamp(commands, ref timingLayout);
                    if (near)
                        timingLayout.NearStart = start;
                    else
                        timingLayout.FarStart = start;
                }
                RecordShadowCascade(commands, shadows, near ? 0U : 1U);
                shadowTiming?.Stamp(commands, ref timingLayout);
            }
            else if (pass.Id == GpuPassPlan.Sky)
            {
                if (shadowTiming is { MeasuresSky: true })
                    timingLayout.SkyStart = shadowTiming.Stamp(commands, ref timingLayout);
                ObserveUse(target.Buffer);
                ObserveUse(_depth);
                _retirementHooks?.BeforeSkyRecord?.Invoke();
                BeginSkyRecordMeasurementForTests();
                RecordTransition(commands, _depth, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_READ);
                var readOnlyDsv = dsv;
                readOnlyDsv.ptr += _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_DSV);
                BindFrameTargets(commands, target, rtv, readOnlyDsv);
                DrawSky(commands, target, frame.Sky!.Value, ref timingLayout);
                RestoreWritableDepth(commands);
                EndSkyRecordMeasurementForTests();
                _retirementHooks?.AfterSkyRecord?.Invoke();
                if (shadowTiming is { MeasuresSky: true })
                    timingLayout.SkyEnd = shadowTiming.Stamp(commands, ref timingLayout);
            }
            else if (pass.Id == GpuPassPlan.Terrain)
            {
                BindFrameTargets(commands, target, rtv, dsv);
                _terrainTiming?.BeginTerrain(commands, ref timingLayout);
                RecordTerrain(commands, target, frame.Scene!.Value, shadows.Constants, terrainPbr, terrainAppearance);
                _terrainTiming?.EndTerrain(commands, ref timingLayout);
            }
            else if (pass.Id == GpuPassPlan.Props)
                deferredProps = DrawProps(commands, target, frame.Scene!.Value, frame.Sequence, hasTransparentPass);
            else if (pass.Id == GpuPassPlan.TransparentProps && deferredProps is { } transparent)
            {
                RecordTransition(commands, _depth, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_READ);
                var readOnlyDsv = dsv;
                readOnlyDsv.ptr += _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_DSV);
                BindFrameTargets(commands, target, rtv, readOnlyDsv);
                DrawDeferredProps(commands, target, transparent);
                RestoreWritableDepth(commands);
            }
            else if (pass.Id == GpuPassPlan.FollowOn)
                Draw(commands, target, subsequent!.Value);
            else if (pass.Id == GpuPassPlan.Copy)
            {
                // End the frame sample before the color/depth transfer for CPU readback.
                shadowTiming?.EndFrame(commands, ref timingLayout);
                readback!.Record(commands, target.Buffer, _depth);
            }
            else if (pass.Id == GpuPassPlan.Present)
            {
                ObserveUse(target.Buffer);
                if (readback is null)
                    RecordTransition(commands, target.Buffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
                        D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
            }
            else if (pass.Id != GpuPassPlan.Readback && pass.Id != GpuPassPlan.TransparentProps)
                throw new InvalidOperationException("The validated pass has no fixed-route recorder.");
            // DXGI Present is issued by the owner. CPU readback consumes only retired work.
        }
        if (shadowTiming is not null && timingLayout.FrameEnd == 0)
            shadowTiming.EndFrame(commands, ref timingLayout);
    }

    private void RestoreWritableDepth(ID3D12GraphicsCommandList* commands) =>
        RecordTransition(commands, _depth, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_READ,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE);

    private static void BindFrameTargets(ID3D12GraphicsCommandList* commands, GpuTarget target,
        D3D12_CPU_DESCRIPTOR_HANDLE rtv, D3D12_CPU_DESCRIPTOR_HANDLE dsv)
    {
        var viewport = new D3D12_VIEWPORT { Width = target.Width, Height = target.Height, MaxDepth = 1 };
        var scissor = new TerraFX.Interop.Windows.RECT { right = target.Width, bottom = target.Height };
        commands->RSSetViewports(1, &viewport);
        commands->RSSetScissorRects(1, &scissor);
        commands->OMSetRenderTargets(1, &rtv, false, &dsv);
        commands->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    }

    private void RecordTerrain(ID3D12GraphicsCommandList* commands, GpuTarget target, in RenderScene scene,
        in ShadowConstants shadows, bool terrainPbr, bool terrainAppearance) => Draw(commands, target, scene, shadows,
        terrainAppearance ? null : FindTexture(_frameTexture), terrainPbr ? scene.TerrainMaterial : null, terrainAppearance);

    private void Draw(ID3D12GraphicsCommandList* commands, GpuTarget target, in RenderScene scene,
        ShadowConstants shadows = default, TextureEntry? texture = null, PbrMaterialDescriptor? material = null,
        bool terrainAppearance = false)
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(scene.VerticalFieldOfView,
            (float)target.Width / target.Height, scene.NearPlane, scene.FarPlane);
        var fogColorOpacity = Vector4.Zero;
        var fogDepth = Vector4.Zero;
        if (scene.Fog is { MaxOpacity: > 0 } fog)
        {
            fogColorOpacity = new(fog.Color, fog.MaxOpacity);
            fogDepth = GetFogDepthConstants(fog);
        }
        var sunDirection = Vector4.Zero;
        var sunDirect = Vector4.Zero;
        var sunAmbient = Vector4.Zero;
        if (scene.Sunlight is { } sun)
        {
            sunDirection = new(Vector3.Normalize(sun.Direction), 1);
            sunDirect = new(sun.Direct, 0);
            sunAmbient = new(sun.Ambient, 0);
        }
        var constants = new DrawConstants(scene.World * scene.View * projection, scene.Tint, fogColorOpacity,
            fogDepth, sunDirection, sunDirect, sunAmbient);
        var meshes = ActiveMeshGeometry;
        ID3D12PipelineState* materialPipeline = null;
        if (terrainAppearance)
        {
            materialPipeline = BindTerrainAppearance(commands, scene.View, constants, shadows);
        }
        else if (material is not null && meshes is { HasPackedNormals: true } && texture is null)
        {
            materialPipeline = BindTerrainPbr(commands, scene.View, constants, shadows, material);
        }
        else if (texture is { Ready: true })
        {
            commands->SetGraphicsRootSignature(_textureRoot);
            var heap = _shadowSrv;
            commands->SetDescriptorHeaps(1, &heap);
            commands->SetGraphicsRootDescriptorTable(1, heap->GetGPUDescriptorHandleForHeapStart());
            commands->SetGraphicsRootDescriptorTable(2, TextureGpuDescriptor(texture.Identity.Slot));
            commands->SetGraphicsRoot32BitConstants(0, FrameConstantCount, &constants, 0);
            if (shadows.Enabled)
                commands->SetGraphicsRoot32BitConstants(0, ShadowConstantCount, &shadows, FrameConstantCount);
        }
        else if (shadows.Enabled)
        {
            commands->SetGraphicsRootSignature(_shadowColorRoot);
            var heap = _shadowSrv;
            commands->SetDescriptorHeaps(1, &heap);
            commands->SetGraphicsRootDescriptorTable(1, heap->GetGPUDescriptorHandleForHeapStart());
            commands->SetGraphicsRoot32BitConstants(0, FrameConstantCount, &constants, 0);
            commands->SetGraphicsRoot32BitConstants(0, ShadowConstantCount, &shadows, FrameConstantCount);
        }
        else
        {
            commands->SetGraphicsRootSignature(_root);
            commands->SetGraphicsRoot32BitConstants(0, FrameConstantCount, &constants, 0);
        }
        if (meshes is not null)
        {
            if (!meshes.Draw(commands, shadows.Enabled, texture is not null, materialPipeline))
                return;
        }
        else
        {
            var vertices = new D3D12_VERTEX_BUFFER_VIEW
            {
                BufferLocation = _cubeGeometry->GetGPUVirtualAddress(),
                SizeInBytes = VertexCount * VertexStride,
                StrideInBytes = VertexStride,
            };
            commands->IASetVertexBuffers(0, 1, &vertices);
            commands->SetPipelineState(texture is null ? _pipeline : _texturePipeline);
            commands->DrawInstanced(VertexCount, 1, 0, 0);
        }
        if (_resourceDiagnostics is not null)
        {
            ObserveUse(target.Buffer);
            ObserveUse(_depth);
            ObserveUse(ActiveMeshGeometry is { } mesh ? mesh.Resource : _cubeGeometry);
            if (terrainAppearance)
                ObserveFrameTerrainMaterials();
            else if (texture is not null)
                ObserveUse(texture.Resource);
            if (shadows.Near.W != 0)
                ObserveUse(_nearShadow);
            if (shadows.Far.W != 0)
                ObserveUse(_farShadow);
        }
    }

    private ID3D12PipelineState* BindTerrainAppearance(ID3D12GraphicsCommandList* commands, in Matrix4x4 view,
        in DrawConstants lighting, ShadowConstants shadows)
    {
        commands->SetGraphicsRootSignature(_terrainAppearanceRoot);
        var heap = _shadowSrv;
        commands->SetDescriptorHeaps(1, &heap);
        commands->SetGraphicsRootDescriptorTable(1, heap->GetGPUDescriptorHandleForHeapStart());
        commands->SetGraphicsRootConstantBufferView(2, FrameTerrainMaterialAddress);

        // This shader reads the frame's half-scaled world eye from the unused tint register.
        var constants = lighting with { Tint = new(HalfEye(view), 0) };
        commands->SetGraphicsRoot32BitConstants(0, FrameConstantCount, &constants, 0);
        if (shadows.Enabled)
            commands->SetGraphicsRoot32BitConstants(0, ShadowConstantCount, &shadows, FrameConstantCount);
        return shadows.Enabled ? _terrainAppearanceShadowPipeline : _terrainAppearancePipeline;
    }

    private ID3D12PipelineState* BindTerrainPbr(ID3D12GraphicsCommandList* commands, in Matrix4x4 view,
        in DrawConstants lighting, in ShadowConstants shadows, PbrMaterialDescriptor material)
    {
        commands->SetGraphicsRootSignature(shadows.Enabled ? _pbrShadowRoot : _pbrRoot);
        if (shadows.Enabled)
        {
            var heap = _shadowSrv;
            commands->SetDescriptorHeaps(1, &heap);
            commands->SetGraphicsRootDescriptorTable(1, heap->GetGPUDescriptorHandleForHeapStart());
        }

        // Match the TERRAIN_PBR cbuffer layout. Reuse tint and padding lanes so the existing
        // shadow data still fits in root constants, without any upload buffers or new descriptors.
        var constants = lighting with
        {
            Tint = new(material.BaseColor, material.Metallic),
            FogDepth = lighting.FogDepth with { W = material.AmbientOcclusion },
            SunDirection = lighting.SunDirection with { W = material.Roughness },
            SunDirect = lighting.SunDirect with { W = material.Emissive.X },
            SunAmbient = lighting.SunAmbient with { W = material.Emissive.Y },
        };
        var shadowData = shadows with { LightZ = shadows.LightZ with { W = material.Emissive.Z } };
        var halfEye = HalfEye(view);
        commands->SetGraphicsRoot32BitConstants(0, FrameConstantCount, &constants, 0);
        commands->SetGraphicsRoot32BitConstants(0, ShadowConstantCount, &shadowData, FrameConstantCount);
        commands->SetGraphicsRoot32BitConstants(0, 3, &halfEye, FrameConstantCount + ShadowConstantCount);
        return shadows.Enabled ? _pbrShadowPipeline : _pbrPipeline;
    }

    // Rigid row-vector view: eye = -translation * transpose(rotation).
    // Half-scale both endpoints before subtraction in the shader to avoid overflow.
    private static Vector3 HalfEye(in Matrix4x4 view) =>
        Vector3.TransformNormal(-0.5f * view.Translation, Matrix4x4.Transpose(view));

    private ID3D12PipelineState* TerrainPipeline(bool shadows, bool normals, bool textured)
    {
        if (textured)
            return shadows
                ? normals ? _textureShadowNormalPipeline : _textureShadowPipeline
                : normals ? _textureNormalPipeline : _texturePipeline;
        return shadows
            ? normals ? _shadowNormalPipeline : _shadowColorPipeline
            : normals ? _normalPipeline : _pipeline;
    }

    internal static Vector4 GetFogDepthConstants(in RenderFog fog)
    {
        var span = fog.EndDepthMeters - fog.StartDepthMeters;
        // Direct3D flushes subnormal floats to zero. Scale both depths before subtracting.
        var scale = float.IsSubnormal(1f / span) ? 0.25f : 1f;
        return new(fog.StartDepthMeters * scale, 1f / (span * scale), scale, 0);
    }

    private readonly record struct DrawConstants(Matrix4x4 WorldViewProjection, Vector4 Tint,
        Vector4 FogColorOpacity, Vector4 FogDepth, Vector4 SunDirection, Vector4 SunDirect, Vector4 SunAmbient);
    private readonly record struct GpuVertex(Vector3 Position, uint Color);
}
