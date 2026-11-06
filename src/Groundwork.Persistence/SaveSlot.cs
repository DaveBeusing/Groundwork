using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Groundwork.Persistence;

/// <summary>Stores one local Windows NTFS checkpoint with alternating committed files.</summary>
/// <remarks>
/// Supply an application-owned directory. Call outside simulation barriers on a host-tracked I/O worker.
/// A game validator may check application compatibility and reconstructed state before selection or publication.
/// </remarks>
public sealed partial class SaveSlot
{
    private const string First = "save.a";
    private const string Second = "save.b";
    private const string Pending = "save.pending";
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    private readonly string _directory;
    private readonly Func<uint, uint, ISaveTerrainGenerator?> _resolveGenerator;
    private readonly Func<SaveCheckpoint, SaveGameValidation> _validateGame;

    public SaveSlot(string directory, Func<uint, uint, ISaveTerrainGenerator?> resolveGenerator,
        Func<SaveCheckpoint, SaveGameValidation>? validateGame = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Save slots require Windows NTFS.");
        ArgumentNullException.ThrowIfNull(resolveGenerator);
        _directory = Path.GetFullPath(directory);
        _resolveGenerator = resolveGenerator;
        _validateGame = validateGame ?? (static _ => SaveGameValidation.Valid);
    }

    /// <summary>Loads the newest usable committed checkpoint while holding the slot lease.</summary>
    public SaveSlotResult Load() => WithLease(() => Scan().Result);

    /// <summary>Publishes a checkpoint with generation expectedGeneration + 1.</summary>
    public SaveSlotResult Save(ulong expectedGeneration, SaveCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
        SaveCore(expectedGeneration, checkpoint, observe: null, cancellationToken);

    internal SaveSlotResult Save(ulong expectedGeneration, SaveCheckpoint checkpoint, Action<SaveWriteStage> observe,
        CancellationToken cancellationToken = default) => SaveCore(expectedGeneration, checkpoint, observe, cancellationToken);

    private SaveSlotResult SaveCore(ulong expectedGeneration, SaveCheckpoint checkpoint, Action<SaveWriteStage>? observe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (cancellationToken.IsCancellationRequested)
            return new(SaveSlotStatus.Canceled);

        return WithLease(() =>
        {
            var scan = Scan();
            if (scan.Result.Status is not (SaveSlotStatus.Empty or SaveSlotStatus.Loaded))
                return scan.Result;

            var current = scan.Selected?.Decoded.Checkpoint;
            var identity = checkpoint.Identity;
            if (current is not null && (identity.WorldLineageId != current.Identity.WorldLineageId || checkpoint.Recipe != current.Recipe))
                return new(SaveSlotStatus.Conflict, Identity: current.Identity);

            var retry = current is not null && identity.SaveRequestId == current.Identity.SaveRequestId;
            if (!retry && (expectedGeneration != (current?.Identity.Generation ?? 0) ||
                expectedGeneration == ulong.MaxValue || identity.Generation != expectedGeneration + 1 ||
                HasRequestId(scan, identity.SaveRequestId)))
                return new(SaveSlotStatus.Conflict, Identity: current?.Identity);

            var generator = _resolveGenerator(checkpoint.Recipe.GeneratorId, checkpoint.Recipe.GeneratorVersion);
            if (generator is null)
                return new(SaveSlotStatus.Unsupported, Identity: identity);
            byte[] encoded;
            try
            {
                encoded = SaveCodec.Encode(checkpoint, generator);
            }
            catch (InvalidDataException error)
            {
                return new(SaveSlotStatus.InvalidCheckpoint, Error: error);
            }

            if (retry)
                return SameCheckpoint(encoded, scan.Selected!.Bytes)
                    ? new(SaveSlotStatus.AlreadyCommitted, current, current!.Identity, scan.Result.Recovery)
                    : new(SaveSlotStatus.Conflict, Identity: current!.Identity);

            var validation = _validateGame(checkpoint);
            if (validation != SaveGameValidation.Valid)
                return new(validation == SaveGameValidation.Unsupported ? SaveSlotStatus.Unsupported : SaveSlotStatus.InvalidCheckpoint,
                    Identity: identity);

            return Publish(scan, checkpoint, encoded, observe, cancellationToken);
        });
    }

    private SaveSlotResult Publish(SlotScan original, SaveCheckpoint checkpoint, byte[] bytes,
        Action<SaveWriteStage>? observe, CancellationToken cancellationToken)
    {
        var target = original.Selected?.Name == First ? Second : First;
        var pending = SlotPath(Pending);
        var publishing = false;
        var published = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            observe?.Invoke(SaveWriteStage.BeforeWrite);
            WriteCandidate(pending, bytes, observe);
            if (!MatchesFile(Pending, bytes))
                return new(SaveSlotStatus.StorageError, Error: new IOException("The staged checkpoint did not verify."));

            observe?.Invoke(SaveWriteStage.BeforePublish);
            cancellationToken.ThrowIfCancellationRequested();
            publishing = true;
            MoveAtomically(pending, SlotPath(target));
            published = true;
            observe?.Invoke(SaveWriteStage.AfterPublish);
            if (!MatchesFile(target, bytes))
                throw new IOException("The published checkpoint did not verify.");
            observe?.Invoke(SaveWriteStage.BeforeAcknowledge);
            return new(SaveSlotStatus.Committed, checkpoint, checkpoint.Identity);
        }
        catch (OperationCanceledException) when (!publishing && cancellationToken.IsCancellationRequested)
        {
            return new(SaveSlotStatus.Canceled);
        }
        catch (Exception error) when (publishing && error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return ResolvePublication(original, bytes, published, error);
        }
    }

