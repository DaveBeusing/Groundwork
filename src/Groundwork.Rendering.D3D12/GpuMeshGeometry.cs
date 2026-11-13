using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    internal const long MaximumNativeMeshBytes = 64L * 1024 * 1024;
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
            bytes = checked(bytes + MeshBytes(instance.Mesh));

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
        var resource = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
            description, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
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
        Release(resource);
        Interlocked.Add(ref _liveMeshBytes, -allocationBytes);
        Interlocked.Decrement(ref _liveMeshBuffers);
    }

    private static int MeshBytes(RenderMesh mesh) => checked(
        mesh.Positions.Length * (int)GetVertexStride(mesh) + mesh.Indices.Length * sizeof(uint));

    private static uint GetVertexStride(RenderMesh mesh) => mesh.PackedNormals.IsEmpty ? VertexStride : NormalVertexStride;

    private static PackedMesh PackMesh(in RenderMeshInstance instance, byte* mapped, ulong address, ref int offset)
    {
        var positions = instance.Mesh.Positions;
        var indices = instance.Mesh.Indices;
        var colors = instance.Mesh.Colors;
        if (indices.IsEmpty)
            return default;

        var normals = instance.Mesh.PackedNormals;
        var stride = GetVertexStride(instance.Mesh);
        var vertexBytes = checked(positions.Length * (int)stride);
        var indexBytes = checked(indices.Length * sizeof(uint));
        var indexOffset = checked(offset + vertexBytes);

        var minimum = new System.Numerics.Vector3(float.PositiveInfinity);
        var maximum = new System.Numerics.Vector3(float.NegativeInfinity);
        for (var index = 0; index < positions.Length; index++)
        {
            // Put all meshes in the same scene space before projection. Coincident
            // seam vertices then pass through identical floating-point transforms.
            var position = positions[index] + instance.Translation;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new ArgumentException("A translated mesh position is not finite.", nameof(instance));
            minimum = System.Numerics.Vector3.Min(minimum, position);
            maximum = System.Numerics.Vector3.Max(maximum, position);
            var vertex = mapped + offset + index * (int)stride;
            *(GpuVertex*)vertex = new GpuVertex(position, colors.IsEmpty ? uint.MaxValue : colors[index]);
            if (!normals.IsEmpty)
                *(uint*)(vertex + VertexStride) = normals[index];
        }
        indices.CopyTo(new Span<uint>(mapped + indexOffset, indices.Length));

        var vertexView = new D3D12_VERTEX_BUFFER_VIEW
        {
            BufferLocation = checked(address + (ulong)offset),
            SizeInBytes = (uint)vertexBytes,
            StrideInBytes = stride,
        };
        var indexView = new D3D12_INDEX_BUFFER_VIEW
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
        if (mesh.IndexCount == 0)
            return;

        var vertices = mesh.Vertices;
        var indices = mesh.Indices;
        commands->IASetVertexBuffers(0, 1, &vertices);
        commands->IASetIndexBuffer(&indices);
        commands->DrawIndexedInstanced(mesh.IndexCount, 1, 0, 0, 0);
    }

    private readonly record struct PackedMesh(D3D12_VERTEX_BUFFER_VIEW Vertices, D3D12_INDEX_BUFFER_VIEW Indices, uint IndexCount, System.Numerics.Vector3 Center, System.Numerics.Vector3 Extent);
    internal sealed class MeshGeometry : IDisposable
    {
        private readonly GpuDevice _owner;
        private readonly PackedMesh[] _packed;
        private readonly long _allocationBytes;
        private ID3D12Resource* _resource;

        internal MeshGeometry(GpuDevice owner, ID3D12Resource* resource, long allocationBytes, ReadOnlySpan<RenderMeshInstance> meshes)
        {
            _owner = owner;
            _resource = resource;
            _allocationBytes = allocationBytes;
            if (resource == null)
            {
                _packed = [];
                return;
            }

            _packed = new PackedMesh[meshes.Length];
            var unread = new D3D12_RANGE();
            void* mapped = null;
            Check(resource->Map(0, &unread, &mapped), "Map mesh geometry");
            try
            {
                var address = resource->GetGPUVirtualAddress();
                var offset = 0;
                for (var index = 0; index < meshes.Length; index++)
                    _packed[index] = PackMesh(meshes[index], (byte*)mapped, address, ref offset);
            }
            finally
            {
                resource->Unmap(0, null);
            }
        }

        internal void Draw(ID3D12GraphicsCommandList* commands, bool shadows)
        {
            uint stride = 0;
            foreach (ref readonly var mesh in _packed.AsSpan())
            {
                if (mesh.IndexCount == 0)
                    continue;
                if (stride != mesh.Vertices.StrideInBytes)
                {
                    stride = mesh.Vertices.StrideInBytes;
                                        commands->SetPipelineState(shadows
                        ? stride == NormalVertexStride ? _owner._shadowNormalPipeline : _owner._shadowColorPipeline
                        : stride == NormalVertexStride ? _owner._normalPipeline : _owner._pipeline);
                }
                DrawMesh(commands, mesh);
            }
        }

        internal (float Min, float Max) ShadowDepth(ShadowBasis basis, ShadowCascade near, ShadowCascade far)
        {
            var min = float.PositiveInfinity;
            var max = float.NegativeInfinity;
            foreach (ref readonly var mesh in _packed.AsSpan())
            {
                if (mesh.IndexCount == 0 || !Intersects(mesh, basis, near) && !Intersects(mesh, basis, far))
                    continue;
                var center = System.Numerics.Vector3.Dot(mesh.Center, basis.Z);
                var extent = System.Numerics.Vector3.Dot(mesh.Extent, System.Numerics.Vector3.Abs(basis.Z));
                min = Math.Min(min, center - extent);
                max = Math.Max(max, center + extent);
            }
            return (min, max);
        }

        internal void DrawShadow(ID3D12GraphicsCommandList* commands, ShadowBasis basis, ShadowCascade cascade)
        {
            foreach (ref readonly var mesh in _packed.AsSpan())
                if (mesh.IndexCount != 0 && Intersects(mesh, basis, cascade))
                    DrawMesh(commands, mesh);
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
            var resource = _resource;
            if (resource == null)
                return;

            _resource = null;
            _owner.ReleaseMeshBuffer(resource, _allocationBytes);
        }
    }
}
