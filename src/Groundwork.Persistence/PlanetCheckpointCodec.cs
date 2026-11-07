using Groundwork.Voxels;
using static System.Buffers.Binary.BinaryPrimitives;

namespace Groundwork.Persistence;

/// <summary>Reads and writes planetary payload schema 2 in the common checkpoint framing.</summary>
public static class PlanetCheckpointCodec
{
    public const ushort SchemaVersion = 2;
    private const int RecipeOffset = SaveCodec.PrefixBytes;
    private const int EditCountOffset = 68;
    private const int PoseOffset = 72;
    private const int HeaderBytes = 128;
    private const int EditBytes = 8;

    public static byte[] Encode(PlanetCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var bytes = new byte[HeaderBytes + checkpoint.Edits.Length * EditBytes + SaveCodec.DigestBytes];
        SaveCodec.WriteFramePrefix(bytes, checkpoint.Identity, SchemaVersion);
        WriteUInt32LittleEndian(bytes.AsSpan(RecipeOffset), checkpoint.Recipe.GeneratorId);
        WriteUInt32LittleEndian(bytes.AsSpan(RecipeOffset + 4), checkpoint.Recipe.GeneratorVersion);
        WriteUInt32LittleEndian(bytes.AsSpan(RecipeOffset + 8), checkpoint.Recipe.Seed);
        WriteUInt32LittleEndian(bytes.AsSpan(EditCountOffset), (uint)checkpoint.Edits.Length);
        WritePose(bytes.AsSpan(PoseOffset), checkpoint.Pose);

        var offset = HeaderBytes;
        foreach (var edit in checkpoint.Edits)
        {
            WriteInt16LittleEndian(bytes.AsSpan(offset), (short)edit.Sample.X);
            WriteInt16LittleEndian(bytes.AsSpan(offset + 2), (short)edit.Sample.Y);
            WriteInt16LittleEndian(bytes.AsSpan(offset + 4), (short)edit.Sample.Z);
            WriteInt16LittleEndian(bytes.AsSpan(offset + 6), edit.DeltaMillimeters);
            offset += EditBytes;
        }

        SaveCodec.SealFrame(bytes);
        return bytes;
    }

    /// <summary>Validates a complete frame before exposing its checkpoint.</summary>
    /// <remarks>Keep the source bytes stable during the call. Before publishing the pose, rebuild the terrain
    /// with the decoded recipe and edits and verify the grounded footprint against that surface.</remarks>
    public static PlanetCheckpointReadResult Decode(ReadOnlySpan<byte> bytes, Func<uint, uint, bool> supportsGenerator)
    {
        ArgumentNullException.ThrowIfNull(supportsGenerator);
        var frame = SaveCodec.ReadFrame(bytes);
        if (frame.Status != SaveReadStatus.Valid)
            return new(frame.Status, frame.Identity, frame.SchemaVersion);
        if (frame.SchemaVersion != SchemaVersion)
            return new(SaveReadStatus.UnsupportedSchema, frame.Identity, frame.SchemaVersion);

        var malformed = new PlanetCheckpointReadResult(SaveReadStatus.Malformed, frame.Identity, SchemaVersion);
        if (bytes.Length < HeaderBytes + SaveCodec.DigestBytes)
            return malformed;
        var count = ReadUInt32LittleEndian(bytes[EditCountOffset..]);
        if (count > PlanetCheckpoint.MaximumEdits || HeaderBytes + count * EditBytes + SaveCodec.DigestBytes != bytes.Length)
            return malformed;

        var recipe = new PlanetSaveRecipe(ReadUInt32LittleEndian(bytes[RecipeOffset..]),
            ReadUInt32LittleEndian(bytes[(RecipeOffset + 4)..]), ReadUInt32LittleEndian(bytes[(RecipeOffset + 8)..]));
        var pose = ReadPose(bytes[PoseOffset..]);
        if (recipe.GeneratorId == 0 || recipe.GeneratorVersion == 0 || !pose.IsValid)
            return malformed;

        var previous = default(PlanetSampleKey);
        for (var i = 0; i < count; i++)
        {
            var edit = ReadEdit(bytes[(HeaderBytes + i * EditBytes)..]);
            if (!edit.IsValid || (i > 0 && PlanetHeightEdit.Compare(previous, edit.Sample) >= 0))
                return malformed;
            previous = edit.Sample;
        }

        if (!supportsGenerator(recipe.GeneratorId, recipe.GeneratorVersion))
            return new(SaveReadStatus.UnsupportedGenerator, frame.Identity, SchemaVersion);

        var edits = new PlanetHeightEdit[(int)count];
        for (var i = 0; i < edits.Length; i++)
            edits[i] = ReadEdit(bytes[(HeaderBytes + i * EditBytes)..]);
        return new(SaveReadStatus.Valid, frame.Identity, SchemaVersion,
            new PlanetCheckpoint(frame.Identity!.Value, recipe, pose, edits));
    }

    private static PlanetHeightEdit ReadEdit(ReadOnlySpan<byte> bytes) =>
        new(new(ReadInt16LittleEndian(bytes), ReadInt16LittleEndian(bytes[2..]), ReadInt16LittleEndian(bytes[4..])),
            ReadInt16LittleEndian(bytes[6..]));

    private static PlanetLandedPose ReadPose(ReadOnlySpan<byte> bytes) =>
        new(new(ReadDoubleLittleEndian(bytes), ReadDoubleLittleEndian(bytes[8..]), ReadDoubleLittleEndian(bytes[16..])),
            new(ReadDoubleLittleEndian(bytes[24..]), ReadDoubleLittleEndian(bytes[32..]), ReadDoubleLittleEndian(bytes[40..]),
                ReadDoubleLittleEndian(bytes[48..])));

    private static void WritePose(Span<byte> bytes, PlanetLandedPose pose)
    {
        WriteDoubleLittleEndian(bytes, pose.Position.X);
        WriteDoubleLittleEndian(bytes[8..], pose.Position.Y);
        WriteDoubleLittleEndian(bytes[16..], pose.Position.Z);
        WriteDoubleLittleEndian(bytes[24..], pose.Orientation.X);
        WriteDoubleLittleEndian(bytes[32..], pose.Orientation.Y);
        WriteDoubleLittleEndian(bytes[40..], pose.Orientation.Z);
        WriteDoubleLittleEndian(bytes[48..], pose.Orientation.W);
    }
}