    private SaveSlotResult ResolvePublication(SlotScan original, byte[] bytes, bool published, Exception error)
    {
        try
        {
            var current = Scan();
            if (current.Selected is { } selected && selected.Bytes.AsSpan().SequenceEqual(bytes))
                return new(SaveSlotStatus.Committed, selected.Decoded.Checkpoint, selected.Decoded.Identity, current.Result.Recovery);
            if (!published && (current.Result.Status == SaveSlotStatus.Empty && original.Result.Status == SaveSlotStatus.Empty ||
                current.Selected is { } prior && original.Selected is { } baseline &&
                current.Result.Recovery == original.Result.Recovery && prior.Bytes.AsSpan().SequenceEqual(baseline.Bytes)))
                return new(SaveSlotStatus.StorageError, Error: error);
        }
        catch (Exception scanError) when (scanError is IOException or UnauthorizedAccessException)
        {
            return new(SaveSlotStatus.Indeterminate, Error: new AggregateException(error, scanError));
        }

        return new(SaveSlotStatus.Indeterminate, Error: error);
    }

    private static void WriteCandidate(string path, ReadOnlySpan<byte> bytes, Action<SaveWriteStage>? observe)
    {
        RejectReparsePoint(path);
        // Unlink the stale candidate before creating a new file. Never truncate an existing file.
        File.Delete(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1);
        if (observe is null)
        {
            stream.Write(bytes);
        }
        else
        {
            const int partialHeader = 50;
            var partialBody = 100 + (bytes.Length - 132) / 2;
            var partialFooter = bytes.Length - 16;
            stream.Write(bytes[..partialHeader]);
            observe(SaveWriteStage.PartialHeader);
            stream.Write(bytes[partialHeader..partialBody]);
            observe(SaveWriteStage.PartialBody);
            stream.Write(bytes[partialBody..partialFooter]);
            observe(SaveWriteStage.PartialFooter);
            stream.Write(bytes[partialFooter..]);
        }

        observe?.Invoke(SaveWriteStage.BeforeFlush);
        stream.Flush(flushToDisk: true);
        observe?.Invoke(SaveWriteStage.AfterFlush);
    }

