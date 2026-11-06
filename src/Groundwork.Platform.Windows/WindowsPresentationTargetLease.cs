using Groundwork.Core;

namespace Groundwork.Platform.Windows;

public enum PresentationTargetRevocation
{
    Closing = 1,
    NativeDestroyed,
    Released
}

/// <summary>An exclusive attachment to a live Windows host window.</summary>
/// <remarks>Dispose after releasing the swap chain and its references to this window.</remarks>
public sealed class WindowsPresentationTargetLease : IDisposable
{
    private readonly PresentationTarget _target;
    private readonly TaskCompletionSource<PresentationTargetRevocation> _revoked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal WindowsPresentationTargetLease(PresentationTarget target) => _target = target;

    /// <summary>Completes with the first reason this attachment stopped admitting work.</summary>
    public Task<PresentationTargetRevocation> Revoked => _revoked.Task;

    public HostWindowSnapshot Read() => _target.Read();

    /// <summary>Waits for a newer snapshot or revocation. The render owner uses one waiter and intermediate revisions may coalesce.</summary>
    /// <remarks>On revocation the returned revision can equal the observed revision. Check <see cref="Revoked"/>.</remarks>
    public ValueTask<HostWindowSnapshot> WaitForChangeAsync(ulong observedRevision, CancellationToken cancellationToken = default) =>
        _target.WaitForChangeAsync(this, observedRevision, cancellationToken);

    /// <summary>Tries to acquire the one in-flight permit for a native operation.</summary>
    /// <remarks>The provider must check the admitted snapshot for an extent and phase suitable for its operation.</remarks>
    public WindowsPresentationOperation? TryBeginOperation() => Revoked.IsCompleted ? null : _target.TryBeginOperation(this);

    /// <summary>Acknowledges release. Detachment waits for any admitted operation to finish.</summary>
    public void Dispose() => _target.Release(this);

    internal void Revoke(PresentationTargetRevocation reason) => _revoked.TrySetResult(reason);
}

/// <summary>Admits one native operation for an attached Windows target.</summary>
/// <remarks>Use the borrowed handle only for this operation. Keep the permit until the native call has returned.</remarks>
public sealed class WindowsPresentationOperation : IDisposable
{
    private readonly PresentationTarget _target;
    private readonly nint _windowHandle;
    private IRuntimeWorkLease? _ownership;

    internal WindowsPresentationOperation(PresentationTarget target, nint windowHandle,
        HostWindowSnapshot snapshot, IRuntimeWorkLease ownership)
        => (_target, _windowHandle, Snapshot, _ownership) = (target, windowHandle, snapshot, ownership);

    public nint WindowHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _ownership) is null, this);
            return _target.GetOperationHandle(_windowHandle);
        }
    }

    public HostWindowSnapshot Snapshot { get; }

    public void Dispose()
    {
        var ownership = Interlocked.Exchange(ref _ownership, null);
        if (ownership is null)
        {
            return;
        }

        try
        {
            ownership.Dispose();
        }
        finally
        {
            _target.EndOperation();
        }
    }
}
