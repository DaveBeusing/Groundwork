using Groundwork.Core;

namespace Groundwork.Platform.Windows;

internal sealed class PresentationTarget(IRuntimeOwnership ownership) : IDisposable
{
    private readonly Lock _gate = new();
    private WindowsPresentationTargetLease? _lease;
    private TaskCompletionSource? _changed;
    private AutoResetEvent? _drainWake;
    private HostWindowSnapshot _snapshot;
    private nint _window;
    private bool _closed;
    private bool _nativeDestroyed;
    private bool _operationActive;
    private bool _releaseRequested;

    internal void Open(nint window, HostWindowSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_closed)
            {
                _window = window;
                _snapshot = snapshot;
            }
        }
    }

    internal void Publish(HostWindowSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
            SignalChange();
        }
    }

    internal WindowsPresentationTargetLease? TryAcquire()
    {
        lock (_gate)
        {
            if (_closed || _window == 0 || _lease is not null)
            {
                return null;
            }

            _drainWake ??= new AutoResetEvent(false);
            _lease = new WindowsPresentationTargetLease(this);
            return _lease;
        }
    }

    internal HostWindowSnapshot Read()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    internal async ValueTask<HostWindowSnapshot> WaitForChangeAsync(WindowsPresentationTargetLease lease,
        ulong observedRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task changed;
        lock (_gate)
        {
            if (_snapshot.Revision != observedRevision || lease.Revoked.IsCompleted)
            {
                return _snapshot;
            }

            _changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changed = _changed.Task;
        }

        await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Read();
    }

    internal WindowsPresentationOperation? TryBeginOperation(WindowsPresentationTargetLease lease)
    {
        // Acquire Core's stop fence before the local target gate. Never hold both locks.
        var permit = ownership.TryTrack(lease);
        if (permit is null)
        {
            return null;
        }

        try
        {
            lock (_gate)
            {
                if (_closed || _operationActive || _releaseRequested || !ReferenceEquals(_lease, lease))
                {
                    return null;
                }

                var operation = new WindowsPresentationOperation(this, _window, _snapshot, permit);
                _operationActive = true;
                permit = null;
                return operation;
            }
        }
        finally
        {
            permit?.Dispose();
        }
    }

    internal void EndOperation()
    {
        lock (_gate)
        {
            _operationActive = false;
            if (_releaseRequested)
            {
                Detach();
            }
        }
    }

    internal nint GetOperationHandle(nint handle)
    {
        lock (_gate)
        {
            if (_nativeDestroyed)
            {
                throw new InvalidOperationException("The presentation window was destroyed.");
            }

            return handle;
        }
    }

    internal void Release(WindowsPresentationTargetLease lease)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_lease, lease) || _releaseRequested)
            {
                return;
            }

            _releaseRequested = true;
            lease.Revoke(PresentationTargetRevocation.Released);
            SignalChange();
            if (!_operationActive)
            {
                Detach();
            }
        }
    }

    internal bool Revoke(PresentationTargetRevocation reason)
    {
        lock (_gate)
        {
            _closed = true;
            _window = 0;
            _nativeDestroyed |= reason == PresentationTargetRevocation.NativeDestroyed;
            _lease?.Revoke(reason);
            SignalChange();
            return _lease is not null;
        }
    }

    internal AutoResetEvent? DrainWake
    {
        get
        {
            lock (_gate)
            {
                return _lease is null ? null : _drainWake;
            }
        }
    }

    private void SignalChange()
    {
        _changed?.TrySetResult();
        _changed = null;
    }

    private void Detach()
    {
        _lease = null;
        _releaseRequested = false;
        _drainWake!.Set();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_lease is not null)
            {
                throw new InvalidOperationException("An attached presentation target cannot be released by the host.");
            }

            _closed = true;
            _drainWake?.Dispose();
            _drainWake = null;
        }
    }
}
