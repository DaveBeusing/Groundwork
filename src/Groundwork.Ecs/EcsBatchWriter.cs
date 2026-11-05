using System.Collections.Immutable;
using Groundwork.Core;

namespace Groundwork.Ecs;

public readonly record struct EcsBatchIssue(BatchId Batch, JobId Job, BatchRejection Reason,
    ResourceKey? Target, BatchId? ConflictingBatch)
{
    public FaultReportStatus Report(IFaultReporter reporter, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        Span<ResourceKey> targets = stackalloc ResourceKey[1];
        int count = 0;
        if (Target is { } target)
            targets[count++] = target;
        var classification = Reason switch
        {
            BatchRejection.StaleTarget => FaultClass.StaleResult,
            BatchRejection.WriteConflict => FaultClass.WriteConflict,
            BatchRejection.InvalidAccess => FaultClass.InvalidAccess,
            _ => FaultClass.RecoverableJobFault,
        };
        var source = Batch.Source;
        var context = new FaultContext(source.World, source.Tick, source.Phase, Job, source, null);
        return reporter.TryReport(new FaultObservation(classification, FaultSeverity.Warning, FaultSubsystem.Ecs,
            FaultOrigin.Primary, (uint)Reason, timestamp, context, targets[..count], []));
    }
}

/// <summary>Isolated writer staging. The trusted coordinator owns supervision and final publication.</summary>
public sealed class EcsBatchPlan : IDisposable
{
    private readonly WorldPublicationPorts _ports;
    private readonly object _gate = new();
    private CandidateSet? _candidates;
    private EcsRoot? _entry;
    private EcsRoot? _next;
    private PreparedBarrierPublication? _prepared;
    private IRuntimeWorkLease? _lease;

    internal EcsBatchPlan(WorldPublicationPorts ports, CandidateSet candidates) => (_ports, _candidates) = (ports, candidates);

    public ImmutableArray<BatchDisposition> Dispositions { get; private set; } = [];
    public ImmutableArray<EcsBatchIssue> Issues { get; private set; } = [];

    internal bool IsSettled
    {
        get
        {
            lock (_gate)
                return _lease is null;
        }
    }

    internal void Track(IRuntimeWorkLease lease, EcsRoot entry) => (_lease, _entry) = (lease, entry);

    internal void Stage()
    {
        var staged = EcsBatchWriter.Stage(_entry!, _candidates!, _ports.VersionIssuer);
        (_next, Dispositions, Issues) = (staged.Root, staged.Dispositions, staged.Issues);
    }

    public BarrierPreparationStatus Prepare(ulong expectedSafetyGeneration)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_lease is null, this);
            if (_prepared is not null)
                throw new InvalidOperationException("The ECS batch plan is already prepared.");
            var result = _ports.Preparation.TryPrepare(new BarrierPublicationDraft(_candidates!, _next!,
                expectedSafetyGeneration, Dispositions));
            _prepared = result.Publication;
            return result.Status;
        }
    }

    public BarrierOutcome FinalizePublication()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_lease is null, this);
            if (_prepared is not { } prepared)
                throw new InvalidOperationException("Prepare the ECS batch plan before finalization.");
            var outcome = _ports.Finalization.TryFinalize(prepared);
            if (outcome.Status == BarrierStatus.Stopped)
                _ = AbortLocked(BarrierAbortReason.Faulted);
            else
                Release();
            return outcome;
        }
    }

    public BarrierAbortStatus Abort(BarrierAbortReason reason)
    {
        lock (_gate)
            return AbortLocked(reason);
    }

    public void Dispose()
    {
        lock (_gate)
            _ = AbortLocked(BarrierAbortReason.Cancelled);
    }

    private BarrierAbortStatus AbortLocked(BarrierAbortReason reason)
    {
        if (_lease is null)
            return BarrierAbortStatus.AlreadySettled;
        var result = _ports.Finalization.TryAbort(_candidates!, reason);
        if (result is BarrierAbortStatus.Stopped or BarrierAbortStatus.AlreadySettled)
            Release();
        return result;
    }

    private void Release()
    {
        _entry = null;
        _next = null;
        _candidates = null;
        _prepared = null;
        var lease = _lease;
        _lease = null;
        lease?.Dispose();
    }
}

internal sealed record StagedBatchResult(EcsRoot Root, ImmutableArray<BatchDisposition> Dispositions,
    ImmutableArray<EcsBatchIssue> Issues);

internal static class EcsBatchWriter
{
    private readonly record struct WriteTarget(EntityId? Entity, ComponentTypeId Component, PartitionScope? Partition)
    {
        internal ResourceKey ToResource(WorldId world) => Entity is { } entity
            ? new ResourceKey(world, Component, new ResourceScope(ScopeKind.Entity, null, entity))
            : new ResourceKey(world, default, new ResourceScope(ScopeKind.Partition, Partition, null));
    }

