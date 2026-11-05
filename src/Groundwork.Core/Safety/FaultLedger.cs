using System.Runtime.CompilerServices;

namespace Groundwork.Core;

internal sealed class FaultLedger : IFaultReporter, IRuntimeSafetyState, IFaultEvidenceReader
{
    internal const int EmergencySummaryCount = 3;
    internal static int MinimumSummaryPayload => Unsafe.SizeOf<DiagnosticSummary?>();
    private readonly RuntimeSafetyAuthority _runtime;
    private readonly WorldId? _world;
    private readonly Action _stop;
    private RequiredFaultEvidence[] _required;
    private DiagnosticSummary[] _normal;
    private DiagnosticSummary?[] _emergency = new DiagnosticSummary?[EmergencySummaryCount];
    private char[] _normalText;
    private char[] _emergencyText;
    private readonly int _maxTargets;
    private readonly int _textCapacity;
    private readonly AtomicCounter _repeat = new();
    private readonly AtomicCounter _coalesced = new();
    private readonly AtomicCounter _deliveryLoss = new();
    private readonly AtomicCounter _evidenceLoss = new();
    private int _normalCount;
    private int _requiredCount;
    private ulong _generation;
    private bool _criticalPending;
    private SafetyCondition _conditions;
    private int _contendedEvidenceLoss;
    private long _contendedStopTimestamp = long.MaxValue;

    internal FaultLedger(RuntimeSafetyAuthority runtime, WorldId? world, DiagnosticQuota quota, Action stop)
    {
        _runtime = runtime;
        _world = world;
        _stop = stop;
        _maxTargets = quota.MaxTargetsPerRecord;

        // Runtime faults are terminal and use the first-fault summaries. Only a world
        // has a supervisor that can consume an evidence ledger.
        var metadataBytes = MinimumSummaryPayload;
        _textCapacity = Math.Min(quota.MaxTextCharsPerRecord, Math.Max(0, (quota.MaxPayloadBytesPerRecord - metadataBytes) / sizeof(char)));
        var slotBytes = metadataBytes + (long)_textCapacity * sizeof(char);
        var normalSlots = world.HasValue ? (int)Math.Min(quota.NormalRecords, quota.NormalBytes / slotBytes) : 0;
        _normal = new DiagnosticSummary[normalSlots];
        _normalText = new char[checked(normalSlots * _textCapacity)];
        _emergencyText = new char[checked(_emergency.Length * _textCapacity)];
        _required = world.HasValue ? new RequiredFaultEvidence[quota.RequiredEvidenceRecords] : [];
    }

    internal ulong Generation => _generation;
    internal bool HasPending => _requiredCount > 0;
    internal bool HasCriticalPending => _criticalPending;
    internal bool IsTerminal => (_conditions & SafetyCondition.TerminalStop) != 0;
    internal bool EvidenceLost => (_conditions & SafetyCondition.RequiredEvidenceLost) != 0 || HasContendedEvidenceLoss;
    internal bool IsPaused => (_conditions & SafetyCondition.RecoveryPaused) != 0;
    internal bool HasContendedEvidenceLoss => Volatile.Read(ref _contendedEvidenceLoss) != 0;
    internal long ContendedStopTimestamp => Volatile.Read(ref _contendedStopTimestamp);

    internal bool TryPassContendedFence()
        => Interlocked.CompareExchange(ref _contendedEvidenceLoss, 0, 0) == 0;

    public FaultReportStatus TryReport(scoped in FaultObservation observation)
    {
        if (!Monitor.TryEnter(_runtime.Gate))
        {
            if (observation.Classification == FaultClass.DiagnosticOnly)
            {
                _deliveryLoss.Increment();
                _coalesced.Increment();
                return FaultReportStatus.Coalesced;
            }

            if (_world.HasValue)
            {
                Interlocked.CompareExchange(ref _contendedStopTimestamp, _runtime.Clock.LastTimestamp, long.MaxValue);
                Interlocked.Exchange(ref _contendedEvidenceLoss, 1);
                _runtime.SimulationWake.Signal();
            }
            else
            {
                _runtime.FenceOnContendedEvidenceLoss();
            }

            _evidenceLoss.Increment();
            return FaultReportStatus.EvidenceLost;
        }

        try
        {
            return Record(observation);
        }
        finally
        {
            Monitor.Exit(_runtime.Gate);
        }
    }

