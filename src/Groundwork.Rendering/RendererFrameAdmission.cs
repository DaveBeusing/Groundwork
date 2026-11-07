namespace Groundwork.Rendering;

/// <summary>
/// Maintains one pending frame and coherent status for a renderer provider.
/// The provider owns its render wake, the current frame and all device resources.
/// </summary>
public sealed partial class RendererFrameAdmission
{
    private readonly Lock _gate = new();
    private RendererSnapshot _status;
    private RenderFrame? _pending;
    private ulong _takenSequence;
    private bool _takenFrameHasScene;
    private bool _pairedRegionFrames;

    public RendererSnapshot Read()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public void MarkInitialized()
    {
        lock (_gate)
        {
            RequireLifecycle(RendererLifecycle.Created);
            _status = _status with { Lifecycle = RendererLifecycle.Initialized };
        }
    }

    public void MarkRunning()
    {
        lock (_gate)
        {
            RequireLifecycle(RendererLifecycle.Initialized);
            MeshReplacement?.MarkRunning();
            MeshRegionReplacement?.MarkRunning();
            _status = _status with { Lifecycle = RendererLifecycle.Running };
        }
    }

    public RenderSubmissionResult TrySubmit(in RenderFrame frame) => TrySubmit(frame, pairedGeneration: 0);

    private RenderSubmissionResult TrySubmit(in RenderFrame frame, ulong pairedGeneration)
    {
        lock (_gate)
        {
            if (_status.Lifecycle is RendererLifecycle.Stopping or RendererLifecycle.Stopped)
            {
                return RenderSubmissionResult.Closed;
            }

            if (_status.Lifecycle != RendererLifecycle.Running)
            {
                return RenderSubmissionResult.NotRunning;
            }

            if (!frame.IsValid || (_pairedRegionFrames &&
                (!frame.Scene.HasValue || !MeshRegionReplacement!.CanUpdatePairedCamera(pairedGeneration))))
            {
                return RenderSubmissionResult.InvalidFrame;
            }

            if (frame.Sequence <= _status.LastAcceptedSequence)
            {
                return RenderSubmissionResult.StaleSequence;
            }

            var replaced = _pending.HasValue;
            _pending = frame;
            _status = _status with
            {
                LastAcceptedSequence = frame.Sequence,
                ReplacedFrames = replaced ? Increment(_status.ReplacedFrames) : _status.ReplacedFrames
            };
            return replaced ? RenderSubmissionResult.Replaced : RenderSubmissionResult.Accepted;
        }
    }

    /// <summary>Takes the pending value for the single render owner when the surface is drawable.</summary>
    public bool TryTakeLatest(out RenderFrame frame)
    {
        lock (_gate)
        {
            if (_status.Lifecycle == RendererLifecycle.Running && _status.Surface == RenderSurfaceState.Drawable &&
                _pending is { } latest)
            {
                frame = latest;
                _pending = null;
                _takenSequence = latest.Sequence;
                _takenFrameHasScene = latest.Scene.HasValue;
                return true;
            }

            frame = default;
            return false;
        }
    }

    /// <summary>Updates availability and applied extent. A non-drawable surface may retain its last extent.</summary>
    public bool TrySetSurface(RenderSurfaceState surface, int appliedWidth, int appliedHeight)
    {
        if (!Enum.IsDefined(surface) || appliedWidth < 0 || appliedHeight < 0 ||
            (appliedWidth == 0) != (appliedHeight == 0) ||
            (surface == RenderSurfaceState.Drawable && appliedWidth == 0))
        {
            throw new ArgumentException("Invalid surface state or extent.", nameof(surface));
        }

        lock (_gate)
        {
            if (_status.Lifecycle is not (RendererLifecycle.Initialized or RendererLifecycle.Running))
            {
                return false;
            }

            _status = _status with { Surface = surface, AppliedWidth = appliedWidth, AppliedHeight = appliedHeight };
            return true;
        }
    }

    /// <summary>Records a successful normal present of the render owner's current frame.</summary>
    /// <remarks>For an opted-in mesh scene, use the applicable geometry mailbox to record the present.</remarks>
    public void RecordPresented(ulong sequence)
    {
        lock (_gate)
        {
            if ((MeshReplacement is not null || MeshRegionReplacement is not null) && _takenFrameHasScene)
            {
                throw new InvalidOperationException("A mesh scene present must include its geometry generation.");
            }

            RecordPresentedCore(sequence);
        }
    }

    private void RecordPresentedCore(ulong sequence)
    {
        if (sequence == 0 || sequence != _takenSequence ||
            _status.Lifecycle is not (RendererLifecycle.Running or RendererLifecycle.Stopping))
        {
            throw new InvalidOperationException("Only the current frame may be recorded as presented.");
        }

        _status = _status with { LastPresentedSequence = sequence };
    }

    public void RecordSkipped()
    {
        lock (_gate)
        {
            _status = _status with { SkippedFrames = Increment(_status.SkippedFrames) };
        }
    }

    /// <summary>Closes admission and discards pending work. The provider settles any work it already took.</summary>
    public void BeginStop()
    {
        lock (_gate)
        {
            CloseAdmission();
        }
    }

    public void RecordTerminalFault(in RenderFaultSummary fault)
    {
        lock (_gate)
        {
            CloseAdmission();
            _status = _status with { FirstFault = _status.FirstFault ?? fault };
        }
    }

    public void RecordCleanupFault(in RenderFaultSummary fault, bool incomplete)
    {
        lock (_gate)
        {
            _status = _status with
            {
                FirstCleanupFault = _status.FirstCleanupFault ?? fault,
                IncompleteCleanup = _status.IncompleteCleanup || incomplete
            };
        }
    }

    /// <summary>Records proved release after stop. Previous failure evidence remains observable.</summary>
    public void MarkStopped()
    {
        lock (_gate)
        {
            if (_status.Lifecycle == RendererLifecycle.Stopped)
            {
                return;
            }

            RequireLifecycle(RendererLifecycle.Stopping);
            MeshReplacement?.MarkStopped();
            MeshRegionReplacement?.MarkStopped();
            _status = _status with { Lifecycle = RendererLifecycle.Stopped, AppliedWidth = 0, AppliedHeight = 0 };
        }
    }

    private void CloseAdmission()
    {
        if (_status.Lifecycle is not (RendererLifecycle.Stopping or RendererLifecycle.Stopped))
        {
            _status = _status with { Lifecycle = RendererLifecycle.Stopping, Surface = RenderSurfaceState.Unavailable };
            _pending = null;
            MeshReplacement?.Close();
            MeshRegionReplacement?.Close();
        }
    }

    private void RequireLifecycle(RendererLifecycle expected)
    {
        if (_status.Lifecycle != expected)
        {
            throw new InvalidOperationException($"Expected renderer lifecycle {expected}, got {_status.Lifecycle}.");
        }
    }

    private static ulong Increment(ulong count) => count == ulong.MaxValue ? count : count + 1;
}
