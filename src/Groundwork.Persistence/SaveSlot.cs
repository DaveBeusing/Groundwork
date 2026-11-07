namespace Groundwork.Persistence;

/// <summary>Stores one local Windows NTFS checkpoint with alternating committed files.</summary>
/// <remarks>
/// Supply an application-owned directory. Call outside simulation barriers on a host-tracked I/O worker.
/// A game validator may check application compatibility and reconstructed state before selection or publication.
/// </remarks>
public sealed class SaveSlot
{
    private readonly CheckpointSlot<SaveCheckpoint, SaveWorldRecipe> _slot;

    public SaveSlot(string directory, Func<uint, uint, ISaveTerrainGenerator?> resolveGenerator,
        Func<SaveCheckpoint, SaveGameValidation>? validateGame = null)
    {
        ArgumentNullException.ThrowIfNull(resolveGenerator);
        _slot = new(directory, Decode, Encode, static checkpoint => checkpoint.Identity,
            static checkpoint => checkpoint.Recipe, validateGame ?? (static _ => SaveGameValidation.Valid));

        SlotDecode<SaveCheckpoint, SaveWorldRecipe> Decode(byte[] bytes)
        {
            var result = SaveCodec.Decode(bytes, resolveGenerator);
            return new(result.Status, result.Identity, result.Recipe, result.Checkpoint);
        }

        byte[]? Encode(SaveCheckpoint checkpoint)
        {
            var generator = resolveGenerator(checkpoint.Recipe.GeneratorId, checkpoint.Recipe.GeneratorVersion);
            return generator is null ? null : SaveCodec.Encode(checkpoint, generator);
        }
    }

    /// <summary>Loads the newest usable committed checkpoint while holding the slot lease.</summary>
    public SaveSlotResult Load() => Convert(_slot.Load());

    /// <summary>Publishes a checkpoint with generation expectedGeneration + 1.</summary>
    public SaveSlotResult Save(ulong expectedGeneration, SaveCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
        Convert(_slot.Save(expectedGeneration, checkpoint, observe: null, cancellationToken));

    internal SaveSlotResult Save(ulong expectedGeneration, SaveCheckpoint checkpoint, Action<SaveWriteStage> observe,
        CancellationToken cancellationToken = default) => Convert(_slot.Save(expectedGeneration, checkpoint, observe, cancellationToken));

    private static SaveSlotResult Convert(SlotResult<SaveCheckpoint> result) =>
        new(result.Status, result.Checkpoint, result.Identity, result.Recovery, result.Error);
}
