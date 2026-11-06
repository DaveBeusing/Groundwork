using System.Numerics;

namespace Groundwork.Voxels;

/// <summary>One observed neighbor in a six-face meshing input.</summary>
public readonly record struct ChunkNeighbor
{
    public ChunkNeighbor(ChunkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
    }

    private ChunkNeighbor(bool isExterior) => IsExterior = isExterior;

    /// <summary>Known air beyond the generated world. The default value represents missing data.</summary>
    public static ChunkNeighbor ExteriorAir { get; } = new(true);

    internal ChunkSnapshot? Snapshot { get; }
    internal bool IsExterior { get; }
}

/// <summary>Builds deterministic solid-to-air surfaces for chunks and face-adjacent pairs.</summary>
public static class ChunkMesher
{
    public const int MaximumQuadsPerChunk = 6 * ChunkSnapshot.CellCount;
    public const int MaximumVerticesPerChunk = 4 * MaximumQuadsPerChunk;
    public const int MaximumIndicesPerChunk = 6 * MaximumQuadsPerChunk;

    private static readonly Face[] Faces =
    [
        new(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
        new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
        new(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
        new(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX),
        new(-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX),
        new(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
    ];

    public static ChunkMeshPair BuildPair(ChunkSnapshot first, ChunkSnapshot second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var a = first.Coordinate;
        var b = second.Coordinate;
        var x = b.X - a.X;
        var y = b.Y - a.Y;
        var z = b.Z - a.Z;
        if (Math.Abs(x) + Math.Abs(y) + Math.Abs(z) != 1)
        {
            throw new ArgumentException("The chunks must share exactly one face.", nameof(second));
        }

        if (x + y + z < 0)
        {
            (first, second) = (second, first);
        }

        // BuildPair has always treated the other five sides as exterior air.
        var exterior = ChunkNeighbor.ExteriorAir;
        Span<ChunkNeighbor> neighbors = [exterior, exterior, exterior, exterior, exterior, exterior];
        var negativeFace = x != 0 ? 0 : y != 0 ? 2 : 4;
        neighbors[negativeFace + 1] = new(second);
        var low = BuildMesh(first, neighbors);
        neighbors[negativeFace + 1] = exterior;
        neighbors[negativeFace] = new(first);
        return new(first, second, low, BuildMesh(second, neighbors));
    }

    /// <summary>Builds a chunk with observations in -X, +X, -Y, +Y, -Z, +Z order.</summary>
    /// <exception cref="ArgumentException">A face is missing or contains a snapshot at the wrong coordinate.</exception>
    public static ChunkMesh Build(ChunkSnapshot chunk, ReadOnlySpan<ChunkNeighbor> neighbors)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (neighbors.Length != Faces.Length)
        {
            throw new ArgumentException("Exactly six face observations are required.", nameof(neighbors));
        }

        for (var face = 0; face < Faces.Length; face++)
        {
            var neighbor = neighbors[face];
            if (neighbor.Snapshot is { } snapshot)
            {
                var normal = Faces[face].Normal;
                var coordinate = chunk.Coordinate;
                var expected = new ChunkCoordinate(coordinate.X + (int)normal.X,
                    coordinate.Y + (int)normal.Y, coordinate.Z + (int)normal.Z);
                if (snapshot.Coordinate != expected)
                {
                    throw new ArgumentException($"Face {face} requires neighbor {expected}.", nameof(neighbors));
                }
            }
            else if (!neighbor.IsExterior)
            {
                throw new ArgumentException($"Face {face} has no neighbor observation.", nameof(neighbors));
            }
        }

        return BuildMesh(chunk, neighbors);
    }

    private static ChunkMesh BuildMesh(ChunkSnapshot chunk, ReadOnlySpan<ChunkNeighbor> neighbors)
    {
        // Remember visible faces so the output arrays can be allocated at their exact size.
        Span<byte> visibility = stackalloc byte[ChunkSnapshot.CellCount];
        var quadCount = 0;
        for (var cell = 0; cell < visibility.Length; cell++)
        {
            byte mask = 0;
            if (chunk.IsSolid(cell))
            {
                var position = LocalPosition(cell);
                for (var face = 0; face < Faces.Length; face++)
                {
                    if (!IsSolidNeighbor(chunk, neighbors[face].Snapshot, position, Faces[face].Normal))
                    {
                        mask |= (byte)(1 << face);
                        quadCount++;
                    }
                }
            }

            visibility[cell] = mask;
        }

        if (quadCount > MaximumQuadsPerChunk)
        {
            throw new InvalidOperationException("The chunk mesh exceeds its geometry budget.");
        }

        if (quadCount == 0)
        {
            return new(chunk, [], []);
        }

        var positions = new Vector3[checked(quadCount * 4)];
        var indices = new uint[checked(quadCount * 6)];
        var quad = 0;
        for (var cell = 0; cell < visibility.Length; cell++)
        {
            var mask = visibility[cell];
            if (mask == 0)
            {
                continue;
            }

            var position = LocalPosition(cell);
            for (var face = 0; face < Faces.Length; face++)
            {
                if ((mask & (1 << face)) != 0)
                {
                    WriteQuad(positions, indices, quad++, position, Faces[face]);
                }
            }
        }

        return new(chunk, positions, indices);
    }

    private static Vector3 LocalPosition(int cell)
    {
        const int size = ChunkSnapshot.Size;
        return new(cell % size, cell / size % size, cell / (size * size));
    }

    private static bool IsSolidNeighbor(ChunkSnapshot chunk, ChunkSnapshot? neighbor, Vector3 position, Vector3 normal)
    {
        const int size = ChunkSnapshot.Size;
        var x = (int)(position.X + normal.X);
        var y = (int)(position.Y + normal.Y);
        var z = (int)(position.Z + normal.Z);
        if ((uint)x < size && (uint)y < size && (uint)z < size)
        {
            return chunk.IsSolid(ChunkSnapshot.Index(x, y, z));
        }

        return neighbor is not null && neighbor.IsSolid(ChunkSnapshot.Index(
            x - size * (int)normal.X, y - size * (int)normal.Y, z - size * (int)normal.Z));
    }

    private static void WriteQuad(Vector3[] positions, uint[] indices, int quad, Vector3 position, Face face)
    {
        var corner = position + Vector3.Max(Vector3.Zero, face.Normal);
        var vertex = quad * 4;
        positions[vertex] = corner;
        positions[vertex + 1] = corner + face.Tangent;
        positions[vertex + 2] = corner + face.Tangent + face.Bitangent;
        positions[vertex + 3] = corner + face.Bitangent;

        var index = quad * 6;
        var first = (uint)vertex;
        indices[index] = first;
        indices[index + 1] = first + 1;
        indices[index + 2] = first + 2;
        indices[index + 3] = first;
        indices[index + 4] = first + 2;
        indices[index + 5] = first + 3;
    }

    private readonly record struct Face(Vector3 Normal, Vector3 Tangent, Vector3 Bitangent);
}
