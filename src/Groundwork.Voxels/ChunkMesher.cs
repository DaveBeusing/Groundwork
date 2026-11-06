using System.Numerics;

namespace Groundwork.Voxels;

/// <summary>Builds deterministic solid-to-air surfaces for one face-adjacent chunk pair.</summary>
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

        var direction = new Vector3(x, y, z);
        if (x + y + z < 0)
        {
            (first, second) = (second, first);
            direction = -direction;
        }

        return new(first, second, BuildMesh(first, second, direction), BuildMesh(second, first, -direction));
    }

    private static ChunkMesh BuildMesh(ChunkSnapshot chunk, ChunkSnapshot neighbor, Vector3 neighborDirection)
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
                    if (!IsSolidNeighbor(chunk, neighbor, position, Faces[face].Normal, neighborDirection))
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

    private static bool IsSolidNeighbor(ChunkSnapshot chunk, ChunkSnapshot neighbor, Vector3 position, Vector3 normal,
        Vector3 neighborDirection)
    {
        const int size = ChunkSnapshot.Size;
        var x = (int)(position.X + normal.X);
        var y = (int)(position.Y + normal.Y);
        var z = (int)(position.Z + normal.Z);
        if ((uint)x < size && (uint)y < size && (uint)z < size)
        {
            return chunk.IsSolid(ChunkSnapshot.Index(x, y, z));
        }

        return normal == neighborDirection && neighbor.IsSolid(ChunkSnapshot.Index(
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
