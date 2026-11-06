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

            gpu = new GpuDevice(_useWarp, _diagnosticsOptions.EnableDebugLayer, _diagnosticsOptions.EnableDred,
                Interlocked.Exchange(ref _meshes, null));
            Volatile.Write(ref _diagnostics, new D3D12DiagnosticsSnapshot(gpu.Adapter, gpu.DebugLayerEnabled,
                gpu.DredEnabled, null, null));
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
            if ((!Stopping && _meshRuntimeFence is not { IsOpen: false }) || destroyed)
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
            _meshes = null;
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

            if (_admission.MeshReplacement is { } meshes && meshes.TryTake(out var candidate))
            {
                stage = RendererStage.MeshReplacement;
                frameSequence = current?.Sequence;
                if (!watchdog.Begin(stage, frameSequence, _operationTimeout))
                    break;
                var installed = TryInstallMesh(gpu, target, chain!, watchdog, meshes, candidate);
                if (!watchdog.Complete() || ShouldStop(target))
                    break;
                redraw |= installed;

                // Frames can arrive while the upload is in progress, including
                // a clear-only frame. Preserve the normal latest-only order.
                if (_admission.TryTakeLatest(out latest))
                {
                    current = latest;
                    redraw = true;
                }
                if (!redraw)
                {
                    _wake!.WaitOne(ProgressPollMilliseconds);
                    continue;
                }
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
                if (frame.Scene.HasValue && _admission.MeshReplacement is { } meshAdmission)
                    meshAdmission.RecordPresented(frame.Sequence, meshAdmission.ReadMeshReplacement().InstalledGeneration);
                else
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

    private bool TryInstallMesh(GpuDevice gpu, WindowsPresentationTargetLease target, WindowsSwapChain chain,
        RendererWatchdog watchdog, RendererFrameAdmission.MeshPairAdmission meshes,
        RendererFrameAdmission.MeshPairAdmission.Candidate candidate)
    {
        GpuDevice.MeshGeometry? prepared = null;
        var retry = false;
        try
        {
            using var operation = TryBegin(target);
            if (operation is null || !Matches(operation.Snapshot, chain) ||
                !CanCommitMesh(target, operation.Snapshot, watchdog))
            {
                retry = true;
                return false;
            }

            if (gpu.IsRemoved)
                throw new InvalidOperationException("The Direct3D 12 device was removed before mesh replacement.");
            if (!gpu.IsIdle)
                throw new InvalidOperationException("Cannot replace geometry before GPU retirement.");

            prepared = gpu.PrepareMeshGeometry(candidate.Pair);
            _beforeMeshInstallation?.Invoke(gpu);
            var installed = meshes.TryInstall<GpuDevice.MeshGeometry?>(candidate, ref gpu.ActiveMeshGeometry, ref prepared,
                () => CanCommitMesh(target, operation.Snapshot, watchdog));
            if (installed)
                _afterMeshInstallation?.Invoke();
            retry = !installed;
            return installed;
        }
        finally
        {
            // On success this is the retired old handle. Otherwise this is
            // private, unsubmitted geometry. Neither needs a new queue signal.
            prepared?.Dispose();
            meshes.ReleaseCandidate(candidate, retry);
        }
    }

    private bool CanCommitMesh(WindowsPresentationTargetLease target, HostWindowSnapshot admitted,
        RendererWatchdog watchdog)
    {
        // The window publishes revisions monotonically. If the subsequent
        // terminal fence and deadline checks pass, they were also open at
        // this snapshot read, which is the commit point. All reads are
        // nonblocking and the portable gate serializes local stop and intent.
        var snapshot = _window.Read();
        return snapshot.Revision == admitted.Revision && IsDrawable(snapshot) && !target.Revoked.IsCompleted &&
            _meshRuntimeFence!.IsOpen && watchdog.IsWithinDeadline;
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

            // Stop has already closed admission. Keep the device and lifetime lease during diagnostics.
            if (gpu?.CaptureDred() is { } removal)
                Volatile.Write(ref _diagnostics, _diagnostics with { DeviceRemoval = removal });

            watchdog.Dispose();
            chain?.Dispose();
            target?.Dispose();
            gpu?.Dispose();
            if (gpu?.FinalDebugSummary is { } debugMessages)
                Volatile.Write(ref _diagnostics, _diagnostics with { DebugMessages = debugMessages });
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

    private WindowsPresentationOperation? TryBegin(WindowsPresentationTargetLease target)
    {
        if (Stopping)
            return null;

        var operation = target.TryBeginOperation();
        if (operation is not null && _meshRuntimeFence is { } fence && (Stopping || !fence.IsOpen))
        {
            // The window permit was acquired first. Both renderer fences must
            // still be open to use it, even when a test isolates the authorities.
            operation.Dispose();
            return null;
        }

        return operation;
    }

    private bool ShouldStop(WindowsPresentationTargetLease target)
    {
        if (target.Revoked.IsCompletedSuccessfully && target.Revoked.Result == PresentationTargetRevocation.NativeDestroyed)
            Fail(RendererStage.Present, new InvalidOperationException("The native presentation window was destroyed."), null, null);
        return Stopping || target.Revoked.IsCompleted || _meshRuntimeFence is { IsOpen: false };
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
