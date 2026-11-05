using System.Collections.Immutable;
using Groundwork.Core;

namespace Groundwork.Jobs;

internal sealed class JobResults(JobDispatchPermit permit, JobResultKind kind) : IJobCommandWriter
{
    private readonly object _gate = new();
    private readonly ImmutableArray<ResourceKey> _scopes = permit.Scope.CommandScopes;
    private readonly List<CompletedBatch> _batches = [];
    private readonly List<JobCommand> _commands = [];
    private readonly HashSet<uint> _provisional = [];
    private ImmutableArray<byte> _output = [];
    private uint _batchSequence;
    private int _commandCount;
    private long _payloadBytes;
    private bool _outputSet;
    private bool _closed;
    private BatchAppendResult? _failure;

    public BatchAppendResult TryAppend(in JobCommand command)
    {
        lock (_gate)
        {
            if (_closed)
                return BatchAppendResult.Closed;
            if (_failure is { } prior)
                return prior;
            if (kind != JobResultKind.Commands || command.BatchSequence == 0 ||
                command.BatchSequence < _batchSequence || command.CommandSequence == 0 ||
                command.Payload.IsDefault ||
                command.BatchSequence == _batchSequence && _commands.Count != 0 &&
                command.CommandSequence <= _commands[^1].CommandSequence)
                return Fail(BatchAppendResult.InvalidCommand);

            bool nextBatch = command.BatchSequence != _batchSequence;
            if (_commandCount == permit.Budget.MaxCommands ||
                command.Payload.Length > permit.Budget.MaxCommandPayloadBytes - _payloadBytes ||
                nextBatch && _batches.Count + (_commands.Count == 0 ? 0 : 1) >= permit.Budget.MaxBatches)
                return Fail(BatchAppendResult.CapacityExceeded);

            if (nextBatch)
            {
                FinishBatch();
                _batchSequence = command.BatchSequence;
            }

            var validation = Validate(command);
            if (validation != BatchAppendResult.Accepted)
                return Fail(validation);

            _commands.Add(command with { Payload = ImmutableArray.Create(command.Payload.AsSpan()) });
            _commandCount++;
            _payloadBytes += command.Payload.Length;
            return BatchAppendResult.Accepted;
        }
    }

    public BatchAppendResult TrySetOutput(ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_closed)
                return BatchAppendResult.Closed;
            if (_failure is { } prior)
                return prior;
            if (kind != JobResultKind.Commandless || _outputSet)
                return Fail(BatchAppendResult.InvalidCommand);
            if (payload.Length > permit.Budget.MaxOutputBytes)
                return Fail(BatchAppendResult.CapacityExceeded);

            _output = ImmutableArray.Create(payload);
            _outputSet = true;
            return BatchAppendResult.Accepted;
        }
    }

    internal JobExecutionCompletion Seal(out BatchAppendResult? failure)
    {
        lock (_gate)
        {
            _closed = true;
            failure = _failure;
            if (failure is not null)
            {
                Clear();
                return new JobExecutionCompletion(permit, JobExecutionStatus.Faulted, [], []);
            }

            FinishBatch();
            var completion = new JobExecutionCompletion(permit, JobExecutionStatus.Succeeded,
                _batches.ToImmutableArray(), _output);
            Clear();
            return completion;
        }
    }

    internal void Close()
    {
        lock (_gate)
            _closed = true;
    }

    internal void Discard()
    {
        lock (_gate)
        {
            _closed = true;
            Clear();
        }
    }

    private void FinishBatch()
    {
        if (_commands.Count != 0)
        {
            _batches.Add(new CompletedBatch(new BatchId(permit.Source, _batchSequence), _commands.ToImmutableArray()));
            _commands.Clear();
        }

        _provisional.Clear();
    }

    private void Clear()
    {
        _commands.Clear();
        _batches.Clear();
        _provisional.Clear();
        _output = [];
    }

    private BatchAppendResult Fail(BatchAppendResult reason)
    {
        _failure = reason;
        return reason;
    }

    private BatchAppendResult Validate(JobCommand command)
    {
        if (command.Kind == JobCommandKind.CreateEntity)
        {
            if (command.Entity.Kind != CommandEntityKind.Provisional || command.Entity.Existing is not null ||
                command.Entity.Provisional is not > 0 || command.Component.Value != 0 ||
                command.ExpectedVersion is not null || !command.Payload.IsEmpty ||
                command.Destination is { } invalid && (invalid.Partition.Value == 0 || invalid.Revision == 0))
                return BatchAppendResult.InvalidCommand;

            if (!_scopes.Any(scope => scope.World == permit.Job.World && scope.Component.Value == 0 &&
                (scope.Scope.Kind == ScopeKind.World ||
                 command.Destination is { } destination && scope.Scope.Partition == destination)))
                return BatchAppendResult.UndeclaredTarget;

            return _provisional.Add(command.Entity.Provisional.Value)
                ? BatchAppendResult.Accepted : BatchAppendResult.InvalidCommand;
        }

        if (command.Destination is not null)
            return BatchAppendResult.InvalidCommand;
        if (command.Kind == JobCommandKind.DestroyEntity)
        {
            if (command.Component.Value != 0 || !command.Payload.IsEmpty ||
                command.Entity.Kind != CommandEntityKind.Existing)
                return BatchAppendResult.InvalidCommand;
        }
        else if (command.Component.Value == 0 ||
            command.Kind is not (JobCommandKind.AddComponent or JobCommandKind.ReplaceComponent or JobCommandKind.RemoveComponent) ||
            command.Kind == JobCommandKind.RemoveComponent && !command.Payload.IsEmpty)
        {
            return BatchAppendResult.InvalidCommand;
        }

        if (command.Entity.Kind == CommandEntityKind.Provisional)
            return command.Entity.Existing is null && command.Entity.Provisional is { } provisional &&
                _provisional.Contains(provisional) && command.ExpectedVersion is null
                ? BatchAppendResult.Accepted : BatchAppendResult.InvalidCommand;
        if (command.Entity.Kind != CommandEntityKind.Existing || command.Entity.Provisional is not null ||
            command.Entity.Existing is not { } entity || entity.Generation == 0 || command.ExpectedVersion is null)
            return BatchAppendResult.InvalidCommand;
        if (entity.World != permit.Job.World)
            return BatchAppendResult.UndeclaredTarget;

        // Only the world writer can verify membership at the authoritative epoch and validate the token.
        return _scopes.Any(scope => scope.World == entity.World &&
            (scope.Component.Value == 0 || scope.Component == command.Component) &&
            (scope.Scope.Kind is ScopeKind.World or ScopeKind.Partition || scope.Scope.Entity == entity))
            ? BatchAppendResult.Accepted : BatchAppendResult.UndeclaredTarget;
    }
}
