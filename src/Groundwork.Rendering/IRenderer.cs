using Groundwork.Core;

namespace Groundwork.Rendering;

public enum RenderSubmissionResult
{
    Accepted = 1,
    Replaced,
    InvalidFrame,
    StaleSequence,
    NotRunning,
    Closed
}

public enum RendererLifecycle
{
    Created,
    Initialized,
    Running,
    Stopping,
    Stopped
}

public enum RenderSurfaceState
{
    Unavailable,
    Suspended,
    Drawable
}

/// <summary>Bounded numeric evidence reported by a renderer provider.</summary>
public readonly record struct RenderFaultSummary(uint Stage, int ErrorCode, int? DeviceRemovalReason, ulong? FrameSequence);

public readonly record struct RendererSnapshot(
    RendererLifecycle Lifecycle,
    RenderSurfaceState Surface,
    int AppliedWidth,
    int AppliedHeight,
    ulong LastAcceptedSequence,
    ulong LastPresentedSequence,
    ulong ReplacedFrames,
    ulong SkippedFrames,
    RenderFaultSummary? FirstFault,
    RenderFaultSummary? FirstCleanupFault,
    bool IncompleteCleanup);

public interface IRenderer : IEngineModule
{
    /// <summary>Copies a valid, newer frame into the provider's latest pending slot without a GPU wait.</summary>
    RenderSubmissionResult TrySubmit(in RenderFrame frame);

    /// <summary>Reads one coherent status. Presented denotes provider acceptance, without display completion.</summary>
    RendererSnapshot Read();
}
