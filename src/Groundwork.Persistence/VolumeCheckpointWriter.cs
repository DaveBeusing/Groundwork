using Groundwork.Voxels;

namespace Groundwork.Persistence;

public enum VolumeCaptureAdmission { Accepted, Busy, Stale, Closed, Invalid }

/// <summary>A capacity-one capture queue. Its worker owns the fixed save operation through settlement.</summary>
public sealed class VolumeCheckpointWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly VolumeCheckpointSlot _slot;
    private IDisposable? _ownership;
    private ulong _base;
    private (ulong Tick, ulong Ordinal) _watermark;
    private Pending? _pending;
    private bool _executing;
    private bool _closed;

    internal VolumeCheckpointWriter(VolumeCheckpointSlot slot, VolumeCheckpoint? durable, IDisposable ownership)
    {
        _slot = slot;
        _ownership = ownership;
        if (durable is not null)
        {
            _base = durable.Generation;
            _watermark = (durable.Tick, durable.CaptureOrdinal);
        }
    }

    /// <summary>Transfers cut ownership only on Accepted. Invoke outside the Core finalization gate.</summary>
    public VolumeCaptureAdmission TryAdmit(VolumeCheckpointCut cut, ulong ordinal,
        Func<VolumeWorldRoot, Memory<byte>, int> encodeGame)
    {
        ArgumentNullException.ThrowIfNull(encodeGame);
        if (cut.Terrain?.EditLineage != _slot.Planet || ordinal == 0 ||
            cut.Publication.Stamp.Tick is 0 or ulong.MaxValue)
            return VolumeCaptureAdmission.Invalid;
        lock (_gate)
        {
            if (_closed)
                return VolumeCaptureAdmission.Closed;
            var key = (cut.Publication.Stamp.Tick, ordinal);
            if (key.CompareTo(_watermark) <= 0)
                return VolumeCaptureAdmission.Stale;
            if (_pending is not null)
                return VolumeCaptureAdmission.Busy;
            if (_base == ulong.MaxValue)
                return VolumeCaptureAdmission.Closed;
            _pending = new(cut, new(_slot.SlotId, _base, Guid.NewGuid()), ordinal, encodeGame);
            _watermark = key;
            return VolumeCaptureAdmission.Accepted;
        }
    }

    /// <summary>Runs on a host-tracked I/O worker. A retry uses the already encoded checkpoint.</summary>
    public VolumeSlotResult ExecutePending(CancellationToken token = default)
    {
        Pending pending;
        lock (_gate)
        {
            if (_executing)
                return new(VolumeSlotStatus.Busy);
            if (_pending is not { } item)
                return new(_closed ? VolumeSlotStatus.Canceled : VolumeSlotStatus.Empty);
            pending = item;
            _executing = true;
        }

        VolumeSlotResult? result = null;
        var enteredStorage = false;
        try
        {
            if (pending.Checkpoint is null)
            {
                if (token.IsCancellationRequested)
                    return result = new(VolumeSlotStatus.Canceled);
                pending.Checkpoint = VolumeCheckpoint.TryCapture(pending.Cut, pending.Operation,
                    _slot.Lineage, pending.EncodeGame, pending.Ordinal);
                pending.ReleaseCut();
                if (pending.Checkpoint is null)
                    return result = new(VolumeSlotStatus.InvalidCheckpoint);
            }
            enteredStorage = true;
            return result = _slot.Save(pending.Checkpoint, pending.Unsettled ? CancellationToken.None : token);
        }
        finally
        {
            lock (_gate)
            {
                _executing = false;
                pending.Unsettled |= result?.Status is VolumeSlotStatus.Indeterminate or VolumeSlotStatus.StorageError ||
                    result is null && enteredStorage;
                if (result?.Status is VolumeSlotStatus.Committed or VolumeSlotStatus.AlreadyCommitted)
                    _base = pending.Checkpoint!.Generation;
                if (result?.Status is VolumeSlotStatus.Stale or VolumeSlotStatus.Conflict or VolumeSlotStatus.ReadOnlyRecovery or
                    VolumeSlotStatus.Incompatible or VolumeSlotStatus.Corrupt)
                    _closed = true;
                var keep = result?.Status is VolumeSlotStatus.Busy or VolumeSlotStatus.Indeterminate or VolumeSlotStatus.StorageError ||
                    result is null && pending.Unsettled;
                if (!keep || _closed && !pending.Unsettled)
                {
                    _pending = null;
                    pending.ReleaseCut();
                }
                ReleaseOwnershipIfClosed();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            if (!_executing && _pending is { Unsettled: false } pending)
            {
                _pending = null;
                pending.ReleaseCut();
            }
            ReleaseOwnershipIfClosed();
        }
    }

    private void ReleaseOwnershipIfClosed()
    {
        if (!_closed || _executing || _pending is not null)
            return;
        _ownership?.Dispose();
        _ownership = null;
    }
    private sealed class Pending(VolumeCheckpointCut cut, VolumeSaveOperation operation, ulong ordinal,
        Func<VolumeWorldRoot, Memory<byte>, int> encodeGame)
    {
        public VolumeSaveOperation Operation { get; } = operation;
        public ulong Ordinal { get; } = ordinal;
        public Func<VolumeWorldRoot, Memory<byte>, int> EncodeGame { get; } = encodeGame;
        public VolumeCheckpointCut Cut { get; private set; } = cut;

        public void ReleaseCut()
        {
            Cut.Dispose();
            Cut = default;
        }
        public VolumeCheckpoint? Checkpoint { get; set; }
        public bool Unsettled { get; set; }
    }
}
