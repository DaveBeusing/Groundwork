using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    internal const long MaximumNativeMeshBytes = 64L * 1024 * 1024;
    private const uint TerrainAppearanceVertexStride = NormalVertexStride + TerrainAppearanceVertex.SizeInBytes;
    // The render owner exclusively mutates both handles. Tests can observe the count during custody.
    internal MeshGeometry? ActiveMeshGeometry;
    private int _liveMeshBuffers;
    private long _liveMeshBytes;

    internal int LiveMeshBuffers => Volatile.Read(ref _liveMeshBuffers);
    internal long LiveMeshBytes => Volatile.Read(ref _liveMeshBytes);

    internal MeshGeometry PrepareMeshGeometry(RenderMeshPair meshes) =>
        PrepareMeshGeometry([meshes.First, meshes.Second]);

    internal MeshGeometry PrepareMeshGeometry(RenderMeshRegion region, long nativeLimit = long.MaxValue) =>
        PrepareMeshGeometry(region.Instances, nativeLimit);

    private MeshGeometry PrepareMeshGeometry(ReadOnlySpan<RenderMeshInstance> meshes, long nativeLimit = long.MaxValue)
    {
        var bytes = 0;
        foreach (ref readonly var instance in meshes)
            bytes = checked(bytes + MeshBytes(instance.Mesh, allowIndexFree: true));

        if (bytes == 0)
            return new MeshGeometry(this, null, 0, meshes);
        if (LiveMeshBuffers >= 2)
            throw new InvalidOperationException("The active and private mesh buffers already occupy both geometry slots.");

        // Meshes draw directly from this upload resource. There is no separate staging buffer.
        // Query the native allocation, which includes the device's buffer alignment.
        var description = Buffer((ulong)bytes);
        var allocation = _device->GetResourceAllocationInfo(1, 1, &description);
        if (allocation.SizeInBytes < (ulong)bytes || allocation.SizeInBytes > (ulong)Math.Min(nativeLimit, MaximumNativeMeshBytes - LiveMeshBytes))
            throw new InvalidOperationException("The prepared mesh exceeds the native geometry budget.");

        var allocationBytes = (long)allocation.SizeInBytes;
        var resource = CreateResource(_device, _meshUploadHeap,
            description, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
        ObserveResource(resource, D3D12ResourceOwner.Terrain, allocation.SizeInBytes);
        Interlocked.Add(ref _liveMeshBytes, allocationBytes);
        Interlocked.Increment(ref _liveMeshBuffers);
        try
        {
            return new MeshGeometry(this, resource, allocationBytes, meshes);
        }
        catch
        {
            ReleaseMeshBuffer(resource, allocationBytes);
            throw;
        }
    }

    private void ReleaseMeshBuffer(ID3D12Resource* resource, long allocationBytes)
    {
        ReleaseObservedResource(resource);
        Interlocked.Add(ref _liveMeshBytes, -allocationBytes);
        Interlocked.Decrement(ref _liveMeshBuffers);
    }

    private static int MeshBytes(RenderMesh mesh, bool allowIndexFree) => checked(
        mesh.Positions.Length * (int)GetVertexStride(mesh) + NativeIndexBytes(mesh, allowIndexFree));

    private static int NativeIndexBytes(RenderMesh mesh, bool allowIndexFree) =>
        allowIndexFree && mesh.IsExpandedTriangleList ? 0 : mesh.Indices.Length * sizeof(uint);

    private static uint GetVertexStride(RenderMesh mesh) => !mesh.TerrainAppearance.IsEmpty ? TerrainAppearanceVertexStride :
        mesh.PackedNormals.IsEmpty ? VertexStride : NormalVertexStride;

    private static PackedMesh PackMesh(in RenderMeshInstance instance, byte* mapped, ulong address, ref int offset,
        bool allowIndexFree)
    {
        var positions = instance.Mesh.Positions;
        var indices = instance.Mesh.Indices;
        var colors = instance.Mesh.Colors;
        if (indices.IsEmpty)
            return default;

        var normals = instance.Mesh.PackedNormals;
        var appearance = instance.Mesh.TerrainAppearance;
        var stride = GetVertexStride(instance.Mesh);
        var vertexBytes = checked(positions.Length * (int)stride);
        var indexBytes = NativeIndexBytes(instance.Mesh, allowIndexFree);
        var indexOffset = checked(offset + vertexBytes);

        var minimum = new System.Numerics.Vector3(float.PositiveInfinity);
        var maximum = new System.Numerics.Vector3(float.NegativeInfinity);
        for (var index = 0; index < positions.Length; index++)
        {
            // Expanded triangles have no shared vertices. Pack in draw order, including
            // the cyclic rotation that puts the canonical material on the first vertex.
            var source = indexBytes == 0 ? (int)indices[index] : index;
            // Put all meshes in the same scene space before projection. Coincident
            // seam vertices then pass through identical floating-point transforms.
            var position = positions[source] + instance.Translation;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new ArgumentException("A translated mesh position is not finite.", nameof(instance));
            minimum = System.Numerics.Vector3.Min(minimum, position);
            maximum = System.Numerics.Vector3.Max(maximum, position);
            var vertex = mapped + offset + index * (int)stride;
            *(GpuVertex*)vertex = new GpuVertex(position, colors.IsEmpty ? uint.MaxValue : colors[source]);
            if (!normals.IsEmpty)
                *(uint*)(vertex + VertexStride) = normals[source];
            if (!appearance.IsEmpty)
            {
                var data = appearance[source];
                *(System.Numerics.Vector3*)(vertex + NormalVertexStride) = data.Texels;
                *(uint*)(vertex + NormalVertexStride + 3 * sizeof(float)) = data.MaterialAndMapping;
            }
        }
        if (indexBytes != 0)
            indices.CopyTo(new Span<uint>(mapped + indexOffset, indices.Length));

        var vertexView = new D3D12_VERTEX_BUFFER_VIEW
        {
            BufferLocation = checked(address + (ulong)offset),
            SizeInBytes = (uint)vertexBytes,
            StrideInBytes = stride,
        };
        var indexView = indexBytes == 0 ? default : new D3D12_INDEX_BUFFER_VIEW
        {
            BufferLocation = checked(address + (ulong)indexOffset),
            SizeInBytes = (uint)indexBytes,
            Format = DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
        };
        offset = checked(indexOffset + indexBytes);
        return new(vertexView, indexView, (uint)indices.Length, (minimum + maximum) * 0.5f, (maximum - minimum) * 0.5f);
    }

    private static void DrawMesh(ID3D12GraphicsCommandList* commands, in PackedMesh mesh)
    {
        if (mesh.DrawCount == 0)
            return;

        var vertices = mesh.Vertices;
        commands->IASetVertexBuffers(0, 1, &vertices);
        if (mesh.Indices.SizeInBytes == 0)
        {
            commands->DrawInstanced(mesh.DrawCount, 1, 0, 0);
        }
        else
        {
            var indices = mesh.Indices;
            commands->IASetIndexBuffer(&indices);
            commands->DrawIndexedInstanced(mesh.DrawCount, 1, 0, 0, 0);
        }
    }

    private readonly record struct PackedMesh(D3D12_VERTEX_BUFFER_VIEW Vertices, D3D12_INDEX_BUFFER_VIEW Indices, uint DrawCount, System.Numerics.Vector3 Center, System.Numerics.Vector3 Extent);
    internal sealed class MeshGeometry : IDisposable
    {
        private readonly GpuDevice _owner;
        private readonly PackedMesh[] _packed;
        private readonly long _allocationBytes;
        private ID3D12Resource* _resource;
        internal ID3D12Resource* Resource => _resource;
        internal int PackedBytes { get; }
        internal bool HasPackedNormals { get; }
        internal bool HasTerrainAppearance { get; }

        internal bool HasLiveBinding(GpuDevice owner) =>
            ReferenceEquals(_owner, owner) && (PackedBytes == 0 || _resource != null);

        internal MeshGeometry(GpuDevice owner, ID3D12Resource* resource, long allocationBytes, ReadOnlySpan<RenderMeshInstance> meshes)
        {
            _owner = owner;
            _resource = resource;
            _allocationBytes = allocationBytes;
            PackedBytes = resource == null ? 0 : checked((int)resource->GetDesc().Width);
            if (resource == null)
            {
                _packed = [];
                return;
            }

            _packed = new PackedMesh[meshes.Length];
            HasPackedNormals = true;
            HasTerrainAppearance = true;
            var unread = new D3D12_RANGE();
            void* mapped = null;
            Check(resource->Map(0, &unread, &mapped), "Map mesh geometry");
            try
            {
                var address = resource->GetGPUVirtualAddress();
                var offset = 0;
                for (var index = 0; index < meshes.Length; index++)
                {
                    var packed = PackMesh(meshes[index], (byte*)mapped, address, ref offset, allowIndexFree: true);
                    _packed[index] = packed;
                    if (packed.DrawCount != 0)
                    {
                        HasPackedNormals &= packed.Vertices.StrideInBytes >= NormalVertexStride;
                        HasTerrainAppearance &= packed.Vertices.StrideInBytes == TerrainAppearanceVertexStride;
                    }
                }
            }
            finally
            {
                resource->Unmap(0, null);
            }
        }

        internal bool Draw(ID3D12GraphicsCommandList* commands, bool shadows, bool textured,
            ID3D12PipelineState* materialPipeline)
        {
            uint stride = 0;
            foreach (ref readonly var mesh in _packed.AsSpan())
            {
                if (mesh.DrawCount == 0)
                    continue;
                if (stride != mesh.Vertices.StrideInBytes)
                {
                    stride = mesh.Vertices.StrideInBytes;
                    commands->SetPipelineState(materialPipeline != null ? materialPipeline :
                        _owner.TerrainPipeline(shadows, stride >= NormalVertexStride, textured));
                }
                DrawMesh(commands, mesh);
            }
            return stride != 0;
        }

        internal (float Min, float Max) ShadowDepth(ShadowBasis basis, ShadowCascade near, ShadowCascade far)
        {
            var min = float.PositiveInfinity;
            var max = float.NegativeInfinity;
            foreach (ref readonly var mesh in _packed.AsSpan())
            {
                if (mesh.DrawCount == 0 || !Intersects(mesh, basis, near) && !Intersects(mesh, basis, far))
                    continue;
                var center = System.Numerics.Vector3.Dot(mesh.Center, basis.Z);
                var extent = System.Numerics.Vector3.Dot(mesh.Extent, System.Numerics.Vector3.Abs(basis.Z));
                min = Math.Min(min, center - extent);
                max = Math.Max(max, center + extent);
            }
            return (min, max);
        }

        internal bool DrawShadow(ID3D12GraphicsCommandList* commands, ShadowBasis basis, ShadowCascade cascade)
        {
            var drawn = false;
            foreach (ref readonly var mesh in _packed.AsSpan())
            {
                if (mesh.DrawCount != 0 && Intersects(mesh, basis, cascade))
                {
                    DrawMesh(commands, mesh);
                    drawn = true;
                }
            }
            return drawn;
        }

        private static bool Intersects(in PackedMesh mesh, ShadowBasis basis, ShadowCascade cascade)
        {
            if (!cascade.Valid)
                return false;
            var delta = basis.Project(mesh.Center) - new System.Numerics.Vector3(cascade.Area.X, cascade.Area.Y, 0);
            var radius = 1 / cascade.Area.Z;
            return MathF.Abs(delta.X) <= radius + System.Numerics.Vector3.Dot(mesh.Extent, System.Numerics.Vector3.Abs(basis.X)) &&
                MathF.Abs(delta.Y) <= radius + System.Numerics.Vector3.Dot(mesh.Extent, System.Numerics.Vector3.Abs(basis.Y));
        }

        public void Dispose()
        {
            if (ReferenceEquals(_owner._shadowMesh, this))
                _owner.InvalidateShadowCache();
            var resource = _resource;
            if (resource == null)
                return;

            _resource = null;
            _owner.ReleaseMeshBuffer(resource, _allocationBytes);
        }
    }
}
