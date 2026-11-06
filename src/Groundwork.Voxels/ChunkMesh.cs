using System.Numerics;

namespace Groundwork.Voxels;

/// <summary>The immutable, chunk-local surface geometry for one occupancy revision.</summary>
public sealed class ChunkMesh
{
    private readonly Vector3[] _positions;
    private readonly uint[] _indices;

    internal ChunkMesh(ChunkSnapshot snapshot, Vector3[] positions, uint[] indices)
    {
        Coordinate = snapshot.Coordinate;
        Revision = snapshot.Revision;
        _positions = positions;
        _indices = indices;
    }

    public ChunkCoordinate Coordinate { get; }

    public ulong Revision { get; }

    public ReadOnlySpan<Vector3> Positions => _positions;

    public ReadOnlySpan<uint> Indices => _indices;
}