    private SlotScan Scan()
    {
        var a = ReadFrame(First);
        var b = ReadFrame(Second);
        if (a is null && b is null)
            return new(new(SaveSlotStatus.Empty), a, b, null);
        if (a?.Decoded.Status == SaveReadStatus.UnsupportedFraming || b?.Decoded.Status == SaveReadStatus.UnsupportedFraming)
            return new(new(SaveSlotStatus.Unsupported), a, b, null);
        if (a?.Decoded.Identity is { } ai && b?.Decoded.Identity is { } bi &&
            (ai.WorldLineageId != bi.WorldLineageId || ai.Generation == bi.Generation && !a.Bytes.AsSpan().SequenceEqual(b.Bytes)))
            return new(new(SaveSlotStatus.Conflict), a, b, null);

        var first = (a?.Decoded.Identity?.Generation ?? 0) >= (b?.Decoded.Identity?.Generation ?? 0) ? a : b;
        var second = ReferenceEquals(first, a) ? b : a;
        SaveRecoveryEvidence? recovery = null;
        foreach (var frame in new[] { first, second })
        {
            if (frame is null)
                continue;

            var decoded = frame.Decoded;
            if (decoded.Status is SaveReadStatus.UnsupportedSchema or SaveReadStatus.UnsupportedGenerator)
                return new(new(SaveSlotStatus.Unsupported, Identity: decoded.Identity), a, b, null);
            if (decoded.Checkpoint is not { } checkpoint)
            {
                recovery = new(frame.Name, "Invalid frame or terrain payload.");
                continue;
            }

            switch (_validateGame(checkpoint))
            {
                case SaveGameValidation.Unsupported:
                    return new(new(SaveSlotStatus.Unsupported, Identity: decoded.Identity), a, b, null);
                case SaveGameValidation.Malformed:
                    recovery = new(frame.Name, "Invalid application payload.");
                    continue;
                case SaveGameValidation.Valid:
                    var other = ReferenceEquals(frame, a) ? b : a;
                    if (other?.Decoded.Recipe is { } recipe && recipe != checkpoint.Recipe)
                        return new(new(SaveSlotStatus.Conflict), a, b, null);
                    if (recovery is null && !ReferenceEquals(frame, second) && second?.Decoded.Status == SaveReadStatus.Malformed)
                        recovery = new(second.Name, "Invalid frame or terrain payload.");
                    return new(new(SaveSlotStatus.Loaded, checkpoint, checkpoint.Identity, recovery), a, b, frame);
                default:
                    throw new InvalidOperationException("The game validator returned an unknown result.");
            }
        }

        return new(new(SaveSlotStatus.Corrupt, Recovery: recovery), a, b, null);
    }

    private SlotFrame? ReadFrame(string name)
    {
        var path = SlotPath(name);
        RejectReparsePoint(path);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        using (stream)
        {
            var length = stream.Length;
            if (length > SaveCodec.MaximumFrameBytes)
                return new(name, [], default);

            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new IOException("The checkpoint changed during the read.");
            return new(name, bytes, SaveCodec.Decode(bytes, _resolveGenerator));
        }
    }

    private bool MatchesFile(string name, ReadOnlySpan<byte> bytes) =>
        ReadFrame(name) is { Decoded.Status: SaveReadStatus.Valid } frame && frame.Bytes.AsSpan().SequenceEqual(bytes);

    private SaveSlotResult WithLease(Func<SaveSlotResult> action)
    {
        try
        {
            var root = Path.GetPathRoot(_directory)!;
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Save slots require a local NTFS drive.");
            var drive = new DriveInfo(root);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) ||
                !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Save slots require a local NTFS drive.");

            // A linked directory could redirect publication to another filesystem.
            for (var parent = new DirectoryInfo(_directory); parent is not null; parent = parent.Parent)
                RejectReparsePoint(parent.FullName);

            Directory.CreateDirectory(_directory);
            var lockPath = SlotPath("save.lock");
            RejectReparsePoint(lockPath);
            FileStream lease;
            try
            {
                lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is SharingViolation or LockViolation)
            {
                return new(SaveSlotStatus.Busy);
            }

            using (lease)
                return action();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(SaveSlotStatus.StorageError, Error: error);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A save slot path cannot contain a reparse point.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private string SlotPath(string name) => Path.Combine(_directory, name);

    private static bool HasRequestId(SlotScan scan, Guid id) =>
        scan.First?.Decoded.Identity?.SaveRequestId == id || scan.Second?.Decoded.Identity?.SaveRequestId == id;

    private static bool SameCheckpoint(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) =>
        first.Slice(24, 16).SequenceEqual(second.Slice(24, 16)) && first[56..^32].SequenceEqual(second[56..^32]);

    private static void MoveAtomically(string source, string destination)
    {
        const uint replaceExisting = 1;
        const uint writeThrough = 8;
        // The same directory and absence of COPY_ALLOWED keep this a rename.
        if (MoveFileEx(@"\\?\" + source, @"\\?\" + destination, replaceExisting | writeThrough) == 0)
            throw new IOException("Could not publish the checkpoint.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MoveFileEx(string existingFile, string newFile, uint flags);

    private sealed record SlotFrame(string Name, byte[] Bytes, SaveReadResult Decoded);
    private sealed record SlotScan(SaveSlotResult Result, SlotFrame? First, SlotFrame? Second, SlotFrame? Selected);
}
