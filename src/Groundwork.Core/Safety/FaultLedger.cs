using System.Runtime.CompilerServices;

namespace Groundwork.Core;

internal sealed class FaultLedger : IFaultReporter, IRuntimeSafetyState, IFaultEvidenceReader, IDiagnosticReader
{
    internal const int EmergencySummaryCount = 3;
    internal static int MinimumSummaryPayload => Unsafe.SizeOf<EmergencySlot>();
    private readonly RuntimeSafetyAuthority _runtime;
    private readonly WorldId? _world;
    private readonly Action _stop;
    private RequiredFaultEvidence[] _required;
    private NormalDelivery? _normal;
    private EmergencyDelivery? _emergency;
    private readonly int _maxTargets;
    private readonly int _textCapacity;
    private readonly AtomicCounter _repeat = new();
    private readonly AtomicCounter _coalesced = new();
    private readonly AtomicCounter _deliveryLoss = new();
    private readonly AtomicCounter _evidenceLoss = new();
    private int _requiredCount;
    private ulong _generation;
    private bool _criticalPending;
    private volatile bool _storageReleased;
    private SafetyCondition _conditions;
    private int _contendedEvidenceLoss;
    private int _diagnosticSettlementQueued;
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
        var normalSlots = (int)Math.Min(quota.NormalRecords, quota.NormalBytes / slotBytes);
        _normal = normalSlots == 0 ? null : new NormalDelivery(normalSlots, _textCapacity);
        _emergency = new EmergencyDelivery(_textCapacity);
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
        if (observation.Classification == FaultClass.DiagnosticOnly)
            return RecordDiagnostic(observation);