    private sealed class BatchAnalysis(CandidateBatch batch)
    {
        internal CandidateBatch Batch { get; } = batch;
        internal HashSet<WriteTarget> Writes { get; } = [];
        internal EcsBatchIssue? Issue { get; set; }
    }

    internal static StagedBatchResult Stage(EcsRoot entry, CandidateSet candidates, ITargetVersionIssuer versions)
    {
        var analyses = candidates.Batches.Select(batch => new BatchAnalysis(batch)).ToArray();
        foreach (var analysis in analyses)
            analysis.Issue = TryStage(entry, entry, analysis.Batch, versions, analysis.Writes, out _);
        RejectConflicts(entry.World, analyses);

        var current = entry;
        var dispositions = ImmutableArray.CreateBuilder<BatchDisposition>(analyses.Length);
        var issues = ImmutableArray.CreateBuilder<EcsBatchIssue>();
        foreach (var analysis in analyses)
        {
            var batch = analysis.Batch;
            bool defer = candidates.Request.Kind == BarrierKind.CommitResults &&
                batch.Commands.Any(command => command.Kind != JobCommandKind.ReplaceComponent);
            if (!analysis.Issue.HasValue && !defer)
            {
                analysis.Issue = TryStage(entry, current, batch, versions, null, out var next);
                if (!analysis.Issue.HasValue)
                    current = next;
            }

            if (analysis.Issue is { } issue)
            {
                issues.Add(issue);
                dispositions.Add(new BatchDisposition(batch.Batch, BatchDispositionKind.Reject, issue.Reason));
            }
            else
            {
                dispositions.Add(new BatchDisposition(batch.Batch,
                    defer ? BatchDispositionKind.Defer : BatchDispositionKind.Commit, BatchRejection.None));
            }
        }
        return new StagedBatchResult(current, dispositions.MoveToImmutable(), issues.ToImmutable());
    }

    private static void RejectConflicts(WorldId world, BatchAnalysis[] analyses)
    {
        var writes = analyses.Where(analysis => !analysis.Issue.HasValue)
            .SelectMany(analysis => analysis.Writes.Select(target => (analysis, target))).ToArray();
        foreach (var partition in writes.Where(item => item.target.Partition.HasValue).GroupBy(item => item.target.Partition!.Value.Partition))
            RejectWildcardGroup(world, partition, item => !item.target.Entity.HasValue);
        foreach (var entity in writes.Where(item => item.target.Entity.HasValue).GroupBy(item => item.target.Entity!.Value))
        {
            if (entity.Any(item => item.target.Component.Value == 0))
            {
                RejectWildcardGroup(world, entity, item => item.target.Component.Value == 0);
            }
            else
            {
                foreach (var component in entity.GroupBy(item => item.target.Component))
                    RejectGroup(world, component);
            }
        }
    }

    private static void RejectWildcardGroup(WorldId world, IEnumerable<(BatchAnalysis analysis, WriteTarget target)> group,
        Func<(BatchAnalysis analysis, WriteTarget target), bool> wildcard)
    {
        var owner = group.FirstOrDefault(wildcard);
        if (owner.analysis is null)
            return;
        foreach (var other in group.Where(item => item.analysis != owner.analysis).DistinctBy(item => item.analysis))
        {
            MarkConflict(world, owner, other);
            MarkConflict(world, other, owner);
        }
    }

    private static void RejectGroup(WorldId world, IEnumerable<(BatchAnalysis analysis, WriteTarget target)> group)
    {
        var distinct = group.DistinctBy(item => item.analysis).ToArray();
        if (distinct.Length < 2)
            return;
        for (int index = 0; index < distinct.Length; index++)
            MarkConflict(world, distinct[index], distinct[index == 0 ? 1 : 0]);
    }

    private static void MarkConflict(WorldId world, (BatchAnalysis analysis, WriteTarget target) item,
        (BatchAnalysis analysis, WriteTarget target) other)
        => item.analysis.Issue ??= new EcsBatchIssue(item.analysis.Batch.Batch, item.analysis.Batch.Permit.Job,
            BatchRejection.WriteConflict, item.target.ToResource(world), other.analysis.Batch.Batch);

