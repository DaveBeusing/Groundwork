using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12;

/// <summary>The world and exact terrain cut in which a texture binding may become visible.</summary>
public readonly record struct D3D12TextureView(Guid World, VolumeViewIdentity View, ulong TerrainRevision, ulong CoverageGeneration)
{
    internal bool IsValid => World != Guid.Empty && View.RendererEpoch != 0 && View.View != 0 && View.Frame.Revision != 0 &&
        TerrainRevision != 0 && CoverageGeneration != 0;
}

public readonly record struct D3D12TextureSlot(ushort Material, MaterialTextureSemantic Semantic);

/// <summary>Sequence belongs to the individual slot. Unrelated palette changes preserve a current target.</summary>
public readonly record struct D3D12TextureTarget(D3D12TextureSlot Slot, ulong Sequence, MaterialTextureReference Reference);

/// <summary>CPU and GPU byte ceilings for one worker, its handoff and the resulting native binding.</summary>
/// <remarks>StagingBytes and NativeBytes cover aligned backing allocations, independently of upload traffic.</remarks>
public readonly record struct D3D12TextureReservation(long SourceBytes, long ScratchBytes, long PayloadBytes,
    long StagingBytes, long NativeBytes)
{
    internal bool IsValid => SourceBytes is > 0 and <= int.MaxValue && ScratchBytes is >= 0 and <= int.MaxValue &&
        PayloadBytes is > 0 and <= int.MaxValue && StagingBytes > 0 && NativeBytes > 0;
}

/// <summary>Independent ceilings covering workers, their reserved handoffs, and retained native objects.</summary>
public readonly record struct D3D12TextureLimits(int Targets, int Requests, long SourceBytes, long ScratchBytes,
    long PayloadBytes, long StagingBytes, long NativeBytes, int Descriptors)
{
    internal bool IsValid => Targets is > 0 and <= D3D12TextureHandoff.MaximumSlots &&
        Requests is > 0 and <= D3D12TextureHandoff.MaximumSlots &&
        Descriptors is > 0 and <= D3D12TextureHandoff.MaximumSlots && SourceBytes > 0 && ScratchBytes >= 0 &&
        PayloadBytes > 0 && StagingBytes > 0 && NativeBytes > 0;
}

/// <summary>Counts issued credits, including canceled workers and unretired native versions.</summary>
public readonly record struct D3D12TextureUsage(int Requests, int Workers, int Handoffs, int Descriptors,
    long SourceBytes, long ScratchBytes, long PayloadBytes, long StagingBytes, long NativeBytes);

public enum D3D12TextureResult { Accepted, Coalesced, Stale, Invalid, Quota, Closed }

/// <summary>An opaque request capability scoped to one stream and one occupation of an entry.</summary>
public readonly record struct D3D12TextureTicket
{
    internal D3D12TextureTicket(long stream, int index, ulong generation) =>
        (Stream, Index, Generation) = (stream, index, generation);

    internal long Stream { get; }
    public int Index { get; }
    public ulong Generation { get; }
}

internal readonly record struct D3D12TextureTransfer(D3D12TextureTicket Ticket, D3D12TextureView View,
    D3D12TextureTarget Target, CookedTexture Texture, D3D12TextureReservation Reservation);

internal readonly record struct D3D12TerrainTextureBinding(D3D12TextureTarget Target, D3D12TextureTicket? Resident);

/// <summary>Bounded producer-to-render-owner handoff for static cooked textures.</summary>
/// <remarks>
/// Reserve before starting a worker or allocating source, scratch, or payload. The worker reads and validates
/// cooked data within that credit off the render thread. One reservation includes a handoff and a descriptor,
/// including overlap between a resident binding and its replacement. A canceled worker settles its own credit
/// only after draining. The render owner settles native credits only after actual retirement or confirmed removal.
/// </remarks>
public sealed class D3D12TextureHandoff
{
    public const int MaximumSlots = 32;

    private enum State : byte { Free, Worker, Ready, Taken, Copy, Resident, Retiring }

