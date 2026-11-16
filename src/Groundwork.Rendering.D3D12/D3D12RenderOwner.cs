using System.Diagnostics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12;

public sealed partial class D3D12Renderer
{
    // Tests opt in before startup. The callback observes the exact retired back buffer
    // submitted to DXGI, together with the sequence and installed volume generation.
    internal Action<ulong, ulong, OffscreenFrame>? VolumePresentReadbackForTests { private get; init; }
    internal Action<D3D12Renderer>? BeforeRetrySelectionForTests { private get; init; }
    internal Func<long>? FirstContentTimestampForTests { private get; init; }

    private D3D12PropCells? _props;
    // The render owner sets this only during the synchronous submit attempt for that registration.
    private FirstContentRetry? _submittingFirstContentRetry;

    private long FirstContentTimestamp() => FirstContentTimestampForTests?.Invoke() ?? Stopwatch.GetTimestamp();

    /// <summary>Bind a dedicated prop stream before starting a volume renderer.</summary>
    public void AttachPropCells(D3D12PropCells stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (_lifecycle)
        lock (stream.Gate)
        {
            if (_volume is null || _threadStarted || Stopping || _props is not null || stream.Attached || stream.Closed)
                throw new InvalidOperationException("Attach a fresh prop stream before starting a volume renderer.");
            stream.BindWorld(_volume.WorldIdentity);
            stream.Attached = true;
            _props = stream;
        }
    }

    private const int MaximumExtent = 4096;
    private const int ProgressPollMilliseconds = 10;
    private static readonly TimeSpan OcclusionInterval = TimeSpan.FromMilliseconds(100);

    private enum OptionalVisualWork { None, FirstUseTexture, TextureRefinement, Props }

    private struct OptionalVisualArbitration
    {
        private bool _propsDue;
        private int _firstUseTurns;
        private const int FirstUseWeight = 2;

        internal readonly OptionalVisualWork Select(bool firstUse, bool refinement, bool props)
        {
            if (props && (_propsDue || (!firstUse && !refinement)))
                return OptionalVisualWork.Props;
            if (firstUse && (!refinement || _firstUseTurns < FirstUseWeight))
                return OptionalVisualWork.FirstUseTexture;
            return refinement ? OptionalVisualWork.TextureRefinement : OptionalVisualWork.None;
        }

