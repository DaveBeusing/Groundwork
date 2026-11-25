using System.Numerics;

namespace Groundwork.Rendering;

// The low 16 bits preserve the canonical terrain ID. The next bit marks a
// common integral-repeat anchor for the primitive; an unmapped vertex has zero texels.
internal readonly record struct TerrainAppearanceVertex(Vector3 Texels, uint MaterialAndMapping)
{
    internal const uint MappingBit = 1u << 16;
    internal const int SizeInBytes = 3 * sizeof(float) + sizeof(uint);
    internal ushort Material => (ushort)(MaterialAndMapping & ushort.MaxValue);
    internal bool HasMapping => (MaterialAndMapping & MappingBit) != 0;
}

/// <summary>An immutable snapshot of local positions and triangle-list indices.</summary>
public sealed class RenderMesh
{
    public const int MaximumVertexCount = 98_304;
    public const int MaximumIndexCount = 147_456;

    private readonly Vector3[] _positions;
    private readonly uint[] _indices;
    private readonly uint[] _colors;
    private readonly uint[] _normals;
    private readonly TerrainAppearanceVertex[] _appearance;

    internal ReadOnlySpan<uint> PackedNormals => _normals;
    internal ReadOnlySpan<TerrainAppearanceVertex> TerrainAppearance => _appearance;
    internal bool IsExpandedTriangleList { get; }

    private RenderMesh(Vector3[] positions, uint[] indices, uint[] colors, uint[] normals,
        TerrainAppearanceVertex[] appearance)
    {
        _positions = positions;
        _indices = indices;
        _colors = colors;
        _normals = normals;
        _appearance = appearance;
        IsExpandedTriangleList = positions.Length == indices.Length && HasUnsharedTriangleVertices(indices);
    }

    public ReadOnlySpan<Vector3> Positions => _positions;
    public ReadOnlySpan<uint> Indices => _indices;
    /// <summary>One RGBA8 value per vertex, with red in the lowest byte. Empty means opaque white.</summary>
    public ReadOnlySpan<uint> Colors => _colors;

    /// <summary>Validates and copies bounded geometry. Keep both inputs stable until this method returns.</summary>
    /// <exception cref="ArgumentException">The positions or indices do not describe a valid mesh.</exception>
    public static RenderMesh Create(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices) =>
        Create(positions, indices, []);

    /// <summary>Validates and copies bounded geometry and optional RGBA8 vertex colors.</summary>
    /// <exception cref="ArgumentException">A buffer has an invalid length or contains invalid geometry.</exception>
    public static RenderMesh Create(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, ReadOnlySpan<uint> colors) =>
        CreateWithTerrainNormals(positions, indices, colors, []);

    internal static RenderMesh CreateWithTerrainNormals(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices,
        ReadOnlySpan<uint> colors, ReadOnlySpan<uint> normals, ReadOnlySpan<TerrainAppearanceVertex> appearance = default)
    {
        if (positions.Length > MaximumVertexCount)
        {
            throw new ArgumentException("Too many mesh positions.", nameof(positions));
        }

        if (indices.Length > MaximumIndexCount || indices.Length % 3 != 0 || indices.IsEmpty != positions.IsEmpty)
        {
            throw new ArgumentException("Mesh indices must form a bounded triangle list, with both buffers empty for an empty mesh.", nameof(indices));
        }
        if (!colors.IsEmpty && colors.Length != positions.Length)
        {
            throw new ArgumentException("A colored mesh requires one RGBA8 value per vertex.", nameof(colors));
        }

        if (!normals.IsEmpty && normals.Length != positions.Length)
            throw new ArgumentException("Terrain requires one packed normal per vertex.", nameof(normals));
        if (!appearance.IsEmpty && (appearance.Length != positions.Length || normals.IsEmpty))
            throw new ArgumentException("Terrain appearance requires one mapping and packed normal per vertex.", nameof(appearance));

        foreach (var vertex in appearance)
        {
            if (vertex.Material == 0 || vertex.MaterialAndMapping > (TerrainAppearanceVertex.MappingBit | ushort.MaxValue) ||
                !IsFinite(vertex.Texels) || !vertex.HasMapping && vertex.Texels != Vector3.Zero)
                throw new ArgumentException("Terrain appearance requires a canonical material and valid mapping.", nameof(appearance));
        }

        foreach (var position in positions)
        {
            if (!IsFinite(position))
            {
                throw new ArgumentException("Mesh positions must be finite.", nameof(positions));
            }
        }

        foreach (var index in indices)
        {
            if (index >= (uint)positions.Length)
            {
                throw new ArgumentException("Mesh indices must address an existing position.", nameof(indices));
            }
        }

        return new RenderMesh(positions.ToArray(), indices.ToArray(), colors.ToArray(), normals.ToArray(), appearance.ToArray());
    }

    internal static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool HasUnsharedTriangleVertices(ReadOnlySpan<uint> indices)
    {
        for (var i = 0; i < indices.Length; i += 3)
        {
            var first = indices[i];
            var second = indices[i + 1];
            var third = indices[i + 2];
            var start = (uint)i;
            if (unchecked(first - start) >= 3 || unchecked(second - start) >= 3 || unchecked(third - start) >= 3 ||
                first == second || first == third || second == third)
                return false;
        }
        return true;
    }
}