    private struct Entry
    {
        internal State State;
        internal ulong Generation;
        internal bool Invalidated;
        internal D3D12TextureView View;
        internal D3D12TextureTarget Target;
        internal D3D12TextureReservation Reservation;
        internal CookedTexture? Texture;
    }

    private static long _nextStream;
    private readonly long _identity = Interlocked.Increment(ref _nextStream);
    private readonly D3D12TextureLimits _limits;
    private readonly D3D12TextureTarget[] _targets;
    private readonly D3D12TextureTarget[] _publicationScratch;
    private readonly Entry[] _entries;
    private Guid _ownerWorld;
    private ulong _ownerEpoch;
    private D3D12TextureView _view;
    private ulong _paletteRevision;
    private MaterialPalette? _terrainPalette;
    private int _targetCount;
    private ulong _generation;
    private long _materialBacking;
    private D3D12TextureUsage _peak;
    private Action? _wake;
    private int _revoked;
    private bool _drained;
    private bool Closed => Volatile.Read(ref _revoked) != 0;

    internal readonly Lock Gate = new();

    public D3D12TextureHandoff(D3D12TextureLimits limits)
    {
        if (!limits.IsValid)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _limits = limits;
        _targets = new D3D12TextureTarget[limits.Targets];
        _publicationScratch = new D3D12TextureTarget[limits.Targets];
        _entries = new Entry[limits.Descriptors];
    }

    /// <summary>Declare a complete palette cut, carrying forward only exact per-slot targets.</summary>
    /// <remarks>Target order is immaterial. A changed slot in the same cut needs a strictly higher sequence.</remarks>
    public bool Publish(D3D12TextureView view, ulong paletteRevision, ReadOnlySpan<D3D12TextureTarget> targets) =>
        Publish(view, paletteRevision, targets, null);

    /// <summary>Publishes an immutable terrain palette with its exact cooked sampler targets.</summary>
    /// <remarks>The renderer uses numeric material inputs until the matching targets are resident.
    /// Include headroom for the renderer's aligned material constant buffer in the same native backing budget.</remarks>
    public bool PublishTerrainAppearance(D3D12TextureView view, MaterialPalette palette, ReadOnlySpan<D3D12TextureTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return palette.Entries.Length <= MaximumSlots && Publish(view, palette.VisualRevision, targets, palette);
    }

    private bool Publish(D3D12TextureView view, ulong paletteRevision, ReadOnlySpan<D3D12TextureTarget> targets, MaterialPalette? terrainPalette)
    {
        if (Closed)
            return false;
        Action? wake;
        lock (Gate)
        {
            if (Closed || !view.IsValid || paletteRevision == 0 || targets.Length > _targets.Length ||
                (_ownerWorld != Guid.Empty && _ownerWorld != view.World) ||
                (_ownerEpoch != 0 && view.View.RendererEpoch != _ownerEpoch) || (_view.IsValid && !CanAdvanceView(view)))
                return false;

            // Validate the bounded owned snapshot so caller-backed spans cannot change the accepted cut.
            var stableTargets = _publicationScratch.AsSpan(0, targets.Length);
            targets.CopyTo(stableTargets);
            try
            {
                var sameView = view == _view;
                if (sameView && paletteRevision < _paletteRevision)
                    return false;
                for (var i = 0; i < stableTargets.Length; i++)
                {
                    var target = stableTargets[i];
                    if (target.Slot.Material == 0 || target.Sequence == 0 || target.Reference is null ||
                        target.Reference.Semantic != target.Slot.Semantic ||
                        terrainPalette is not null && GetMap(terrainPalette.Resolve(target.Slot.Material), target.Slot.Semantic) != target.Reference)
                        return false;
                    for (var j = 0; j < i; j++)
                        if (stableTargets[j].Slot == target.Slot)
                            return false;

                    if (sameView && FindTarget(target.Slot) is { } previous &&
                        (target.Sequence < previous.Sequence || (target.Sequence == previous.Sequence && target != previous)))
                        return false;
                }

                if (sameView && paletteRevision == _paletteRevision)
                {
                    if (stableTargets.Length != _targetCount || _terrainPalette != terrainPalette)
                        return false;
                    foreach (var target in stableTargets)
                        if (FindTarget(target.Slot) != target)
                            return false;
                    return true;
                }

                _view = view;
                _paletteRevision = paletteRevision;
                _terrainPalette = terrainPalette;
                Array.Clear(_targets);
                stableTargets.CopyTo(_targets);
                _targetCount = stableTargets.Length;
                foreach (ref var entry in _entries.AsSpan())
                    if (entry.State != State.Free && (entry.View != view || FindTarget(entry.Target.Slot) != entry.Target))
                        Invalidate(ref entry);

                wake = _wake;
            }
            finally
            {
                stableTargets.Clear();
            }
        }

        wake?.Invoke();
        return true;
    }

