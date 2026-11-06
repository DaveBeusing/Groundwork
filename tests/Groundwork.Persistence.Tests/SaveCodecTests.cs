using System.Buffers.Binary;
using System.Security.Cryptography;
using Groundwork.Voxels;

namespace Groundwork.Persistence.Tests;

[TestClass]
public sealed class SaveCodecTests
{
    private const int FooterSize = 32;
    private const int GameLengthOffset = 96;
    private const int ChangedChunkCountOffset = 84;
    private const int EditedCellCountOffset = 88;
    private const int FirstChunkOffset = 100;
    private const int FirstCellOffset = 116;
    private const int ReferenceFrameSize = 168;
    private const string ReferenceChecksum = "31C91F7703B1E481DC1AB05878E33098A6C60E8B525D6244F4624D626E5F6F9E";

    private static readonly SaveFrameIdentity ReferenceIdentity = new(
        1, new Guid("01020304-0506-0708-090a-0b0c0d0e0f10"), new Guid("11121314-1516-1718-191a-1b1c1d1e1f20"));

    private static readonly SaveWorldRecipe ReferenceRecipe = new(1, 1, 0x57505535, new RegionBounds(-4, 4, -2, 2));
    private static readonly byte[] ReferenceGamePayload = Convert.FromHexString("00AEFFFF00100000001E000000000000");

    [TestMethod]
    public void SchemaOneMatchesTheGoldenFrameAndItsContractChecksum()
    {
        var expected = ReferenceFrame();
        var actual = SaveCodec.Encode(CreateReferenceCheckpoint(), FloorGenerator.Instance);

        Assert.HasCount(ReferenceFrameSize, actual);
        Assert.AreEqual(ReferenceChecksum, Convert.ToHexString(SHA256.HashData(actual.AsSpan(0, actual.Length - FooterSize))));
        CollectionAssert.AreEqual(expected, actual);

        var result = SaveCodec.Decode(expected, ResolveReferenceGenerator);

        Assert.AreEqual(SaveReadStatus.Valid, result.Status);
        Assert.AreEqual<SaveFrameIdentity?>(ReferenceIdentity, result.Identity);
        var checkpoint = result.Checkpoint ?? throw new AssertFailedException("Expected a decoded checkpoint.");
        Assert.AreEqual(ReferenceRecipe, checkpoint.Recipe);
        Assert.AreEqual(new ChunkCoordinate(-1, 0, 0), checkpoint.Terrain[0].Coordinate);
        Assert.AreEqual(new TerrainCellDelta(1087, 0), checkpoint.Terrain[0].Cells[0]);
        CollectionAssert.AreEqual(ReferenceGamePayload, checkpoint.GamePayload.ToArray());
    }

    [TestMethod]
    public void RoundTripPreservesSignedAddressesExactGeneratorAndOpaqueGamePayload()
    {
        var identity = ReferenceIdentity with { Generation = ulong.MaxValue };
        var recipe = new SaveWorldRecipe(7, 19, uint.MaxValue, new RegionBounds(-1024, 1023, -1024, 1023));
        TerrainChunkDelta[] terrain =
        [
            new(new ChunkCoordinate(-1024, 0, 1023), [new(0, 0), new(4095, 1)]),
            new(new ChunkCoordinate(1023, 0, -1024), [new(1087, 0), new(1088, 1)]),
        ];
        // Three position fields, one inventory count, and two opaque item records.
        var payload = Convert.FromHexString("00AEFFFF00100000001E00000200000001000000050000007B00000002000000");
        var checkpoint = new SaveCheckpoint(identity, recipe, terrain, 0, payload);
        var frame = SaveCodec.Encode(checkpoint, FloorGenerator.Instance);
        (uint Id, uint Version) requested = default;

        var result = SaveCodec.Decode(frame, (id, version) =>
        {
            requested = (id, version);
            return FloorGenerator.Instance;
        });

        Assert.AreEqual(SaveReadStatus.Valid, result.Status);
        Assert.AreEqual((7u, 19u), requested);
        var loaded = result.Checkpoint ?? throw new AssertFailedException("Expected a decoded checkpoint.");
        Assert.AreEqual(identity, loaded.Identity);
        Assert.AreEqual(recipe, loaded.Recipe);
        Assert.AreEqual(0u, loaded.GameSchemaVersion);
        CollectionAssert.AreEqual(payload, loaded.GamePayload.ToArray());
        Assert.AreEqual(terrain.Length, loaded.Terrain.Length);
        for (var i = 0; i < terrain.Length; i++)
        {
            Assert.AreEqual(terrain[i].Coordinate, loaded.Terrain[i].Coordinate);
            CollectionAssert.AreEqual(terrain[i].Cells.ToArray(), loaded.Terrain[i].Cells.ToArray());
        }

        CollectionAssert.AreEqual(frame, SaveCodec.Encode(loaded, FloorGenerator.Instance));
    }

