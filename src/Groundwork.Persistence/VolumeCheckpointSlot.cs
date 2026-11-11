using System.Security.Cryptography;
using Groundwork.Voxels;

namespace Groundwork.Persistence;

public enum VolumeSlotStatus
{
    Empty, Loaded, ReadOnlyRecovery, Committed, AlreadyCommitted, Busy, Stale, Conflict,
    Incompatible, Corrupt, InvalidCheckpoint, StorageError, Indeterminate, Canceled
}

public sealed record VolumeSlotResult(VolumeSlotStatus Status, VolumeCheckpoint? Checkpoint = null,
    string? Member = null, Exception? Error = null);

/// <summary>Two committed members in a dedicated, explicitly reserved WP11 namespace.</summary>
public sealed class VolumeCheckpointSlot
{
    private const string First = "volume.a";
    private const string Second = "volume.b";
    private const string Intent = "volume.intent";
    private readonly string _directory;
    private readonly Guid _slot;
    private readonly Guid _lineage;
    private readonly PlanetDescriptor _planet;
    private readonly Func<VolumeCheckpoint, SaveGameValidation> _validate;
    private const string Reservation = "volume.reservation";

    public VolumeCheckpointSlot(string existingV2Root, Guid slot, Guid lineage, PlanetDescriptor planet,
        Func<VolumeCheckpoint, SaveGameValidation> validate)
        : this(existingV2Root, slot, lineage, planet, validate, reserved: false) { }