    internal bool TryCaptureTerrainAppearance(D3D12TextureView view, Span<D3D12TerrainTextureBinding> bindings,
        out MaterialPalette? palette, out int count)
    {
        palette = null;
        count = 0;
        lock (Gate)
        {
            if (Closed || view != _view || _terrainPalette is null || bindings.Length < _targetCount)
                return false;

            palette = _terrainPalette;
            count = _targetCount;
            for (var i = 0; i < count; i++)
            {
                var target = _targets[i];
                bindings[i] = new(target, TryGetResident(view, target.Slot, out var ticket) ? ticket : null);
            }
            return true;
        }
    }

    private static MaterialTextureReference? GetMap(PbrMaterialDescriptor descriptor, MaterialTextureSemantic semantic) => semantic switch
    {
        MaterialTextureSemantic.BaseColor => descriptor.Textures.BaseColor,
        MaterialTextureSemantic.Normal => descriptor.Textures.Normal,
        MaterialTextureSemantic.Roughness => descriptor.Textures.Roughness,
        MaterialTextureSemantic.Metallic => descriptor.Textures.Metallic,
        _ => null,
    };

    private bool CanAdvanceView(D3D12TextureView view)
    {
        if (view.World != _view.World || view.View.RendererEpoch < _view.View.RendererEpoch)
            return false;
        if (view.View.RendererEpoch != _view.View.RendererEpoch)
            return true;
        return view.View.View >= _view.View.View && view.TerrainRevision >= _view.TerrainRevision &&
            view.CoverageGeneration >= _view.CoverageGeneration &&
            (view.View.View != _view.View.View || view.View == _view.View);
    }

    /// <summary>Reserve this exact target before dispatch. Coalesced calls do not receive a new worker ticket.</summary>
    public D3D12TextureResult TryRequest(D3D12TextureView view, D3D12TextureTarget target,
        D3D12TextureReservation reservation, out D3D12TextureTicket ticket)
    {
        ticket = default;
        if (Closed)
            return D3D12TextureResult.Closed;
        if (!reservation.IsValid)
            return D3D12TextureResult.Invalid;
        lock (Gate)
        {
            if (Closed)
                return D3D12TextureResult.Closed;
            if (!view.IsValid || view != _view || FindTarget(target.Slot) != target)
                return D3D12TextureResult.Stale;

            var free = -1;
            for (var i = 0; i < _entries.Length; i++)
            {
                ref var entry = ref _entries[i];
                if (entry.State == State.Free)
                {
                    if (free < 0)
                        free = i;
                }
                else if (MatchesCurrent(in entry, view) && entry.Target == target && entry.State != State.Retiring)
                {
                    return D3D12TextureResult.Coalesced;
                }
            }

            var used = CountUsage();
            if (free < 0 || used.Requests == _limits.Requests ||
                reservation.SourceBytes > _limits.SourceBytes - used.SourceBytes ||
                reservation.ScratchBytes > _limits.ScratchBytes - used.ScratchBytes ||
                reservation.PayloadBytes > _limits.PayloadBytes - used.PayloadBytes ||
                reservation.StagingBytes > _limits.StagingBytes - used.StagingBytes ||
                reservation.NativeBytes > _limits.NativeBytes - used.NativeBytes)
                return D3D12TextureResult.Quota;

            var generation = checked(++_generation);
            _entries[free] = new Entry
            {
                State = State.Worker,
                View = view,
                Target = target,
                Reservation = reservation,
                Generation = generation
            };
            ticket = new(_identity, free, generation);
            RecordPeak(CountUsage());
            return D3D12TextureResult.Accepted;
        }
    }

