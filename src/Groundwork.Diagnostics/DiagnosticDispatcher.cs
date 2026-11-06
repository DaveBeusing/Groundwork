using System.Diagnostics.CodeAnalysis;
using Groundwork.Core;

namespace Groundwork.Diagnostics;

public interface IDiagnosticSink
{
    ValueTask WriteAsync(DiagnosticEvent observation, CancellationToken cancellationToken);
}

/// <summary>Delivers bounded batches from one Core diagnostic source to one host-configured sink.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "StopAsync uses the shared deadline and retains ownership until in-flight sink work releases.")]
public sealed class DiagnosticDispatcher
{
    private readonly Lock _gate = new();
    private readonly IDiagnosticReader _reader;
    private readonly IDiagnosticSink _sink;
    private readonly IRuntimeWorkLease _ownership;
    private readonly CancellationTokenSource _shutdown = new();
    private Task<int>? _active;
    private Task? _stopped;
    private int _deliveredEmergency;
    private int _sinkDisabled;

    public DiagnosticDispatcher(IDiagnosticReader reader, IRuntimeOwnership ownership, IDiagnosticSink sink)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(sink);
        _reader = reader;
        _sink = sink;
        try
        {
            _ownership = ownership.TryTrack(this) ?? throw new InvalidOperationException("No diagnostic operation capacity is available.");
        }
        catch
        {
            _shutdown.Dispose();
            throw;
        }
    }

    public bool SinkDisabled => Volatile.Read(ref _sinkDisabled) != 0;

    /// <summary>Schedules one delivery batch on a worker. An overlapping or reentrant drain returns zero.</summary>
    public Task<int> DrainAsync(int maxEvents)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvents);
        lock (_gate)
        {
            if (_stopped is not null || SinkDisabled || _active is { IsCompleted: false })
                return Task.FromResult(0);

            return _active = Task.Run(() => DrainCoreAsync(maxEvents));
        }
    }

    /// <summary>Closes delivery and waits for tracked sink work within the Core settlement budget.</summary>
    public async ValueTask StopAsync(RuntimeDeadline deadline, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        _ = deadline.GetRemainingForSettlement(clock);
        Task stopped;
        lock (_gate)
        {
            var active = _active;
            stopped = _stopped ??= Task.Run(() => FinishAsync(active), CancellationToken.None);
        }

        while (!stopped.IsCompleted)
        {
            var remaining = deadline.GetRemainingForSettlement(clock);
            if (remaining == TimeSpan.Zero)
            {
                if (stopped.IsCompleted)
                    break;
                throw new IncompleteCleanupException(FaultSubsystem.Diagnostics);
            }

            var interval = TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 1, int.MaxValue));
            try
            {
                await stopped.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                // Recheck Core's deadline after the system timer wakes.
            }
        }

        await stopped.ConfigureAwait(false);
    }

    private async Task<int> DrainCoreAsync(int maxEvents)
    {
        int delivered = 0;
        try
        {
            var emergency = _reader.ReadEmergency();
            while (delivered < maxEvents && !_shutdown.IsCancellationRequested && TryTake(emergency, out var observation))
            {
                await _sink.WriteAsync(observation, _shutdown.Token).ConfigureAwait(false);
                delivered++;
            }
        }
        catch (OperationCanceledException error) when (_shutdown.IsCancellationRequested && error.CancellationToken == _shutdown.Token)
        {
            // Cooperative dispatcher shutdown.
        }
        catch (Exception error)
        {
            DisableSink(error);
        }

        return delivered;
    }

    private bool TryTake(DiagnosticEmergencySnapshot emergency, out DiagnosticEvent observation)
    {
        if (emergency.Primary is { } primary && (_deliveredEmergency & 1) == 0)
        {
            _deliveredEmergency |= 1;
            observation = primary;
            return true;
        }
        if (emergency.Cleanup is { } cleanup && (_deliveredEmergency & 2) == 0)
        {
            _deliveredEmergency |= 2;
            observation = cleanup;
            return true;
        }

        // A sink failure is observable through the emergency reader, independently of delivery.
        return _reader.TryRead(out observation);
    }

    private async Task FinishAsync(Task<int>? active)
    {
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            DisableSink(error);
        }
        finally
        {
            try
            {
                if (active is not null)
                    await active.ConfigureAwait(false);
            }
            finally
            {
                _shutdown.Dispose();
                _ownership.Dispose();
            }
        }
    }

    private void DisableSink(Exception error)
    {
        if (Interlocked.Exchange(ref _sinkDisabled, 1) == 0)
            _reader.RecordSinkFailure(unchecked((uint)error.HResult), TimeProvider.System.GetTimestamp());
    }
}
