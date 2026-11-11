using System.Collections.Immutable;
using Groundwork.Core;

namespace Groundwork.Ecs;

internal sealed class EcsScopePolicy : IJobScopePolicy
{
    public JobScopeAdmission ValidateAdmission(JobSubmission submission, in CommittedWorldView world)
    {
        if (world.Root is not EcsRoot root || submission.Source.World != root.World)
            return new(JobScopeStatus.InvalidAccess, null);

        var partitions = ImmutableDictionary.CreateBuilder<PartitionId, EcsPartition>();
        foreach (var key in submission.ReadScopes.Concat(submission.CommandScopes))
        {
            if (key.World != root.World || key.Component.Value != 0 && !root.Registry.TryGetSize(key.Component, out _))
                return new(JobScopeStatus.InvalidAccess, null);

            switch (key.Scope.Kind)
            {
                case ScopeKind.World when key.Scope.Partition is null && key.Scope.Entity is null:
                    break;
                case ScopeKind.Entity when key.Scope.Partition is null && key.Scope.Entity is { } entity &&
                    root.Find(entity, out _) == ComponentReadStatus.Present:
                    break;
                case ScopeKind.Partition when key.Scope.Entity is null && key.Scope.Partition is { } requested &&
                    root.Partitions.TryGetValue(requested.Partition, out var partition) && partition.Revision == requested.Revision:
                    partitions[requested.Partition] = partition;
                    break;
                default:
                    return new(JobScopeStatus.InvalidAccess, null);
            }
        }

        var scope = new EcsScope(root.World, Normalize(submission.ReadScopes), Normalize(submission.CommandScopes), partitions.ToImmutable());
        foreach (var target in submission.SnapshotTargets)
        {
            if (!scope.AllowsRead(root, target) || target.Component.Value != 0 && !root.Registry.TryGetSize(target.Component, out _))
                return new(JobScopeStatus.InvalidAccess, null);
        }
        return new(JobScopeStatus.Authorized, scope);
    }

    public bool CanDispatch(IAdmittedJobScope scope, in CommittedWorldView world)
    {
        if (scope is not EcsScope own || world.Root is not EcsRoot root || own.World != root.World)
            return false;
        foreach (var key in own.ReadScopes.Concat(own.CommandScopes))
        {
            if (key.Scope.Entity is { } entity && root.Find(entity, out _) != ComponentReadStatus.Present ||
                key.Scope.Partition is { } partition && !root.Partitions.ContainsKey(partition.Partition))
                return false;
        }
        return true;
    }

    public JobScopeOverlap EvaluateOverlap(IAdmittedJobScope first, IAdmittedJobScope second, in CommittedWorldView world)
    {
        if (first is not EcsScope left || second is not EcsScope right || world.Root is not EcsRoot root ||
            left.World != root.World || right.World != root.World)
            return JobScopeOverlap.Unknown;
        return Intersects(root, left, left.CommandScopes, right, right.ReadScopes) ||
            Intersects(root, left, left.CommandScopes, right, right.CommandScopes) ||
            Intersects(root, left, left.ReadScopes, right, right.CommandScopes)
            ? JobScopeOverlap.Excluded : JobScopeOverlap.Independent;
    }

    private static bool Intersects(EcsRoot root, EcsScope left, ImmutableArray<ResourceKey> leftKeys,
        EcsScope right, ImmutableArray<ResourceKey> rightKeys)
    {
        foreach (var a in leftKeys)
        foreach (var b in rightKeys)
        {
            if (a.Component.Value != 0 && b.Component.Value != 0 && a.Component != b.Component)
                continue;
            if (a.Scope.Kind == ScopeKind.World || b.Scope.Kind == ScopeKind.World)
                return true;
            if (a.Scope.Entity is { } entity && right.InScope(root, b.Scope, entity) ||
                b.Scope.Entity is { } other && left.InScope(root, a.Scope, other))
                return true;
            if (a.Scope.Partition is { } x && b.Scope.Partition is { } y && x.Partition == y.Partition &&
                (a.Component.Value == 0 || b.Component.Value == 0 || root.Partitions[x.Partition].Members.Any(
                    member => left.InScope(root, a.Scope, member) && right.InScope(root, b.Scope, member))))
                return true;
        }
        return false;
    }

    private static ImmutableArray<ResourceKey> Normalize(ImmutableArray<ResourceKey> keys) =>
        [.. keys.Distinct().OrderBy(key => key.Component.Value).ThenBy(key => key.Scope.Kind)
            .ThenBy(key => key.Scope.Partition?.Partition.Value).ThenBy(key => key.Scope.Partition?.Revision)
            .ThenBy(key => key.Scope.Entity?.Slot).ThenBy(key => key.Scope.Entity?.Generation)];
}

internal sealed record EcsScope(WorldId World, ImmutableArray<ResourceKey> ReadScopes,
    ImmutableArray<ResourceKey> CommandScopes, ImmutableDictionary<PartitionId, EcsPartition> Partitions) : IAdmittedJobScope
{
    public bool MayContain(PartitionScope partition, EntityId entity) =>
        !Partitions.TryGetValue(partition.Partition, out var admitted) ||
        admitted.Revision != partition.Revision || admitted.Members.Contains(entity);

    internal bool AllowsRead(EcsRoot root, ComponentTarget target) => Allows(root, ReadScopes, target);
    internal bool AllowsWrite(EcsRoot root, ComponentTarget target) => Allows(root, CommandScopes, target);

    internal bool AllowsCreate(PartitionScope? destination) => CommandScopes.Any(key => key.Component.Value == 0 &&
        (key.Scope.Kind == ScopeKind.World || destination.HasValue && key.Scope.Partition == destination));

    internal bool AllowsProvisional(ComponentTypeId component, PartitionScope? destination) =>
        CommandScopes.Any(key => (key.Component.Value == 0 || key.Component == component) &&
            (key.Scope.Kind == ScopeKind.World || destination.HasValue && key.Scope.Partition == destination));

    private bool Allows(EcsRoot root, ImmutableArray<ResourceKey> keys, ComponentTarget target) =>
        root.World == World && root.Find(target.Entity, out _) == ComponentReadStatus.Present &&
        keys.Any(key => (key.Component.Value == 0 || key.Component == target.Component) && InScope(root, key.Scope, target.Entity));

    internal bool InScope(EcsRoot root, ResourceScope scope, EntityId entity) => scope.Kind switch
    {
        ScopeKind.World => true,
        ScopeKind.Entity => scope.Entity == entity,
        ScopeKind.Partition when scope.Partition is { } partition =>
            Partitions[partition.Partition].Members.Contains(entity) && root.Partitions[partition.Partition].Members.Contains(entity),
        _ => false,
    };
}
