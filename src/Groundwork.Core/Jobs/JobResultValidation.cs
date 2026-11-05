using System.Collections.Immutable;

namespace Groundwork.Core;

internal sealed partial class WorldPublicationAuthority
{
    private static bool TryGetReservation(JobSubmission submission, out long descriptorBytes, out long resultBytes)
    {
        descriptorBytes = 0;
        resultBytes = 0;
        var budget = submission.Budget;
        if (submission.Dependencies.IsDefault || submission.ReadScopes.IsDefault ||
            submission.CommandScopes.IsDefault || submission.SnapshotTargets.IsDefault || submission.Arguments.IsDefault ||
            budget.MaxBatches < 0 || budget.MaxCommands < 0 || budget.MaxCommandPayloadBytes < 0 ||
            budget.MaxOutputBytes < 0 || budget.MaxSnapshotBytes < 0)
            return false;

        // Fixed conservative charges for the retained descriptor, per-item metadata and copied payload.
        descriptorBytes = 512L + submission.Arguments.Length + submission.Dependencies.Length * 64L +
            (submission.ReadScopes.Length + (long)submission.CommandScopes.Length) * 128L +
            submission.SnapshotTargets.Length * 64L;
        long resultMetadata = budget.MaxBatches * 256L + budget.MaxCommands * 192L + budget.MaxOutputBytes;
        if (budget.MaxCommandPayloadBytes > long.MaxValue - resultMetadata)
            return false;

        resultBytes = budget.MaxCommandPayloadBytes + resultMetadata;
        return true;
    }

    private static JobSubmission CopySubmission(JobSubmission submission)
        => submission with
        {
            Dependencies = ImmutableArray.Create(submission.Dependencies.AsSpan()),
            ReadScopes = ImmutableArray.Create(submission.ReadScopes.AsSpan()),
            CommandScopes = ImmutableArray.Create(submission.CommandScopes.AsSpan()),
            SnapshotTargets = ImmutableArray.Create(submission.SnapshotTargets.AsSpan()),
            Arguments = ImmutableArray.Create(submission.Arguments.AsSpan()),
        };

    private static bool IsDescriptorValid(JobSubmission submission, WorldId world)
    {
        if (submission.Handler.Value == 0 ||
            submission.InputKind is not (JobInputKind.None or JobInputKind.Borrowed or JobInputKind.OwnedSnapshot) ||
            submission.ResultKind is not (JobResultKind.Commandless or JobResultKind.Commands) ||
            submission.InputKind != JobInputKind.OwnedSnapshot && !submission.SnapshotTargets.IsEmpty ||
            submission.InputKind == JobInputKind.None && !submission.ReadScopes.IsEmpty ||
            submission.ResultKind == JobResultKind.Commandless && !submission.CommandScopes.IsEmpty)
            return false;

        var prerequisites = new HashSet<JobId>();
        foreach (var dependency in submission.Dependencies)
        {
            if (dependency.Prerequisite.World != world || dependency.Prerequisite.Value == 0 ||
                dependency.Kind is not (DependencyKind.Execution or DependencyKind.Commit) ||
                !prerequisites.Add(dependency.Prerequisite))
                return false;
        }

        if (submission.ReadScopes.Any(key => !IsScopeValid(key, world)) ||
            submission.CommandScopes.Any(key => !IsScopeValid(key, world)))
            return false;

        return submission.SnapshotTargets.All(target => IsEntity(target.Entity, world));
    }

    private static bool IsScopeValid(ResourceKey key, WorldId world)
    {
        if (key.World != world)
            return false;

        return key.Scope.Kind switch
        {
            ScopeKind.World => key.Scope.Partition is null && key.Scope.Entity is null,
            ScopeKind.Partition => key.Scope.Entity is null && key.Scope.Partition is { } partition &&
                IsPartition(partition),
            ScopeKind.Entity => key.Scope.Partition is null && key.Scope.Entity is { } entity &&
                IsEntity(entity, world),
            _ => false,
        };
    }

    private static bool IsEntity(EntityId entity, WorldId world)
        => entity.World == world && entity.Generation != 0;

    private static bool IsPartition(PartitionScope scope)
        => scope.Partition.Value != 0 && scope.Revision != 0;

