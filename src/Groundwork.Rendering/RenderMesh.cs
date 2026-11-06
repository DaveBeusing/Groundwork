using System.Numerics;

namespace Groundwork.Rendering;

/// <summary>An immutable snapshot of local positions and triangle-list indices.</summary>
public sealed class RenderMesh
{
    public const int MaximumVertexCount = 98_304;
    public const int MaximumIndexCount = 147_456;

    private readonly Vector3[] _positions;
    private readonly uint[] _indices;

    private RenderMesh(Vector3[] positions, uint[] indices)
    {
        _positions = positions;
        _indices = indices;
    }

    public ReadOnlySpan<Vector3> Positions => _positions;
    public ReadOnlySpan<uint> Indices => _indices;

    /// <summary>Validates and copies bounded geometry. Keep both inputs stable until this method returns.</summary>
    /// <exception cref="ArgumentException">The positions or indices do not describe a valid mesh.</exception>
    public static RenderMesh Create(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)
    {
        if (positions.Length > MaximumVertexCount)
        {
            throw new ArgumentException("Too many mesh positions.", nameof(positions));
        }

        if (indices.Length > MaximumIndexCount || indices.Length % 3 != 0 || indices.IsEmpty != positions.IsEmpty)
        {
            throw new ArgumentException("Mesh indices must form a bounded triangle list, with both buffers empty for an empty mesh.", nameof(indices));
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

        return new RenderMesh(positions.ToArray(), indices.ToArray());
    }

    internal static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
