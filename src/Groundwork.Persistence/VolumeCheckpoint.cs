using System.Buffers.Binary;
using System.Security.Cryptography;
using Groundwork.Core;
using Groundwork.Voxels;

namespace Groundwork.Persistence;

public readonly record struct VolumeSaveOperation(Guid SlotId, ulong BaseGeneration, Guid Nonce);

/// <summary>An immutable WP11 checkpoint. Generation is derived from the operation's fixed base.</summary>
public sealed class VolumeCheckpoint
{
    public const int MaximumOverlayRows = VolumeStreamingCoordinator.MaximumOverlaySamples;
    public const int MaximumGameBytes = 65_536;
    private readonly byte[] _overlay;
    private readonly byte[] _game;

    public VolumeCheckpoint(VolumeSaveOperation operation, Guid lineage, PlanetDescriptor planet, ulong tick,
        ulong revision, ReadOnlySpan<byte> overlay, ReadOnlySpan<byte> game, ulong captureOrdinal = 1, PlanetWalkState? player = null)
    {
        ArgumentNullException.ThrowIfNull(planet);
        if (operation.SlotId == Guid.Empty || operation.Nonce == Guid.Empty ||
            operation.BaseGeneration == ulong.MaxValue || lineage == Guid.Empty || tick is 0 or ulong.MaxValue || revision == 0 || captureOrdinal == 0 ||
            game.Length > MaximumGameBytes || !IsCanonicalOverlay(overlay) || !ValidPlayer(player))
            throw new InvalidDataException("Invalid volume checkpoint.");
        (Operation, Lineage, Planet, Tick, TerrainRevision) = (operation, lineage, planet, tick, revision);
        CaptureOrdinal = captureOrdinal;
        Player = player;
        _overlay = overlay.ToArray();
        _game = game.ToArray();
    }

    public VolumeSaveOperation Operation { get; }
    public Guid Lineage { get; }
    public PlanetDescriptor Planet { get; }
    public ulong Tick { get; }
    public ulong TerrainRevision { get; }
    public ulong CaptureOrdinal { get; }
    public PlanetWalkState? Player { get; }
    public ulong Generation => Operation.BaseGeneration + 1;
    public ReadOnlySpan<byte> Overlay => _overlay;
    public ReadOnlySpan<byte> Game => _game;

    /// <summary>Copies one committed tick on a host worker. The encoder reads only the supplied immutable root.</summary>
    internal static VolumeCheckpoint? TryCapture(VolumeCheckpointCut cut, VolumeSaveOperation operation,
        Guid lineage, Func<VolumeWorldRoot, Memory<byte>, int> encodeGame, ulong captureOrdinal = 1)
    {

        ArgumentNullException.ThrowIfNull(encodeGame);
        var lease = cut.Terrain;
        if (lease?.EditLineage is not { } planet || lease.EditOverlayBytes > 8 + 32 * MaximumOverlayRows)
            return null;
        var overlay = new byte[lease.EditOverlayBytes];
        if (!lease.TryWriteEditOverlay(overlay, out var written) || written != overlay.Length)
            return null;
        var game = new byte[MaximumGameBytes];
        var root = (VolumeWorldRoot)cut.Publication.Root;
        var size = encodeGame(root, game);
        if (size < 0 || size > game.Length)
            return null;
        return new VolumeCheckpoint(operation, lineage, planet, cut.Publication.Stamp.Tick, lease.Info.TerrainRevision,
            overlay, game.AsSpan(0, size), captureOrdinal, root.Player);
    }
    /// <summary>Rebuilds a streaming authority with the exact generator lineage and saved terrain revision.</summary>
    public VolumeStreamingCoordinator RestoreTerrain(WorldSafetyPorts world, IPlanetBrickSource baseline,
        VolumeStreamingLimits limits, int maximumOverlaySamples, Func<ushort, bool> supportsMaterial)
    {
        ArgumentNullException.ThrowIfNull(world);
        var stamp = world.Publication.Committed.Read().Stamp;
        if (stamp.Tick != Tick || stamp.Kind != 0)
            throw new InvalidOperationException("Open a fresh Core world at the checkpoint's completed tick.");
        return new(world, baseline, limits, maximumOverlaySamples, Planet, TerrainRevision, Overlay, supportsMaterial, Player);
    }
    internal static bool ValidPlayer(PlanetWalkState? player) => player is not { } pose ||
        Finite(pose.Position) && Finite(pose.Heading) && Finite(pose.Velocity) &&
        pose.Position != default && pose.Heading != default;

