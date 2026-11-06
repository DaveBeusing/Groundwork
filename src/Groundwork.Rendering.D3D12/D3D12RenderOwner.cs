using System.Diagnostics;
using Groundwork.Core;
using Groundwork.Platform.Windows;

namespace Groundwork.Rendering.D3D12;

public sealed partial class D3D12Renderer
{
    private const int MaximumExtent = 4096;
    private const int ProgressPollMilliseconds = 10;
    private static readonly TimeSpan OcclusionInterval = TimeSpan.FromMilliseconds(100);

    private void RunOwner(RendererWatchdog watchdog)
    {
        IRuntimeWorkLease? lifetime = null;
        WindowsPresentationTargetLease? target = null;
        GpuDevice? gpu = null;
        WindowsSwapChain? chain = null;
        var stage = RendererStage.Startup;
        ulong? frameSequence = null;

        try
        {
            if (Stopping)
                return;

            lifetime = _ownership.TryTrack(this)
                ?? throw new InvalidOperationException("The runtime denied the renderer's lifetime lease.");
            target = _window.TryAcquirePresentationTarget()
                ?? throw new InvalidOperationException("An open and unattached Windows presentation target is required.");
            if (ShouldStop(target) || !watchdog.TryAdvance(stage))
                return;

            gpu = new GpuDevice(_useWarp);
            var snapshot = target.Read();
            if (IsDrawable(snapshot) && ApplySurface(gpu, target, watchdog, stage, ref chain) == SurfaceApplication.Denied && !ShouldStop(target))
                throw new InvalidOperationException("The runtime denied initial swap-chain admission.");

            if (!watchdog.Complete() || ShouldStop(target))
                return;
            lock (_lifecycle)
            {
                if (Stopping || target.Revoked.IsCompleted)
                    return;

                _admission.TrySetSurface(chain is null ? RenderSurfaceState.Suspended : RenderSurfaceState.Drawable,
                    chain?.Width ?? 0, chain?.Height ?? 0);
                _admission.MarkRunning();
                Volatile.Write(ref _startupSucceeded, true);
                _ready.TrySetResult();
            }

            RunFrames(gpu, target, watchdog, ref chain, ref stage, ref frameSequence);
        }
        catch (Exception error)
        {
            // A revoked HWND can invalidate an operation between the grant and handle access.
            var destroyed = target?.Revoked.IsCompletedSuccessfully == true &&
                target.Revoked.Result == PresentationTargetRevocation.NativeDestroyed;
            if (!Stopping || destroyed)
            {
                var removed = gpu?.RemovedReason;
                Fail(stage, error, removed < 0 ? removed : null, frameSequence);
            }
            else
            {
                _admission.RecordCleanupFault(new RenderFaultSummary((uint)RendererStage.Cleanup,
                    error.HResult, null, frameSequence), incomplete: false);
            }
        }
        finally
        {
            CloseAdmission();
            _ready.TrySetResult();
            SettleOwner(gpu, chain, target, lifetime, watchdog);
        }
    }

