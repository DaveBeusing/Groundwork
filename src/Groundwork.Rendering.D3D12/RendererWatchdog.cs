using System.Diagnostics;

namespace Groundwork.Rendering.D3D12;

internal enum RendererStage : uint { Startup = 1, Resize, Submit, Present, Retire, Visibility, Cleanup, MeshReplacement }

internal sealed class RendererWatchdog(Action<RendererStage, ulong?> timeout) : IDisposable
{
    private readonly Lock _gate = new();
    private Timer? _timer;
    private long _deadline;
    private RendererStage _stage;
    private ulong? _sequence;
    private bool _armed;
    private bool _expired;
    private bool _disposed;

    internal bool Begin(RendererStage stage, ulong? sequence, TimeSpan budget)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_expired)
                return false;
            if (_armed)
                throw new InvalidOperationException("A renderer operation already owns the progress deadline.");

            _deadline = Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency);
            _stage = stage;
            _sequence = sequence;
            _armed = true;
            _timer ??= new Timer(static state => ((RendererWatchdog)state!).OnTimer(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(budget, Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    // The render owner reads this only during its current operation. A timer
    // can expire it only after the same monotonic deadline.
    internal bool IsWithinDeadline => Stopwatch.GetTimestamp() < _deadline;

    internal bool TryAdvance(RendererStage stage)
    {
        (RendererStage Stage, ulong? Sequence) expired;
        lock (_gate)
        {
            if (!_armed)
                return false;
            if (Stopwatch.GetTimestamp() < _deadline)
            {
                _stage = stage;
                return true;
            }
            expired = Expire();
        }

        timeout(expired.Stage, expired.Sequence);
        return false;
    }

    internal bool Complete()
    {
        (RendererStage Stage, ulong? Sequence)? expired = null;
        lock (_gate)
        {
            if (!_armed)
                return !_expired;

            if (Stopwatch.GetTimestamp() >= _deadline)
                expired = Expire();
            _armed = false;
            _timer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (expired is { } failure)
            timeout(failure.Stage, failure.Sequence);
        return expired is null;
    }

    private void OnTimer()
    {
        (RendererStage Stage, ulong? Sequence) expired;
        lock (_gate)
        {
            if (!_armed || _disposed)
                return;

            var now = Stopwatch.GetTimestamp();
            if (now < _deadline)
            {
                var remaining = Stopwatch.GetElapsedTime(now, _deadline);
                _timer!.Change(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining,
                    Timeout.InfiniteTimeSpan);
                return;
            }

            expired = Expire();
        }

        timeout(expired.Stage, expired.Sequence);
    }

    private (RendererStage, ulong?) Expire()
    {
        _armed = false;
        _expired = true;
        return (_stage, _sequence);
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            _armed = false;
            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        // Fault reporting by an already-claimed callback must finish before the
        // render owner relinquishes its lifetime lease.
        if (timer is not null)
            timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
