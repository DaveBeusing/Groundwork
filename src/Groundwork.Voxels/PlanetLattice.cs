using System.Buffers.Binary;

namespace Groundwork.Voxels;

/// <summary>A signed lattice vertex. Its components are independent of chart, LOD and frame.</summary>
public readonly record struct LatticeCoordinate(long X, long Y, long Z)
{
    /// <summary>
    /// Finds the canonical brick and local index on each axis by Euclidean division.
    /// A shared boundary vertex uses local index zero in its canonical brick.
    /// </summary>
    public (LatticeBrick Brick, int X, int Y, int Z) Locate()
    {
        var (bx, x) = Split(X);
        var (by, y) = Split(Y);
        var (bz, z) = Split(Z);
        return (new LatticeBrick(bx, by, bz), x, y, z);
    }

    private static (long Brick, int Local) Split(long axis)
    {
        var (brick, local) = Math.DivRem(axis, PlanetDescriptor.BrickEdge);
        if (local < 0)
            return (brick - 1, (int)local + PlanetDescriptor.BrickEdge);
        return (brick, (int)local);
    }
}

/// <summary>A brick on the same planet-centred lattice, with 32 cells per axis.</summary>
public readonly record struct LatticeBrick(long X, long Y, long Z)
{
    /// <summary>Resolves a brick vertex, including the shared upper boundary at index 32.</summary>
    public LatticeCoordinate Vertex(int x, int y, int z) =>
        new(Resolve(X, x), Resolve(Y, y), Resolve(Z, z));

    private static long Resolve(long brick, int local)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(local);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(local, PlanetDescriptor.BrickEdge);
        return checked(brick * PlanetDescriptor.BrickEdge + local);
    }
}

/// <summary>A canonical sample scoped to a world and planet, independent of its generated value.</summary>
public readonly record struct PlanetLatticeSample
{
    public const int EncodedSize = 56;

    public PlanetLatticeSample(Guid worldId, Guid planetId, LatticeCoordinate vertex)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(worldId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(planetId, Guid.Empty);
        WorldId = worldId;
        PlanetId = planetId;
        Vertex = vertex;
    }

    public Guid WorldId { get; }
    public Guid PlanetId { get; }
    public LatticeCoordinate Vertex { get; }

    /// <summary>Writes two network-order UUIDs followed by three little-endian signed Int64 coordinates.</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length != EncodedSize)
            throw new ArgumentException("Expected an exact sample-sized buffer.", nameof(destination));
        if (WorldId == Guid.Empty || PlanetId == Guid.Empty)
            throw new InvalidOperationException("The sample has no world or planet identity.");

        WorldId.TryWriteBytes(destination[..16], bigEndian: true, out _);
        PlanetId.TryWriteBytes(destination[16..32], bigEndian: true, out _);
        BinaryPrimitives.WriteInt64LittleEndian(destination[32..40], Vertex.X);
        BinaryPrimitives.WriteInt64LittleEndian(destination[40..48], Vertex.Y);
        BinaryPrimitives.WriteInt64LittleEndian(destination[48..56], Vertex.Z);
    }

    public static PlanetLatticeSample Read(ReadOnlySpan<byte> source)
    {
        if (source.Length != EncodedSize)
            throw new ArgumentException("Expected an exact sample-sized buffer.", nameof(source));

        return new PlanetLatticeSample(new Guid(source[..16], bigEndian: true), new Guid(source[16..32], bigEndian: true),
            new LatticeCoordinate(BinaryPrimitives.ReadInt64LittleEndian(source[32..40]),
                BinaryPrimitives.ReadInt64LittleEndian(source[40..48]),
                BinaryPrimitives.ReadInt64LittleEndian(source[48..56])));
    }
}
