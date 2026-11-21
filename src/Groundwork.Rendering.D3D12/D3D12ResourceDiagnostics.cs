using System.Diagnostics;
using System.Runtime.CompilerServices;
using Groundwork.Rendering.Passes;

namespace Groundwork.Rendering.D3D12;

/// <summary>Owner of a renderer-tracked D3D12 backing allocation.</summary>
public enum D3D12ResourceOwner
{
    Presentation,
    Depth,
    Terrain,
    Shadow,
    Props,
    Readback,
    Texture,
    TextureStaging
}

/// <summary>Native resource counts and aligned backing bytes. Released totals are cumulative.</summary>
public readonly record struct D3D12ResourceBalance(int Live, ulong LiveBytes, int Retained, ulong RetainedBytes,
    ulong Released, ulong ReleasedBytes);

/// <summary>Observed transitions and distinct native allocations used by one submitted pass.</summary>
public readonly record struct D3D12PassObservation(int Barriers, int ResourceUses);

[Flags]
public enum D3D12CustodyStatus
{
    None = 0,
    UnassignedSubmission = 1,
    FenceSentinelObserved = 2,
    ConfirmedRemoval = 4,
    IncompleteCleanup = 8
}

/// <summary>One coherent owner publication. Frame sequences match the renderer and WP11 receipts.</summary>
public struct D3D12ResourceSnapshot
{
    public ulong FrameSequence { readonly get; internal set; }
    public long DrawTimestamp { readonly get; internal set; }
    public ulong DrawFence { readonly get; internal set; }
    public ulong PresentedSequence { readonly get; internal set; }
    public long PresentTimestamp { readonly get; internal set; }
    public ulong PresentFence { readonly get; internal set; }
    public ulong RetiredPresentSequence { readonly get; internal set; }
    public ulong RetiredPresentFence { readonly get; internal set; }
    public long RetiredPresentTimestamp { readonly get; internal set; }
    public ulong SubmittedFence { readonly get; internal set; }
    /// <summary>Last assigned texture-copy fence, independently of draw and Present fences.</summary>
    public ulong TextureCopyFence { readonly get; internal set; }
    public ulong CompletedFence { readonly get; internal set; }
    public ulong UnassignedFence { readonly get; internal set; }
    public ulong UnassignedFrameSequence { readonly get; internal set; }
    public D3D12CustodyStatus Custody { readonly get; internal set; }
    /// <summary>At least one allocation was untracked, or a backing total saturated.</summary>
    public bool LedgerOverflowed { readonly get; internal set; }
    /// <summary>At least one native allocation size was unavailable. Its count is included, and its bytes are omitted.</summary>
    public bool LedgerHasUnknownSizes { readonly get; internal set; }

    internal OwnerBalances Balances;
    internal PassObservations Observations;
    internal uint ObservedPasses;

    /// <summary>Live and retained are disjoint. A retained allocation is still charged until release.</summary>
    public readonly D3D12ResourceBalance GetBalance(D3D12ResourceOwner owner)
    {
        var index = (int)owner;
        if ((uint)index >= OwnerBalances.Length)
            throw new ArgumentOutOfRangeException(nameof(owner));
        return Balances[index];
    }

    /// <summary>Gets observations from the last draw with an assigned fence.</summary>
    public readonly bool TryGetPass(RenderPassId id, out D3D12PassObservation observation)
    {
        var index = id.Value - 1;
        if (index >= PassObservations.Length || (ObservedPasses & (1U << (int)index)) == 0)
        {
            observation = default;
            return false;
        }
        observation = Observations[(int)index];
        return true;
    }

    [InlineArray(Length)]
    internal struct OwnerBalances
    {
        internal const int Length = 8;
        private D3D12ResourceBalance _element;
    }

    [InlineArray(Length)]
    internal struct PassObservations
    {
        internal const int Length = 10;
        private D3D12PassObservation _element;
    }
}