    private static EcsBatchIssue? TryStage(EcsRoot entry, EcsRoot start, CandidateBatch batch, ITargetVersionIssuer versions,
        HashSet<WriteTarget>? writes, out EcsRoot next)
    {
        next = start;
        if (batch.Permit.Scope is not EcsScope scope || scope.World != entry.World || batch.Commands.IsDefaultOrEmpty)
            return Issue(batch, BatchRejection.InvalidAccess, null);
        var root = start;
        var provisional = new Dictionary<uint, (EntityId Entity, PartitionScope? Destination)>();
        foreach (var command in batch.Commands)
        {
            if (command.Kind == JobCommandKind.CreateEntity)
            {
                var failure = Create(entry, batch, scope, command, provisional, writes, ref root);
                if (failure.HasValue)
                    return failure;
                continue;
            }

            EntityId entity;
            if (command.Entity.Existing is { } existing)
            {
                entity = existing;
                var failure = ValidateExisting(entry, batch, scope, command, entity, versions);
                if (failure.HasValue)
                    return failure;
                _ = entry.Find(entity, out var existingSlot);
                var partition = existingSlot!.Partition is { } member
                    ? new PartitionScope(member, entry.Partitions[member].Revision) : (PartitionScope?)null;
                writes?.Add(new WriteTarget(entity, command.Component, partition));
            }
            else if (command.Entity.Provisional is { } id && provisional.TryGetValue(id, out var created) &&
                command.Kind != JobCommandKind.DestroyEntity && scope.AllowsProvisional(command.Component, created.Destination))
            {
                entity = created.Entity;
            }
            else
            {
                return Issue(batch, BatchRejection.InvalidAccess, null);
            }

            var target = command.Entity.Existing.HasValue ? new WriteTarget(entity, command.Component, null).ToResource(entry.World) : (ResourceKey?)null;
            _ = root.Find(entity, out var slot);
            if (slot is null)
                return Issue(batch, BatchRejection.InvalidBatch, target);
            if (command.Kind == JobCommandKind.DestroyEntity)
            {
                if (slot.Partition is { } partition)
                    writes?.Add(new WriteTarget(null, default, new PartitionScope(partition, entry.Partitions[partition].Revision)));
                if (!root.TryDestroy(entity, slot, out root))
                    return Issue(batch, BatchRejection.InvalidBatch, target);
                continue;
            }

            if (!root.Registry.TryGetSize(command.Component, out int size) ||
                (command.Kind == JobCommandKind.RemoveComponent ? !command.Payload.IsEmpty : command.Payload.Length != size))
                return Issue(batch, BatchRejection.InvalidBatch, target);
            bool present = slot.Cells.GetValueOrDefault(command.Component).Present;
            if (command.Kind is not (JobCommandKind.AddComponent or JobCommandKind.RemoveComponent or JobCommandKind.ReplaceComponent) ||
                (command.Kind == JobCommandKind.AddComponent ? present : !present) ||
                !root.TryWrite(entity, slot, command.Component, command.Kind == JobCommandKind.RemoveComponent ? default : command.Payload, out root))
                return Issue(batch, BatchRejection.InvalidBatch, target);
        }
        next = root;
        return null;
    }

    private static EcsBatchIssue? Create(EcsRoot entry, CandidateBatch batch, EcsScope scope, JobCommand command,
        Dictionary<uint, (EntityId, PartitionScope?)> provisional, HashSet<WriteTarget>? writes, ref EcsRoot root)
    {
        if (command.Entity.Provisional is not { } id || provisional.ContainsKey(id) || !scope.AllowsCreate(command.Destination))
            return Issue(batch, BatchRejection.InvalidAccess, null);
        if (command.Destination is { } destination)
        {
            var write = new WriteTarget(null, default, destination);
            if (!entry.Partitions.TryGetValue(destination.Partition, out var partition) || partition.Revision != destination.Revision)
                return Issue(batch, BatchRejection.StaleTarget, write.ToResource(entry.World));
            writes?.Add(write);
        }

        if (!root.TryCreate(command.Destination?.Partition, out root, out var entity))
            return Issue(batch, BatchRejection.InvalidBatch, null);
        provisional.Add(id, (entity, command.Destination));
        return null;
    }

    private static EcsBatchIssue? ValidateExisting(EcsRoot entry, CandidateBatch batch, EcsScope scope, JobCommand command,
        EntityId entity, ITargetVersionIssuer versions)
    {
        var target = new ComponentTarget(entity, command.Component);
        var key = new WriteTarget(entity, command.Component, null).ToResource(entry.World);
        _ = entry.Find(entity, out var slot);
        if (slot is null)
            return Issue(batch, BatchRejection.StaleTarget, key);
        if (!scope.AllowsWrite(entry, target) || command.ExpectedVersion is not { } expected ||
            !versions.TryRead(expected, batch.Permit, target, out var stamp))
            return Issue(batch, BatchRejection.InvalidAccess, key);
        var cell = slot.Cells.GetValueOrDefault(command.Component);
        bool structural = command.Component.Value == 0;
        return stamp.Present == (structural || cell.Present) && stamp.Mutation == (structural ? slot.Mutation : cell.Mutation)
            ? null : Issue(batch, BatchRejection.StaleTarget, key);
    }

    private static EcsBatchIssue Issue(CandidateBatch batch, BatchRejection reason, ResourceKey? target)
        => new(batch.Batch, batch.Permit.Job, reason, target, null);
}