    [TestMethod]
    public void CheckpointAndTerrainOwnTheirInputArrays()
    {
        var cells = new[] { new TerrainCellDelta(1087, 0) };
        var terrain = new[] { new TerrainChunkDelta(new ChunkCoordinate(-1, 0, 0), cells) };
        var game = ReferenceGamePayload.ToArray();
        var checkpoint = new SaveCheckpoint(ReferenceIdentity, ReferenceRecipe, terrain, 1, game);
        var expected = ReferenceFrame();

        cells[0] = new TerrainCellDelta(0, 1);
        terrain[0] = new TerrainChunkDelta(default, [new(0, 0)]);
        game.AsSpan().Fill(byte.MaxValue);

        CollectionAssert.AreEqual(expected, SaveCodec.Encode(checkpoint, FloorGenerator.Instance));

        var result = SaveCodec.Decode(expected, ResolveReferenceGenerator);
        var decoded = result.Checkpoint ?? throw new AssertFailedException("Expected a decoded checkpoint.");
        var original = expected.ToArray();
        expected.AsSpan().Clear();

        CollectionAssert.AreEqual(original, SaveCodec.Encode(decoded, FloorGenerator.Instance));
    }

    [TestMethod]
    public void RejectsEveryTruncationAndExtraBytesBeforeResolvingTheGenerator()
    {
        var frame = ReferenceFrame();
        for (var length = 0; length < frame.Length; length++)
        {
            var result = SaveCodec.Decode(frame.AsSpan(0, length), NeverResolve);
            Assert.AreEqual(SaveReadStatus.Malformed, result.Status, $"Prefix length {length}.");
            Assert.IsNull(result.Checkpoint);
        }

        var trailing = new byte[frame.Length + 1];
        frame.CopyTo(trailing, 0);
        AssertMalformed(trailing);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(64)]
    [DataRow(118)]
    [DataRow(167)]
    public void RejectsCorruptionWithAnUnchangedFooter(int offset)
    {
        var frame = ReferenceFrame();
        frame[offset] ^= 0x80;
        AssertMalformed(frame);
    }