/// <summary>Single native owner, bounded read retries, no sink calls on the producer path.</summary>
internal sealed class ResourceDiagnostics
{
    // Keep the original scene inventory plus all 32 texture slots and one copy staging allocation.
    private const int Capacity = 128;
    private readonly Entry[] _entries = new Entry[Capacity];
    private D3D12ResourceSnapshot _state;
    private D3D12ResourceSnapshot _published;
    private long _revision;
    private D3D12ResourceSnapshot.PassObservations _passes;
    private uint _passIds;
    private UInt128 _usedResources;
    private UInt128 _passResources;
    private int _passIndex;

    internal bool TryRead(out D3D12ResourceSnapshot snapshot)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = Volatile.Read(ref _revision);
            if (before == 0 || (before & 1) != 0)
                continue;
            snapshot = _published;
            // The second sequence read must follow the entire snapshot copy.
            Thread.MemoryBarrier();
            if (Volatile.Read(ref _revision) == before)
                return true;
        }
        snapshot = default;
        return false;
    }

    internal void Track(nint pointer, D3D12ResourceOwner owner, ulong bytes)
    {
        if (bytes is 0 or ulong.MaxValue)
        {
            _state.LedgerHasUnknownSizes = true;
            bytes = 0;
        }
        foreach (ref var slot in _entries.AsSpan())
        {
            if (slot.Pointer != 0)
                continue;
            slot = new(pointer, owner, bytes, 0, false);
            Publish();
            return;
        }
        _state.LedgerOverflowed = true;
        Publish();
    }

    internal void Release(nint pointer) => Forget(pointer, released: true);

    // The native resource stays live under the device session's separate ledger.
    internal void ForgetTransferred(nint pointer) => Forget(pointer, released: false);

    private void Forget(nint pointer, bool released)
    {
        var index = IndexOf(pointer);
        if (index < 0)
            return;
        ref var slot = ref _entries[index];
        if (released)
        {
            ref var balance = ref _state.Balances[(int)slot.Owner];
            balance = balance with
            {
                Released = AddSaturating(balance.Released, 1),
                ReleasedBytes = AddSaturating(balance.ReleasedBytes, slot.Bytes)
            };
        }
        slot = default;
        var bit = (UInt128)1 << index;
        _usedResources &= ~bit;
        _passResources &= ~bit;
        Publish();
    }

    internal void BeginRecording()
    {
        _passes = default;
        _passIds = 0;
        _usedResources = _passResources = 0;
    }

    internal void BeginPass(RenderPassId pass)
    {
        _passIndex = (int)pass.Value - 1;
        _passIds |= 1U << _passIndex;
        _passResources = 0;
    }

    internal void Barrier() => _passes[_passIndex] =
        _passes[_passIndex] with { Barriers = _passes[_passIndex].Barriers + 1 };

    internal void Use(nint pointer)
    {
        var index = IndexOf(pointer);
        if (index < 0)
            return;
        var bit = (UInt128)1 << index;
        if ((_passResources & bit) == 0)
        {
            _passResources |= bit;
            _usedResources |= bit;
            _passes[_passIndex] = _passes[_passIndex] with { ResourceUses = _passes[_passIndex].ResourceUses + 1 };
        }
    }

    internal void BeginDraw(ulong sequence, ulong fence)
    {
        HoldDrawResources();
        BeginSubmission(sequence, fence);
    }

    internal void BeginPresent(ulong fence, nint buffer)
    {
        // The owner holds the accepted draw and its terrain reader until the separate
        // post-Present submission retires. Keep its recorded resources in the same cut.
        HoldDrawResources();
        HoldResource(buffer);
        BeginSubmission(_state.FrameSequence, fence);
    }

    internal void BeginTextureCopy(ulong fence, nint staging, nint destination)
    {
        HoldResource(staging);
        HoldResource(destination);
        // Standalone copy work has no frame sequence or pass observations.
        BeginSubmission(0, fence);
    }

    internal void SignalTextureCopy(ulong fence)
    {
        _state.TextureCopyFence = fence;
        Signal(fence, draw: false);
    }

    private void HoldResource(nint pointer)
    {
        var index = IndexOf(pointer);
        if (index >= 0)
            _entries[index].Pending = true;
    }

    private void HoldDrawResources()
    {
        var used = _usedResources;
        while (used != 0)
        {
            var index = (int)UInt128.TrailingZeroCount(used);
            _entries[index].Pending = true;
            used &= used - 1;
        }
    }

    private void BeginSubmission(ulong sequence, ulong fence)
    {
        _state.UnassignedFrameSequence = sequence;
        _state.UnassignedFence = fence;
        _state.Custody |= D3D12CustodyStatus.UnassignedSubmission;
        Publish();
    }

    internal void Signal(ulong fence, bool draw)
    {
        foreach (ref var slot in _entries.AsSpan())
        {
            if (!slot.Pending)
                continue;
            slot.LastUse = fence;
            slot.Pending = false;
        }
        if (draw)
        {
            _state.FrameSequence = _state.UnassignedFrameSequence;
            _state.DrawTimestamp = Stopwatch.GetTimestamp();
            _state.DrawFence = fence;
            _state.Observations = _passes;
            _state.ObservedPasses = _passIds;
        }
        _state.SubmittedFence = fence;
        _state.UnassignedFence = _state.UnassignedFrameSequence = 0;
        _state.Custody &= ~D3D12CustodyStatus.UnassignedSubmission;
        Publish();
    }

    internal void Presented(ulong fence, long timestamp)
    {
        _state.PresentedSequence = _state.FrameSequence;
        _state.PresentTimestamp = timestamp;
        _state.PresentFence = fence;
        Publish();
    }

    internal void Complete(ulong value)
    {
        if (value == ulong.MaxValue)
        {
            NoteCustody(D3D12CustodyStatus.FenceSentinelObserved);
            return;
        }
        if (value <= _state.CompletedFence)
            return;
        _state.CompletedFence = value;
        if (_state.PresentFence != 0 && _state.CompletedFence >= _state.PresentFence &&
            _state.RetiredPresentFence != _state.PresentFence)
        {
            _state.RetiredPresentSequence = _state.PresentedSequence;
            _state.RetiredPresentTimestamp = Stopwatch.GetTimestamp();
            _state.RetiredPresentFence = _state.PresentFence;
        }
        Publish();
    }

    internal void NoteCustody(D3D12CustodyStatus status)
    {
        if ((_state.Custody & status) == status)
            return;
        _state.Custody |= status;
        Publish();
    }

    private void Publish()
    {
        for (var owner = 0; owner < D3D12ResourceSnapshot.OwnerBalances.Length; owner++)
        {
            ref var balance = ref _state.Balances[owner];
            balance = balance with { Live = 0, LiveBytes = 0, Retained = 0, RetainedBytes = 0 };
        }
        foreach (ref readonly var entry in _entries.AsSpan())
        {
            if (entry.Pointer == 0)
                continue;
            ref var balance = ref _state.Balances[(int)entry.Owner];
            // Unsignaled work remains charged even if the native fence reports the sentinel.
            balance = entry.Pending || entry.LastUse > _state.CompletedFence
                ? balance with { Retained = balance.Retained + 1, RetainedBytes = AddSaturating(balance.RetainedBytes, entry.Bytes) }
                : balance with { Live = balance.Live + 1, LiveBytes = AddSaturating(balance.LiveBytes, entry.Bytes) };
        }
        Interlocked.Increment(ref _revision);
        _published = _state;
        Interlocked.Increment(ref _revision);
    }

    private int IndexOf(nint pointer)
    {
        if (pointer != 0)
            for (var i = 0; i < _entries.Length; i++)
                if (_entries[i].Pointer == pointer)
                    return i;
        return -1;
    }

    private ulong AddSaturating(ulong a, ulong b)
    {
        if (b <= ulong.MaxValue - a)
            return a + b;
        _state.LedgerOverflowed = true;
        return ulong.MaxValue;
    }

    private record struct Entry(nint Pointer, D3D12ResourceOwner Owner, ulong Bytes, ulong LastUse, bool Pending);
}
