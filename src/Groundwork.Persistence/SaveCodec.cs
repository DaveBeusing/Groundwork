using System.Security.Cryptography;
using Groundwork.Voxels;
using static System.Buffers.Binary.BinaryPrimitives;

namespace Groundwork.Persistence;

/// <summary>Reads and writes the bounded, little-endian WP06 checkpoint format.</summary>
public static class SaveCodec
{
    public const int MaximumFrameBytes = 2 * 1024 * 1024;
    public const int MaximumChangedChunks = 1024;
    public const int MaximumEditedCells = 262144;
    public const int MaximumGamePayloadBytes = 256 * 1024;

    private const ushort FramingVersion = 1;
    private const ushort SchemaVersion = 1;
    internal const int PrefixBytes = 56;
    private const int HeaderBytes = 100;
    private const int ChunkHeaderBytes = 16;
    private const int CellRecordBytes = 4;
    internal const int DigestBytes = 32;
    private static ReadOnlySpan<byte> Magic => "GWSAVE01"u8;

    /// <summary>Encodes a canonical checkpoint using the generator registered for its exact recipe.</summary>
    public static byte[] Encode(SaveCheckpoint checkpoint, ISaveTerrainGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(generator);
        ValidateIdentity(checkpoint.Identity);

        TerrainOverlay.Validate(checkpoint.Recipe, checkpoint.Terrain, generator);
        var editCount = 0;
        foreach (var chunk in checkpoint.Terrain)
            editCount = checked(editCount + chunk.Cells.Length);

        var length = checked(HeaderBytes + checkpoint.Terrain.Length * ChunkHeaderBytes +
            editCount * CellRecordBytes + checkpoint.GamePayload.Length + DigestBytes);
        if (length > MaximumFrameBytes)
            throw new InvalidDataException("The checkpoint exceeds the frame size limit.");

        var bytes = new byte[length];
        WriteHeader(bytes, checkpoint, editCount);
        var offset = HeaderBytes;
        foreach (var chunk in checkpoint.Terrain)
        {
            WriteInt32LittleEndian(bytes.AsSpan(offset), chunk.Coordinate.X);
            WriteInt32LittleEndian(bytes.AsSpan(offset + 4), chunk.Coordinate.Y);
            WriteInt32LittleEndian(bytes.AsSpan(offset + 8), chunk.Coordinate.Z);
            WriteUInt32LittleEndian(bytes.AsSpan(offset + 12), (uint)chunk.Cells.Length);
            offset += ChunkHeaderBytes;
            foreach (var cell in chunk.Cells)
            {
                WriteUInt16LittleEndian(bytes.AsSpan(offset), cell.Index);
                bytes[offset + 2] = cell.Occupancy;
                // The reserved byte remains zero in the new array.
                offset += CellRecordBytes;
            }
        }

        checkpoint.GamePayload.CopyTo(bytes.AsSpan(offset));
        SealFrame(bytes);
        return bytes;
    }

    /// <summary>Validates a complete frame before exposing its checkpoint.</summary>
    /// <remarks>Keep the input bytes stable during this call. Game payload interpretation belongs to the caller.</remarks>
    public static SaveReadResult Decode(ReadOnlySpan<byte> bytes, Func<uint, uint, ISaveTerrainGenerator?> resolveGenerator)
    {
        ArgumentNullException.ThrowIfNull(resolveGenerator);
        var frame = ReadFrame(bytes);
        if (frame.Status != SaveReadStatus.Valid)
            return frame;
        if (frame.SchemaVersion != SchemaVersion)
            return new SaveReadResult(SaveReadStatus.UnsupportedSchema, frame.Identity, frame.SchemaVersion);
        return DecodeSchemaOne(bytes, frame.Identity!.Value, resolveGenerator);
    }

    internal static SaveReadResult ReadFrame(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 10 || !bytes[..8].SequenceEqual(Magic))
            return new SaveReadResult(SaveReadStatus.Malformed);
        if (ReadUInt16LittleEndian(bytes[8..]) != FramingVersion)
            return new SaveReadResult(SaveReadStatus.UnsupportedFraming);
        if (bytes.Length is < PrefixBytes + DigestBytes or > MaximumFrameBytes ||
            ReadUInt32LittleEndian(bytes[12..]) != bytes.Length || !HasValidDigest(bytes))
            return new SaveReadResult(SaveReadStatus.Malformed);

