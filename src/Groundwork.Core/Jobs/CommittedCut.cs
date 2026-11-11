namespace Groundwork.Core;

/// <summary>A single preallocated reference to a world's finalized CommitResults cut.</summary>
public sealed class CommittedCut
{
    private readonly WorldPublicationAuthority _owner;

    internal CommittedCut(WorldPublicationAuthority owner, ulong tick) => (_owner, Tick) = (owner, tick);

    internal ulong Tick { get; }
    internal CommittedWorldView? Publication { get; set; }

    public CommittedWorldView? Read() => _owner.ReadCut(this);

    public void Release() => _owner.ReleaseCut(this);
}

internal sealed partial class WorldPublicationAuthority
{
    private CommittedCut? _committedCut;

    internal CommittedCut? ReserveCut(ulong tick)
    {
        lock (_control.Gate)
        {
            if (_control.IsTerminal || _committedCut is not null || _candidates is not null ||
                _stamp.Kind != BarrierKind.BeginTick || _stamp.Tick != tick)
                return null;
            return _committedCut = new CommittedCut(this, tick);
        }
    }

    internal CommittedWorldView? ReadCut(CommittedCut cut)
    {
        lock (_control.Gate)
            return _committedCut == cut ? cut.Publication : null;
    }

    internal void ReleaseCut(CommittedCut cut)
    {
        lock (_control.Gate)
        {
            if (_committedCut != cut)
                return;
            _committedCut = null;
            cut.Publication = null;
        }
    }

    private void BindCut(BarrierStamp stamp)
    {
        if (stamp.Kind == BarrierKind.CommitResults && _committedCut is { } cut && cut.Tick == stamp.Tick)
            cut.Publication = new CommittedWorldView(stamp, CurrentRoot);
    }
}