    internal FaultReportStatus Record(scoped in FaultObservation observation)
    {
        if (observation.Classification == FaultClass.DiagnosticOnly)
        {
            return RecordOptional(observation);
        }

        if (IsTerminal || HasContendedEvidenceLoss || _runtime.IsPublicationFenced)
        {
            return FaultReportStatus.EvidenceLost;
        }

        var summary = new DiagnosticSummary(observation);
        RecordEmergency(summary, observation.RedactedText);
        if (!IsValidRequired(observation) || _generation == ulong.MaxValue)
        {
            return LoseEvidence();
        }

        _generation++;
        if (!_world.HasValue)
        {
            _stop();
            return FaultReportStatus.Recorded;
        }

        var count = Math.Max(1, observation.Targets.Length);
        if (count > _required.Length - _requiredCount)
        {
            return LoseEvidence();
        }

        for (var i = 0; i < count; i++)
        {
            _required[_requiredCount++] = new RequiredFaultEvidence(
                _generation, observation.Classification, observation.Subsystem, observation.Origin,
                observation.Code, observation.Context, observation.Targets.IsEmpty ? null : observation.Targets[i]);
        }

        _criticalPending |= observation.Classification == FaultClass.UnknownCritical;
        if (observation.Classification is FaultClass.UnsafePartialCommit or FaultClass.BorrowedDrainExpired or FaultClass.IncompleteCleanup)
        {
            if (observation.Classification == FaultClass.IncompleteCleanup)
            {
                MarkIncomplete();
            }

            _stop();
        }
        else
        {
            _runtime.SimulationWake.Signal();
        }

        return FaultReportStatus.Recorded;
    }

    internal void MarkStopped() => _conditions |= SafetyCondition.TerminalStop;
    internal void MarkIncomplete() => _conditions |= SafetyCondition.IncompleteCleanup;
    internal void Pause() => _conditions |= SafetyCondition.RecoveryPaused;

    internal void Continue()
    {
        Array.Clear(_required, 0, _requiredCount);
        _requiredCount = 0;
        _criticalPending = false;
        _conditions &= ~SafetyCondition.RecoveryPaused;
    }

    internal void ReleaseStorage()
    {
        _required = [];
        _normal = [];
        _emergency = [];
        _normalText = [];
        _emergencyText = [];
        _requiredCount = 0;
        _normalCount = 0;
    }

    public RuntimeSafetySnapshot Read()
    {
        lock (_runtime.Gate)
        {
            var conditions = _conditions | _runtime.InheritedConditions;
            if (HasContendedEvidenceLoss)
            {
                conditions |= SafetyCondition.TerminalStop | SafetyCondition.RequiredEvidenceLost;
            }
            var repeat = _repeat.Value;
            var coalesced = _coalesced.Value;
            var deliveryLoss = _deliveryLoss.Value;
            var evidenceLoss = _evidenceLoss.Value;
            if (repeat == ulong.MaxValue || coalesced == ulong.MaxValue ||
                deliveryLoss == ulong.MaxValue || evidenceLoss == ulong.MaxValue)
            {
                conditions |= SafetyCondition.CounterSaturated;
            }

            return new(_generation, _requiredCount, conditions, repeat, coalesced, deliveryLoss, evidenceLoss);
        }
    }

    public FaultEvidenceReadStatus CopyPending(
        ulong generation, Span<RequiredFaultEvidence> destination, out int count)
    {
        lock (_runtime.Gate)
        {
            count = 0;
            if (EvidenceLost || _runtime.HasContendedEvidenceLoss)
            {
                return FaultEvidenceReadStatus.EvidenceLost;
            }

            if (generation != _generation)
            {
                return FaultEvidenceReadStatus.StaleGeneration;
            }

            count = _requiredCount;
            if (destination.Length < count)
            {
                return FaultEvidenceReadStatus.InsufficientCapacity;
            }

            _required.AsSpan(0, count).CopyTo(destination);
            return FaultEvidenceReadStatus.Copied;
        }
    }

