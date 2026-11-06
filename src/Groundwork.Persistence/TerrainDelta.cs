using Groundwork.Voxels;

namespace Groundwork.Persistence;

/// <summary>Fills a chunk with the base occupancy for an exact saved world recipe.</summary>
public interface ISaveTerrainGenerator
{
    /// <summary>Writes every cell in X-fast order using only occupancy values 0 and 1.</summary>
    void Generate(SaveWorldRecipe recipe, ChunkCoordinate coordinate, Span<byte> cells);
}

/// <summary>A final occupancy value at an X-fast local index.</summary>
public readonly record struct TerrainCellDelta(ushort Index, byte Occupancy);

/// <summary>An immutable collection of changes for one chunk.</summary>
public sealed class TerrainChunkDelta
{
    private readonly TerrainCellDelta[] _cells;

    /// <summary>Copies a bounded collection for later canonical validation.</summary>
    public TerrainChunkDelta(ChunkCoordinate coordinate, ReadOnlySpan<TerrainCellDelta> cells)
    {
        if (cells.Length > ChunkSnapshot.CellCount)
            throw new InvalidDataException("A chunk delta exceeds the chunk cell count.");

        Coordinate = coordinate;
        _cells = cells.ToArray();
    }

    public ChunkCoordinate Coordinate { get; }
    public ReadOnlySpan<TerrainCellDelta> Cells => _cells;
}

/// <summary>Validates and applies canonical terrain changes relative to a saved recipe.</summary>
public static class TerrainOverlay
{
    /// <summary>Checks the schema limits, canonical order and base occupancy of the changed cells.</summary>
    public static void Validate(SaveWorldRecipe recipe, ReadOnlySpan<TerrainChunkDelta> chunks,
        ISaveTerrainGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ValidateStructure(recipe, chunks);
        ValidateBaseOccupancy(recipe, chunks, generator);
    }

    /// <summary>Regenerates a chunk and applies its validated changes before creating an immutable snapshot.</summary>
    public static ChunkSnapshot CreateSnapshot(SaveWorldRecipe recipe, ChunkCoordinate coordinate, ulong revision,
        ReadOnlySpan<TerrainCellDelta> cells, ISaveTerrainGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentOutOfRangeException.ThrowIfZero(revision);
        ValidateRecipe(recipe);
        ValidateCoordinate(recipe, coordinate);
        ValidateCells(cells);

        Span<byte> occupancy = stackalloc byte[ChunkSnapshot.CellCount];
        GenerateBase(recipe, coordinate, occupancy, generator);
        ValidateBaseOccupancy(cells, occupancy);
        foreach (var cell in cells)
            occupancy[cell.Index] = cell.Occupancy;

        return ChunkSnapshot.Create(coordinate, revision, occupancy);
    }

    internal static void ValidateRecipe(SaveWorldRecipe recipe)
    {
        if (recipe.GeneratorId == 0 || recipe.GeneratorVersion == 0)
            throw new InvalidDataException("Generator ID and version must be nonzero.");

        var bounds = recipe.Bounds;
        if (bounds.MinX > bounds.MaxX || bounds.MinZ > bounds.MaxZ ||
            !new ChunkCoordinate(bounds.MinX, 0, bounds.MinZ).IsValid ||
            !new ChunkCoordinate(bounds.MaxX, 0, bounds.MaxZ).IsValid)
            throw new InvalidDataException("The world recipe has invalid chunk bounds.");
    }

    internal static void ValidateStructure(SaveWorldRecipe recipe, ReadOnlySpan<TerrainChunkDelta> chunks)
    {
        ValidateRecipe(recipe);
        if (chunks.Length > SaveCodec.MaximumChangedChunks)
            throw new InvalidDataException("The terrain overlay exceeds the changed chunk limit.");

        ChunkCoordinate? previous = null;
        var remaining = SaveCodec.MaximumEditedCells;
        foreach (var chunk in chunks)
        {
            if (chunk is null)
                throw new InvalidDataException("The terrain overlay contains a missing chunk record.");

            var coordinate = chunk.Coordinate;
            ValidateCoordinate(recipe, coordinate);
            if (previous is { } prior && CompareCoordinates(prior, coordinate) >= 0)
                throw new InvalidDataException("Chunk records must be unique and sorted by signed X, Y and Z.");
            previous = coordinate;

            var cells = chunk.Cells;
            if (cells.IsEmpty)
                throw new InvalidDataException("A changed chunk must have at least one cell record.");
            if (cells.Length > remaining)
                throw new InvalidDataException("The terrain overlay exceeds the edited cell limit.");
            remaining -= cells.Length;
            ValidateCells(cells);
        }
    }

    internal static void ValidateBaseOccupancy(SaveWorldRecipe recipe, ReadOnlySpan<TerrainChunkDelta> chunks,
        ISaveTerrainGenerator generator)
    {
        Span<byte> occupancy = stackalloc byte[ChunkSnapshot.CellCount];
        foreach (var chunk in chunks)
        {
            GenerateBase(recipe, chunk.Coordinate, occupancy, generator);
            ValidateBaseOccupancy(chunk.Cells, occupancy);
        }
    }

    private static void ValidateCoordinate(SaveWorldRecipe recipe, ChunkCoordinate coordinate)
    {
        if (!recipe.Bounds.Contains(coordinate))
            throw new InvalidDataException("The chunk coordinate is outside the saved world.");
    }

    private static void ValidateCells(ReadOnlySpan<TerrainCellDelta> cells)
    {
        var previousIndex = -1;
        foreach (var cell in cells)
        {
            if (!IsCanonicalCell(cell.Index, cell.Occupancy, previousIndex))
                throw new InvalidDataException("Cell records require unique ascending indices and occupancy 0 or 1.");
            previousIndex = cell.Index;
        }
    }

    internal static bool IsCanonicalCell(ushort index, byte occupancy, int previousIndex) =>
        index < ChunkSnapshot.CellCount && index > previousIndex && occupancy <= 1;

    internal static int CompareCoordinates(ChunkCoordinate left, ChunkCoordinate right)
    {
        var x = left.X.CompareTo(right.X);
        if (x != 0)
            return x;
        var y = left.Y.CompareTo(right.Y);
        return y != 0 ? y : left.Z.CompareTo(right.Z);
    }

    private static void ValidateBaseOccupancy(ReadOnlySpan<TerrainCellDelta> cells, ReadOnlySpan<byte> occupancy)
    {
        foreach (var cell in cells)
            if (cell.Occupancy == occupancy[cell.Index])
                throw new InvalidDataException("A cell change repeats the generated base occupancy.");
    }

    private static void GenerateBase(SaveWorldRecipe recipe, ChunkCoordinate coordinate, Span<byte> occupancy,
        ISaveTerrainGenerator generator)
    {
        // Detect a generator that leaves any cell unwritten, even when the buffer is reused.
        occupancy.Fill(byte.MaxValue);
        try
        {
            generator.Generate(recipe, coordinate, occupancy);
        }
        catch (InvalidDataException error)
        {
            throw new InvalidOperationException("The terrain generator failed while producing base occupancy.", error);
        }

        if (occupancy.ContainsAnyExcept((byte)0, (byte)1))
            throw new InvalidOperationException("The terrain generator must write every cell with occupancy 0 or 1.");
    }
}