    private void RunFrames(GpuDevice gpu, WindowsPresentationTargetLease target, RendererWatchdog watchdog,
        ref WindowsSwapChain? chain, ref RendererStage stage, ref ulong? frameSequence)
    {
        Task? targetChange = null;
        RenderFrame? current = null;
        var redraw = false;
        var pendingRetirement = false;
        var occluded = false;
        var lastProbe = 0L;

        while (!ShouldStop(target))
        {
            var snapshot = target.Read();
            ObserveTargetChanges(target, snapshot.Revision, ref targetChange);

            if (pendingRetirement)
            {
                stage = RendererStage.Retire;
                if (gpu.IsRemoved)
                    throw new InvalidOperationException("The Direct3D 12 device was removed before frame retirement.");
                if (!gpu.IsIdle)
                {
                    _wake!.WaitOne(ProgressPollMilliseconds);
                    continue;
                }

                if (!watchdog.Complete())
                    break;
                pendingRetirement = false;
                if (ShouldStop(target))
                    break;
            }

            if (!IsDrawable(snapshot))
            {
                SetSurface(RenderSurfaceState.Suspended, chain);
                redraw |= current.HasValue;
                _wake!.WaitOne();
                continue;
            }

            if (chain is null || !Matches(snapshot, chain))
            {
                stage = RendererStage.Resize;
                if (!watchdog.Begin(stage, null, _operationTimeout))
                    break;
                var applied = ApplySurface(gpu, target, watchdog, stage, ref chain);
                if (!watchdog.Complete())
                    break;
                if (applied != SurfaceApplication.Applied)
                {
                    _wake!.WaitOne(ProgressPollMilliseconds);
                    continue;
                }

                occluded = false;
                redraw |= current.HasValue;
            }

            if (ShouldStop(target))
                break;

            if (occluded)
            {
                SetSurface(RenderSurfaceState.Suspended, chain);
                if (Stopwatch.GetElapsedTime(lastProbe) < OcclusionInterval)
                {
                    _wake!.WaitOne(ProgressPollMilliseconds);
                    continue;
                }

                lastProbe = Stopwatch.GetTimestamp();
                stage = RendererStage.Visibility;
                if (!watchdog.Begin(stage, null, _operationTimeout))
                    break;
                occluded = TryPresent(target, chain!, watchdog, testOnly: true) != PresentResult.Presented;
                if (!watchdog.Complete())
                    break;
                if (occluded)
                    continue;
                redraw |= current.HasValue;
            }

            SetSurface(RenderSurfaceState.Drawable, chain);
            if (_admission.TryTakeLatest(out var latest))
            {
                current = latest;
                redraw = true;
            }

            if (!redraw || current is not { } frame)
            {
                _wake!.WaitOne();
                continue;
            }

            if (!chain!.CanBeginFrame())
            {
                _wake!.WaitOne(ProgressPollMilliseconds);
                continue;
            }

            frameSequence = frame.Sequence;
            stage = RendererStage.Submit;
            if (!watchdog.Begin(stage, frameSequence, _operationTimeout))
                break;

            if (!TrySubmitFrame(gpu, target, chain, watchdog, frame))
            {
                if (!watchdog.Complete())
                    break;
                _admission.RecordSkipped();
                _wake!.WaitOne(ProgressPollMilliseconds);
                continue;
            }

            pendingRetirement = true;
            stage = RendererStage.Present;
            var result = TryPresent(target, chain, watchdog, testOnly: false);
            if (!watchdog.TryAdvance(RendererStage.Retire))
                break;
            if (result == PresentResult.Presented)
            {
                _admission.RecordPresented(frame.Sequence);
                redraw = false;
            }
            else
            {
                _admission.RecordSkipped();
                occluded = result == PresentResult.Occluded;
                if (occluded)
                    lastProbe = Stopwatch.GetTimestamp();
            }

        }
    }

