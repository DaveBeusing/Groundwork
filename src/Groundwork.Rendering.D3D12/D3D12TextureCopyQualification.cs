using System.Diagnostics;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

/// <summary>An explicit, bounded qualification control for the next optional copy of an exact ticket.</summary>
/// <remarks>Requires the native debug layer, DRED and resource snapshots. It leaves ordinary rendering untouched.</remarks>
public sealed class D3D12TextureCopyQualification
{
    private Hold? _armed;

    /// <summary>Arm the ticket before giving the cooked texture to the optional upload stream.</summary>
    public Hold Arm(D3D12TextureTicket ticket)
    {
        if (ticket.Generation == 0)
            throw new ArgumentException("A texture copy qualification requires an issued ticket.", nameof(ticket));

        var hold = new Hold(this, ticket);
        if (Interlocked.CompareExchange(ref _armed, hold, null) is not null)
        {
            hold.Dispose();
            throw new InvalidOperationException("A qualification texture copy is already armed.");
        }

        hold.StartWatchdog();
        return hold;
    }

    internal Hold? Find(D3D12TextureTicket ticket) =>
        Volatile.Read(ref _armed) is { } hold && hold.Ticket == ticket ? hold : null;

    /// <summary>The optional copy's real queue signal and the host-monotonic timestamps surrounding submission.</summary>
    public readonly record struct Submission(D3D12TextureTicket Ticket, ulong CopyFence, long WaitQueuedAt, long SubmittedAt);

    /// <summary>One exact copy hold. Disposing releases the wait even if qualification did not finish.</summary>
    public sealed unsafe class Hold : IDisposable
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);
        private readonly D3D12TextureCopyQualification _owner;
        private readonly Lock _gate = new();
        private readonly Timer _watchdog;
        private readonly TaskCompletionSource<Submission> _submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GpuDevice? _gpuOwner;
        private ID3D12Fence* _waitFence;
        private long _queuedAt;
        private long _releasedAt;
        private Exception? _failure;

        internal Hold(D3D12TextureCopyQualification owner, D3D12TextureTicket ticket)
        {
            _owner = owner;
            Ticket = ticket;
            _watchdog = new(static state => ((Hold)state!).Expire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        internal D3D12TextureTicket Ticket { get; }

        /// <summary>Completes after the native copy receives its queue fence and the stream accepts custody.</summary>
        public Task<Submission> Submitted => _submitted.Task;

        internal void StartWatchdog() => _watchdog.Change(Limit, Timeout.InfiniteTimeSpan);

        internal bool TryEnqueue(GpuDevice gpu)
        {
            lock (_gate)
            {
                // A contender may have obtained the same armed ticket before the first owner queued its copy.
                if (_releasedAt != 0 || _gpuOwner is not null)
                    return false;
                if (!gpu.DebugLayerEnabled || !gpu.DredEnabled || !gpu.HasResourceDiagnostics)
                    throw new InvalidOperationException("Qualification texture holds require native Debug Layer, DRED and resource snapshots.");

                ID3D12Fence* fence = null;
                GpuNative.Check(gpu.Device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE,
                    __uuidof<ID3D12Fence>(), (void**)&fence), "Create qualification texture fence");
                try
                {
                    GpuNative.Check(gpu.Queue->Wait(fence, 1), "Queue qualification texture wait");
                    _gpuOwner = gpu;
                    _waitFence = fence;
                    _queuedAt = Stopwatch.GetTimestamp();
                    return true;
                }
                catch
                {
                    fence->Release();
                    throw;
                }
            }
        }

        internal void MarkSubmitted(GpuDevice gpu, ulong fence)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_gpuOwner, gpu) && _releasedAt == 0)
                    _submitted.TrySetResult(new(Ticket, fence, _queuedAt, Stopwatch.GetTimestamp()));
            }
        }

        /// <summary>Release the submitted copy and return its host-monotonic release timestamp.</summary>
        public long Release()
        {
            lock (_gate)
            {
                if (_failure is not null)
                    throw new InvalidOperationException("The qualification texture wait failed.", _failure);
                if (!_submitted.Task.IsCompletedSuccessfully || _releasedAt != 0)
                    throw new InvalidOperationException("There is no submitted qualification texture wait to release.");

                FinishUnderLock();
                if (_failure is not null)
                    throw new InvalidOperationException("The qualification texture wait failed.", _failure);
                return _releasedAt;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                FinishUnderLock();
                _submitted.TrySetCanceled();
            }
        }

        private void Expire()
        {
            lock (_gate)
            {
                if (_releasedAt != 0)
                    return;
                _failure ??= new TimeoutException("The qualification texture wait expired.");
                FinishUnderLock(finalAttempt: true);
                _submitted.TrySetException(_failure);
            }
        }

        private void FinishUnderLock(bool finalAttempt = false)
        {
            if (_releasedAt != 0)
                return;
            var releasedAt = Stopwatch.GetTimestamp();
            if (_waitFence != null)
            {
                var result = _waitFence->Signal(1).Value;
                if (result < 0)
                {
                    _failure ??= new GpuNativeException("Release qualification texture wait", result);
                    if (!finalAttempt)
                        return; // Let the independent watchdog make the final attempt.
                }
            }
            _releasedAt = releasedAt;
            _watchdog.Dispose();
            Interlocked.CompareExchange(ref _owner._armed, null, this);
        }

        // The releasing CPU thread does not relinquish native custody. The owner
        // retains the wait fence to the copy's retirement or proven teardown.
        internal void SettleNative(GpuDevice gpu)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_gpuOwner, gpu))
                    return;
                // A device-removal or exceptional teardown may settle before the watchdog.
                FinishUnderLock(finalAttempt: true);
                _submitted.TrySetCanceled();
                _waitFence->Release();
                _waitFence = null;
                _gpuOwner = null;
            }
        }
    }
}