    private bool TryCopyCompletion(JobRecord job, JobExecutionCompletion completion,
        out List<BatchRecord> batches, out ImmutableArray<byte> output)
    {
        batches = [];
        output = [];
        var budget = job.Submission.Budget;
        if (completion.Batches.IsDefault || completion.CommandlessOutput.IsDefault ||
            completion.Batches.Length > budget.MaxBatches ||
            completion.CommandlessOutput.Length > budget.MaxOutputBytes)
            return false;

        if (job.Submission.ResultKind == JobResultKind.Commandless)
        {
            if (!completion.Batches.IsEmpty)
                return false;
            output = ImmutableArray.Create(completion.CommandlessOutput.AsSpan());
            return true;
        }

        if (!completion.CommandlessOutput.IsEmpty)
            return false;

        int commandsRemaining = budget.MaxCommands;
        long payloadRemaining = budget.MaxCommandPayloadBytes;
        uint lastBatch = 0;
        foreach (var batch in completion.Batches)
        {
            if (batch is null || batch.Batch.Source != job.Submission.Source || batch.Batch.Sequence <= lastBatch ||
                batch.Commands.IsDefaultOrEmpty || batch.Commands.Length > commandsRemaining)
                return false;

            lastBatch = batch.Batch.Sequence;
            commandsRemaining -= batch.Commands.Length;
            var copies = ImmutableArray.CreateBuilder<JobCommand>(batch.Commands.Length);
            var provisional = new HashSet<uint>();
            uint lastCommand = 0;
            bool structural = false;
            foreach (var command in batch.Commands)
            {
                if (command.BatchSequence != lastBatch || command.CommandSequence <= lastCommand ||
                    command.Payload.IsDefault || command.Payload.Length > payloadRemaining ||
                    !ValidateCommand(job, completion.Permit, command, provisional))
                    return false;

                lastCommand = command.CommandSequence;
                payloadRemaining -= command.Payload.Length;
                structural |= command.Kind != JobCommandKind.ReplaceComponent;
                copies.Add(command with { Payload = ImmutableArray.Create(command.Payload.AsSpan()) });
            }

            batches.Add(new BatchRecord(completion.Permit, batch.Batch, copies.MoveToImmutable(), structural));
        }

        return true;
    }

    private bool ValidateCommand(JobRecord job, JobDispatchPermit permit, JobCommand command, HashSet<uint> provisional)
    {
        if (command.Kind == JobCommandKind.CreateEntity)
        {
            return command.Entity.Kind == CommandEntityKind.Provisional && command.Entity.Existing is null &&
                command.Entity.Provisional is > 0 and var created && command.Component.Value == 0 &&
                command.ExpectedVersion is null && command.Payload.IsEmpty &&
                (command.Destination is null || IsPartition(command.Destination.Value)) &&
                job.Submission.CommandScopes.Any(key => key.Component.Value == 0 &&
                    (key.Scope.Kind == ScopeKind.World ||
                    command.Destination is { } destination && key.Scope.Partition == destination)) &&
                provisional.Add(created);
        }

        if (command.Destination is not null)
            return false;

        if (command.Kind == JobCommandKind.DestroyEntity)
        {
            return command.Entity is { Kind: CommandEntityKind.Existing, Provisional: null } &&
                command.Entity.Existing is { } existing && command.Component.Value == 0 && command.Payload.IsEmpty &&
                CouldCover(job.Submission.CommandScopes, new ComponentTarget(existing, command.Component)) &&
                command.ExpectedVersion is { } version &&
                TryRead(version, permit, new ComponentTarget(existing, command.Component), out var stamp) && stamp.Present;
        }

        if (command.Component.Value == 0 ||
            command.Kind is not (JobCommandKind.AddComponent or JobCommandKind.ReplaceComponent or JobCommandKind.RemoveComponent) ||
            command.Kind == JobCommandKind.RemoveComponent && !command.Payload.IsEmpty)
            return false;

        return command.Entity.Kind switch
        {
            CommandEntityKind.Provisional => command.Entity.Existing is null &&
                command.Entity.Provisional is { } id && provisional.Contains(id) && command.ExpectedVersion is null,
            CommandEntityKind.Existing => command.Entity.Provisional is null &&
                command.Entity.Existing is { } target && IsEntity(target, permit.Job.World) &&
                CouldCover(job.Submission.CommandScopes, new ComponentTarget(target, command.Component)) &&
                command.ExpectedVersion is { } version &&
                TryRead(version, permit, new ComponentTarget(target, command.Component), out _),
            _ => false,
        };
    }

    private static bool CouldCover(ImmutableArray<ResourceKey> keys, ComponentTarget target)
    {
        foreach (var key in keys)
        {
            // Only ECS can resolve admitted/current partition membership. This rejects scopes that
            // cannot cover the target even if membership were valid.
            if (key.World == target.Entity.World &&
                (key.Component.Value == 0 || key.Component == target.Component) &&
                (key.Scope.Kind is ScopeKind.World or ScopeKind.Partition || key.Scope.Entity == target.Entity))
                return true;
        }

        return false;
    }

    private sealed class BatchRecord(JobDispatchPermit permit, BatchId id, ImmutableArray<JobCommand> commands, bool structural)
    {
        internal JobDispatchPermit Permit { get; } = permit;
        internal BatchId Id { get; } = id;
        internal ImmutableArray<JobCommand> Commands { get; } = commands;
        internal bool Structural { get; } = structural;
        internal bool Selected { get; set; }
        internal bool Deferred { get; set; }
        internal BatchReceipt? Receipt { get; set; }
    }
}
