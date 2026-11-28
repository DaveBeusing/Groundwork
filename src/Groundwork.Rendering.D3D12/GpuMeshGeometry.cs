using System.Numerics;
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
            bytes = checked(bytes + MeshBytes(instance.Mesh, allowIndexFree: TwilightPreviewEnabledForTests));

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
        mesh.Positions.Length * (int)GetVertexStride(mesh) +
        AlignIndexBytes(mesh.Indices.Length * GetIndexStride(mesh, allowIndexFree)));

    private static int AlignIndexBytes(int bytes) => (bytes + sizeof(uint) - 1) & -sizeof(uint);

    private static int GetIndexStride(RenderMesh mesh, bool allowIndexFree)
    {
        if (allowIndexFree && mesh.IsExpandedTriangleList)
            return 0;

        // RenderMesh validates that every index addresses an existing vertex.
        return mesh.Positions.Length <= ushort.MaxValue + 1 ? sizeof(ushort) : sizeof(uint);
    }

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
        var indexStride = GetIndexStride(instance.Mesh, allowIndexFree);
        var indexBytes = checked(indices.Length * indexStride);
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
        if (indexStride == sizeof(ushort))
        {
            var compact = new Span<ushort>(mapped + indexOffset, indices.Length);
            for (var index = 0; index < indices.Length; index++)
                compact[index] = (ushort)indices[index];
        }
        else if (indexStride == sizeof(uint))
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
            Format = indexStride == sizeof(ushort) ? DXGI_FORMAT.DXGI_FORMAT_R16_UINT : DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
        };
        // The next mesh's float and uint vertex fields require four-byte alignment.
        offset = checked(indexOffset + AlignIndexBytes(indexBytes));
        return new(vertexView, indexView, (uint)indices.Length, (minimum + maximum) * 0.5f, (maximum - minimum) * 0.5f,
            minimum, maximum);
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

    private readonly record struct PackedMesh(D3D12_VERTEX_BUFFER_VIEW Vertices, D3D12_INDEX_BUFFER_VIEW Indices,
        uint DrawCount, Vector3 Center, Vector3 Extent, Vector3 Minimum, Vector3 Maximum);

    private readonly record struct MeshClipPlane(Vector4 Coefficients, Vector4 ErrorScale);

    private static void ExtractMeshClipPlanes(in Matrix4x4 matrix, Span<MeshClipPlane> planes)
    {
        // The vertex shader multiplies row vectors. Direct3D clips x/y to [-w, w] and z to [0, w].
        var x = new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41);
        var y = new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42);
        var z = new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43);
        var w = new Vector4(matrix.M14, matrix.M24, matrix.M34, matrix.M44);
        var absW = Vector4.Abs(w);
        var absX = absW + Vector4.Abs(x);
        var absY = absW + Vector4.Abs(y);
        var absZ = Vector4.Abs(z);
        planes[0] = new(w + x, absX);
        planes[1] = new(w - x, absX);
        planes[2] = new(w + y, absY);
        planes[3] = new(w - y, absY);
        planes[4] = new(z, absZ);
        planes[5] = new(w - z, absW + absZ);
    }

    private static bool IsOutsideMeshFrustum(in PackedMesh mesh, ReadOnlySpan<MeshClipPlane> planes)
    {
        var magnitude = new Vector4(Vector3.Max(Vector3.Abs(mesh.Minimum), Vector3.Abs(mesh.Maximum)), 1);
        foreach (ref readonly var plane in planes)
        {
            var coefficients = plane.Coefficients;
            var furthest = new Vector4(
                coefficients.X >= 0 ? mesh.Maximum.X : mesh.Minimum.X,
                coefficients.Y >= 0 ? mesh.Maximum.Y : mesh.Minimum.Y,
                coefficients.Z >= 0 ? mesh.Maximum.Z : mesh.Minimum.Z, 1);
            var distance = Vector4.Dot(coefficients, furthest);
            // Allow for cancellation in the plane and rounding in the shader's clip-space dots.
            // Nonfinite or ambiguous results keep the mesh eligible to draw.
            var error = MathF.Max(1, Vector4.Dot(plane.ErrorScale, magnitude)) * (16f / 16_777_216f);
            if (float.IsFinite(distance) && distance < -error)
                return true;
        }

        return false;
    }
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
                    var packed = PackMesh(meshes[index], (byte*)mapped, address, ref offset,
                        allowIndexFree: owner.TwilightPreviewEnabledForTests);
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

        internal bool Draw(ID3D12GraphicsCommandList* commands, in Matrix4x4 worldViewProjection,
            bool shadows, bool textured, ID3D12PipelineState* materialPipeline)
        {
            Span<MeshClipPlane> planes = stackalloc MeshClipPlane[6];
            ExtractMeshClipPlanes(worldViewProjection, planes);
            uint stride = 0;
            foreach (ref readonly var mesh in _packed.AsSpan())
            {
                if (mesh.DrawCount == 0 || IsOutsideMeshFrustum(mesh, planes))
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