    /// <summary>Transfer a validated cooked payload after the worker has drained its source and scratch storage.</summary>
    /// <remarks>A stale or incompatible completion settles its worker credit without enqueuing an upload.</remarks>
    public D3D12TextureResult TryComplete(D3D12TextureTicket ticket, CookedTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        Action? wake;
        lock (Gate)
        {
            if (!FindEntry(ticket) || _entries[ticket.Index].State != State.Worker)
                return Closed ? D3D12TextureResult.Closed : D3D12TextureResult.Stale;
            ref var entry = ref _entries[ticket.Index];
            if (!MatchesCurrent(in entry, _view))
            {
                entry = default;
                return Closed ? D3D12TextureResult.Closed : D3D12TextureResult.Stale;
            }

            if (texture.CanonicalBytes.Length > entry.Reservation.PayloadBytes ||
                !MatchesBinding(entry.Target.Reference, texture))
            {
                entry = default;
                return D3D12TextureResult.Invalid;
            }

            entry.Texture = texture;
            entry.State = State.Ready;
            wake = _wake;
        }

        wake?.Invoke();
        return D3D12TextureResult.Accepted;
    }

    /// <summary>Settle a worker that finished without transferring a result.</summary>
    public bool CompleteWorker(D3D12TextureTicket ticket)
    {
        lock (Gate)
        {
            if (!FindEntry(ticket) || _entries[ticket.Index].State != State.Worker)
                return false;
            _entries[ticket.Index] = default;
            return true;
        }
    }

    /// <summary>Prevent this outstanding request from installing. Running and native custody remain charged.</summary>
    public bool Cancel(D3D12TextureTicket ticket)
    {
        lock (Gate)
        {
            if (!FindEntry(ticket))
                return false;
            ref var entry = ref _entries[ticket.Index];
            if (entry.Invalidated || entry.State is State.Resident or State.Retiring)
                return false;
            Invalidate(ref entry);
            return true;
        }
    }

    public D3D12TextureUsage ReadUsage()
    {
        lock (Gate)
            return CountUsage();
    }

    public D3D12TextureUsage ReadPeakUsage()
    {
        lock (Gate)
            return _peak;
    }

    internal bool TryReserveMaterialBacking(long bytes)
    {
        lock (Gate)
        {
            if (Closed || bytes <= 0 || _materialBacking != 0 || bytes > _limits.NativeBytes - CountUsage().NativeBytes)
                return false;

            _materialBacking = bytes;
            RecordPeak(CountUsage());
            return true;
        }
    }

    internal void ReleaseMaterialBacking(long bytes)
    {
        lock (Gate)
        {
            if (bytes <= 0 || bytes != _materialBacking)
                throw new InvalidOperationException("The retired material buffer does not match its reserved backing.");
            _materialBacking = 0;
        }
    }

    internal void BindOwner(Guid world, Action wake, ulong rendererEpoch = 0)
    {
        if (world == Guid.Empty)
            throw new ArgumentException("The renderer must supply a world.", nameof(world));
        ArgumentNullException.ThrowIfNull(wake);
        lock (Gate)
        {
            if (Closed || _wake is not null || _view.IsValid &&
                (_view.World != world || rendererEpoch != 0 && _view.View.RendererEpoch != rendererEpoch))
                throw new InvalidOperationException("The texture stream does not match this renderer attachment.");
            _ownerWorld = world;
            _ownerEpoch = rendererEpoch;
            _wake = wake;
        }
    }

    // Stop and watchdog threads revoke admission without taking the lock the
    // renderer may hold around a stalled native call.
    internal void Revoke() => Volatile.Write(ref _revoked, 1);

