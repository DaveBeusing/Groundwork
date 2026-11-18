using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12;

public sealed partial class D3D12Renderer
{
    private readonly D3D12TextureView _sessionOrbitTextureView;
    private int _sessionOrbitPresented;

    /// <summary>Creates a renderer for fixed planet-centred orbital meshes on a shared device session.</summary>
    /// <remarks>Build each mesh with <see cref="TryBuildOrbitalTerrainMesh"/>. Submit identity-world scene
    /// frames with <see cref="TrySubmit"/>. The supplied view identifies this immutable region and must use
    /// a fresh positive renderer epoch for a replacement renderer. Publish the palette and maps for that view
    /// to the supplied fresh stream. Optional texture work starts after the first scene presents.</remarks>
    public static D3D12Renderer CreateWithSessionTexturedMeshes(WindowsHostModule window, IRuntimeOwnership ownership,
        RenderMeshRegion region, D3D12DeviceSession session, D3D12TextureHandoff stream, D3D12TextureView view,
        TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(window, ownership, region, session, stream, view, startupTimeout, operationTimeout);
    }

    internal D3D12Renderer(WindowsHostModule window, IRuntimeOwnership ownership, RenderMeshRegion region,
        D3D12DeviceSession session, D3D12TextureHandoff stream, D3D12TextureView view,
        TimeSpan? startupTimeout, TimeSpan? operationTimeout, GpuRetirementTestHooks? retirementHooks = null)
        : this(session.Diagnostics, window, ownership, session.UseWarp, startupTimeout, operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(stream);
        if (!view.IsValid)
            throw new ArgumentException("Orbital appearance requires a valid texture view.", nameof(view));

        var indexCount = 0;
        foreach (ref readonly var instance in region.Instances)
        {
            if (instance.Translation != Vector3.Zero || instance.Mesh.TerrainAppearance.IsEmpty)
                throw new ArgumentException("Orbital appearance requires mapped, planet-centred meshes.", nameof(region));
            indexCount += instance.Mesh.Indices.Length;
        }
        if (!FitsVolumeGeometry(indexCount / 3, GpuDevice.MaximumNativeMeshBytes / 2, TerrainAppearanceVertex.SizeInBytes))
            throw new ArgumentException("The orbital region exceeds the bounded terrain appearance geometry budget.", nameof(region));

        stream.BindOwner(view.World, Wake, view.View.RendererEpoch, fixedView: view);
        _initialGeometry = region;
        _deviceSession = session;
        _textures = stream;
        _sessionOrbitTextureView = view;
        _retirementHooks = retirementHooks;
    }

    /// <summary>Builds an orbital mesh with radial normals and per-triangle canonical triplanar mapping.</summary>
    /// <remarks>Supply one authoritative planet-centred position and nonzero material ID per source vertex.
    /// Source positions must be the float projections of those canonical positions. A triangle wider than
    /// the mapping window keeps its material fallback. Returns false if the mesh cannot fit after vertex
    /// expansion. Split it along triangle boundaries or use the original untextured mesh in that case.</remarks>
    public static bool TryBuildOrbitalTerrainMesh(RenderMesh source, ReadOnlySpan<PlanetVector> canonicalPositions,
        ReadOnlySpan<ushort> materialIds, [NotNullWhen(true)] out RenderMesh? mesh)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Positions.Length != canonicalPositions.Length || source.Positions.Length != materialIds.Length)
            throw new ArgumentException("Orbital appearance requires one canonical position and material ID per source vertex.");

        mesh = null;
        var count = source.Indices.Length;
        if (count == 0 || count > RenderMesh.MaximumVertexCount)
            return false;

        var samples = new OrbitAppearanceSample[canonicalPositions.Length];
        for (var vertex = 0; vertex < samples.Length; vertex++)
        {
            var point = canonicalPositions[vertex];
            var projection = new Vector3((float)point.X, (float)point.Y, (float)point.Z);
            if (source.Positions[vertex] != projection)
                throw new ArgumentException("Orbital mesh positions must match their canonical projections.", nameof(canonicalPositions));

            var mapping = PlanetTriplanarMapping.FromCanonical(point);
            var length = Math.Sqrt(point.X * point.X + point.Y * point.Y + point.Z * point.Z);
            if (!(length > 0) || materialIds[vertex] == 0)
                throw new ArgumentException("Orbital appearance requires nonzero canonical positions and material IDs.");

            var outward = new Vector3((float)(point.X / length), (float)(point.Y / length), (float)(point.Z / length));
            samples[vertex] = new(mapping, materialIds[vertex], PackedTerrainNormal.Encode(outward),
                source.Colors.IsEmpty ? uint.MaxValue : source.Colors[vertex]);
        }

        var positions = new Vector3[count];
        var indices = new uint[count];
        var normals = new uint[count];
        var colors = source.Colors.IsEmpty ? [] : new uint[count];
        var appearance = new TerrainAppearanceVertex[count];
        PackOrbitTriangles(source.Positions, source.Indices, samples, positions, indices, normals, colors, appearance, 0);

        mesh = RenderMesh.CreateWithTerrainNormals(positions, indices, colors, normals, appearance);
        return true;
    }

    private void RecordSessionOrbitPresent(in RenderFrame frame)
    {
        if (_sessionOrbitTextureView.IsValid && frame.Scene.HasValue)
            Volatile.Write(ref _sessionOrbitPresented, 1);
    }
}
