using System.Collections.Immutable;
using Groundwork.Core;

namespace Groundwork.Ecs;

/// <summary>The host-side ECS capability. Hand workers only their dispatched input and command writer.</summary>
public sealed class EcsWorld
{
    private EcsWorld(WorldSafetyPorts safety, WorldId id)
    {
        Safety = safety;
        Id = id;
        Inputs = new EcsInputSource(safety);
    }

    public WorldSafetyPorts Safety { get; }
    public WorldId Id { get; }
    public IJobInputSource Inputs { get; }

    public static EcsWorld Open(RuntimeSafetyAuthority runtime, ResourceQuota quota, IBarrierSupervisorPolicy supervisor,
        ComponentRegistry registry, EcsLimits limits, IEnumerable<PartitionId>? partitions = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(limits);
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
        if (!runtime.TryAllocateWorldId(out var id))
            throw new InvalidOperationException("No world slot or identity is available.");

        try
        {
            var root = EcsRoot.Create(id, registry, limits, partitions);
            return new EcsWorld(runtime.OpenWorld(id, quota, supervisor, root, new EcsScopePolicy()), id);
        }
        catch
        {
            runtime.TryAbandonWorldId(id);
            throw;
        }
    }

    public ComponentRead<T> Read<T>(EntityId entity) where T : unmanaged
    {
        try
        {
            return ReadRoot().Read<T>(entity);
        }
        catch (ObjectDisposedException)
        {
            return new(ComponentReadStatus.DisposedWorld, default, null);
        }
    }

    public ImmutableArray<EcsComponentRow<T>> Query<T>() where T : unmanaged => [.. ReadRoot().Enumerate<T>()];

    public PartitionScope? FindPartition(PartitionId id) => ReadRoot().Partitions.TryGetValue(id, out var partition)
        ? new PartitionScope(id, partition.Revision) : null;

    public EcsObserverState CaptureObserver(BarrierStamp stamp)
    {
        var view = Safety.Publication.Committed.Read();
        if (stamp != view.Stamp || stamp.Kind != BarrierKind.CommitResults)
            throw new ArgumentException("The observer must capture the current completed CommitResults barrier.", nameof(stamp));
        return new EcsObserverState((EcsRoot)view.Root);
    }

    public EcsBatchPlan Stage(CandidateSet candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var view = Safety.Publication.Committed.Read();
        if (candidates.Request.Entry != view.Stamp)
            throw new ArgumentException("The sealed candidates must belong to the current world and barrier entry.", nameof(candidates));
        return EcsBatchWriter.Stage((EcsRoot)view.Root, candidates, Safety.Publication.VersionIssuer);
    }

    private EcsRoot ReadRoot() => (EcsRoot)Safety.Publication.Committed.Read().Root;
}

public sealed class EcsObserverState : IWorldObserverState
{
    private readonly EcsRoot _root;

    internal EcsObserverState(EcsRoot root) => _root = root;
    public WorldId World => _root.World;
    public ComponentRead<T> Read<T>(EntityId entity) where T : unmanaged => _root.Read<T>(entity);
    public ImmutableArray<EcsComponentRow<T>> Query<T>() where T : unmanaged => [.. _root.Enumerate<T>()];
}
