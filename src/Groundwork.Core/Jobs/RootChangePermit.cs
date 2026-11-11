namespace Groundwork.Core;

public enum RootChangeStatus { Pending, Committed, Revoked }

/// <summary>A revocable root change bound to one sealed CommitResults barrier.</summary>
public sealed class RootChangePermit
{
    internal RootChangePermit(WorldPublicationAuthority owner, CandidateSet candidates, IWorldStateRoot nextRoot)
    {
        Owner = owner;
        Candidates = candidates;
        NextRoot = nextRoot;
    }

    internal WorldPublicationAuthority Owner { get; }
    internal CandidateSet Candidates { get; }
    internal IWorldStateRoot NextRoot { get; }
    internal RootChangeStatus State { get; set; }
    internal BarrierStamp? FinalizedStamp { get; set; }
    public BarrierStamp? CommittedStamp => Owner.ReadRootChangeStamp(this);
    public RootChangeStatus Status => Owner.ReadRootChange(this);
    public void Revoke() => Owner.RevokeRootChange(this);
}

public interface IRootChangeAuthority
{
    RootChangePermit? TryReserve(CandidateSet candidates, IWorldStateRoot nextRoot);
}

internal sealed partial class WorldPublicationAuthority
{
    private RootChangePermit? _rootChange;
    internal IRootChangeAuthority RootChanges => new RootChangePort(this);

    internal RootChangePermit? TryReserveRootChange(CandidateSet candidates, IWorldStateRoot nextRoot)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(nextRoot);
        var world = nextRoot.World;
        lock (_control.Gate)
        {
            if (_control.IsTerminal || _rootChange is not null || _prepared is not null ||
                !ReferenceEquals(_candidates, candidates) || candidates.Request.Kind != BarrierKind.CommitResults ||
                world != _stamp.World || ReferenceEquals(nextRoot, CurrentRoot))
                return null;
            return _rootChange = new RootChangePermit(this, candidates, nextRoot);
        }
    }

    internal BarrierStamp? ReadRootChangeStamp(RootChangePermit permit)
    {
        lock (_control.Gate)
            return permit.FinalizedStamp;
    }

    internal RootChangeStatus ReadRootChange(RootChangePermit permit)
    {
        lock (_control.Gate)
            return permit.State;
    }

    internal void RevokeRootChange(RootChangePermit permit)
    {
        lock (_control.Gate)
            if (permit.State == RootChangeStatus.Pending)
            {
                permit.State = RootChangeStatus.Revoked;
                // A failed preparation has no published candidate to fence. Return its slot
                // so the sealed barrier can retry or publish unchanged. Prepared attempts
                // retain their revocation fence until Core settles that exact attempt.
                if (ReferenceEquals(_rootChange, permit) && _prepared is null)
                    _rootChange = null;
            }
    }

    private bool ValidRootChange(BarrierPublicationDraft draft) =>
        draft.RootChange is { } permit && ReferenceEquals(permit, _rootChange) &&
        permit.State == RootChangeStatus.Pending && ReferenceEquals(permit.NextRoot, draft.NextRoot) &&
        ReferenceEquals(permit.Candidates, draft.Candidates);

    private sealed class RootChangePort(WorldPublicationAuthority owner) : IRootChangeAuthority
    {
        public RootChangePermit? TryReserve(CandidateSet candidates, IWorldStateRoot nextRoot) =>
            owner.TryReserveRootChange(candidates, nextRoot);
    }
}
