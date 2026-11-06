using System.Numerics;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    private const uint VertexCount = 36;
    private const uint VertexStride = 3 * sizeof(float);

    private void CreatePipeline()
    {
        var rootParameter = new D3D12_ROOT_PARAMETER
        {
            ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS,
            ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_ALL,
        };
        rootParameter.Constants = new D3D12_ROOT_CONSTANTS { Num32BitValues = 20 };
        var rootDescription = new D3D12_ROOT_SIGNATURE_DESC
        {
            NumParameters = 1,
            pParameters = &rootParameter,
            Flags = D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT,
        };
        ID3DBlob* rootBlob = null;
        ID3DBlob* error = null;
        try
        {
            Check(SerializeRootSignature(&rootDescription, D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1,
                &rootBlob, &error), "Serialize root signature");
            fixed (ID3D12RootSignature** root = &_root)
                Check(_device->CreateRootSignature(0, rootBlob->GetBufferPointer(), rootBlob->GetBufferSize(),
                    __uuidof<ID3D12RootSignature>(), (void**)root), "Create root signature");
        }
        finally
        {
            Release(error);
            Release(rootBlob);
        }

        var vertexShader = LoadShader("Cube.vs.dxil");
        var pixelShader = LoadShader("Cube.ps.dxil");
        fixed (byte* vs = vertexShader, ps = pixelShader, position = "POSITION\0"u8)
        {
            var element = new D3D12_INPUT_ELEMENT_DESC
            {
                SemanticName = (sbyte*)position,
                Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
            };
            var description = new D3D12_GRAPHICS_PIPELINE_STATE_DESC
            {
                pRootSignature = _root,
                VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vs, BytecodeLength = (nuint)vertexShader.Length },
                PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = ps, BytecodeLength = (nuint)pixelShader.Length },
                InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = &element, NumElements = 1 },
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
                    DepthWriteMask = D3D12_DEPTH_WRITE_MASK.D3D12_DEPTH_WRITE_MASK_ALL,
                    DepthFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_LESS,
                },
                PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE,
                NumRenderTargets = 1,
                DSVFormat = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            };
            description.RTVFormats[0] = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
            description.BlendState.RenderTarget[0] = new D3D12_RENDER_TARGET_BLEND_DESC
            {
                SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE,
                DestBlend = D3D12_BLEND.D3D12_BLEND_ZERO,
                BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD,
                SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE,
                DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_ZERO,
                BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD,
                LogicOp = D3D12_LOGIC_OP.D3D12_LOGIC_OP_NOOP,
                RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL,
            };
            fixed (ID3D12PipelineState** pipeline = &_pipeline)
                Check(_device->CreateGraphicsPipelineState(&description,
                    __uuidof<ID3D12PipelineState>(), (void**)pipeline), "Create graphics pipeline");
        }
    }

    private void CreateCubeGeometry()
    {
        _geometry = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
            Buffer(VertexCount * VertexStride), D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
        var unread = new D3D12_RANGE();
        void* mapped = null;
        Check(_geometry->Map(0, &unread, &mapped), "Map cube vertices");
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
            var vertices = new Span<Vector3>(mapped, (int)VertexCount);
            for (int index = 0; index < indices.Length; index++)
            {
                byte corner = indices[index];
                vertices[index] = new(
                    (corner & 1) == 0 ? -0.5f : 0.5f,
                    (corner & 2) == 0 ? -0.5f : 0.5f,
                    (corner & 4) == 0 ? -0.5f : 0.5f);
            }
        }
        finally
        {
            _geometry->Unmap(0, null);
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

    private void Record(ID3D12GraphicsCommandList* commands, GpuTarget target, in RenderFrame frame,
        RenderScene? subsequent, Readback? readback)
    {
        Transition(commands, target.Buffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET);
        var rtv = target.Rtv;
        var dsv = _depthHeap->GetCPUDescriptorHandleForHeapStart();
        var clear = frame.ClearColor;
        commands->ClearRenderTargetView(rtv, (float*)&clear, 0, null);
        commands->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAGS.D3D12_CLEAR_FLAG_DEPTH, 1, 0, 0, null);
        if (frame.Scene is { } scene)
        {
            var viewport = new D3D12_VIEWPORT { Width = target.Width, Height = target.Height, MaxDepth = 1 };
            var scissor = new TerraFX.Interop.Windows.RECT { right = target.Width, bottom = target.Height };
            commands->RSSetViewports(1, &viewport);
            commands->RSSetScissorRects(1, &scissor);
            commands->OMSetRenderTargets(1, &rtv, false, &dsv);
            commands->SetGraphicsRootSignature(_root);
            commands->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            if (_meshGeometry is null)
            {
                var vertices = new D3D12_VERTEX_BUFFER_VIEW
                {
                    BufferLocation = _geometry->GetGPUVirtualAddress(),
                    SizeInBytes = VertexCount * VertexStride,
                    StrideInBytes = VertexStride,
                };
                commands->IASetVertexBuffers(0, 1, &vertices);
            }
            Draw(commands, target, scene);
            if (subsequent is { } next)
                Draw(commands, target, next);
        }

        if (readback is not null)
            readback.Record(commands, target.Buffer, _depth);
        else
            Transition(commands, target.Buffer, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
    }

    private void Draw(ID3D12GraphicsCommandList* commands, GpuTarget target, in RenderScene scene)
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(scene.VerticalFieldOfView,
            (float)target.Width / target.Height, scene.NearPlane, scene.FarPlane);
        var constants = new DrawConstants(scene.World * scene.View * projection, scene.Tint);
        commands->SetGraphicsRoot32BitConstants(0, 20, &constants, 0);
        if (_meshGeometry is { } meshes)
        {
            DrawMesh(commands, meshes.First);
            DrawMesh(commands, meshes.Second);
        }
        else
        {
            commands->DrawInstanced(VertexCount, 1, 0, 0);
        }
    }

    private readonly record struct DrawConstants(Matrix4x4 WorldViewProjection, Vector4 Tint);
}
