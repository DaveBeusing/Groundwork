using System.Collections.Immutable;
using Groundwork.Core;

namespace Groundwork.Voxels;

/// <summary>Unforgeable, one-tick movement proposal. Only Core finalization changes the player root.</summary>
public sealed class VolumeMovementDraft
{
    internal VolumeMovementDraft(VolumeWorldRoot root, BarrierStamp stamp, PlanetWalkResult result)
    { Root = root; Stamp = stamp; Result = result; }
    internal VolumeWorldRoot Root { get; }
    internal BarrierStamp Stamp { get; }
    public PlanetWalkResult Result { get; }
    public VolumePublicationInfo Terrain => Root.Terrain;
}

public sealed partial class VolumeStreamingCoordinator
{
    /// <summary>
    /// Plans one 60 Hz step at a finalized BeginTick. A held/failed query never accrues input.
    /// The first call may establish a non-penetrating spawn; later calls must use the committed pose.
    /// </summary>
    public VolumeMovementDraft? TryPlanMovement(PlanetWalkState state, double forward, double right,
        out VolumeQueryStatus status, VolumeQueryLimits? limits = null)
    {
        lock (_gate)
        {
            Refresh();
            status = VolumeQueryStatus.CoverageUnavailable;
            var committed = _world.Publication.Committed.Read();
            if (_world.State.Read().Conditions != SafetyCondition.None ||
                _world.State.Read().PendingRequiredEvidenceRecords != 0 || _stopped || !_movementOpen || _active is not { } active ||
                committed.Root is not VolumeWorldRoot root || root.Owner != this || root.Generation != active ||
                active.View != _view || committed.Stamp.Kind != BarrierKind.BeginTick)
                return null;
            if (root.Player is { } player && player != state) { status = VolumeQueryStatus.Invalid; return null; }
            var result = VolumeWalker.Step(active, state, forward, right, limits ?? VolumeQueryLimits.Default, out status);
            return status is VolumeQueryStatus.Clear or VolumeQueryStatus.Contact
                ? new VolumeMovementDraft(root, committed.Stamp, result) : null;
        }
    }

    /// <summary>
    /// Revalidates the complete terrain/view/pose base and tick at CommitResults. The host supplies
    /// any ordinary gameplay state/dispositions here, so pose and gameplay publish in the same root.
    /// Stop or a new view revokes the permit through the existing publication authority.
    /// </summary>
    public PreparedBarrierPublication? TryPrepareMovement(VolumeMovementDraft draft, CandidateSet candidates,
        ImmutableArray<BatchDisposition> dispositions, IWorldStateRoot? composedState = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_gate)
        {
            Refresh();
            var current = _world.Publication.Committed.Read();
            if (_world.State.Read().Conditions != SafetyCondition.None ||
                _world.State.Read().PendingRequiredEvidenceRecords != 0 || _stopped || !_movementOpen || _prepared is not null ||
                !ReferenceEquals(current.Root, draft.Root) || draft.Root.Owner != this ||
                draft.Root.Generation != _active || draft.Terrain.View != _view ||
                candidates.Request.Kind != BarrierKind.CommitResults || candidates.Request.Tick != draft.Stamp.Tick ||
                candidates.Request.Entry != draft.Stamp)
                return null;
            var state = composedState ?? draft.Root.State;
            if (state.World != draft.Root.World) return null;
            var root = new VolumeWorldRoot(state, this, draft.Root.Generation, draft.Result.State);
            var permit = _world.Publication.RootChanges.TryReserve(candidates, root);
            if (permit is null) return null;
            var result = _world.Publication.Preparation.TryPrepare(new BarrierPublicationDraft(
                candidates, root, _world.State.Read().PendingFaultGeneration, dispositions, permit));
            if (result.Publication is not { } prepared) { permit.Revoke(); return null; }
            _movementPermit?.Revoke();
            _movementPermit = permit;
            return prepared;
        }
    }
    private RootChangePermit? _movementPermit;
}

internal static class VolumeWalker
{
    private const double Radius = PlanetSurfaceWalker.BodyRadius;
    private const double Height = PlanetSurfaceWalker.BodyHeight;
    private const double Dt = PlanetSurfaceWalker.StepSeconds;
    private const double Walkable = 0.7071067811865476;
    private static CollisionVector Up(PlanetVector position)
    {
        var p = new CollisionVector(position); var length = p.Length;
        return p / length;
    }
    private static VolumeCapsule Body(PlanetWalkState state)
    {
        var up = Up(state.Position); var foot = new CollisionVector(state.Position);
        return new((foot + up * Radius).Planet, (foot + up * (Height - Radius)).Planet, Radius);
    }
    internal static VolumeSweepResult Validate(VolumeGeneration g, PlanetWalkState state, VolumeQueryLimits limits)
    {
        if (!Valid(state)) return new(VolumeQueryStatus.Invalid, 0, default, g.Info, 0);
        return VolumeCollision.Sweep(g, Body(state), default, limits);
    }
    private static bool Valid(PlanetWalkState state) => new CollisionVector(state.Position).Finite &&
        new CollisionVector(state.Velocity).Finite && new CollisionVector(state.Heading).Finite &&
        Up(state.Position).Finite && new CollisionVector(state.Heading).Length > 0 &&
        double.IsFinite(new CollisionVector(state.Velocity).Length);

