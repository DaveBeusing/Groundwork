using System.Buffers.Binary;
using System.Security.Cryptography;
using Groundwork.Voxels;

namespace Groundwork.Persistence.Tests;

[TestClass]
public sealed class PlanetCheckpointCodecTests
{
    private const string ReferenceHex =
        "475753415645303101000200B000000001000000000000000102030405060708" +
        "090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F200100000001000000" +
        "3855505702000000000000000000504000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000F03F" +
        "00FE00FE00FEE7FF0002000000027800F4BD8984B29C60075791FCF43DBC1AB3" +
        "85A14D8FEC795B4D9C5D62670BB0BD75";
    private static readonly SaveFrameIdentity Identity = new(1, Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10"),
        Guid.Parse("11121314-1516-1718-191a-1b1c1d1e1f20"));
    private static readonly PlanetSaveRecipe Recipe = new(1, 1, 0x57505538);
    private static readonly PlanetLandedPose Pose = new(new(64, 0, 0), PlanetFlightOrientation.Identity);
    private static readonly PlanetSampleKey Corner = new(-512, -512, -512);
    private static readonly PlanetSampleKey Edge = new(512, 0, 512);

    [TestMethod]
    public void ReferenceFrameEncodesAndRoundTripsByteForByte()
    {
        var edits = new PlanetHeightEdit[] { new(Corner, -25), new(Edge, 120) };
        var checkpoint = new PlanetCheckpoint(Identity, Recipe, Pose, edits);
        edits[0] = default;
        var bytes = Convert.FromHexString(ReferenceHex);
        CollectionAssert.AreEqual(bytes, PlanetCheckpointCodec.Encode(checkpoint));
        var decoded = PlanetCheckpointCodec.Decode(bytes, SupportsRecipe);
        Assert.AreEqual(SaveReadStatus.Valid, decoded.Status);
        var restored = decoded.Checkpoint;
        Assert.IsNotNull(restored);
        Assert.AreEqual(Identity, restored.Identity);
        Assert.AreEqual(Recipe, restored.Recipe);
        Assert.AreEqual(Pose, restored.Pose);
        Assert.AreEqual((short)-25, restored.GetHeightDelta(Corner));
        Assert.AreEqual((short)120, restored.GetHeightDelta(Edge));
        Assert.AreEqual((short)0, restored.GetHeightDelta(new(512, 0, 0)));
        CollectionAssert.AreEqual(bytes, PlanetCheckpointCodec.Encode(restored));
    }

    [TestMethod]
    public void DecodedEditsRebuildIdenticallyAcrossFacesAndLod()
    {
        var checkpoint = PlanetCheckpointCodec.Decode(Convert.FromHexString(ReferenceHex), SupportsRecipe).Checkpoint;
        Assert.IsNotNull(checkpoint);
        var coarseX = Build(new(PlanetFace.PositiveX, 0, 0, 0));
        var coarseZ = Build(new(PlanetFace.PositiveZ, 0, 0, 0));
        var fineX = Build(new(PlanetFace.PositiveX, 5, 0, 16));
        var reference = Position(coarseX, Edge);
        Assert.AreEqual(reference, Position(coarseZ, Edge));
        Assert.AreEqual(reference, Position(fineX, Edge));
        Assert.AreEqual(64.12, Math.Sqrt(reference.X * reference.X + reference.Z * reference.Z), 1e-10);
        var corner = Position(Build(new(PlanetFace.NegativeX, 0, 0, 0)), Corner);
        Assert.AreEqual(corner, Position(Build(new(PlanetFace.NegativeY, 0, 0, 0)), Corner));
        Assert.AreEqual(corner, Position(Build(new(PlanetFace.NegativeZ, 0, 0, 0)), Corner));

        PlanetPatchMesh Build(PlanetPatchKey key) => PlanetPatchMesh.Build(key, 64,
            sample => checkpoint.GetHeightDelta(sample) / 1000d);
        static PlanetVector Position(PlanetPatchMesh mesh, PlanetSampleKey sample) => mesh.Positions[mesh.Samples.IndexOf(sample)];
    }

    [TestMethod]
    [DataRow(128, 1, DisplayName = "Odd lattice coordinate")]
    [DataRow(128, -514, DisplayName = "Outside cube")]
    [DataRow(134, 0, DisplayName = "Zero delta")]
    [DataRow(134, 501, DisplayName = "Out of range delta")]
    [DataRow(68, 4097, DisplayName = "Excessive count")]
    [DataRow(68, 1, DisplayName = "Inconsistent count")]
    [DataRow(56, 0, DisplayName = "Missing generator")]
    public void RejectsMalformedPayloadEvenWithValidChecksum(int offset, int value)
    {
        var frame = Convert.FromHexString(ReferenceHex);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(offset), (short)value);
        AssertMalformed(frame);
    }

    [TestMethod]
    public void RejectsDuplicateUnsortedInteriorAndInvalidPoseRecords()
    {
        var reference = Convert.FromHexString(ReferenceHex);
        var frame = reference.ToArray();
        frame.AsSpan(128, 6).CopyTo(frame.AsSpan(136));
        AssertMalformed(frame);
        frame = reference.ToArray();
        reference.AsSpan(128, 8).CopyTo(frame.AsSpan(136));
        reference.AsSpan(136, 8).CopyTo(frame.AsSpan(128));
        AssertMalformed(frame);
        frame = reference.ToArray();
        frame.AsSpan(128, 6).Clear();
        AssertMalformed(frame);
        frame = reference.ToArray();
        BinaryPrimitives.WriteDoubleLittleEndian(frame.AsSpan(72), double.NaN);
        AssertMalformed(frame);
        frame = reference.ToArray();
        BinaryPrimitives.WriteDoubleLittleEndian(frame.AsSpan(120), 2);
        AssertMalformed(frame);
    }

    [TestMethod]
    public void DetectsUnsupportedGeneratorAndDamagedFrame()
    {
        var bytes = Convert.FromHexString(ReferenceHex);
        var unsupported = PlanetCheckpointCodec.Decode(bytes, static (_, _) => false);
        Assert.AreEqual(SaveReadStatus.UnsupportedGenerator, unsupported.Status);
        Assert.AreEqual<SaveFrameIdentity?>(Identity, unsupported.Identity);
        Assert.IsNull(unsupported.Checkpoint);
        bytes[^1] ^= 1;
        Assert.AreEqual(SaveReadStatus.Malformed, PlanetCheckpointCodec.Decode(bytes, SupportsRecipe).Status);
        Assert.AreEqual(SaveReadStatus.Malformed, PlanetCheckpointCodec.Decode(bytes.AsSpan(0, 127), SupportsRecipe).Status);
    }

    private static void AssertMalformed(byte[] frame)
    {
        SHA256.HashData(frame.AsSpan(0, frame.Length - 32), frame.AsSpan(frame.Length - 32));
        var result = PlanetCheckpointCodec.Decode(frame, static (_, _) => throw new AssertFailedException("Malformed payload reached recipe resolution."));
        Assert.AreEqual(SaveReadStatus.Malformed, result.Status);
        Assert.IsNull(result.Checkpoint);
    }

    private static bool SupportsRecipe(uint id, uint version) => id == 1 && version == 1;
}
