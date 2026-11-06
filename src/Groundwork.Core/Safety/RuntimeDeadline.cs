namespace Groundwork.Core;

public readonly struct RuntimeDeadline
{
    private readonly DeadlineClock? _clock;
    private readonly bool _expired;
    private readonly long _watchdogTimestamp;

    internal RuntimeDeadline(DeadlineClock clock, long timestamp, long watchdogTimestamp, bool expired = false)
        => (_clock, Timestamp, _watchdogTimestamp, _expired) = (clock, timestamp, watchdogTimestamp, expired);

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

    /// <summary>Returns the remaining settlement budget without sampling the supplied clock.</summary>
    public TimeSpan GetRemainingForSettlement(TimeProvider clock)
    {
        var source = ValidateClock(clock);
        if (_expired || !source.IsValid)
        {
            return TimeSpan.Zero;
        }

        var watchdog = TimeProvider.System;
        var clockTicks = ((Int128)Timestamp - source.LastTimestamp) * TimeSpan.TicksPerSecond / source.Frequency;
        var watchdogTicks = ((Int128)_watchdogTimestamp - watchdog.GetTimestamp()) * TimeSpan.TicksPerSecond /
            watchdog.TimestampFrequency;
        var remaining = Int128.Min(clockTicks, watchdogTicks);
        return TimeSpan.FromTicks((long)Int128.Clamp(remaining, 0, long.MaxValue));
    }

    internal bool ForcedExpired => _expired;

    internal bool IsExpiredForSettlement()
        => _expired || _clock is null || !_clock.IsValid || _clock.LastTimestamp >= Timestamp ||
           TimeProvider.System.GetTimestamp() >= _watchdogTimestamp;

    internal RuntimeDeadline RestrictTo(RuntimeDeadline other)
    {
        if (_clock is null || !ReferenceEquals(_clock, other._clock))
        {
            throw new ArgumentException("Deadlines must share their clock domain.", nameof(other));
        }

        return new RuntimeDeadline(
            _clock, Math.Min(Timestamp, other.Timestamp), Math.Min(_watchdogTimestamp, other._watchdogTimestamp),
            _expired || other._expired);
    }

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

    internal RuntimeDeadline StartFromTrustedSample(long duration)
    {
        var now = ReferenceEquals(Provider, TimeProvider.System) ? TimeProvider.System.GetTimestamp() : LastTimestamp;
        return At(now, duration, IsValid);
    }

    internal RuntimeDeadline At(long now, long duration, bool valid = true, long? watchdogOrigin = null)
    {
        var watchdog = TimeProvider.System;
        var watchdogNow = watchdogOrigin ?? watchdog.GetTimestamp();
        if (!valid || now > long.MaxValue - duration)
        {
            return new RuntimeDeadline(this, now, watchdogNow, expired: true);
        }

        var timestamp = now + duration;
        // Floor rather than round up: the real-time cap cannot extend the
        // remaining budget at the last trusted injected-clock observation.
        var remaining = Int128.Max(0, (Int128)timestamp - Math.Max(now, LastTimestamp));
        var watchdogDuration = remaining * watchdog.TimestampFrequency / Frequency;
        var watchdogDeadline = (long)Int128.Min(long.MaxValue, (Int128)watchdogNow + watchdogDuration);
        if (ReferenceEquals(Provider, watchdog))
        {
            watchdogDeadline = Math.Min(timestamp, watchdogDeadline);
        }

        return new RuntimeDeadline(this, timestamp, watchdogDeadline);
    }

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