    internal static PlanetWalkResult Step(VolumeGeneration g, PlanetWalkState state, double forward, double right,
        VolumeQueryLimits limits, out VolumeQueryStatus status)
    {
        var held = new PlanetWalkResult(state, PlanetWalkStatus.Held, false);
        status = VolumeQueryStatus.Invalid;
        if (!limits.Valid || !Valid(state) || !double.IsFinite(forward) || !double.IsFinite(right) ||
            Math.Abs(forward) > 1 || Math.Abs(right) > 1) return held;
        var up = Up(state.Position); var heading = new CollisionVector(state.Heading);
        heading -= up * CollisionVector.Dot(heading, up);
        if (heading.Length < 1e-9) return held;
        heading /= heading.Length;
        var remaining = limits.TriangleTests;
        var valid = Validate(g, state, limits);
        status = valid.Status;
        if (status != VolumeQueryStatus.Clear) return held;
        remaining -= valid.TriangleTests;
        VolumeSweepResult Query(PlanetWalkState pose, PlanetVector delta)
        {
            if (remaining <= 0) return new(VolumeQueryStatus.BudgetExceeded, 0, default, g.Info, 0);
            var target = pose with { Position = (new CollisionVector(pose.Position) + new CollisionVector(delta)).Planet };
            if (!Valid(target) || CollisionVector.Dot(Up(pose.Position), Up(target.Position)) <= 0)
                return new(VolumeQueryStatus.Invalid, 0, default, g.Info, 0);
            var targetBody = Body(target);
            var result = VolumeCollision.Sweep(g, Body(pose), delta, new VolumeQueryLimits(remaining, limits.Iterations),
                targetBody.A, targetBody.B);
            remaining -= result.TriangleTests;
            return result;
        }
        var support = Query(state, (up * -0.02).Planet);
        status = support.Status;
        if (status is not (VolumeQueryStatus.Clear or VolumeQueryStatus.Contact)) return held;
        var grounded = status == VolumeQueryStatus.Contact && CollisionVector.Dot(new CollisionVector(support.Normal), up) >= Walkable;
        var input = Math.Sqrt(forward * forward + right * right);
        var tangent = (heading * forward + CollisionVector.Cross(heading, up) * right) * (PlanetSurfaceWalker.WalkSpeed / Math.Max(1, input));
        if (grounded)
        {
            var normal = new CollisionVector(support.Normal);
            tangent -= normal * CollisionVector.Dot(tangent, normal);
        }
        var travel = tangent * Dt;
        if (grounded)
        {
            var normal = new CollisionVector(support.Normal);
            var destinationUp = Up((new CollisionVector(state.Position) + travel).Planet);
            // Transport the lower cap along its current support plane while radial up changes.
            // This correction is included in the finite sweep, never applied as an unchecked snap.
            var capShift = CollisionVector.Dot((destinationUp - up) * Radius, normal);
            travel += up * Math.Max(0, -capShift / CollisionVector.Dot(up, normal));
        }
        var horizontal = Query(state, travel.Planet);
        status = horizontal.Status;
        if (status is not (VolumeQueryStatus.Clear or VolumeQueryStatus.Contact)) return held;
        // Walls stop this action. No accumulated displacement, hidden snap or penetration recovery.
        if (status == VolumeQueryStatus.Contact) return new(state, PlanetWalkStatus.Held, grounded);
        var next = state with { Position = (new CollisionVector(state.Position) + travel).Planet };
        // Ground support must be checked at the destination as well: a ledge begins falling.
        support = Query(next, (up * -0.02).Planet);
        status = support.Status;
        if (status is not (VolumeQueryStatus.Clear or VolumeQueryStatus.Contact)) return held;
        grounded = status == VolumeQueryStatus.Contact && CollisionVector.Dot(new CollisionVector(support.Normal), up) >= Walkable;
        var radial = CollisionVector.Dot(new CollisionVector(state.Velocity), up);
        var vertical = grounded && radial <= 0 ? 0 : radial - PlanetSurfaceWalker.GravityStrength * Dt;
        var fall = Query(next, (up * (vertical * Dt)).Planet);
        status = fall.Status;
        if (status is not (VolumeQueryStatus.Clear or VolumeQueryStatus.Contact)) return held;
        var fraction = fall.Status == VolumeQueryStatus.Contact ? fall.Fraction : 1;
        next = next with { Position = (new CollisionVector(next.Position) + up * (vertical * Dt * fraction)).Planet };
        if (fall.Status == VolumeQueryStatus.Contact)
        {
            grounded = vertical < 0 && CollisionVector.Dot(new CollisionVector(fall.Normal), up) >= Walkable;
            vertical = 0;
        }
        var nextUp = Up(next.Position);
        heading -= nextUp * CollisionVector.Dot(heading, nextUp);
        if (heading.Length < 1e-9) { status = VolumeQueryStatus.Invalid; return held; }
        next = next with { Heading = (heading / heading.Length).Planet, Velocity = (tangent + up * vertical).Planet };
        status = VolumeQueryStatus.Clear;
        return new(next, next.Position == state.Position ? PlanetWalkStatus.Held : PlanetWalkStatus.Moved, grounded);
    }
}