    private static bool Finite(PlanetVector vector) =>
        double.IsFinite(vector.X) && double.IsFinite(vector.Y) && double.IsFinite(vector.Z);
    internal static bool IsCanonicalOverlay(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            return false;
        var density = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var material = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        if (density < 0 || material < 0 || (long)density + material > MaximumOverlayRows ||
            bytes.Length != 8 + 32 * (density + material))
            return false;
        (long X, long Y, long Z) previous = default;
        for (var i = 0; i < density + material; i++)
        {
            var row = bytes[(8 + 32 * i)..];
            var key = (BinaryPrimitives.ReadInt64LittleEndian(row), BinaryPrimitives.ReadInt64LittleEndian(row[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(row[16..]));
            var value = BinaryPrimitives.ReadInt64LittleEndian(row[24..]);
            if (!InRange(key.Item1) || !InRange(key.Item2) || !InRange(key.Item3) ||
                i != 0 && i != density && previous.CompareTo(key) >= 0 ||
                (i < density ? value is not (-128 or 128) : value is < 1 or > ushort.MaxValue))
                return false;
            previous = key;
        }
        return true;

        static bool InRange(long coordinate) =>
            coordinate >= -PlanetDensityField.MaximumCoordinate && coordinate <= PlanetDensityField.MaximumCoordinate;
    }
}

public enum VolumeCheckpointStatus { Valid, DamagedCompatible, Incompatible }

public readonly record struct VolumeCheckpointRead(VolumeCheckpointStatus Status, VolumeCheckpoint? Checkpoint = null);

public static class VolumeCheckpointCodec
{
    public const ushort Schema = 1;
    private const int HeaderFields = 170;
    private const int BodyFields = 97;
    internal const int HeaderLength = HeaderFields + 32;
    public const int MaximumBytes = HeaderLength + BodyFields + 8 + 32 * VolumeCheckpoint.MaximumOverlayRows +
        VolumeCheckpoint.MaximumGameBytes + 32;

    public static byte[] Encode(VolumeCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var bytes = new byte[HeaderLength + BodyFields + checkpoint.Overlay.Length + checkpoint.Game.Length + 32];
        var h = bytes.AsSpan();
        "GWVOLCP1"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], Schema);
        checkpoint.Operation.SlotId.TryWriteBytes(h[10..]);
        checkpoint.Lineage.TryWriteBytes(h[26..]);
        BinaryPrimitives.WriteUInt64LittleEndian(h[42..], checkpoint.Operation.BaseGeneration);
        checkpoint.Operation.Nonce.TryWriteBytes(h[50..]);
        var planet = checkpoint.Planet;
        planet.WorldId.TryWriteBytes(h[66..]);
        planet.PlanetId.TryWriteBytes(h[82..]);
        BinaryPrimitives.WriteUInt64LittleEndian(h[98..], planet.Seed);
        BinaryPrimitives.WriteUInt32LittleEndian(h[106..], planet.GeneratorVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(h[110..], planet.TerrainSchemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(h[114..], planet.MaterialCatalogVersion);
        Convert.FromHexString(planet.ParameterSha256).CopyTo(h[118..]);
        BinaryPrimitives.WriteDoubleLittleEndian(h[150..], planet.RadiusMeters);
        BinaryPrimitives.WriteDoubleLittleEndian(h[158..], planet.CellSizeMeters);
        BinaryPrimitives.WriteInt32LittleEndian(h[166..], checkpoint.Overlay.Length);
        SHA256.HashData(h[..HeaderFields], h[HeaderFields..HeaderLength]);
        var body = h[HeaderLength..];
        BinaryPrimitives.WriteUInt64LittleEndian(body, checkpoint.Tick);
        BinaryPrimitives.WriteUInt64LittleEndian(body[8..], checkpoint.TerrainRevision);
        BinaryPrimitives.WriteUInt64LittleEndian(body[16..], checkpoint.CaptureOrdinal);
        WritePlayer(body[24..BodyFields], checkpoint.Player);
        checkpoint.Overlay.CopyTo(body[BodyFields..]);
        checkpoint.Game.CopyTo(body[(BodyFields + checkpoint.Overlay.Length)..]);
        SHA256.HashData(h[..^32], h[^32..]);
        return bytes;
    }

    public static VolumeCheckpointRead Decode(ReadOnlySpan<byte> bytes, Guid slot, Guid lineage, PlanetDescriptor planet)
    {
        if (bytes.Length < HeaderLength || bytes.Length > MaximumBytes ||
            !bytes[..8].SequenceEqual("GWVOLCP1"u8) || BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]) != Schema ||
            !HashMatches(bytes[..HeaderFields], bytes[HeaderFields..HeaderLength]))
            return new(VolumeCheckpointStatus.Incompatible);

        var operation = new VolumeSaveOperation(new Guid(bytes.Slice(10, 16)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[42..]), new Guid(bytes.Slice(50, 16)));
        if (slot == Guid.Empty || lineage == Guid.Empty || operation.SlotId != slot ||
            new Guid(bytes.Slice(26, 16)) != lineage || operation.BaseGeneration == ulong.MaxValue || operation.Nonce == Guid.Empty ||
            new Guid(bytes.Slice(66, 16)) != planet.WorldId || new Guid(bytes.Slice(82, 16)) != planet.PlanetId ||
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[98..]) != planet.Seed ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[106..]) != planet.GeneratorVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[110..]) != planet.TerrainSchemaVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[114..]) != planet.MaterialCatalogVersion ||
            !bytes.Slice(118, 32).SequenceEqual(Convert.FromHexString(planet.ParameterSha256)) ||
            BinaryPrimitives.ReadDoubleLittleEndian(bytes[150..]) != planet.RadiusMeters ||
            BinaryPrimitives.ReadDoubleLittleEndian(bytes[158..]) != planet.CellSizeMeters)
            return new(VolumeCheckpointStatus.Incompatible);

        var overlayLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[166..]);
        var gameLength = bytes.Length - HeaderLength - (long)BodyFields - 32 - overlayLength;
        if (overlayLength is < 8 or > 8 + 32 * VolumeCheckpoint.MaximumOverlayRows ||
            gameLength is < 0 or > VolumeCheckpoint.MaximumGameBytes || !HashMatches(bytes[..^32], bytes[^32..]))
            return new(VolumeCheckpointStatus.DamagedCompatible);

        var body = bytes[HeaderLength..^32];
        var tick = BinaryPrimitives.ReadUInt64LittleEndian(body);
        var revision = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
        var captureOrdinal = BinaryPrimitives.ReadUInt64LittleEndian(body[16..]);
        var overlay = body.Slice(BodyFields, overlayLength);
        var poseBytes = body[24..BodyFields];
        var player = ReadPlayer(poseBytes);
        return poseBytes[0] > 1 || poseBytes[0] == 0 && poseBytes[1..].ContainsAnyExcept((byte)0) ||
            !VolumeCheckpoint.ValidPlayer(player) || tick is 0 or ulong.MaxValue || revision == 0 || captureOrdinal == 0 || !VolumeCheckpoint.IsCanonicalOverlay(overlay)
            ? new(VolumeCheckpointStatus.DamagedCompatible)
            : new(VolumeCheckpointStatus.Valid,
                new VolumeCheckpoint(operation, lineage, planet, tick, revision, overlay, body[(BodyFields + overlayLength)..], captureOrdinal, player));
    }

    private static void WritePlayer(Span<byte> bytes, PlanetWalkState? player)
    {
        if (player is not { } pose)
            return;
        bytes[0] = 1;
        WriteVector(bytes[1..], pose.Position);
        WriteVector(bytes[25..], pose.Heading);
        WriteVector(bytes[49..], pose.Velocity);
    }

    private static PlanetWalkState? ReadPlayer(ReadOnlySpan<byte> bytes) => bytes[0] == 1
        ? new PlanetWalkState(ReadVector(bytes[1..]), ReadVector(bytes[25..]), ReadVector(bytes[49..])) : null;

    private static void WriteVector(Span<byte> bytes, PlanetVector vector)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, vector.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[8..], vector.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[16..], vector.Z);
    }

    private static PlanetVector ReadVector(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadDoubleLittleEndian(bytes),
        BinaryPrimitives.ReadDoubleLittleEndian(bytes[8..]), BinaryPrimitives.ReadDoubleLittleEndian(bytes[16..]));
    private static bool HashMatches(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(data, digest);
        return CryptographicOperations.FixedTimeEquals(digest, expected);
    }
}
