using System.Numerics;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    // The render owner exclusively mutates both handles. Tests can observe the count during custody.
    internal MeshGeometry? ActiveMeshGeometry;
    private int _liveMeshBuffers;

    internal int LiveMeshBuffers => Volatile.Read(ref _liveMeshBuffers);

    internal MeshGeometry PrepareMeshGeometry(RenderMeshPair meshes)
    {
        var bytes = checked(MeshBytes(meshes.First.Mesh) + MeshBytes(meshes.Second.Mesh));
        if (bytes == 0)
            return new MeshGeometry(this, null, meshes);
        if (LiveMeshBuffers >= 2)
            throw new InvalidOperationException("The active and private mesh buffers already occupy both geometry slots.");

        var resource = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD,
            Buffer((ulong)bytes), D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ);
        Interlocked.Increment(ref _liveMeshBuffers);
        try
        {
            return new MeshGeometry(this, resource, meshes);
        }
        catch
        {
            ReleaseMeshBuffer(resource);
            throw;
        }
    }

    private void ReleaseMeshBuffer(ID3D12Resource* resource)
    {
        Release(resource);
        Interlocked.Decrement(ref _liveMeshBuffers);
    }

    private static int MeshBytes(RenderMesh mesh) => checked(
        mesh.Positions.Length * (int)VertexStride + mesh.Indices.Length * sizeof(uint));

    private static PackedMesh PackMesh(in RenderMeshInstance instance, byte* mapped, ulong address, ref int offset)
    {
        var positions = instance.Mesh.Positions;
        var indices = instance.Mesh.Indices;
        if (indices.IsEmpty)
            return default;

        var vertexBytes = checked(positions.Length * (int)VertexStride);
        var indexBytes = checked(indices.Length * sizeof(uint));
        var indexOffset = checked(offset + vertexBytes);
        var vertices = new Span<Vector3>(mapped + offset, positions.Length);
        for (var index = 0; index < vertices.Length; index++)
        {
            // Put both meshes in the same scene space before projection. Coincident
            // seam vertices then pass through identical floating-point transforms.
            var position = positions[index] + instance.Translation;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new ArgumentException("A translated mesh position is not finite.", nameof(instance));
            vertices[index] = position;
        }
        indices.CopyTo(new Span<uint>(mapped + indexOffset, indices.Length));

        var vertexView = new D3D12_VERTEX_BUFFER_VIEW
        {
            BufferLocation = checked(address + (ulong)offset),
            SizeInBytes = (uint)vertexBytes,
            StrideInBytes = VertexStride,
        };
        var indexView = new D3D12_INDEX_BUFFER_VIEW
        {
            BufferLocation = checked(address + (ulong)indexOffset),
            SizeInBytes = (uint)indexBytes,
            Format = DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
        };
        offset = checked(indexOffset + indexBytes);
        return new(vertexView, indexView, (uint)indices.Length);
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

    private readonly record struct PackedMesh(D3D12_VERTEX_BUFFER_VIEW Vertices, D3D12_INDEX_BUFFER_VIEW Indices, uint IndexCount);
    internal sealed class MeshGeometry : IDisposable
    {
        private readonly GpuDevice _owner;
        private readonly PackedMesh _first;
        private readonly PackedMesh _second;
        private ID3D12Resource* _resource;

        internal MeshGeometry(GpuDevice owner, ID3D12Resource* resource, RenderMeshPair meshes)
        {
            _owner = owner;
            _resource = resource;
            if (resource == null)
                return;

            var unread = new D3D12_RANGE();
            void* mapped = null;
            Check(resource->Map(0, &unread, &mapped), "Map mesh geometry");
            try
            {
                var address = resource->GetGPUVirtualAddress();
                var offset = 0;
                _first = PackMesh(meshes.First, (byte*)mapped, address, ref offset);
                _second = PackMesh(meshes.Second, (byte*)mapped, address, ref offset);
            }
            finally
            {
                resource->Unmap(0, null);
            }
        }

        internal void Draw(ID3D12GraphicsCommandList* commands)
        {
            DrawMesh(commands, _first);
            DrawMesh(commands, _second);
        }

        public void Dispose()
        {
            var resource = _resource;
            if (resource == null)
                return;

            _resource = null;
            _owner.ReleaseMeshBuffer(resource);
        }
    }
}
