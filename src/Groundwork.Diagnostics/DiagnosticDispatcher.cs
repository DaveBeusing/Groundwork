using System.Diagnostics.CodeAnalysis;
using Groundwork.Core;

namespace Groundwork.Diagnostics;

public interface IDiagnosticSink
{
    /// <summary>Consumes one observation. Release it and remove its cancellation registrations at normal completion.</summary>
    /// <remarks>A callback already running during dispatcher shutdown may use the observation until the callback returns.</remarks>
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
    private PendingDelivery? _pending;
    private DiagnosticReadLease _shutdownDelivery;
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
            while (delivered < maxEvents && CanDeliver() && TryTake(out var observation, out var lease))
            {
                // Selection may have raced stop. Admission is ordered under the same
                // gate as stop and disable, while the sink itself runs outside the gate.
                if (!CanDeliver())
                {
                    lease.Dispose();
                    break;
                }

                try
                {
                    await _sink.WriteAsync(observation, _shutdown.Token).ConfigureAwait(false);
                    delivered++;
                }
                finally
                {
                    ReleaseDelivery(lease);
                }
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
        finally
        {
            if (SinkDisabled)
                ReleasePending();
        }

        return delivered;
    }

    private bool CanDeliver()
    {
        lock (_gate)
            return _stopped is null && !SinkDisabled;
    }

    private bool TryTake(out DiagnosticEvent observation, out DiagnosticReadLease lease)
    {
        lease = default;
        if (TryTakeEmergency(_reader.ReadEmergency(), out observation))
            return true;

        if (_pending is null)
        {
            if (!_reader.TryAcquire(out var normal, out var acquired))
                return TryTakeEmergency(_reader.ReadEmergency(), out observation);

            // Retain this slot if emergency publication raced the normal acquisition.
            // A batch that ends after the emergency can deliver it on the next drain.
            _pending = new(normal, acquired);
            if (TryTakeEmergency(_reader.ReadEmergency(), out observation))
                return true;
        }

        var pending = _pending.Value;
        _pending = null;
        (observation, lease) = (pending.Observation, pending.Lease);
        return true;
    }

    private bool TryTakeEmergency(DiagnosticEmergencySnapshot emergency, out DiagnosticEvent observation)
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
        observation = default;
        return false;
    }

    private void ReleasePending()
    {
        if (_pending is { } pending)
        {
            _pending = null;
            pending.Lease.Dispose();
        }
    }

    private void ReleaseDelivery(DiagnosticReadLease lease)
    {
        lock (_gate)
        {
            if (_stopped is not null)
            {
                // There is one admitted sink write. Stop joins cancellation callbacks
                // and this drain before releasing its observation.
                _shutdownDelivery = lease;
            }
            else
            {
                lease.Dispose();
            }
        }
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
                _shutdownDelivery.Dispose();
                ReleasePending();
                _shutdown.Dispose();
                _ownership.Dispose();
            }
        }
    }

    private void DisableSink(Exception error)
    {
        lock (_gate)
        {
            if (SinkDisabled)
                return;
            Volatile.Write(ref _sinkDisabled, 1);
        }

        _reader.RecordSinkFailure(unchecked((uint)error.HResult), TimeProvider.System.GetTimestamp());
    }

    private readonly record struct PendingDelivery(DiagnosticEvent Observation, DiagnosticReadLease Lease);
}
