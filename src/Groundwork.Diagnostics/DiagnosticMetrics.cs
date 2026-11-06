namespace Groundwork.Diagnostics;

/// <summary>Stable engine measurements. Duration values are in nanoseconds, all other values are counts.</summary>
public enum DiagnosticMetric
{
    TickWorkNanoseconds = 1,
    TickBacklogNanoseconds = 2,
    TickDroppedNanoseconds = 3,
    JobQueueNanoseconds = 4,
    JobWorkerNanoseconds = 5,
    JobDispatchNanoseconds = 6,
    FrameWorkNanoseconds = 7,
    CompletedTicks = 8,
    RejectedBatches = 9,
    DependencyCancellations = 10,
    RecoveryPauses = 11,
    FatalStops = 12,
}

/// <summary>A coherent view of the accepted samples for one measurement.</summary>
public readonly record struct DiagnosticMetricReading(long Samples, long Total, long Latest, bool Saturated);

/// <summary>Bounded, backend-neutral measurements. Producers drop samples under contention.</summary>
public sealed class DiagnosticMetrics
{
    private const int MetricCount = (int)DiagnosticMetric.FatalStops;
    private readonly MetricSlot[]? _slots;
    private long _dropped;

    public DiagnosticMetrics() : this(true) { }

    private DiagnosticMetrics(bool enabled)
    {
        if (!enabled)
            return;

        _slots = new MetricSlot[MetricCount];
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = new MetricSlot();
    }

    /// <summary>A shared recorder that discards all measurements without allocating metric storage.</summary>
    public static DiagnosticMetrics Disabled { get; } = new(false);

    /// <summary>The number of valid samples dropped because their slot was busy, saturated at <see cref="long.MaxValue"/>.</summary>
    public long DroppedMeasurements
    {
        get
        {
            var dropped = Volatile.Read(ref _dropped);
            return dropped < 0 ? long.MaxValue : dropped;
        }
    }

    /// <summary>Records a nonnegative value in the metric's defined unit. Returns false for disabled, invalid or contended calls.</summary>
    public bool TryRecord(DiagnosticMetric metric, long value = 1)
    {
        var index = (int)metric - 1;
        return _slots is { } slots && (uint)index < MetricCount && value >= 0 &&
            TryUpdate(slots[index], value, clamped: false);
    }

    /// <summary>Converts nonnegative elapsed timestamp ticks to whole nanoseconds using the supplied clock frequency.</summary>
    public bool TryRecordElapsed(DiagnosticMetric metric, long timestampTicks, long timestampFrequency)
    {
        var index = (int)metric - 1;
        if (_slots is not { } slots || (uint)index >= (int)DiagnosticMetric.FrameWorkNanoseconds ||
            timestampTicks < 0 || timestampFrequency <= 0)
            return false;

        var elapsed = (Int128)timestampTicks * 1_000_000_000 / timestampFrequency;
        return TryUpdate(slots[index], (long)Int128.Min(elapsed, long.MaxValue), elapsed > long.MaxValue);
    }

    /// <summary>Reads a coherent snapshot for one metric. Call from the observation path.</summary>
    public DiagnosticMetricReading Read(DiagnosticMetric metric)
    {
        var index = (int)metric - 1;
        if ((uint)index >= MetricCount)
            throw new ArgumentOutOfRangeException(nameof(metric));
        if (_slots is not { } slots)
            return default;

        var slot = slots[index];
        lock (slot.Gate)
            return new(slot.Samples, slot.Total, slot.Latest, slot.Saturated);
    }

    private bool TryUpdate(MetricSlot slot, long value, bool clamped)
    {
        if (!slot.Gate.TryEnter())
        {
            if (Volatile.Read(ref _dropped) < long.MaxValue && Interlocked.Increment(ref _dropped) < 0)
                Interlocked.Exchange(ref _dropped, long.MaxValue);
            return false;
        }

        try
        {
            slot.Latest = value;
            if (slot.Samples == long.MaxValue)
                slot.Saturated = true;
            else
                slot.Samples++;

            if (value > long.MaxValue - slot.Total)
            {
                slot.Total = long.MaxValue;
                slot.Saturated = true;
            }
            else
            {
                slot.Total += value;
                slot.Saturated |= clamped;
            }

            return true;
        }
        finally
        {
            slot.Gate.Exit();
        }
    }

    private sealed class MetricSlot
    {
        internal Lock Gate { get; } = new();
        internal long Samples { get; set; }
        internal long Total { get; set; }
        internal long Latest { get; set; }
        internal bool Saturated { get; set; }
    }
}