        if (!Monitor.TryEnter(_runtime.Gate))
        {
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
            return RecordDiagnostic(observation);

        var terminal = IsTerminal || HasContendedEvidenceLoss || _runtime.IsPublicationFenced;
        var valid = IsValidObservation(observation);

        if (valid && observation.Classification == FaultClass.IncompleteCleanup)
        {
            MarkIncomplete();
        }

        if (!valid || _storageReleased)
        {
            return LoseEvidence(terminal);
        }

        // Emergency primary, cleanup and sink summaries survive the terminal
        // fence and stay separate from the bounded supervisor evidence ledger.
        var retained = RecordEmergency(new DiagnosticEvent(observation), observation.RedactedText);
        if (retained == FaultReportStatus.Coalesced && !terminal)
            RecordOptional(observation, countCoalesced: false);
        if (_generation == ulong.MaxValue)
        {
            return LoseEvidence(terminal);
        }

        _generation++;
        if (!_world.HasValue)
        {
            if (!terminal)
            {
                _stop();
            }

            return retained;
        }

        var count = Math.Max(1, observation.Targets.Length);
        if (count > _required.Length - _requiredCount)
        {
            return LoseEvidence(terminal);
        }

        for (var i = 0; i < count; i++)
        {
            _required[_requiredCount++] = new RequiredFaultEvidence(
                _generation, observation.Classification, observation.Subsystem, observation.Origin,
                observation.Code, observation.Context, observation.Targets.IsEmpty ? null : observation.Targets[i]);
        }

        _criticalPending |= observation.Classification == FaultClass.UnknownCritical;
        if (!terminal)
        {
            if (observation.Classification is FaultClass.UnsafePartialCommit or FaultClass.BorrowedDrainExpired or FaultClass.IncompleteCleanup)
            {
                _stop();
            }
            else
            {
                _runtime.SimulationWake.Signal();
            }
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
        _storageReleased = true;
        _required = [];
        Volatile.Write(ref _normal, null);
        Volatile.Write(ref _emergency, null);
        _requiredCount = 0;
    }

    // Called under the safety gate only when the scope is otherwise ready to release.
    internal bool TryCloseDiagnostics()
    {
        if (Volatile.Read(ref _normal) is { } queue)
        {
            Volatile.Write(ref queue.Closing, true);
            if (!Monitor.TryEnter(queue.Gate))
                return false;
            try
            {
                if (queue.InFlight)
                    return false;
            }
            finally
            {
                Monitor.Exit(queue.Gate);
            }
        }

        if (Volatile.Read(ref _emergency) is not { } emergency)
            return true;

        // The first two slots are written only under the safety gate. A sink
        // can write its separate slot concurrently without that gate.
        Volatile.Write(ref emergency.Closing, true);
        return Interlocked.CompareExchange(ref emergency.Slots[2].State, 3, 0) != 1;
    }

    private void QueueDiagnosticSettlement()
    {
        if (Interlocked.CompareExchange(ref _diagnosticSettlementQueued, 1, 0) != 0)
            return;

        ThreadPool.UnsafeQueueUserWorkItem(static ledger =>
        {
            Volatile.Write(ref ledger._diagnosticSettlementQueued, 0);
            ledger._runtime.ReconcileDiagnosticSettlement(ledger._world);
        }, this, preferLocal: false);
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

    public FaultEvidenceReadStatus ReadPending(ulong generation, int index, out RequiredFaultEvidence record, out int count)
    {
        lock (_runtime.Gate)
        {
            record = default;
            count = 0;
            if (EvidenceLost || _runtime.HasContendedEvidenceLoss)
                return FaultEvidenceReadStatus.EvidenceLost;
            if (generation != _generation)
                return FaultEvidenceReadStatus.StaleGeneration;

            count = _requiredCount;
            if ((uint)index >= (uint)count)
                return FaultEvidenceReadStatus.InsufficientCapacity;
            record = _required[index];
            return FaultEvidenceReadStatus.Copied;
        }
    }

    bool IDiagnosticReader.TryAcquire(out DiagnosticEvent observation, out DiagnosticReadLease lease)
    {
        if (Volatile.Read(ref _normal) is { } queue && !Volatile.Read(ref queue.Closing))
        {
            try
            {
                lock (queue.Gate)
                {
                    if (!Volatile.Read(ref queue.Closing) && queue.Count != 0 && !queue.InFlight && queue.Ticket != ulong.MaxValue)
                    {
                        queue.InFlight = true;
                        observation = queue.Records[queue.Head];
                        lease = new DiagnosticReadLease(this, ++queue.Ticket);
                        return true;
                    }
                }
            }
            finally
            {
                if (Volatile.Read(ref queue.Closing))
                    QueueDiagnosticSettlement();
            }
        }

        observation = default;
        lease = default;
        return false;
    }

    internal void ReleaseDiagnostic(ulong ticket)
    {
        if (Volatile.Read(ref _normal) is not { } queue)
            return;

        try
        {
            lock (queue.Gate)
            {
                if (!queue.InFlight || ticket != queue.Ticket)
                    return;

                queue.Text.AsSpan(queue.Head * _textCapacity, _textCapacity).Clear();
                queue.Head = (queue.Head + 1) % queue.Records.Length;
                queue.Count--;
                queue.InFlight = false;
            }
        }
        finally
        {
            if (Volatile.Read(ref queue.Closing))
                QueueDiagnosticSettlement();
        }
    }

    DiagnosticEmergencySnapshot IDiagnosticReader.ReadEmergency()
    {
        return Volatile.Read(ref _emergency) is { } delivery
            ? new(delivery.Slots[0].Read(), delivery.Slots[1].Read(), delivery.Slots[2].Read()) : default;
    }

    void IDiagnosticReader.RecordSinkFailure(uint code, long timestamp)
    {
        RecordEmergency(new DiagnosticEvent(FaultClass.DiagnosticOnly, FaultSeverity.Error,
            FaultSubsystem.Diagnostics, FaultOrigin.Sink, code, timestamp, new(_world, null, null, null, null, null)), []);
    }

    private FaultReportStatus LoseEvidence(bool terminal)
    {
        _conditions |= SafetyCondition.RequiredEvidenceLost;
        _evidenceLoss.Increment();
        if (!terminal)
        {
            _stop();
        }

        return FaultReportStatus.EvidenceLost;
    }

    private bool IsValidObservation(scoped in FaultObservation observation)
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

    private FaultReportStatus RecordDiagnostic(scoped in FaultObservation observation)
    {
        if (!IsValidObservation(observation) || _storageReleased)
            return LoseDelivery(countCoalesced: true);

        return RecordOptional(observation);
    }

    private FaultReportStatus RecordOptional(scoped in FaultObservation observation, bool countCoalesced = true)
    {
        if (Volatile.Read(ref _normal) is not { } queue || Volatile.Read(ref queue.Closing) || !Monitor.TryEnter(queue.Gate))
            return LoseDelivery(countCoalesced);

        try
        {
            if (Volatile.Read(ref queue.Closing) || queue.Count == queue.Records.Length)
                return LoseDelivery(countCoalesced);

            queue.Records[queue.Tail] = new DiagnosticEvent(observation);
            CopyDetail(observation.RedactedText, queue.Text, queue.Tail);
            queue.Tail = (queue.Tail + 1) % queue.Records.Length;
            queue.Count++;
            if (!observation.Targets.IsEmpty)
                _deliveryLoss.Increment();
            return FaultReportStatus.Recorded;
        }
        finally
        {
            Monitor.Exit(queue.Gate);
            if (Volatile.Read(ref queue.Closing))
                QueueDiagnosticSettlement();
        }
    }

    private FaultReportStatus LoseDelivery(bool countCoalesced)
    {
        if (countCoalesced)
            _coalesced.Increment();
        _deliveryLoss.Increment();
        return FaultReportStatus.Coalesced;
    }

    private FaultReportStatus RecordEmergency(DiagnosticEvent summary, ReadOnlySpan<char> text)
    {
        var index = (int)summary.Origin - 1;
        if ((uint)index >= EmergencySummaryCount || Volatile.Read(ref _emergency) is not { } delivery || _storageReleased)
            return LoseDelivery(countCoalesced: true);

        ref var slot = ref delivery.Slots[index];
        if (Interlocked.CompareExchange(ref slot.State, 1, 0) != 0)
        {
            _coalesced.Increment();
            if (slot.Read() is { } first && summary.Code == first.Code && summary.Classification == first.Classification &&
                summary.Context == first.Context && summary.Subsystem == first.Subsystem)
            {
                _repeat.Increment();
            }

            return FaultReportStatus.Coalesced;
        }

        slot.Event = summary;
        CopyDetail(text, delivery.Text, index);
        Volatile.Write(ref slot.State, 2);
        if (index == 2 && Volatile.Read(ref delivery.Closing))
            QueueDiagnosticSettlement();
        return FaultReportStatus.Recorded;
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

    private sealed class NormalDelivery(int slots, int textCapacity)
    {
        internal object Gate { get; } = new();
        internal DiagnosticEvent[] Records { get; } = new DiagnosticEvent[slots];
        internal char[] Text { get; } = new char[checked(slots * textCapacity)];
        internal int Count;
        internal int Head;
        internal int Tail;
        internal bool InFlight;
        internal bool Closing;
        internal ulong Ticket;
    }

    private sealed class EmergencyDelivery(int textCapacity)
    {
        internal EmergencySlot[] Slots { get; } = new EmergencySlot[EmergencySummaryCount];
        internal char[] Text { get; } = new char[checked(EmergencySummaryCount * textCapacity)];
        internal bool Closing;
    }

    private struct EmergencySlot
    {
        internal DiagnosticEvent Event;
        // 0 is empty, 1 is being written, 2 is immutable and visible, 3 is closed.
        internal int State;
        internal DiagnosticEvent? Read() => Volatile.Read(ref State) == 2 ? Event : null;
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
