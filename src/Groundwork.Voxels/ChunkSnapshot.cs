namespace Groundwork.Voxels;

/// <summary>One immutable occupancy revision with X as the innermost storage axis.</summary>
public sealed class ChunkSnapshot
{
    public const int Size = 16;
    public const int CellCount = Size * Size * Size;

    private readonly byte[] _cells;

    private ChunkSnapshot(ChunkCoordinate coordinate, ulong revision, byte[] cells)
    {
        Coordinate = coordinate;
        Revision = revision;
        _cells = cells;
    }

    public ChunkCoordinate Coordinate { get; }

    public ulong Revision { get; }

    public byte this[int x, int y, int z]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)x, (uint)Size, nameof(x));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)y, (uint)Size, nameof(y));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)z, (uint)Size, nameof(z));
            return _cells[Index(x, y, z)];
        }
    }

    /// <summary>Copies a stable caller buffer. The scene owner assigns and orders revisions.</summary>
    public static ChunkSnapshot Create(ChunkCoordinate coordinate, ulong revision, ReadOnlySpan<byte> cells)
    {
        if (!coordinate.IsValid)
        {
            throw new ArgumentException("The chunk coordinate is outside the supported grid.", nameof(coordinate));
        }

        ArgumentOutOfRangeException.ThrowIfZero(revision);
        if (cells.Length != CellCount || cells.ContainsAnyExcept((byte)0, (byte)1))
        {
            throw new ArgumentException("A chunk requires exactly 4096 cells of air or solid.", nameof(cells));
        }

        return new(coordinate, revision, cells.ToArray());
    }

    internal bool IsSolid(int index) => _cells[index] != 0;

    internal static int Index(int x, int y, int z) => x + Size * (y + Size * z);
}