    private FaultReportStatus LoseEvidence()
    {
        _conditions |= SafetyCondition.RequiredEvidenceLost;
        _evidenceLoss.Increment();
        _stop();
        return FaultReportStatus.EvidenceLost;
    }

    private bool IsValidRequired(scoped in FaultObservation observation)
    {
        var context = observation.Context;
        if (!Enum.IsDefined(observation.Classification) || observation.Classification == FaultClass.Unspecified ||
            observation.Subsystem == FaultSubsystem.Unspecified || !Enum.IsDefined(observation.Subsystem) ||
            observation.Origin == FaultOrigin.Unspecified || !Enum.IsDefined(observation.Origin) ||
            context.World != _world || observation.Targets.Length > _maxTargets ||
            (context.Phase is { } phaseValue && !Enum.IsDefined(phaseValue)) ||
            (context.Job is { } job && (job.World != _world || job.Value == 0)) ||
            (context.Source is { } source && (source.World != _world ||
                source.Tick == 0 || source.Ordinal == 0 ||
                source.Phase is not (TickPhase.Gameplay or TickPhase.Base) ||
                (context.Tick is { } tick && source.Tick != tick) ||
                (context.Phase is { } phase && source.Phase != phase))))
        {
            return false;
        }

        foreach (var target in observation.Targets)
        {
            if (target.World != _world || !HasValidScope(target.Scope, target.World))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasValidScope(ResourceScope scope, WorldId world) => scope switch
    {
        { Kind: ScopeKind.World, Partition: null, Entity: null } => true,
        { Kind: ScopeKind.Partition, Partition: { Partition.Value: > 0, Revision: > 0 }, Entity: null } => true,
        { Kind: ScopeKind.Entity, Partition: null, Entity: { Generation: > 0 } entity } => entity.World == world,
        _ => false
    };

    private FaultReportStatus RecordOptional(scoped in FaultObservation observation)
    {
        if (IsTerminal || _normalCount == _normal.Length)
        {
            _coalesced.Increment();
            _deliveryLoss.Increment();
            return FaultReportStatus.Coalesced;
        }

        var index = _normalCount++;
        _normal[index] = new DiagnosticSummary(observation);
        CopyDetail(observation.RedactedText, _normalText, index);
        if (!observation.Targets.IsEmpty)
        {
            _deliveryLoss.Increment();
        }

        return FaultReportStatus.Recorded;
    }

    private void RecordEmergency(DiagnosticSummary summary, ReadOnlySpan<char> text)
    {
        var index = (int)summary.Origin - 1;
        if ((uint)index >= (uint)_emergency.Length)
        {
            _deliveryLoss.Increment();
            return;
        }

        if (_emergency[index] is { } first)
        {
            _coalesced.Increment();
            if (summary.Code == first.Code && summary.Classification == first.Classification &&
                summary.Context == first.Context && summary.Subsystem == first.Subsystem)
            {
                _repeat.Increment();
            }

            return;
        }

        _emergency[index] = summary;
        CopyDetail(text, _emergencyText, index);
    }

    private void CopyDetail(ReadOnlySpan<char> source, char[] storage, int index)
    {
        var retained = Math.Min(source.Length, _textCapacity);
        source[..retained].CopyTo(storage.AsSpan(index * _textCapacity, retained));
        if (retained != source.Length)
        {
            _deliveryLoss.Increment();
        }
    }

    private readonly record struct DiagnosticSummary(
        FaultClass Classification, FaultSeverity Severity, FaultSubsystem Subsystem,
        FaultOrigin Origin, uint Code, long Timestamp, FaultContext Context)
    {
        internal DiagnosticSummary(scoped in FaultObservation value)
            : this(value.Classification, value.Severity, value.Subsystem, value.Origin,
                value.Code, value.Timestamp, value.Context) { }
    }

    private sealed class AtomicCounter
    {
        private ulong _value;

        internal ulong Value => Volatile.Read(ref _value);

        internal void Increment()
        {
            for (var i = 0; i < 4; i++)
            {
                var value = Value;
                if (value == ulong.MaxValue || Interlocked.CompareExchange(ref _value, value + 1, value) == value)
                {
                    return;
                }
            }

            // Even under sustained contention keep the fault path finite and monotonic.
            Interlocked.Exchange(ref _value, ulong.MaxValue);
        }
    }
}