    private VolumeCheckpointSlot(string existingV2Root, Guid slot, Guid lineage, PlanetDescriptor planet,
        Func<VolumeCheckpoint, SaveGameValidation> validate, bool reserved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingV2Root);
        ArgumentOutOfRangeException.ThrowIfEqual(slot, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(lineage, Guid.Empty);
        ArgumentNullException.ThrowIfNull(planet);
        ArgumentNullException.ThrowIfNull(validate);
        var root = Path.GetFullPath(existingV2Root);
        if (!Path.IsPathFullyQualified(existingV2Root) || !char.IsAsciiLetter(root[0]) || root[1] != ':' ||
            !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), "v2", StringComparison.Ordinal))
            throw new ArgumentException("Expected a local absolute path to the v2 namespace.", nameof(existingV2Root));
        (_slot, _lineage, _planet, _validate) = (slot, lineage, planet, validate);
        _directory = Path.Combine(root, slot.ToString("N"));
        if (reserved)
        {
            using var pin = new VolumeNtfsDirectory(root);
            VolumeNtfsDirectory.Reserve(_directory);
            using var reservedDirectory = new VolumeNtfsDirectory(_directory);
            using var lease = VolumeNtfsDirectory.OpenFile(Path.Combine(_directory, "volume.lock"), lease: true);
            using var marker = VolumeNtfsDirectory.OpenFile(Path.Combine(_directory, Reservation), candidate: true);
            marker.Write(ReservationBytes());
            marker.Flush(flushToDisk: true);
        }
    }

    public static VolumeCheckpointSlot Reserve(string existingV2Root, Guid slot, Guid lineage, PlanetDescriptor planet,
        Func<VolumeCheckpoint, SaveGameValidation> validate) =>
        new(existingV2Root, slot, lineage, planet, validate, reserved: true);

    public VolumeSlotResult Load() => WithLease(_ => ReadSettled().Result);

    public VolumeSlotResult Save(VolumeCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
        Save(checkpoint, null, cancellationToken);

    internal VolumeSlotResult Save(VolumeCheckpoint checkpoint, Action<SaveWriteStage>? observe, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return WithLease(directory =>
        {
            var scan = ReadSettled();
            if (scan.Result.Status is not (VolumeSlotStatus.Empty or VolumeSlotStatus.Loaded))
                return scan.Result;
            if (checkpoint.Operation.SlotId != _slot || checkpoint.Lineage != _lineage || checkpoint.Planet != _planet)
                return new(VolumeSlotStatus.Incompatible);
            var bytes = VolumeCheckpointCodec.Encode(checkpoint);
            foreach (var member in new[] { scan.A, scan.B })
                if (member.Checkpoint?.Operation == checkpoint.Operation)
                    return member.Bytes!.AsSpan().SequenceEqual(bytes)
                        ? new(VolumeSlotStatus.AlreadyCommitted, member.Checkpoint)
                        : new(VolumeSlotStatus.Conflict);
            if ((scan.Result.Checkpoint?.Generation ?? 0) != checkpoint.Operation.BaseGeneration)
                return new(VolumeSlotStatus.Stale);
            if (scan.Result.Checkpoint is { } latest &&
                (checkpoint.Tick < latest.Tick || checkpoint.Tick == latest.Tick && checkpoint.CaptureOrdinal <= latest.CaptureOrdinal ||
                    checkpoint.TerrainRevision < latest.TerrainRevision))
                return new(VolumeSlotStatus.Stale);
            var validation = _validate(checkpoint);
            if (validation != SaveGameValidation.Valid)
                return new(validation == SaveGameValidation.Unsupported ? VolumeSlotStatus.Incompatible : VolumeSlotStatus.InvalidCheckpoint);
            return Publish(directory, scan, checkpoint, bytes, observe, token);
        });
    }

    private VolumeSlotResult Publish(VolumeNtfsDirectory directory, SlotScan initial, VolumeCheckpoint checkpoint,
        byte[] bytes, Action<SaveWriteStage>? observe, CancellationToken token)
    {
        var target = checkpoint.Generation % 2 == 1 ? First : Second;
        var pending = Path.Combine(_directory, $"stage-{Guid.NewGuid():N}.tmp");
        var ownPending = false;
        var publishAttempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            using (var candidate = VolumeNtfsDirectory.OpenFile(pending, candidate: true))
            {
                ownPending = true;
                observe?.Invoke(SaveWriteStage.BeforeWrite);
                candidate.Write(bytes.AsSpan(0, VolumeCheckpointCodec.HeaderLength / 2));
                observe?.Invoke(SaveWriteStage.PartialHeader);
                candidate.Write(bytes.AsSpan(VolumeCheckpointCodec.HeaderLength / 2, bytes.Length / 2 - VolumeCheckpointCodec.HeaderLength / 2));
                observe?.Invoke(SaveWriteStage.PartialBody);
                candidate.Write(bytes.AsSpan(bytes.Length / 2, bytes.Length - bytes.Length / 2 - 16));
                observe?.Invoke(SaveWriteStage.PartialFooter);
                candidate.Write(bytes.AsSpan(bytes.Length - 16));
                observe?.Invoke(SaveWriteStage.BeforeFlush);
                candidate.Flush(flushToDisk: true);
                observe?.Invoke(SaveWriteStage.AfterFlush);
                candidate.Position = 0;
                var verified = new byte[bytes.Length];
                candidate.ReadExactly(verified);
                if (!bytes.AsSpan().SequenceEqual(verified))
                    return new(VolumeSlotStatus.StorageError);
                observe?.Invoke(SaveWriteStage.BeforePublish);
                token.ThrowIfCancellationRequested();
                var current = Scan();
                if (!Same(initial.A, current.A) || !Same(initial.B, current.B))
                    return new(VolumeSlotStatus.Conflict);
                WriteIntent(current, target, bytes);
                publishAttempted = true;
                directory.Publish(candidate, target, (target == First ? current.A : current.B).Bytes is not null);
                ownPending = false;
            }
            observe?.Invoke(SaveWriteStage.AfterPublish);
            observe?.Invoke(SaveWriteStage.BeforeAcknowledge);
            return Settle(target, bytes, checkpoint);
        }
        catch (OperationCanceledException) when (!publishAttempted) { return new(VolumeSlotStatus.Canceled); }
        catch (Exception error) when (StorageException(error))
        {
            return publishAttempted ? Settle(target, bytes, checkpoint, error) : new(VolumeSlotStatus.StorageError, Error: error);
        }
        finally
        {
            if (ownPending)
                try { File.Delete(pending); }
                catch (Exception error) when (StorageException(error)) { }
        }
    }

    private VolumeSlotResult Settle(string target, byte[] bytes, VolumeCheckpoint checkpoint, Exception? error = null)
    {
        var scan = Scan();
        var member = target == First ? scan.A : scan.B;
        if (scan.Result.Status != VolumeSlotStatus.Loaded || member.Bytes is not { } actual ||
            !bytes.AsSpan().SequenceEqual(actual))
            return new(VolumeSlotStatus.Indeterminate, Member: target, Error: error ?? scan.Result.Error);
        return new(VolumeSlotStatus.Committed, checkpoint);
    }

    private SlotScan ReadSettled()
    {
        var scan = Scan();
        if (scan.Result.Status is not (VolumeSlotStatus.Loaded or VolumeSlotStatus.Empty))
            return scan;
        var path = Path.Combine(_directory, Intent);
        byte[] marker = new byte[129];
        try
        {
            using var file = VolumeNtfsDirectory.OpenFile(path);
            if (file.Length != marker.Length)
                return scan with { Result = new(VolumeSlotStatus.Indeterminate, Member: Intent) };
            file.ReadExactly(marker);
        }
        catch (IOException error) when ((error.HResult & 0xffff) == 2) { return scan; }
        catch (Exception error) when (StorageException(error) || error is InvalidDataException)
        {
            return scan with { Result = new(VolumeSlotStatus.Indeterminate, Member: Intent, Error: error) };
        }
        var a = MemberDigest(scan.A);
        var b = MemberDigest(scan.B);
        if (!SHA256.HashData(marker.AsSpan(0, 97)).AsSpan().SequenceEqual(marker.AsSpan(97)) ||
            marker[0] is not (1 or 2) ||
            !(a.AsSpan().SequenceEqual(marker.AsSpan(1, 32)) &&
                b.AsSpan().SequenceEqual(marker.AsSpan(33, 32)) ||
                a.AsSpan().SequenceEqual(marker.AsSpan(marker[0] == 1 ? 65 : 1, 32)) &&
                b.AsSpan().SequenceEqual(marker.AsSpan(marker[0] == 2 ? 65 : 33, 32))))
            return scan with { Result = new(VolumeSlotStatus.Indeterminate, Member: Intent) };
        File.Delete(path);
        return scan;
    }

    private void WriteIntent(SlotScan scan, string target, byte[] bytes)
    {
        var marker = new byte[129];
        marker[0] = target == First ? (byte)1 : (byte)2;
        MemberDigest(scan.A).CopyTo(marker, 1);
        MemberDigest(scan.B).CopyTo(marker, 33);
        SHA256.HashData(bytes).CopyTo(marker, 65);
        SHA256.HashData(marker.AsSpan(0, 97), marker.AsSpan(97));
        using var file = VolumeNtfsDirectory.OpenFile(Path.Combine(_directory, Intent), candidate: true);
        file.Write(marker);
        file.Flush(flushToDisk: true);
    }

    private static byte[] MemberDigest(Member member) =>
        member.Bytes is { } bytes ? SHA256.HashData(bytes) : new byte[32];
    private SlotScan Scan()
    {
        var a = Read(First, odd: true);
        var b = Read(Second, odd: false);
        var result = Classify(a, b);
        return new(a, b, result);
    }

    private static VolumeSlotResult Classify(Member a, Member b)
    {
        foreach (var status in new[] { VolumeSlotStatus.StorageError, VolumeSlotStatus.Incompatible })
        {
            if (a.Status == status)
                return new(status, Member: First, Error: a.Error);
            if (b.Status == status)
                return new(status, Member: Second, Error: b.Error);
        }
        var winner = a.Checkpoint is { } first && first.Generation > (b.Checkpoint?.Generation ?? 0)
            ? first : b.Checkpoint;
        if (a.Status == VolumeSlotStatus.Corrupt || b.Status == VolumeSlotStatus.Corrupt)
            return new(winner is null ? VolumeSlotStatus.Corrupt : VolumeSlotStatus.ReadOnlyRecovery,
                winner, a.Status == VolumeSlotStatus.Corrupt ? First : Second);
        if (winner is not null)
        {
            if (a.Checkpoint is { } left && b.Checkpoint is { } right &&
                winner.Generation - Math.Min(left.Generation, right.Generation) != 1 ||
                (a.Checkpoint is null || b.Checkpoint is null) && winner.Generation != 1)
                return new(VolumeSlotStatus.Incompatible);
            return new(VolumeSlotStatus.Loaded, winner);
        }
        return new(VolumeSlotStatus.Empty);
    }

    private Member Read(string name, bool odd)
    {
        try
        {
            using var file = VolumeNtfsDirectory.OpenFile(Path.Combine(_directory, name));
            if (file.Length > VolumeCheckpointCodec.MaximumBytes)
                return new(VolumeSlotStatus.Incompatible);
            var identity = VolumeNtfsDirectory.Identify(file);
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            var decoded = VolumeCheckpointCodec.Decode(bytes, _slot, _lineage, _planet);
            if (decoded.Status == VolumeCheckpointStatus.Incompatible)
                return new(VolumeSlotStatus.Incompatible, bytes);
            if (decoded.Checkpoint is not { } checkpoint)
                return new(VolumeSlotStatus.Corrupt, bytes);
            if ((checkpoint.Generation % 2 == 1) != odd)
                return new(VolumeSlotStatus.Incompatible, bytes);
            return _validate(checkpoint) switch
            {
                SaveGameValidation.Valid => new(VolumeSlotStatus.Loaded, bytes, checkpoint, Identity: identity),
                SaveGameValidation.Malformed => new(VolumeSlotStatus.Corrupt, bytes),
                _ => new(VolumeSlotStatus.Incompatible, bytes),
            };
        }
        catch (IOException error) when ((error.HResult & 0xffff) == 2) { return new(VolumeSlotStatus.Empty); }
        catch (InvalidDataException error) { return new(VolumeSlotStatus.Incompatible, Error: error); }
        catch (Exception error) when (StorageException(error)) { return new(VolumeSlotStatus.StorageError, Error: error); }
    }

    private VolumeSlotResult WithLease(Func<VolumeNtfsDirectory, VolumeSlotResult> action)
    {
        try
        {
            using var directory = new VolumeNtfsDirectory(_directory);
            using var lease = VolumeNtfsDirectory.OpenFile(Path.Combine(_directory, "volume.lock"), lease: true);
            try
            {
                var expected = ReservationBytes();
                using var marker = VolumeNtfsDirectory.OpenFile(Path.Combine(_directory, Reservation));
                if (marker.Length == expected.Length)
                {
                    var actual = new byte[expected.Length];
                    marker.ReadExactly(actual);
                    if (expected.AsSpan().SequenceEqual(actual))
                        return action(directory);
                }
            }
            catch (IOException error) when ((error.HResult & 0xffff) == 2) { }
            return new(VolumeSlotStatus.Incompatible, Member: Reservation);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            return new(VolumeSlotStatus.Busy, Error: error);
        }
        catch (InvalidDataException error) { return new(VolumeSlotStatus.Incompatible, Error: error); }
        catch (Exception error) when (StorageException(error)) { return new(VolumeSlotStatus.StorageError, Error: error); }
    }

    private byte[] ReservationBytes() =>
        VolumeCheckpointCodec.Encode(new VolumeCheckpoint(new(_slot, 0, _slot), _lineage, _planet, 1, 1, new byte[8], []));
    private static bool StorageException(Exception error) => error is IOException or UnauthorizedAccessException;

    private static bool Same(Member left, Member right) =>
        left.Status == right.Status && left.Identity == right.Identity && (left.Bytes is null ? right.Bytes is null :
            right.Bytes is { } rightBytes && left.Bytes.AsSpan().SequenceEqual(rightBytes));

    private sealed record Member(VolumeSlotStatus Status, byte[]? Bytes = null,
        VolumeCheckpoint? Checkpoint = null, Exception? Error = null, VolumeNtfsDirectory.FileIdentity Identity = default);
    private sealed record SlotScan(Member A, Member B, VolumeSlotResult Result);
}