    internal void Close()
    {
        Revoke();
        lock (Gate)
        {
            if (_drained)
                return;
            _drained = true;
            _wake = null;
            _terrainPalette = null;
            foreach (ref var entry in _entries.AsSpan())
                if (entry.State != State.Free)
                    Invalidate(ref entry);
        }
    }

    internal bool HasReady(D3D12TextureView view)
    {
        lock (Gate)
            return FindReady(view) >= 0;
    }

    internal bool TryTake(D3D12TextureView view, out D3D12TextureTransfer transfer)
    {
        lock (Gate)
        {
            var index = FindReady(view);
            if (index < 0)
            {
                transfer = default;
                return false;
            }

            ref var entry = ref _entries[index];
            entry.State = State.Taken;
            transfer = new(Ticket(index), view, entry.Target, entry.Texture!, entry.Reservation);
            return true;
        }
    }

    /// <summary>Return a transfer whose native copy did not begin. The owner controls wake re-arming.</summary>
    internal bool TryReturnToReady(D3D12TextureTicket ticket, D3D12TextureView view)
    {
        lock (Gate)
        {
            if (!FindEntry(ticket))
                return false;
            ref var entry = ref _entries[ticket.Index];
            if (entry.State != State.Taken || !MatchesCurrent(in entry, view))
                return false;
            entry.State = State.Ready;
            return true;
        }
    }

    internal bool MarkCopySubmitted(D3D12TextureTicket ticket)
    {
        lock (Gate)
        {
            if (!FindEntry(ticket) || _entries[ticket.Index].State != State.Taken)
                return false;

            ref var entry = ref _entries[ticket.Index];
            entry.State = State.Copy;
            entry.Texture = null;
            return true;
        }
    }

    internal bool IsCurrent(D3D12TextureTicket ticket, D3D12TextureView view)
    {
        lock (Gate)
            return FindEntry(ticket) && MatchesCurrent(in _entries[ticket.Index], view);
    }

    /// <summary>Install after a successful assigned copy fence and physical staging release.</summary>
    internal bool TryInstall(D3D12TextureTicket ticket, D3D12TextureView view, out D3D12TextureTicket predecessor)
    {
        predecessor = default;
        lock (Gate)
        {
            if (!FindEntry(ticket))
                return false;
            ref var entry = ref _entries[ticket.Index];
            if (entry.State != State.Copy || !MatchesCurrent(in entry, view))
                return false;
            for (var i = 0; i < _entries.Length; i++)
            {
                ref var previous = ref _entries[i];
                if (previous.State != State.Resident || previous.Target.Slot != entry.Target.Slot)
                    continue;

                predecessor = Ticket(i);
                previous.State = State.Retiring;
                break;
            }

            entry.State = State.Resident;

            return true;
        }
    }

    /// <summary>Release owner-held credit after native objects are disposed or if none were acquired.</summary>
    internal bool Release(D3D12TextureTicket ticket)
    {
        lock (Gate)
        {
            if (!FindEntry(ticket) || _entries[ticket.Index].State is not (State.Taken or State.Copy or State.Resident or State.Retiring))
                return false;

            _entries[ticket.Index] = default;
            return true;
        }
    }

    internal bool TryGetResident(D3D12TextureView view, D3D12TextureSlot slot, out D3D12TextureTicket ticket)
    {
        lock (Gate)
        {
            for (var i = 0; i < _entries.Length; i++)
            {
                ref readonly var entry = ref _entries[i];
                if (entry.State == State.Resident && entry.Target.Slot == slot && MatchesCurrent(in entry, view))
                {
                    ticket = Ticket(i);
                    return true;
                }
            }

            ticket = default;
            return false;
        }
    }

    /// <summary>Static reference route selects the lowest currently targeted base-color material.</summary>
    internal bool TryGetDrawableResident(D3D12TextureView view, out D3D12TextureTicket ticket)
    {
        lock (Gate)
        {
            ticket = default;
            var slot = default(D3D12TextureSlot);
            for (var i = 0; i < _targetCount; i++)
            {
                var candidate = _targets[i].Slot;
                if (candidate.Semantic == MaterialTextureSemantic.BaseColor && (slot.Material == 0 || candidate.Material < slot.Material))
                    slot = candidate;
            }

            return slot.Material != 0 && TryGetResident(view, slot, out ticket);
        }
    }

