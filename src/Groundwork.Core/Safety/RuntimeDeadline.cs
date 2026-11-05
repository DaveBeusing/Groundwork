namespace Groundwork.Core;

public readonly struct RuntimeDeadline
{
    private readonly DeadlineClock? _clock;
    private readonly bool _expired;

    internal RuntimeDeadline(DeadlineClock clock, long timestamp, bool expired = false)
        => (_clock, Timestamp, _expired) = (clock, timestamp, expired);

    public long Timestamp { get; }

    public bool IsExpired(TimeProvider clock)
    {
        var source = ValidateClock(clock);
        return _expired || !source.TryRead(out var now) || now >= Timestamp;
    }

    public TimeSpan GetRemaining(TimeProvider clock)
    {
        var source = ValidateClock(clock);
        if (_expired || !source.TryRead(out var now) || now >= Timestamp)
        {
            return TimeSpan.Zero;
        }

        var ticks = ((Int128)Timestamp - now) * TimeSpan.TicksPerSecond / source.Frequency;
        return TimeSpan.FromTicks((long)Int128.Min(ticks, long.MaxValue));
    }

    internal bool ForcedExpired => _expired;

    private DeadlineClock ValidateClock(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (_clock is null || !ReferenceEquals(_clock.Provider, clock))
        {
            throw new ArgumentException("The deadline must belong to the supplied clock.", nameof(clock));
        }

        return _clock;
    }
}

internal sealed class DeadlineClock
{
    private long _latest;
    private int _invalid;

    internal DeadlineClock(TimeProvider provider)
    {
        Provider = provider;
        Frequency = provider.TimestampFrequency;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Frequency);
        _latest = provider.GetTimestamp();
    }

    internal TimeProvider Provider { get; }
    internal long Frequency { get; }
    internal long LastTimestamp => Volatile.Read(ref _latest);
    internal bool IsValid => Volatile.Read(ref _invalid) == 0;

    internal long GetDuration(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        var units = ((Int128)duration.Ticks * Frequency + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
        if (units > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration does not fit the clock domain.");
        }

        return (long)units;
    }

    internal RuntimeDeadline Start(long duration)
    {
        var valid = TryRead(out var now);
        return At(now, duration, valid);
    }

    internal RuntimeDeadline At(long now, long duration, bool valid = true)
        => !valid || now > long.MaxValue - duration
            ? new RuntimeDeadline(this, now, expired: true)
            : new RuntimeDeadline(this, now + duration);

    internal bool TryRead(out long now)
    {
        var minimum = Volatile.Read(ref _latest);
        now = Provider.GetTimestamp();
        if (now < minimum)
        {
            Volatile.Write(ref _invalid, 1);
        }

        var observed = minimum;
        while (now > observed)
        {
            var previous = Interlocked.CompareExchange(ref _latest, now, observed);
            if (previous == observed)
            {
                break;
            }

            observed = previous;
        }

        now = Math.Max(now, observed);
        return IsValid;
    }
}