    [TestMethod]
    [DataRow(12, uint.MaxValue)]
    [DataRow(12, 167u)]
    [DataRow(56, 0u)]
    [DataRow(60, 0u)]
    [DataRow(68, 5u)]
    [DataRow(72, 1024u)]
    [DataRow(76, 3u)]
    [DataRow(80, 1024u)]
    [DataRow(ChangedChunkCountOffset, 0u)]
    [DataRow(ChangedChunkCountOffset, 1025u)]
    [DataRow(ChangedChunkCountOffset, uint.MaxValue)]
    [DataRow(EditedCellCountOffset, 0u)]
    [DataRow(EditedCellCountOffset, 262145u)]
    [DataRow(EditedCellCountOffset, uint.MaxValue)]
    [DataRow(GameLengthOffset, 15u)]
    [DataRow(GameLengthOffset, 17u)]
    [DataRow(GameLengthOffset, 262145u)]
    [DataRow(GameLengthOffset, uint.MaxValue)]
    [DataRow(FirstChunkOffset, 5u)]
    [DataRow(FirstChunkOffset + 4, 1u)]
    [DataRow(FirstChunkOffset + 8, 3u)]
    [DataRow(FirstChunkOffset + 12, 0u)]
    [DataRow(FirstChunkOffset + 12, 4097u)]
    [DataRow(FirstChunkOffset + 12, uint.MaxValue)]
    public void RejectsInvalidFieldsEvenWithAValidChecksum(int offset, uint replacement)
    {
        var frame = ReferenceFrame();
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), replacement);
        RewriteChecksum(frame);

        AssertMalformed(frame);
    }

    [TestMethod]
    [DataRow(16, 8)]
    [DataRow(24, 16)]
    [DataRow(40, 16)]
    public void RejectsZeroGenerationAndIdentifiers(int offset, int length)
    {
        var frame = ReferenceFrame();
        frame.AsSpan(offset, length).Clear();
        RewriteChecksum(frame);

        AssertMalformed(frame);
    }

    [TestMethod]
    public void RejectsTrailingPayloadEvenIfLengthAndChecksumAreUpdated()
    {
        var original = ReferenceFrame();
        var frame = new byte[original.Length + 1];
        original.AsSpan(0, original.Length - FooterSize).CopyTo(frame);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), (uint)frame.Length);
        RewriteChecksum(frame);

        AssertMalformed(frame);
    }

    [TestMethod]
    [DataRow(FirstCellOffset + 2, 2)]
    [DataRow(FirstCellOffset + 3, 1)]
    public void RejectsInvalidOccupancyAndNonzeroReservedByte(int offset, int replacement)
    {
        var frame = ReferenceFrame();
        frame[offset] = (byte)replacement;
        RewriteChecksum(frame);

        AssertMalformed(frame);
    }

    [TestMethod]
    public void RejectsOutOfRangeCellIndexAndRedundantEdit()
    {
        var invalidIndex = ReferenceFrame();
        BinaryPrimitives.WriteUInt16LittleEndian(invalidIndex.AsSpan(FirstCellOffset), 4096);
        RewriteChecksum(invalidIndex);
        AssertMalformed(invalidIndex);

        var redundant = ReferenceFrame();
        redundant[FirstCellOffset + 2] = 1;
        RewriteChecksum(redundant);
        AssertMalformed(redundant, ResolveReferenceGenerator);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void RejectsDuplicateAndDescendingCellIndices(int replacementIndex)
    {
        var checkpoint = CreateReferenceCheckpoint(
            [new(new ChunkCoordinate(-1, 0, 0), [new(1, 0), new(2, 0)])]);
        var frame = SaveCodec.Encode(checkpoint, FloorGenerator.Instance);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(FirstCellOffset + 4), (ushort)replacementIndex);
        RewriteChecksum(frame);

        AssertMalformed(frame);
    }

    [TestMethod]
    [DataRow(-2)]
    [DataRow(-1)]
    public void RejectsDuplicateAndDescendingChunkCoordinates(int replacementX)
    {
        var checkpoint = CreateReferenceCheckpoint(
            [new(new ChunkCoordinate(-1, 0, 0), [new(1, 0)]), new(default, [new(1, 0)])]);
        var frame = SaveCodec.Encode(checkpoint, FloorGenerator.Instance);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(FirstChunkOffset + 20), replacementX);
        RewriteChecksum(frame);

        AssertMalformed(frame);
    }

    [TestMethod]
    public void ReportsUnknownFramingAndIntactFutureSchemaSeparatelyFromCorruption()
    {
        var unknownFraming = ReferenceFrame();
        BinaryPrimitives.WriteUInt16LittleEndian(unknownFraming.AsSpan(8), 2);
        RewriteChecksum(unknownFraming);
        var unsupportedFraming = SaveCodec.Decode(unknownFraming, NeverResolve);
        Assert.AreEqual(SaveReadStatus.UnsupportedFraming, unsupportedFraming.Status);
        Assert.IsNull(unsupportedFraming.Checkpoint);

        // An unknown schema can use a different body size and layout from schema 1.
        var future = new byte[56 + FooterSize];
        ReferenceFrame().AsSpan(0, 56).CopyTo(future);
        BinaryPrimitives.WriteUInt16LittleEndian(future.AsSpan(10), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(future.AsSpan(12), (uint)future.Length);
        RewriteChecksum(future);

        var unsupportedSchema = SaveCodec.Decode(future, NeverResolve);
        Assert.AreEqual(SaveReadStatus.UnsupportedSchema, unsupportedSchema.Status);
        Assert.AreEqual<SaveFrameIdentity?>(ReferenceIdentity, unsupportedSchema.Identity);
        Assert.IsNull(unsupportedSchema.Checkpoint);

        future[^1] ^= 1;
        AssertMalformed(future);
    }

    [TestMethod]
    public void ReportsAnUnsupportedGeneratorWithTheVerifiedFrameIdentity()
    {
        var frame = ReferenceFrame();
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(60), 2);
        RewriteChecksum(frame);

        var result = SaveCodec.Decode(frame, ResolveReferenceGenerator);

        Assert.AreEqual(SaveReadStatus.UnsupportedGenerator, result.Status);
        Assert.AreEqual<SaveFrameIdentity?>(ReferenceIdentity, result.Identity);
        Assert.AreEqual<SaveWorldRecipe?>(ReferenceRecipe with { GeneratorVersion = 2 }, result.Recipe);
        Assert.IsNull(result.Checkpoint);
    }

    [TestMethod]
    public void LargestGamePayloadRoundTripsWithoutInterpretation()
    {
        var payload = new byte[256 * 1024];
        payload[0] = 0xD2;
        payload[^1] = 0xA7;
        var checkpoint = new SaveCheckpoint(ReferenceIdentity, ReferenceRecipe, [], uint.MaxValue, payload);

        var result = SaveCodec.Decode(SaveCodec.Encode(checkpoint, FloorGenerator.Instance), ResolveReferenceGenerator);

        Assert.AreEqual(SaveReadStatus.Valid, result.Status);
        var loaded = result.Checkpoint ?? throw new AssertFailedException("Expected a decoded checkpoint.");
        Assert.AreEqual(uint.MaxValue, loaded.GameSchemaVersion);
        CollectionAssert.AreEqual(payload, loaded.GamePayload.ToArray());
    }

    [TestMethod]
    public void MaximumChangedChunkCountRoundTripsAndTheNextChunkIsRejected()
    {
        var recipe = ReferenceRecipe with { Bounds = new RegionBounds(-1024, 1023, -2, 2) };
        var chunks = new TerrainChunkDelta[1025];
        for (var i = 0; i < chunks.Length; i++)
            chunks[i] = new TerrainChunkDelta(new ChunkCoordinate(i - 512, 0, 0), [new(0, 0)]);

        var atLimit = new SaveCheckpoint(ReferenceIdentity, recipe, chunks.AsSpan(0, 1024), 1, ReferenceGamePayload);
        var result = SaveCodec.Decode(SaveCodec.Encode(atLimit, FloorGenerator.Instance), ResolveReferenceGenerator);

        Assert.AreEqual(SaveReadStatus.Valid, result.Status);
        var loaded = result.Checkpoint ?? throw new AssertFailedException("Expected a decoded checkpoint.");
        Assert.AreEqual(1024, loaded.Terrain.Length);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            SaveCodec.Encode(new SaveCheckpoint(ReferenceIdentity, recipe, chunks, 1, ReferenceGamePayload), FloorGenerator.Instance));
    }

    [TestMethod]
    public void MaximumEditedCellCountRoundTripsAndTheNextCellIsRejected()
    {
        var recipe = ReferenceRecipe with { Bounds = new RegionBounds(-32, 32, -2, 2) };
        var cells = new TerrainCellDelta[ChunkSnapshot.CellCount];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = new TerrainCellDelta((ushort)i, (byte)(i / ChunkSnapshot.Size % ChunkSnapshot.Size >= 4 ? 1 : 0));

        var chunks = new TerrainChunkDelta[65];
        for (var i = 0; i < chunks.Length - 1; i++)
            chunks[i] = new TerrainChunkDelta(new ChunkCoordinate(i - 32, 0, 0), cells);
        chunks[^1] = new TerrainChunkDelta(new ChunkCoordinate(32, 0, 0), [new(0, 0)]);

        var atLimit = new SaveCheckpoint(ReferenceIdentity, recipe, chunks.AsSpan(0, 64), 1, ReferenceGamePayload);
        var result = SaveCodec.Decode(SaveCodec.Encode(atLimit, FloorGenerator.Instance), ResolveReferenceGenerator);

        Assert.AreEqual(SaveReadStatus.Valid, result.Status);
        var loaded = result.Checkpoint ?? throw new AssertFailedException("Expected a decoded checkpoint.");
        Assert.AreEqual(64, loaded.Terrain.Length);
        foreach (var chunk in loaded.Terrain)
            Assert.AreEqual(4096, chunk.Cells.Length);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            SaveCodec.Encode(new SaveCheckpoint(ReferenceIdentity, recipe, chunks, 1, ReferenceGamePayload), FloorGenerator.Instance));
    }

    [TestMethod]
    public void OversizedGamePayloadIsRejectedByTheWriter()
    {
        var oversized = new byte[256 * 1024 + 1];

        Assert.ThrowsExactly<InvalidDataException>(() =>
            SaveCodec.Encode(new SaveCheckpoint(ReferenceIdentity, ReferenceRecipe, [], 1, oversized), FloorGenerator.Instance));
    }

    private static SaveCheckpoint CreateReferenceCheckpoint() => CreateReferenceCheckpoint(
        [new(new ChunkCoordinate(-1, 0, 0), [new(1087, 0)])]);

    private static SaveCheckpoint CreateReferenceCheckpoint(ReadOnlySpan<TerrainChunkDelta> chunks) =>
        new(ReferenceIdentity, ReferenceRecipe, chunks, 1, ReferenceGamePayload);

    private static ISaveTerrainGenerator? ResolveReferenceGenerator(uint id, uint version) =>
        id == 1 && version == 1 ? FloorGenerator.Instance : null;

    private static ISaveTerrainGenerator? NeverResolve(uint id, uint version) =>
        throw new AssertFailedException($"Unexpected generator resolution for {id}/{version}.");

    private static void AssertMalformed(byte[] frame, Func<uint, uint, ISaveTerrainGenerator?>? resolver = null)
    {
        var result = SaveCodec.Decode(frame, resolver ?? NeverResolve);
        Assert.AreEqual(SaveReadStatus.Malformed, result.Status);
        Assert.IsNull(result.Checkpoint);
    }

    private static void RewriteChecksum(byte[] frame) =>
        SHA256.HashData(frame.AsSpan(0, frame.Length - FooterSize), frame.AsSpan(frame.Length - FooterSize));

    private static byte[] ReferenceFrame() => Convert.FromHexString(
        "475753415645303101000100A80000000100000000000000" +
        "0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20" +
        "010000000100000035555057FCFFFFFF04000000FEFFFFFF02000000" +
        "01000000010000000100000010000000FFFFFFFF0000000000000000010000003F040000" +
        "00AEFFFF00100000001E000000000000" + ReferenceChecksum);
}

internal sealed class FloorGenerator : ISaveTerrainGenerator
{
    internal static FloorGenerator Instance { get; } = new();

    public void Generate(SaveWorldRecipe recipe, ChunkCoordinate coordinate, Span<byte> cells)
    {
        const int rowSize = ChunkSnapshot.Size;
        const int sliceSize = rowSize * rowSize;
        cells.Clear();
        for (var z = 0; z < rowSize; z++)
            cells.Slice(z * sliceSize, 4 * rowSize).Fill(1);
    }
}
