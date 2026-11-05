using System.Collections.Immutable;
using Groundwork.Core;

namespace Groundwork.Ecs;

internal sealed class EcsInputSource(WorldSafetyPorts safety) : IJobInputSource
{
    public JobInputCapture TryCapture(in JobInputRequest request)
    {
        if (request.Permit is not { Scope: EcsScope scope } permit)
            return new(JobInputCaptureStatus.InvalidPermit, null);
        var acquisition = safety.Publication.Capture.TryAcquire(permit);
        if (acquisition.Lease is not { } pin)
            return new(acquisition.Status switch
            {
                JobCapturePermitStatus.CapacityExceeded => JobInputCaptureStatus.CapacityExceeded,
                JobCapturePermitStatus.Expired => JobInputCaptureStatus.Closed,
                _ => JobInputCaptureStatus.InvalidPermit,
            }, null);

        var view = pin.World;
        if (view.Root is not EcsRoot root || view.Stamp != permit.DispatchStamp || root.World != scope.World)
        {
            pin.Dispose();
            return new(JobInputCaptureStatus.InvalidPermit, null);
        }

        if (permit.InputKind == JobInputKind.Borrowed)
            return new(JobInputCaptureStatus.Captured, new BorrowedInput(safety, pin, permit, scope, root.Registry));

        using (pin)
        {
            if (permit.InputKind != JobInputKind.OwnedSnapshot)
                return new(JobInputCaptureStatus.InvalidPermit, null);
            return CaptureSnapshot(root, permit, scope);
        }
    }

    private JobInputCapture CaptureSnapshot(EcsRoot root, JobDispatchPermit permit, EcsScope scope)
    {
        const long headerCharge = 128;
        const long targetCharge = 192;
        long remaining = permit.Budget.MaxSnapshotBytes - headerCharge;
        if (remaining < 0)
            return new(JobInputCaptureStatus.CapacityExceeded, null);

        foreach (var target in permit.SnapshotTargets)
        {
            if (!scope.AllowsRead(root, target) || !TryLookup(root, target, out var cell))
                return new(JobInputCaptureStatus.InvalidAccess, null);
            long charge = targetCharge + (cell.Present && target.Component.Value != 0 ? cell.Bytes.Length : 0);
            if (charge > remaining)
                return new(JobInputCaptureStatus.CapacityExceeded, null);
            remaining -= charge;
        }

        var copied = ImmutableDictionary.CreateBuilder<ComponentTarget, CapturedCell>();
        try
        {
            foreach (var target in permit.SnapshotTargets)
            {
                if (copied.ContainsKey(target))
                    continue;
                _ = TryLookup(root, target, out var cell);
                bool present = target.Component.Value == 0 || cell.Present;
                var stamp = new TargetVersionStamp(permit, target, present, cell.Mutation);
                var version = safety.Publication.VersionIssuer.Issue(stamp);
                var bytes = cell.Present ? ImmutableArray.Create(cell.Bytes.AsSpan()) : default;
                copied.Add(target, new CapturedCell(present ? ComponentReadStatus.Present : ComponentReadStatus.Missing, bytes, version));
            }
        }
        catch (InvalidOperationException) when (safety.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop))
        {
            return new(JobInputCaptureStatus.Closed, null);
        }

        return new(JobInputCaptureStatus.Captured, new SnapshotInput(permit.DispatchStamp, root.Registry, copied.ToImmutable()));
    }

    private static bool TryLookup(EcsRoot root, ComponentTarget target, out ComponentCell cell)
    {
        cell = default;
        _ = root.Find(target.Entity, out var slot);
        if (slot is null)
            return false;
        if (target.Component.Value == 0)
        {
            cell = new ComponentCell(slot.Mutation, default);
            return true;
        }
        if (!root.Registry.TryGetSize(target.Component, out _))
            return false;
        cell = slot.Cells.GetValueOrDefault(target.Component);
        return true;
    }

    private readonly record struct CapturedCell(ComponentReadStatus Status, ImmutableArray<byte> Bytes, TargetVersion? Version);

    private abstract class JobInput(BarrierStamp stamp, ComponentRegistry registry) : IJobInput
    {
        protected object Gate { get; } = new();
        public BarrierStamp CapturedAt { get; } = stamp;

        public ComponentRead<T> Read<T>(EntityId entity) where T : unmanaged
        {
            lock (Gate)
            {
                if (!registry.TryGetId<T>(out var id))
                    return new(ComponentReadStatus.UndeclaredAccess, default, null);
                var result = ReadCell(new ComponentTarget(entity, id.Value));
                return new(result.Status, result.Status == ComponentReadStatus.Present
                    ? ComponentRegistry.Decode<T>(result.Bytes) : default, result.Version);
            }
        }

        public EntityRead ReadEntity(EntityId entity)
        {
            lock (Gate)
            {
                var result = ReadCell(new ComponentTarget(entity, default));
                return new(result.Status, result.Version);
            }
        }

        protected abstract CapturedCell ReadCell(ComponentTarget target);
        public abstract void Dispose();
    }

    private sealed class SnapshotInput(BarrierStamp stamp, ComponentRegistry registry,
        ImmutableDictionary<ComponentTarget, CapturedCell> cells) : JobInput(stamp, registry)
    {
        private ImmutableDictionary<ComponentTarget, CapturedCell>? _cells = cells;

        protected override CapturedCell ReadCell(ComponentTarget target)
        {
            var snapshot = _cells;
            if (snapshot is null)
                return new(ComponentReadStatus.ExpiredLease, default, null);
            if (target.Entity.World.Value == 0 || target.Entity.Generation == 0)
                return new(ComponentReadStatus.InvalidId, default, null);
            if (target.Entity.World != CapturedAt.World)
                return new(ComponentReadStatus.ForeignWorld, default, null);
            return snapshot.GetValueOrDefault(target, new(ComponentReadStatus.UndeclaredAccess, default, null));
        }

        public override void Dispose()
        {
            lock (Gate)
                _cells = null;
        }
    }

    private sealed class BorrowedInput(WorldSafetyPorts safety, IJobCaptureLease pin, JobDispatchPermit permit,
        EcsScope scope, ComponentRegistry registry) : JobInput(permit.DispatchStamp, registry)
    {
        private IJobCaptureLease? _pin = pin;

        protected override CapturedCell ReadCell(ComponentTarget target)
        {
            if (_pin is null)
                return new(ComponentReadStatus.ExpiredLease, default, null);
            var root = (EcsRoot)_pin.World.Root;
            var identity = root.Find(target.Entity, out _);
            if (identity != ComponentReadStatus.Present)
                return new(identity, default, null);
            if (!scope.AllowsRead(root, target) || !TryLookup(root, target, out var cell))
                return new(ComponentReadStatus.UndeclaredAccess, default, null);

            bool present = target.Component.Value == 0 || cell.Present;
            try
            {
                var version = safety.Publication.VersionIssuer.Issue(new TargetVersionStamp(permit, target, present, cell.Mutation));
                return new(present ? ComponentReadStatus.Present : ComponentReadStatus.Missing, cell.Bytes, version);
            }
            catch (InvalidOperationException) when (safety.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop))
            {
                return new(ComponentReadStatus.DisposedWorld, default, null);
            }
        }

        public override void Dispose()
        {
            lock (Gate)
            {
                _pin?.Dispose();
                _pin = null;
            }
        }
    }
}
