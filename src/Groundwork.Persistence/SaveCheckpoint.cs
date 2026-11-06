using Groundwork.Voxels;

namespace Groundwork.Persistence;

/// <summary>The immutable generator selection and finite chunk bounds of a world lineage.</summary>
public readonly record struct SaveWorldRecipe(uint GeneratorId, uint GeneratorVersion, uint Seed, RegionBounds Bounds);

/// <summary>Commit metadata common to all payload schemas in framing version 1.</summary>
public readonly record struct SaveFrameIdentity(ulong Generation, Guid WorldLineageId, Guid SaveRequestId);

/// <summary>An immutable world overlay and application checkpoint captured together.</summary>
public sealed class SaveCheckpoint
{
    private readonly TerrainChunkDelta[] _terrain;
    private readonly byte[] _gamePayload;

    public SaveCheckpoint(SaveFrameIdentity identity, SaveWorldRecipe recipe, ReadOnlySpan<TerrainChunkDelta> terrain,
        uint gameSchemaVersion, ReadOnlySpan<byte> gamePayload)
    {
        if (terrain.Length > SaveCodec.MaximumChangedChunks || gamePayload.Length > SaveCodec.MaximumGamePayloadBytes)
            throw new InvalidDataException("The checkpoint exceeds the terrain or game payload limit.");

        Identity = identity;
        Recipe = recipe;
        GameSchemaVersion = gameSchemaVersion;
        _terrain = terrain.ToArray();
        _gamePayload = gamePayload.ToArray();
    }

    public SaveFrameIdentity Identity { get; }
    public SaveWorldRecipe Recipe { get; }
    public ReadOnlySpan<TerrainChunkDelta> Terrain => _terrain;
    public uint GameSchemaVersion { get; }
    public ReadOnlySpan<byte> GamePayload => _gamePayload;
}

public enum SaveReadStatus
{
    Malformed,
    Valid,
    UnsupportedFraming,
    UnsupportedSchema,
    UnsupportedGenerator,
}

/// <summary>The decode outcome. Verified framing metadata can be available without a decoded checkpoint.</summary>
public readonly record struct SaveReadResult
{
    internal SaveReadResult(SaveReadStatus status, SaveFrameIdentity? identity = null,
        ushort? schemaVersion = null, SaveWorldRecipe? recipe = null, SaveCheckpoint? checkpoint = null)
    {
        Status = status;
        Identity = identity;
        SchemaVersion = schemaVersion;
        Recipe = recipe;
        Checkpoint = checkpoint;
    }

    public SaveReadStatus Status { get; }
    public SaveFrameIdentity? Identity { get; }
    public ushort? SchemaVersion { get; }
    /// <summary>The structurally verified recipe, including when its generator is unavailable.</summary>
    public SaveWorldRecipe? Recipe { get; }
    public SaveCheckpoint? Checkpoint { get; }
}