    private void ObserveTargetChanges(WindowsPresentationTargetLease target, ulong revision, ref Task? notification)
    {
        if (notification is { IsCompleted: false })
            return;

        notification = target.WaitForChangeAsync(revision).AsTask();
        _ = notification.ContinueWith(static (_, state) => ((D3D12Renderer)state!).Wake(), this,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void SettleOwner(GpuDevice? gpu, WindowsSwapChain? chain, WindowsPresentationTargetLease? target,
        IRuntimeWorkLease? lifetime, RendererWatchdog watchdog)
    {
        try
        {
            // Signaling already-submitted work occurs in GpuDevice before it returns.
            // An unretirable submission stays owned until device removal proves quiescence.
            while (gpu is not null && !gpu.IsRemoved && !gpu.IsIdle)
                _wake!.WaitOne(ProgressPollMilliseconds);

            watchdog.Dispose();
            chain?.Dispose();
            target?.Dispose();
            gpu?.Dispose();
            lifetime?.Dispose();
            Finish();
        }
        catch (Exception cleanup)
        {
            _custody = new RenderCustody(gpu, chain, target, lifetime, watchdog);
            RecordIncomplete(cleanup);
        }
    }

    private SurfaceApplication ApplySurface(GpuDevice gpu, WindowsPresentationTargetLease target,
        RendererWatchdog watchdog, RendererStage stage, ref WindowsSwapChain? chain)
    {
        using var operation = TryBegin(target);
        if (operation is null)
            return SurfaceApplication.Denied;
        if (!IsDrawable(operation.Snapshot) || !watchdog.TryAdvance(stage))
            return SurfaceApplication.Suspended;

        var snapshot = operation.Snapshot;
        if (chain is null)
            chain = new WindowsSwapChain(gpu, operation, snapshot.ClientWidth, snapshot.ClientHeight);
        else if (!Matches(snapshot, chain))
        {
            if (_resize is { } resize)
                resize(chain, snapshot.ClientWidth, snapshot.ClientHeight);
            else
                chain.Resize(snapshot.ClientWidth, snapshot.ClientHeight);
        }
        return SurfaceApplication.Applied;
    }

    private enum SurfaceApplication { Denied, Suspended, Applied }

    private bool TrySubmitFrame(GpuDevice gpu, WindowsPresentationTargetLease target, WindowsSwapChain chain,
        RendererWatchdog watchdog, in RenderFrame frame)
    {
        using var operation = TryBegin(target);
        return operation is not null && Matches(operation.Snapshot, chain) && watchdog.TryAdvance(RendererStage.Submit) &&
            gpu.TryRender(chain.CurrentBuffer, frame);
    }

    private PresentResult TryPresent(WindowsPresentationTargetLease target, WindowsSwapChain chain,
        RendererWatchdog watchdog, bool testOnly)
    {
        using var operation = TryBegin(target);
        return operation is not null && Matches(operation.Snapshot, chain) &&
            watchdog.TryAdvance(testOnly ? RendererStage.Visibility : RendererStage.Present)
            ? chain.Present(testOnly) : PresentResult.Busy;
    }

    private WindowsPresentationOperation? TryBegin(WindowsPresentationTargetLease target) =>
        Stopping ? null : target.TryBeginOperation();

    private bool ShouldStop(WindowsPresentationTargetLease target)
    {
        if (target.Revoked.IsCompletedSuccessfully && target.Revoked.Result == PresentationTargetRevocation.NativeDestroyed)
            Fail(RendererStage.Present, new InvalidOperationException("The native presentation window was destroyed."), null, null);
        return Stopping || target.Revoked.IsCompleted;
    }

    private bool Stopping => _admission.Read().Lifecycle is RendererLifecycle.Stopping or RendererLifecycle.Stopped;

    private void SetSurface(RenderSurfaceState state, WindowsSwapChain? chain) =>
        _admission.TrySetSurface(state, chain?.Width ?? 0, chain?.Height ?? 0);

    private static bool IsDrawable(HostWindowSnapshot snapshot) => snapshot.Phase == HostWindowPhase.Open &&
        snapshot.ClientWidth is > 0 and <= MaximumExtent && snapshot.ClientHeight is > 0 and <= MaximumExtent;

    private static bool Matches(HostWindowSnapshot snapshot, WindowsSwapChain chain) =>
        IsDrawable(snapshot) && snapshot.ClientWidth == chain.Width && snapshot.ClientHeight == chain.Height;

    // The lifetime credit retains these references if teardown itself faults.
    private sealed record RenderCustody(GpuDevice? Gpu, WindowsSwapChain? Chain, WindowsPresentationTargetLease? Target,
        IRuntimeWorkLease? Lifetime, RendererWatchdog Watchdog);
}
