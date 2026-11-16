namespace Groundwork.Rendering.D3D12;

/// <summary>Per-device fault injection for owner tests. Public construction always uses native signals and observations.</summary>
/// <remarks>Callbacks run on the GPU owner. A simulated signal failure leaves retirement unproved.</remarks>
internal sealed record GpuRetirementTestHooks(
    Func<GpuDevice, ulong, int?>? SignalFailure = null,
    Func<GpuDevice, ulong, ulong>? CompletedValue = null,
    Func<GpuDevice, int?>? PresentFailure = null,
    Action<GpuDevice>? BeforeVolumeUpload = null,
    Func<ulong, bool>? RejectPreparedPlan = null,
    Func<ulong, bool>? RejectRecordedPlan = null,
    Action<ulong>? BeforeExecute = null,
    Func<bool, int?>? PbrPipelineCreationFailure = null,
    Action<ulong, int>? BeforeValidateRecordedBindings = null);
