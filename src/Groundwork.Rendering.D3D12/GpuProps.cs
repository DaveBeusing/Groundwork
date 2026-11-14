using System.Numerics;
using Groundwork.Voxels;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    internal D3D12PropCells? PropStream;
    internal VolumePublicationInfo? PropTerrain;
    private ID3D12RootSignature* _propRoot;
    private ID3D12PipelineState* _propPipeline;
    private ID3D12Resource* _propData;
    private ID3D12Resource* _propGeometry;
    private byte* _propMapped;
    private byte* _propGeometryMapped;
    private PackedMesh[]? _propMeshes;
    private VisibleProp[]? _visibleProps;
    private PropInstanceData[]? _propStage;
    private int _propMeshCursor;
    private int _propGeometryOffset;
    private int _nextPropRequest;
    private int _propPayloadCapacity;
    private long _propNativeBytes;

    private readonly record struct PropReference(Vector3 Offset, uint Payload);

    private void DrawProps(ID3D12GraphicsCommandList* commands, GpuTarget target, in RenderScene scene)
    {
        if (PropStream is not { } stream || PropTerrain is not { } terrain || scene.PropOrigin is not { } origin)
            return;

        // The window owner only renders after the preceding submission and Present retire.
        // Offscreen callers also wait for retirement before touching this arena again.
        if (!IsIdle)
            return;
        lock (stream.Gate)
        {
            if (stream.Closed || stream.Publication.RendererEpoch != terrain.View.RendererEpoch ||
                stream.Publication.TerrainRevision != terrain.TerrainRevision ||
                stream.Publication.CoverageGeneration != terrain.Generation || !origin.IsValid || !scene.PropEye.IsValid)
            {
                stream.Statistics = new(0, 0, 0, 0, 0, 0, _propNativeBytes);
                return;
            }

            InitializeProps(stream);
            var reserve = 16 * stream.Limits.ResidentInstances;
            var available = D3D12PropCells.MaximumUploadBytes - reserve;
            var uploaded = UploadProps(stream, available);
            if (_propMeshCursor != _propMeshes!.Length)
            {
                stream.Statistics = new(uploaded, uploaded, 0, 0, 0, stream.Admission.Usage.PendingCells, _propNativeBytes);
                return;
            }

            var lens = Matrix4x4.CreatePerspectiveFieldOfView(scene.VerticalFieldOfView,
                (float)target.Width / target.Height, scene.NearPlane, scene.FarPlane);
            var projection = scene.View * lens;
            var cameraRelative = scene.View;
            cameraRelative.Translation = Vector3.Zero;
            Span<PropFrustumPlane> planes = stackalloc PropFrustumPlane[6];
            ExtractPropFrustum(cameraRelative * lens, planes);
            foreach (var plane in planes)
            {
                if (plane.IsValid)
                    continue;
                stream.Statistics = new(uploaded, uploaded, 0, 0, 0, stream.Admission.Usage.PendingCells, _propNativeBytes);
                return;
            }
            var count = stream.Admission.Select(scene.PropEye, planes, _visibleProps);
            var metadata = new Span<PropReference>(_propMapped + _propPayloadCapacity, count);
            for (var i = 0; i < count; i++)
            {
                var visible = _visibleProps![i];
                if (!stream.Admission.TryRead(visible, out _, out var point))
                    throw new InvalidOperationException("Prop visibility references a nonresident cell.");
                var delta = point.RelativeTo(origin);
                metadata[i] = new(new((float)delta.X, (float)delta.Y, (float)delta.Z),
                    visible.CellSlot * (uint)stream.InstancesPerCell + (uint)visible.InstanceIndex);
            }

            var draws = 0;
            if (count != 0)
            {
                var fogDepth = scene.Fog is { MaxOpacity: > 0 } fog ? GetFogDepthConstants(fog) : Vector4.Zero;
                var fogColor = scene.Fog is { } f ? new Vector4(f.Color, f.MaxOpacity) : Vector4.Zero;
                var constants = new DrawConstants(projection, scene.Tint, fogColor, fogDepth, default, default, default);
                commands->SetPipelineState(_propPipeline);
                commands->SetGraphicsRootSignature(_propRoot);
                commands->SetGraphicsRoot32BitConstants(0, FrameConstantCount, &constants, 0);
                var address = _propData->GetGPUVirtualAddress();
                commands->SetGraphicsRootShaderResourceView(1, address);
                commands->SetGraphicsRootShaderResourceView(2, address + (uint)_propPayloadCapacity);
                for (var start = 0; start < count;)
                {
                    var first = _visibleProps![start];
                    var end = start + 1;
                    while (end < count && SameGroup(first, _visibleProps[end]))
                        end++;
                    var mesh = FindPropMesh(stream, first);
                    var vertices = mesh.Vertices;
                    var indices = mesh.Indices;
                    commands->IASetVertexBuffers(0, 1, &vertices);
                    commands->IASetIndexBuffer(&indices);
                    commands->DrawIndexedInstanced(mesh.IndexCount, (uint)(end - start), 0, 0, (uint)start);
                    draws++;
                    start = end;
                }
            }
            var metadataBytes = count * sizeof(PropReference);
            var deferred = 0;
            foreach (var request in stream.Ready)
                if (request.Generation != 0)
                    deferred++;
            stream.Statistics = new(uploaded + metadataBytes, uploaded, metadataBytes, draws, count, deferred, _propNativeBytes);
        }
    }

    private void InitializeProps(D3D12PropCells stream)
    {
        if (_propRoot != null)
            return;

        _propPayloadCapacity = checked(stream.Limits.Cells * stream.InstancesPerCell * PropInstanceData.ByteSize);
        var data = Buffer((ulong)(_propPayloadCapacity + 16 * stream.Limits.ResidentInstances));
        var geometry = Buffer((ulong)stream.GeometryBytes);
        var dataBytes = _device->GetResourceAllocationInfo(1, 1, &data).SizeInBytes;
        var geometryBytes = _device->GetResourceAllocationInfo(1, 1, &geometry).SizeInBytes;
        if (dataBytes > 64 * 1024 * 1024 || geometryBytes > 64 * 1024 * 1024 - dataBytes)
            throw new InvalidOperationException("Prop native allocation exceeds 64 MiB.");

        _propData = CreateResource(_device, _meshUploadHeap, data, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
        _propGeometry = CreateResource(_device, _meshUploadHeap, geometry, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
        _propMapped = MapProp(_propData);
        _propGeometryMapped = MapProp(_propGeometry);
        _propNativeBytes = (long)(dataBytes + geometryBytes);
        _propMeshes = new PackedMesh[stream.Assets.Length * 2];
        _visibleProps = new VisibleProp[stream.Limits.ResidentInstances];
        _propStage = new PropInstanceData[stream.InstancesPerCell];
        _propRoot = CreateFrameRoot(FrameConstantCount, props: true);
        _propPipeline = CreateFramePipeline(_propRoot, sky: false, props: true);
    }

    private static byte* MapProp(ID3D12Resource* resource)
    {
        var range = new D3D12_RANGE();
        void* mapped;
        Check(resource->Map(0, &range, &mapped), "Map prop upload");
        return (byte*)mapped;
    }

    private int UploadProps(D3D12PropCells stream, int available)
    {
        var uploaded = 0;
        while (_propMeshCursor < _propMeshes!.Length)
        {
            var asset = stream.Assets[_propMeshCursor / 2];
            var mesh = (_propMeshCursor & 1) == 0 ? asset.Near : asset.Far;
            var bytes = MeshBytes(mesh);
            if (bytes > available - uploaded)
                return uploaded;
            _propMeshes[_propMeshCursor++] = PackMesh(new(mesh, Vector3.Zero), _propGeometryMapped,
                _propGeometry->GetGPUVirtualAddress(), ref _propGeometryOffset);
            uploaded += bytes;
        }

        for (var scan = 0; scan < stream.Ready.Length; scan++)
        {
            var index = (_nextPropRequest + scan) % stream.Ready.Length;
            var request = stream.Ready[index];
            if (request.Generation == 0)
                continue;
            var result = stream.Admission.CopyStaged(request, _propStage, out var count, out _);
            if (result == PropCellResult.Stale)
            {
                stream.Ready[index] = default;
                continue;
            }
            if (result != PropCellResult.Accepted)
                continue;
            var bytes = count * PropInstanceData.ByteSize;
            if (bytes > available - uploaded)
            {
                _nextPropRequest = index;
                break;
            }
            if (stream.Admission.TryInstall(request) != PropCellResult.Accepted)
                continue;
            if (count != 0)
            {
                var offset = (int)_propStage![0].CellSlot * stream.InstancesPerCell * PropInstanceData.ByteSize;
                _propStage.AsSpan(0, count).CopyTo(new Span<PropInstanceData>(_propMapped + offset, count));
            }
            stream.Ready[index] = default;
            uploaded += bytes;
        }
        return uploaded;
    }

    private static bool SameGroup(VisibleProp left, VisibleProp right) =>
        left.Model == right.Model && left.Material == right.Material && left.Lod == right.Lod;

    private PackedMesh FindPropMesh(D3D12PropCells stream, VisibleProp prop)
    {
        for (var i = 0; i < stream.Assets.Length; i++)
            if (stream.Assets[i].Model.Model == prop.Model && stream.Assets[i].Model.Material == prop.Material)
                return _propMeshes![2 * i + prop.Lod];
        throw new InvalidOperationException("Unknown resident prop model.");
    }

    private static void ExtractPropFrustum(Matrix4x4 matrix, Span<PropFrustumPlane> planes)
    {
        var x = new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41);
        var y = new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42);
        var z = new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43);
        var w = new Vector4(matrix.M14, matrix.M24, matrix.M34, matrix.M44);
        planes[0] = Plane(w + x);
        planes[1] = Plane(w - x);
        planes[2] = Plane(w + y);
        planes[3] = Plane(w - y);
        planes[4] = Plane(z);
        planes[5] = Plane(w - z);

        static PropFrustumPlane Plane(Vector4 coefficients)
        {
            var normal = new Vector3(coefficients.X, coefficients.Y, coefficients.Z);
            var length = normal.Length();
            // Very large far planes can round to infinity in the projection.
            return length > 0 ? new(normal / length, coefficients.W / length) : new(Vector3.UnitZ, double.MaxValue);
        }
    }

    private void ReleaseProps()
    {
        if (_propMapped != null)
            _propData->Unmap(0, null);
        if (_propGeometryMapped != null)
            _propGeometry->Unmap(0, null);
        Release(_propData);
        Release(_propGeometry);
        Release(_propPipeline);
        Release(_propRoot);
        if (PropStream is { } stream)
        {
            lock (stream.Gate)
                stream.Statistics = default;
        }
    }
}