        var identity = new SaveFrameIdentity(ReadUInt64LittleEndian(bytes[16..]),
            new Guid(bytes.Slice(24, 16), bigEndian: true), new Guid(bytes.Slice(40, 16), bigEndian: true));
        if (!IsValidIdentity(identity))
            return new SaveReadResult(SaveReadStatus.Malformed);

        var schema = ReadUInt16LittleEndian(bytes[10..]);
        if (schema == 0)
            return new SaveReadResult(SaveReadStatus.Malformed, identity, schema);
        return new SaveReadResult(SaveReadStatus.Valid, identity, schema);
    }

    private static SaveReadResult DecodeSchemaOne(ReadOnlySpan<byte> bytes, SaveFrameIdentity identity,
        Func<uint, uint, ISaveTerrainGenerator?> resolveGenerator)
    {
        var malformed = new SaveReadResult(SaveReadStatus.Malformed, identity, SchemaVersion);
        if (bytes.Length < HeaderBytes + DigestBytes)
            return malformed;

        var recipe = new SaveWorldRecipe(ReadUInt32LittleEndian(bytes[56..]), ReadUInt32LittleEndian(bytes[60..]),
            ReadUInt32LittleEndian(bytes[64..]), new RegionBounds(ReadInt32LittleEndian(bytes[68..]),
                ReadInt32LittleEndian(bytes[72..]), ReadInt32LittleEndian(bytes[76..]), ReadInt32LittleEndian(bytes[80..])));
        var chunkCount = ReadUInt32LittleEndian(bytes[84..]);
        var editCount = ReadUInt32LittleEndian(bytes[88..]);
        var gameSchema = ReadUInt32LittleEndian(bytes[92..]);
        var gameBytes = ReadUInt32LittleEndian(bytes[96..]);
        if (chunkCount > MaximumChangedChunks || editCount > MaximumEditedCells ||
            gameBytes > MaximumGamePayloadBytes ||
            HeaderBytes + (long)chunkCount * ChunkHeaderBytes + (long)editCount * CellRecordBytes + gameBytes + DigestBytes != bytes.Length)
            return malformed;

        var terrainBytes = bytes.Slice(HeaderBytes, bytes.Length - HeaderBytes - DigestBytes - (int)gameBytes);
        try
        {
            TerrainOverlay.ValidateRecipe(recipe);
        }
        catch (InvalidDataException)
        {
            return malformed;
        }

        // Validate raw counts, ordering and bounds before using any record count to allocate.
        if (!ReadTerrain(terrainBytes, recipe, (int)chunkCount, (int)editCount, destination: null))
            return malformed;

        var generator = resolveGenerator(recipe.GeneratorId, recipe.GeneratorVersion);
        if (generator is null)
            return new SaveReadResult(SaveReadStatus.UnsupportedGenerator, identity, SchemaVersion, recipe);

        var terrain = new TerrainChunkDelta[(int)chunkCount];
        if (!ReadTerrain(terrainBytes, recipe, terrain.Length, (int)editCount, terrain))
            return malformed;

        try
        {
            TerrainOverlay.ValidateBaseOccupancy(recipe, terrain, generator);
        }
        catch (InvalidDataException)
        {
            return malformed;
        }

        var gamePayload = bytes.Slice(HeaderBytes + terrainBytes.Length, (int)gameBytes);
        var checkpoint = new SaveCheckpoint(identity, recipe, terrain, gameSchema, gamePayload);
        return new SaveReadResult(SaveReadStatus.Valid, identity, SchemaVersion, recipe, checkpoint);
    }

    private static bool ReadTerrain(ReadOnlySpan<byte> bytes, SaveWorldRecipe recipe, int chunkCount, int editCount,
        TerrainChunkDelta[]? destination)
    {
        var offset = 0;
        var previousChunk = default(ChunkCoordinate);
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            if (bytes.Length - offset < ChunkHeaderBytes)
                return false;

            var coordinate = new ChunkCoordinate(ReadInt32LittleEndian(bytes[offset..]),
                ReadInt32LittleEndian(bytes[(offset + 4)..]), ReadInt32LittleEndian(bytes[(offset + 8)..]));
            var count = ReadUInt32LittleEndian(bytes[(offset + 12)..]);
            offset += ChunkHeaderBytes;
            if (!recipe.Bounds.Contains(coordinate) || count is 0 or > ChunkSnapshot.CellCount ||
                count > editCount || count * CellRecordBytes > bytes.Length - offset ||
                (chunkIndex > 0 && TerrainOverlay.CompareCoordinates(previousChunk, coordinate) >= 0))
                return false;

            var cells = destination is null ? null : new TerrainCellDelta[(int)count];
            var previousIndex = -1;
            for (var cell = 0; cell < (int)count; cell++)
            {
                var index = ReadUInt16LittleEndian(bytes[offset..]);
                var occupancy = bytes[offset + 2];
                if (!TerrainOverlay.IsCanonicalCell(index, occupancy, previousIndex) || bytes[offset + 3] != 0)
                    return false;

                if (cells is not null)
                    cells[cell] = new TerrainCellDelta(index, occupancy);
                previousIndex = index;
                offset += CellRecordBytes;
            }

            if (destination is not null)
                destination[chunkIndex] = new TerrainChunkDelta(coordinate, cells);
            previousChunk = coordinate;
            editCount -= (int)count;
        }

        return editCount == 0 && offset == bytes.Length;
    }

    private static bool HasValidDigest(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[DigestBytes];
        SHA256.HashData(bytes[..^DigestBytes], digest);
        return CryptographicOperations.FixedTimeEquals(digest, bytes[^DigestBytes..]);
    }

    private static bool IsValidIdentity(SaveFrameIdentity identity) =>
        identity.Generation != 0 && identity.WorldLineageId != Guid.Empty && identity.SaveRequestId != Guid.Empty;

    internal static void ValidateIdentity(SaveFrameIdentity identity)
    {
        if (!IsValidIdentity(identity))
            throw new InvalidDataException("The checkpoint requires a nonzero commit identity.");
    }

    internal static void SealFrame(Span<byte> bytes) => SHA256.HashData(bytes[..^DigestBytes], bytes[^DigestBytes..]);

    internal static void WriteFramePrefix(Span<byte> bytes, SaveFrameIdentity identity, ushort schemaVersion)
    {
        Magic.CopyTo(bytes);
        WriteUInt16LittleEndian(bytes[8..], FramingVersion);
        WriteUInt16LittleEndian(bytes[10..], schemaVersion);
        WriteUInt32LittleEndian(bytes[12..], (uint)bytes.Length);
        WriteUInt64LittleEndian(bytes[16..], identity.Generation);
        identity.WorldLineageId.TryWriteBytes(bytes.Slice(24, 16), bigEndian: true, out _);
        identity.SaveRequestId.TryWriteBytes(bytes.Slice(40, 16), bigEndian: true, out _);
    }

    private static void WriteHeader(Span<byte> bytes, SaveCheckpoint checkpoint, int editCount)
    {
        WriteFramePrefix(bytes, checkpoint.Identity, SchemaVersion);
        var recipe = checkpoint.Recipe;
        WriteUInt32LittleEndian(bytes[56..], recipe.GeneratorId);
        WriteUInt32LittleEndian(bytes[60..], recipe.GeneratorVersion);
        WriteUInt32LittleEndian(bytes[64..], recipe.Seed);
        WriteInt32LittleEndian(bytes[68..], recipe.Bounds.MinX);
        WriteInt32LittleEndian(bytes[72..], recipe.Bounds.MaxX);
        WriteInt32LittleEndian(bytes[76..], recipe.Bounds.MinZ);
        WriteInt32LittleEndian(bytes[80..], recipe.Bounds.MaxZ);
        WriteUInt32LittleEndian(bytes[84..], (uint)checkpoint.Terrain.Length);
        WriteUInt32LittleEndian(bytes[88..], (uint)editCount);
        WriteUInt32LittleEndian(bytes[92..], checkpoint.GameSchemaVersion);
        WriteUInt32LittleEndian(bytes[96..], (uint)checkpoint.GamePayload.Length);
    }
}