        internal void Consume(OptionalVisualWork work, bool propsEligible)
        {
            if (work == OptionalVisualWork.Props)
            {
                _propsDue = false;
                return;
            }

            // Texture-only or suspended turns cannot erase a prop's next compatible turn.
            _propsDue |= propsEligible;
            _firstUseTurns = work == OptionalVisualWork.FirstUseTexture
                ? Math.Min(_firstUseTurns + 1, FirstUseWeight) : 0;
        }
    }

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
                retirementHooks: _retirementHooks, region: Interlocked.Exchange(ref _initialGeometry, null),
                session: _deviceSession, resourceDiagnostics: _resourceDiagnostics);
            Volatile.Write(ref _observedGpu, gpu);
            gpu.PropStream = _props;
            if (_textures is { } textures)
                _textureOwner = new(textures, gpu);
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
            var removed = gpu?.RemovedReason;
            ObserveTerminalGpuCustody(gpu, removed);
            if ((!Stopping && _meshRuntimeFence is not { IsOpen: false }) || destroyed)
            {
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
            _initialGeometry = null;
            _props?.Close();
            CloseAdmission();
            _textures?.Close();
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
        RenderFrame? volumePresented = null;
        FirstContentRetry? retry = null;
        var executedFirst = 0UL;
        var occluded = false;
        var lastProbe = 0L;
        var visualArbitration = new OptionalVisualArbitration();
        var servicedTextureBeforeFrame = false;

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
                    // A superseding mask can be in flight while an old retry expires.
                    // Its deadline cannot bypass retirement or make the fence poll spin.
                    _wake!.WaitOne(ProgressPollMilliseconds);
                    continue;
                }

                if (!watchdog.Complete())
                    break;
                pendingRetirement = false;
                if (ShouldStop(target))
                    break;
                if (volumePresented is { } retired)
                {
                    RecordRetiredVolumeFrame(retired);
                    if (retired.Scene.HasValue && VolumePresentReadbackForTests is { } capture)
                        capture(retired.Sequence, _volumeInstalled!.Info.Generation, gpu.ReadRetiredPresent());
                    volumePresented = null;
                }
            }

            if (_textureOwner is { HasSubmittedCopy: true } textures)
            {
                stage = RendererStage.TextureUpload;
                if (!textures.TryRetire(CurrentTextureView()))
                {
                    // Texture copies have their own progress deadline and retire
                    // independently of camera, simulation and surface events.
                    _wake!.WaitOne(ProgressPollMilliseconds);
                    continue;
                }
                PublishTextureDiagnostics(gpu);
                if (!watchdog.Complete())
                    break;
            }

            if (retry is { } waiting && (!CanRetryFirstContent(waiting.Frame) ||
                waiting.IsExpired(FirstContentTimestamp(), _operationTimeout)))
            {
                SettleRejectedRegistration(gpu, waiting.Frame);
                if (current?.Sequence == waiting.Frame.Frame)
                {
                    current = null;
                    redraw = false;
                }
                retry = null;
            }

            if (!ServiceVolumeStage(gpu, watchdog, ref stage))
                break;

            if (!IsDrawable(snapshot))
            {
                SetSurface(RenderSurfaceState.Suspended, chain);
                redraw |= current.HasValue;
                RefreshTextures(gpu);
                var work = SelectOptionalVisualWork(visualArbitration, gpu, target, retry, null, out _);
                if (!ServiceOptionalTextures(gpu, target, watchdog, retry, ref stage, work, out var spentTurn))
                    break;
                if (spentTurn)
                    visualArbitration.Consume(work, propsEligible: false);
                WaitForFrame(_textureOwner is { HasSubmittedCopy: true }
                    ? ProgressPollMilliseconds : Timeout.Infinite);
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
                    WaitForFrame(ProgressPollMilliseconds);
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
                    WaitForFrame(ProgressPollMilliseconds);
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
            if (retry.HasValue)
                BeforeRetrySelectionForTests?.Invoke(this);
            if (_admission.TryTakeLatest(out var latest))
            {
                current = latest;
                redraw = true;
            }

            if (RefreshTextures(gpu))
                redraw |= current is { Scene: not null };

            if (_admission.MeshRegionReplacement is { } meshes && meshes.TryTake(out var candidate))
            {
                stage = RendererStage.MeshReplacement;
                frameSequence = current?.Sequence;
                if (!watchdog.Begin(stage, frameSequence, _operationTimeout))
                    break;
                var installed = TryInstallMesh(gpu, target, chain!, watchdog, meshes, candidate);
                if (!watchdog.Complete() || ShouldStop(target))
                    break;
                if (installed && candidate.Frame is { } pairedFrame)
                    current = pairedFrame;
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
                    WaitForFrame(ProgressPollMilliseconds);
                    continue;
                }
            }

            if (!redraw || current is not { } frame)
            {
                servicedTextureBeforeFrame = false;
                var work = SelectOptionalVisualWork(visualArbitration, gpu, target, retry, null, out _);
                if (!ServiceOptionalTextures(gpu, target, watchdog, retry, ref stage, work, out var spentTurn))
                    break;
                if (spentTurn)
                    visualArbitration.Consume(work, propsEligible: false);
                WaitForFrame(_textureOwner is { HasSubmittedCopy: true }
                    ? ProgressPollMilliseconds : Timeout.Infinite);
                continue;
            }

            if (!CanDrawVolume(frame))
            {
                if (GetUnpresentedRegistration(frame) is { } revoked)
                {
                    SettleRejectedRegistration(gpu, revoked);
                    ClearRetry(revoked);
                }
                redraw = false;
                servicedTextureBeforeFrame = false;
                continue;
            }

            if (!chain!.CanBeginFrame())
            {
                WaitForFrame(ProgressPollMilliseconds);
                continue;
            }

            var visualWork = OptionalVisualWork.None;
            if (!servicedTextureBeforeFrame && frame.Scene is { } scene)
            {
                visualWork = SelectOptionalVisualWork(visualArbitration, gpu, target, retry, scene, out var propsEligible);
                if (visualWork is OptionalVisualWork.FirstUseTexture or OptionalVisualWork.TextureRefinement)
                {
                    if (!ServiceOptionalTextures(gpu, target, watchdog, retry, ref stage, visualWork, out var spentTurn))
                        break;
                    if (spentTurn)
                    {
                        visualArbitration.Consume(visualWork, propsEligible);
                        servicedTextureBeforeFrame = true;
                        // Retirement, terrain staging, and newest-frame selection all run
                        // again before recording the draw that follows this copy.
                        continue;
                    }
                    visualWork = propsEligible ? OptionalVisualWork.Props : OptionalVisualWork.None;
                }
            }

            frameSequence = frame.Sequence;
            stage = RendererStage.Submit;
            if (!watchdog.Begin(stage, frameSequence, _operationTimeout))
                break;

            var firstContent = GetUnpresentedRegistration(frame);
            var selectedRetry = retry is { } pending && ReferenceEquals(pending.Frame, firstContent) ? pending : (FirstContentRetry?)null;
            var allowPropUploads = !servicedTextureBeforeFrame || retry.HasValue || HasProtectedTextureCompetition();
            var submission = TrySubmitFrame(gpu, target, chain, watchdog, frame, selectedRetry, allowPropUploads);
            if (submission != GpuFrameSubmitResult.Submitted)
            {
                if (!watchdog.Complete())
                    break;
                _admission.RecordSkipped();
                if (submission == GpuFrameSubmitResult.Rejected)
                {
                    if (firstContent is not null && executedFirst != frame.Sequence && CanRetryFirstContent(firstContent))
                    {
                        if (retry is { } previous && !ReferenceEquals(previous.Frame, firstContent))
                            SettleRejectedRegistration(gpu, previous.Frame);
                        retry = selectedRetry?.RejectAgain() ?? new FirstContentRetry(firstContent, FirstContentTimestamp(), 1);
                        if (retry.Value.IsExpired(FirstContentTimestamp(), _operationTimeout))
                            continue;
                        // Retry on a real owner wake or settle on the monotonic deadline. Avoid
                        // a normal progress poll that would run the rejected plan repeatedly.
                        WaitForFrame();
                    }
                    else
                    {
                        if (firstContent is not null)
                        {
                            SettleRejectedRegistration(gpu, firstContent);
                            ClearRetry(firstContent);
                        }
                        redraw = false;
                    }
                }
                else
                    WaitForFrame(ProgressPollMilliseconds);
                continue;
            }

            servicedTextureBeforeFrame = false;
            if (visualWork == OptionalVisualWork.Props)
                visualArbitration.Consume(visualWork, propsEligible: true);

            if (firstContent is not null)
            {
                executedFirst = frame.Sequence;
                ClearRetry(firstContent);
            }
            pendingRetirement = true;
            stage = RendererStage.Present;
            var result = TryPresent(target, chain, watchdog, testOnly: false);
            if (!watchdog.TryAdvance(RendererStage.Retire))
                break;
            if (result == PresentResult.Presented)
            {
                if (_volume is not null)
                    volumePresented = frame;
                else if (frame.Scene.HasValue && _admission.MeshRegionReplacement is { } meshAdmission)
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

        void WaitForFrame(int maximum = Timeout.Infinite)
        {
            var timeout = retry?.WaitMilliseconds(FirstContentTimestamp(), _operationTimeout) ?? maximum;
            if (maximum != Timeout.Infinite)
                timeout = Math.Min(maximum, timeout);
            _wake!.WaitOne(timeout);
        }

        // A newer neutral mask may submit while the old registration still holds its
        // native mesh and reader. Keep that retry until its registration settles at idle.
        void ClearRetry(VolumeFrameRegistration registration)
        {
            if (retry is { } pending && ReferenceEquals(pending.Frame, registration))
                retry = null;
        }
    }

    private bool TryInstallMesh(GpuDevice gpu, WindowsPresentationTargetLease target, WindowsSwapChain chain,
        RendererWatchdog watchdog, RendererFrameAdmission.MeshRegionAdmission meshes,
        RendererFrameAdmission.MeshRegionAdmission.Candidate candidate)
    {
        GpuDevice.MeshGeometry? prepared = null;
        var retry = false;
        var installed = false;
        try
        {
            if (!CanInstallVolume(candidate.Generation))
                return false;
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

            prepared = _volume is null ? gpu.PrepareMeshGeometry(candidate.Region) : GetVolumeNativeCandidate(candidate.Generation);
            if (prepared is null)
                return false;
            _beforeMeshInstallation?.Invoke(gpu);
            installed = meshes.TryInstall<GpuDevice.MeshGeometry?>(candidate, ref gpu.ActiveMeshGeometry, ref prepared,
                () => CanCommitMesh(target, operation.Snapshot, watchdog) && CanInstallVolume(candidate.Generation));
            if (installed)
                _afterMeshInstallation?.Invoke();
            retry = !installed && CanInstallVolume(candidate.Generation);
            return installed;
        }
        finally
        {
            // On success this is the retired old handle. Otherwise this is
            // private, unsubmitted geometry. Neither needs a new queue signal.
            if (_volume is null || installed)
                prepared?.Dispose();
            meshes.ReleaseCandidate(candidate, retry);
            SettleVolumeCandidate(candidate.Generation, installed, retry);
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
            {
                ObserveTerminalGpuCustody(gpu, removal.Reason);
                Volatile.Write(ref _diagnostics, _diagnostics with { DeviceRemoval = removal });
            }

            watchdog.Dispose();
            chain?.Dispose();
            target?.Dispose();
            ReleasePrivateVolumeCustody();
            gpu?.Dispose();
            _textureOwner?.SettleAfterDeviceDisposal();
            if (_textureOwner is not null && gpu is not null)
                PublishTextureDiagnostics(gpu);
            _textureOwner = null;
            ReleaseVolumeCustody();
            Volatile.Write(ref _observedGpu, null);
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

    private OptionalVisualWork SelectOptionalVisualWork(OptionalVisualArbitration arbitration, GpuDevice gpu,
        WindowsPresentationTargetLease target, FirstContentRetry? retry, RenderScene? scene, out bool propsEligible)
    {
        propsEligible = false;
        if (!CanServiceOptionalTextures(gpu, target, retry) || _textureOwner is not { } textures)
            return OptionalVisualWork.None;

        var view = CurrentTextureView();
        var firstUse = textures.HasReady(view, firstUse: true);
        var refinement = textures.HasReady(view, firstUse: false);
        propsEligible = scene is { } drawable && _volumeInstalled is { } installed &&
            gpu.HasReadyPropUploads(drawable, installed.Info);
        return arbitration.Select(firstUse, refinement, propsEligible);
    }

    private GpuFrameSubmitResult TrySubmitFrame(GpuDevice gpu, WindowsPresentationTargetLease target, WindowsSwapChain chain,
        RendererWatchdog watchdog, in RenderFrame frame, FirstContentRetry? retry, bool allowPropUploads)
    {
        using var operation = TryBegin(target);
        if (operation is null || !Matches(operation.Snapshot, chain) || !watchdog.TryAdvance(RendererStage.Submit))
            return GpuFrameSubmitResult.Busy;
        if (!CanDrawVolume(frame))
            return GpuFrameSubmitResult.Rejected;
        gpu.PropTerrain = frame.Scene.HasValue ? _volumeInstalled?.Info : null;
        CaptureFrameTexture(gpu, frame);
        _submittingFirstContentRetry = retry;
        gpu.AllowPropUploads = allowPropUploads;
        try
        {
            return frame.Scene.HasValue && VolumePresentReadbackForTests is not null
                ? gpu.TryRenderWithPresentReadback(chain.CurrentBuffer, frame, this)
                : gpu.TryRender(chain.CurrentBuffer, frame, this);
        }
        finally
        {
            gpu.AllowPropUploads = true;
            _submittingFirstContentRetry = null;
            ClearFrameTexture(gpu);
        }
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