    private int FindReady(D3D12TextureView view)
    {
        var oldest = -1;
        for (var i = 0; i < _entries.Length; i++)
        {
            ref readonly var entry = ref _entries[i];
            if (entry.State == State.Ready && MatchesCurrent(in entry, view) &&
                (oldest < 0 || entry.Generation < _entries[oldest].Generation))
                oldest = i;
        }

        return oldest;
    }

    private static void Invalidate(ref Entry entry)
    {
        // Ready owns no active worker and has no native use. All other owners settle explicitly.
        if (entry.State == State.Ready)
            entry = default;
        else
            entry.Invalidated = true;
    }

    private bool MatchesCurrent(in Entry entry, D3D12TextureView view) =>
        !Closed && !entry.Invalidated && view == _view && entry.View == view;

    private bool FindEntry(D3D12TextureTicket ticket) =>
        ticket.Stream == _identity && (uint)ticket.Index < (uint)_entries.Length && ticket.Generation != 0 &&
        _entries[ticket.Index].Generation == ticket.Generation;

    private D3D12TextureTicket Ticket(int i) => new(_identity, i, _entries[i].Generation);

    private D3D12TextureTarget? FindTarget(D3D12TextureSlot slot)
    {
        for (var i = 0; i < _targetCount; i++)
            if (_targets[i].Slot == slot)
                return _targets[i];
        return null;
    }

    private static bool MatchesBinding(MaterialTextureReference reference, CookedTexture texture)
    {
        var expectedSemantic = reference.Semantic switch
        {
            MaterialTextureSemantic.BaseColor or MaterialTextureSemantic.Emissive => TextureSemantic.Color,
            MaterialTextureSemantic.Normal => TextureSemantic.Normal,
            _ => TextureSemantic.Data
        };
        var colorSpace = texture.ColorSpace switch
        {
            TextureColorSpace.Linear => MaterialTextureColorSpace.Linear,
            TextureColorSpace.Srgb => MaterialTextureColorSpace.Srgb,
            _ => default
        };
        return texture.Semantic == expectedSemantic && reference.MatchesBinding(texture.AssetId, colorSpace);
    }

    private D3D12TextureUsage CountUsage()
    {
        var requests = 0;
        var workers = 0;
        var handoffs = 0;
        var descriptors = 0;
        long source = 0, scratch = 0, payload = 0, staging = 0, native = _materialBacking;
        foreach (ref readonly var entry in _entries.AsSpan())
        {
            var state = entry.State;
            if (state == State.Free)
                continue;
            var reserve = entry.Reservation;
            descriptors++;
            native += reserve.NativeBytes;

            if (state is State.Resident or State.Retiring)
                continue;
            requests++;
            staging += reserve.StagingBytes;

            if (state == State.Copy)
                continue;
            payload += reserve.PayloadBytes;

            handoffs++;
            if (state == State.Worker)
            {
                workers++;
                source += reserve.SourceBytes;
                scratch += reserve.ScratchBytes;
            }
        }

        return new(requests, workers, handoffs, descriptors, source, scratch, payload, staging, native);
    }

    private void RecordPeak(D3D12TextureUsage used) => _peak = new(
        Math.Max(_peak.Requests, used.Requests), Math.Max(_peak.Workers, used.Workers),
        Math.Max(_peak.Handoffs, used.Handoffs), Math.Max(_peak.Descriptors, used.Descriptors),
        Math.Max(_peak.SourceBytes, used.SourceBytes), Math.Max(_peak.ScratchBytes, used.ScratchBytes),
        Math.Max(_peak.PayloadBytes, used.PayloadBytes), Math.Max(_peak.StagingBytes, used.StagingBytes),
        Math.Max(_peak.NativeBytes, used.NativeBytes));
}
