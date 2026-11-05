using System.Collections.Immutable;
using Groundwork.Core;

namespace Groundwork.Ecs;

/// <summary>The host-side ECS capability. Hand workers only their dispatched input and command writer.</summary>
public sealed class EcsWorld
{
    private readonly object _operationGate = new();
    private readonly EcsStorageBudget.Reservation _reservation;
    private EcsBatchPlan? _activePlan;
    private BarrierRequest? _lastStaged;

    private EcsWorld(WorldSafetyPorts safety, WorldId id, EcsStorageBudget.Reservation reservation)
    {
        _reservation = reservation;
        Safety = safety;
        Id = id;
        Inputs = new EcsInputSource(safety);
    }

    public WorldSafetyPorts Safety { get; }
    public WorldId Id { get; }
    public IJobInputSource Inputs { get; }

    public static EcsWorld Open(RuntimeSafetyAuthority runtime, ResourceQuota quota, IBarrierSupervisorPolicy supervisor,
        ComponentRegistry registry, EcsLimits limits, EcsStorageBudget budget, IEnumerable<PartitionId>? partitions = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxEntitySlots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxComponentTypes);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxPartitions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxWorldBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxRetainedWorldBytes);
        if (registry.Count > limits.MaxComponentTypes)
            throw new ArgumentException("The component catalog exceeds the world limit.", nameof(limits));
        // A job can retain admitted partition history, and each observer lease can retain two roots.
        // Leave room for the current pair, committed state, capture/preflight and replacement roots.
        long retainedRoots = quota.Jobs.Jobs + 2L * quota.Jobs.RetentionHandles + 8;
        if ((Int128)retainedRoots * limits.MaxWorldBytes > limits.MaxRetainedWorldBytes)
            throw new ArgumentException("The retained ECS budget cannot cover the configured job and observer limits.", nameof(limits));
        var reservation = budget.TryReserve(limits.MaxRetainedWorldBytes) ??
            throw new InvalidOperationException("The aggregate ECS storage budget is exhausted.");
        WorldId id = default;
        bool adopted = false;
        try
        {
            if (!runtime.TryAllocateWorldId(out id))
                throw new InvalidOperationException("No world slot or identity is available.");
            var root = EcsRoot.Create(id, registry, limits, partitions);
            var safety = runtime.OpenWorld(id, quota, supervisor, root, new EcsScopePolicy());
            adopted = true;
            return new EcsWorld(safety, id, reservation);
        }
        catch
        {
            if (!adopted)
            {
                if (id.Value != 0)
                    runtime.TryAbandonWorldId(id);
                reservation.Release();
            }
            throw;
        }
    }

    /// <summary>Acknowledges storage reclamation after Core reports the world released.</summary>
    public bool TryReleaseStorage()
    {
        try
        {
            _ = Safety.Publication.Committed.Read().Stamp;
            return false;
        }
        catch (ObjectDisposedException)
        {
            _reservation.Release();
            return true;
        }
    }

    public ComponentRead<T> Read<T>(EntityId entity) where T : unmanaged
    {
        try
        {
            return WithPinnedRoot(view => ((EcsRoot)view.Root).Read<T>(entity));
        }
        catch (ObjectDisposedException)
        {
            return new(ComponentReadStatus.DisposedWorld, default, null);
        }
    }

    public ImmutableArray<EcsComponentRow<T>> Query<T>() where T : unmanaged =>
        WithPinnedRoot<ImmutableArray<EcsComponentRow<T>>>(view => [.. ((EcsRoot)view.Root).Enumerate<T>()]);

    public PartitionScope? FindPartition(PartitionId id) => WithPinnedRoot<PartitionScope?>(view =>
        ((EcsRoot)view.Root).Partitions.TryGetValue(id, out var partition) ? new PartitionScope(id, partition.Revision) : null);

    public ObserverPublication PublishObserver(BarrierStamp stamp, ulong expectedSafetyGeneration)
    {
        try
        {
            return WithPinnedRoot(view =>
            {
                if (stamp != view.Stamp || stamp.Kind != BarrierKind.CommitResults)
                    return ObserverPublication.Blocked;
                return Safety.Publication.Observer.TryPublish(new ObserverPublicationDraft(stamp, expectedSafetyGeneration,
                    new EcsObserverState((EcsRoot)view.Root)));
            });
        }
        catch (ObjectDisposedException)
        {
            return ObserverPublication.Blocked;
        }
    }

    public EcsBatchPlan Stage(CandidateSet candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_operationGate)
        {
            if (_activePlan is { IsSettled: false } || _lastStaged == candidates.Request)
                throw new InvalidOperationException("A sealed barrier can only be staged once.");

            var plan = new EcsBatchPlan(Safety.Publication, candidates);
            var lease = Track(plan);
            try
            {
                var view = Safety.Publication.Committed.Read();
                if (candidates.Request.Entry != view.Stamp)
                    throw new ArgumentException("The sealed candidates must belong to the current world and barrier entry.", nameof(candidates));
                plan.Track(lease, (EcsRoot)view.Root);
                _activePlan = plan;
                _lastStaged = candidates.Request;
                plan.Stage();
                return plan;
            }
            catch
            {
                if (plan.IsSettled)
                    lease.Dispose();
                else
                    plan.Abort(BarrierAbortReason.PreparationFailed);
                throw;
            }
        }
    }

    private T WithPinnedRoot<T>(Func<CommittedWorldView, T> read)
    {
        lock (_operationGate)
        {
            var owner = new HostRead();
            using var lease = Track(owner);
            var view = Safety.Publication.Committed.Read();
            owner.Root = view.Root;
            try
            {
                return read(view);
            }
            finally
            {
                owner.Root = null;
            }
        }
    }

    private IRuntimeWorkLease Track(object owner)
    {
        if (Safety.Ownership.TryTrack(owner) is { } lease)
            return lease;
        ObjectDisposedException.ThrowIf(Safety.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop), this);
        throw new InvalidOperationException("No Core operation capacity is available for the ECS root owner.");
    }

    private sealed class HostRead
    {
        internal IWorldStateRoot? Root { get; set; }
    }
}

public sealed class EcsObserverState : IWorldObserverState
{
    private readonly EcsRoot _root;

    internal EcsObserverState(EcsRoot root) => _root = root;
    public WorldId World => _root.World;
    public ComponentRead<T> Read<T>(EntityId entity) where T : unmanaged => _root.Read<T>(entity);
    public ImmutableArray<EcsComponentRow<T>> Query<T>() where T : unmanaged => [.. _root.Enumerate<T>()];
}
