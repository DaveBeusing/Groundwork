namespace Groundwork.Persistence;

/// <summary>Publishes planetary checkpoints to a dedicated Windows NTFS slot.</summary>
/// <remarks>Supply an application-owned directory separate from flat save slots. Call outside
/// simulation barriers on a host-tracked I/O worker. The validator must rebuild terrain from
/// the recipe and edits and verify the landed footprint before selection or publication.</remarks>
public sealed class PlanetSaveSlot
{
    private readonly CheckpointSlot<PlanetCheckpoint, PlanetSaveRecipe> _slot;

    public PlanetSaveSlot(string directory, Func<uint, uint, bool> supportsGenerator,
        Func<PlanetCheckpoint, SaveGameValidation> validateLandedPose)
    {
        ArgumentNullException.ThrowIfNull(supportsGenerator);
        ArgumentNullException.ThrowIfNull(validateLandedPose);
        _slot = new(directory, Decode, Encode, static checkpoint => checkpoint.Identity,
            static checkpoint => checkpoint.Recipe, validateLandedPose);

        SlotDecode<PlanetCheckpoint, PlanetSaveRecipe> Decode(byte[] bytes)
        {
            var result = PlanetCheckpointCodec.Decode(bytes, supportsGenerator);
            return new(result.Status, result.Identity, result.Recipe, result.Checkpoint);
        }

        byte[]? Encode(PlanetCheckpoint checkpoint) => supportsGenerator(checkpoint.Recipe.GeneratorId, checkpoint.Recipe.GeneratorVersion)
            ? PlanetCheckpointCodec.Encode(checkpoint) : null;
    }

    /// <summary>Loads and validates the newest usable committed checkpoint under the slot lease.</summary>
    public PlanetSaveSlotResult Load() => Convert(_slot.Load());

    /// <summary>Publishes a checkpoint with generation expectedGeneration + 1.</summary>
    public PlanetSaveSlotResult Save(ulong expectedGeneration, PlanetCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
        Convert(_slot.Save(expectedGeneration, checkpoint, observe: null, cancellationToken));

    internal PlanetSaveSlotResult Save(ulong expectedGeneration, PlanetCheckpoint checkpoint, Action<SaveWriteStage> observe,
        CancellationToken cancellationToken = default) => Convert(_slot.Save(expectedGeneration, checkpoint, observe, cancellationToken));

    private static PlanetSaveSlotResult Convert(SlotResult<PlanetCheckpoint> result) =>
        new(result.Status, result.Checkpoint, result.Identity, result.Recovery, result.Error);
}

public sealed record PlanetSaveSlotResult(SaveSlotStatus Status, PlanetCheckpoint? Checkpoint = null,
    SaveFrameIdentity? Identity = null, SaveRecoveryEvidence? Recovery = null, Exception? Error = null);
