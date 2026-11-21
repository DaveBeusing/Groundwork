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
    Action<ulong, int>? BeforeValidateRecordedBindings = null,
    Action<ulong, int>? AfterNativePresent = null,
    Action? BeforeSkyRecord = null,
    Action? AfterSkyRecord = null,
    Action? SkyDrawRecorded = null,
    Action<GpuDevice>? AfterDeviceInitialize = null,
    Action<ulong, ulong, double, double, bool>? AfterSkyTimingRetired = null,
    Action<ulong, ulong, double, long, int>? AfterSkyRecordMeasured = null,
    Action<ulong, long>? AfterSuccessfulPresent = null,
    Action<long, long, bool>? AfterSkyPipelineReady = null,
    Action<ulong, ulong, ulong, ulong, ulong, ulong, ulong>? AfterSkyTimingDetailsRetired = null,
    Action<ulong, ulong, ulong>? AfterSkyClockCalibration = null,
    Action? AfterSkyLookupFirstAllocation = null,
    Action<ulong, ulong, double, double>? AfterSkyLookupGpuTimingRetired = null,
    Action<ulong, ulong, double, double>? AfterSkyLookupRecordMeasured = null)
{
    internal const int SkyQualificationHookSchemaVersion = 2;
}
