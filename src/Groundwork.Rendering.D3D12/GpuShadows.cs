using System.Numerics;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    private const int ShadowSize = 1024;
    private ID3D12DescriptorHeap* _shadowDsv;
    private ID3D12DescriptorHeap* _shadowSrv;
    private ID3D12Resource* _nearShadow;
    private ID3D12Resource* _farShadow;
    private ID3D12RootSignature* _shadowDepthRoot;
    private ID3D12RootSignature* _shadowColorRoot;
    private ID3D12PipelineState* _shadowDepthPipeline;
    private ID3D12PipelineState* _shadowColorPipeline;
    private ID3D12PipelineState* _shadowNormalPipeline;
    private bool _shadowReady;

    private void InitializeShadows()
    {
        if (_shadowReady)
            return;
        _shadowDsv = CreateShadowHeap(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_DSV, false);
        _shadowSrv = CreateShadowHeap(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, true);
        var desc = Texture(ShadowSize, ShadowSize, DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS,
            D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL);
        if (_device->GetResourceAllocationInfo(1, 1, &desc).SizeInBytes > 8 * 1024 * 1024)
            throw new InvalidOperationException("The shadow depth allocation exceeds the WP15 budget.");
        _nearShadow = CreateMap(0);
        _farShadow = CreateMap(1);
        _shadowDepthRoot = CreateFrameRoot(16);
        _shadowColorRoot = CreateFrameRoot(60, shadow: true);
        _shadowDepthPipeline = CreateFramePipeline(_shadowDepthRoot, sky: false, depthOnly: true);
        _shadowColorPipeline = CreateFramePipeline(_shadowColorRoot, sky: false, shadow: true);
        _shadowNormalPipeline = CreateFramePipeline(_shadowColorRoot, sky: false, normals: true, shadow: true);
        _shadowReady = true;

        ID3D12Resource* CreateMap(uint index)
        {
            var texture = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
                Texture(ShadowSize, ShadowSize, DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS,
                    D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL),
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
            var dsv = new D3D12_DEPTH_STENCIL_VIEW_DESC
            {
                Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT,
                ViewDimension = D3D12_DSV_DIMENSION.D3D12_DSV_DIMENSION_TEXTURE2D,
            };
            _device->CreateDepthStencilView(texture, &dsv, ShadowDsv(index));
            var srv = new D3D12_SHADER_RESOURCE_VIEW_DESC
            {
                Format = DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
                ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D,
                Shader4ComponentMapping = 5768, // Identity component mapping.
            };
            srv.Texture2D.MipLevels = 1;
            var handle = _shadowSrv->GetCPUDescriptorHandleForHeapStart();
            handle.ptr += index * _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
            _device->CreateShaderResourceView(texture, &srv, handle);
            return texture;
        }
    }

    private ID3D12DescriptorHeap* CreateShadowHeap(D3D12_DESCRIPTOR_HEAP_TYPE type, bool visible)
    {
        var desc = new D3D12_DESCRIPTOR_HEAP_DESC { Type = type, NumDescriptors = 2,
            Flags = visible ? D3D12_DESCRIPTOR_HEAP_FLAGS.D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE : 0 };
        ID3D12DescriptorHeap* heap = null;
        Check(_device->CreateDescriptorHeap(&desc, __uuidof<ID3D12DescriptorHeap>(), (void**)&heap), "Create shadow descriptor heap");
        return heap;
    }

    private D3D12_CPU_DESCRIPTOR_HANDLE ShadowDsv(uint index)
    {
        var handle = _shadowDsv->GetCPUDescriptorHandleForHeapStart();
        handle.ptr += index * _device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_DSV);
        return handle;
    }

    private ShadowConstants RecordShadows(ID3D12GraphicsCommandList* commands, GpuTarget target, in RenderScene scene)
    {
        if (scene.ShadowOrigin is null || scene.Sunlight is not { } sun || sun.Direct == Vector3.Zero ||
            ActiveMeshGeometry is not { } mesh)
            return default;
        var basis = ShadowBasis.Create(sun.Direction);
        var aspect = (float)target.Width / target.Height;
        var near = basis.Fit(scene, aspect, 0, 26);
        var far = basis.Fit(scene, aspect, 22, 64);
        var (min, max) = mesh.ShadowDepth(basis, near, far);
        var range = max - min + 2;
        if (!float.IsFinite(range))
            return default;

        InitializeShadows();
        var inverseDepth = 1 / range;
        min -= 1;
        DrawCascade(near, _nearShadow, 0);
        DrawCascade(far, _farShadow, 1);
        return new(new(basis.X, min), new(basis.Y, inverseDepth), new(basis.Z, 0), near.Area, far.Area);

        void DrawCascade(ShadowCascade cascade, ID3D12Resource* map, uint index)
        {
            if (!cascade.Valid)
                return;
            Transition(commands, map, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE);
            var handle = ShadowDsv(index);
            commands->ClearDepthStencilView(handle, D3D12_CLEAR_FLAGS.D3D12_CLEAR_FLAG_DEPTH, 1, 0, 0, null);
            var viewport = new D3D12_VIEWPORT { Width = ShadowSize, Height = ShadowSize, MaxDepth = 1 };
            var scissor = new TerraFX.Interop.Windows.RECT { right = ShadowSize, bottom = ShadowSize };
            commands->RSSetViewports(1, &viewport);
            commands->RSSetScissorRects(1, &scissor);
            commands->OMSetRenderTargets(0, null, false, &handle);
            commands->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            commands->SetPipelineState(_shadowDepthPipeline);
            commands->SetGraphicsRootSignature(_shadowDepthRoot);
            var x = basis.X * cascade.Area.Z;
            var y = basis.Y * cascade.Area.Z;
            var z = basis.Z * inverseDepth;
            var transform = new Matrix4x4(x.X, y.X, z.X, 0, x.Y, y.Y, z.Y, 0, x.Z, y.Z, z.Z, 0,
                -cascade.Area.X * cascade.Area.Z, -cascade.Area.Y * cascade.Area.Z, -min * inverseDepth, 1);
            commands->SetGraphicsRoot32BitConstants(0, 16, &transform, 0);
            mesh.DrawShadow(commands, basis, cascade);
            Transition(commands, map, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        }
    }

    private void ReleaseShadows()
    {
        Release(_shadowNormalPipeline);
        Release(_shadowColorPipeline);
        Release(_shadowDepthPipeline);
        Release(_shadowColorRoot);
        Release(_shadowDepthRoot);
        Release(_nearShadow);
        Release(_farShadow);
        Release(_shadowSrv);
        Release(_shadowDsv);
        _shadowNormalPipeline = _shadowColorPipeline = _shadowDepthPipeline = null;
        _shadowColorRoot = _shadowDepthRoot = null;
        _nearShadow = _farShadow = null;
        _shadowSrv = _shadowDsv = null;
        _shadowReady = false;
    }

    private readonly record struct ShadowConstants(Vector4 LightX, Vector4 LightY, Vector4 LightZ,
        Vector4 Near, Vector4 Far)
    {
        internal bool Enabled => Near.W != 0 || Far.W != 0;
    }
}
