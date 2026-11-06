namespace Groundwork.Rendering.D3D12;

/// <summary>Optional native diagnostics applied before creating the Direct3D 12 device.</summary>
/// <remarks>Windows Graphics Tools provides the debug layer. These native settings apply to the process.
/// Request them before creating any Direct3D 12 device, including devices outside this provider.</remarks>
public readonly record struct D3D12DiagnosticsOptions(bool EnableDebugLayer = false, bool EnableDred = false);

/// <summary>The DXGI adapter selected by the renderer. Driver version is optional.</summary>
public sealed record D3D12AdapterInfo(string Description, uint VendorId, uint DeviceId, bool IsSoftware, string? DriverVersion);

/// <summary>Debug queue counts and the optional live-object report collected at teardown.</summary>
/// <remarks>The native report includes objects from any other owner sharing the same Direct3D 12 device.</remarks>
public sealed record D3D12DebugMessages(ulong Warnings, ulong Errors, ulong Corruption, ulong Discarded,
    int? LiveObjectReportResult, int? LiveChildObjects);

/// <summary>Bounded numeric diagnostics for a removed device. DRED query results are HRESULTs when available.</summary>
public sealed record D3D12DeviceRemoval(int Reason, int? BreadcrumbResult, int? PageFaultResult,
    IReadOnlyList<D3D12Breadcrumb> Breadcrumbs, bool? PageFaultAddressReported, IReadOnlyList<int> ExistingAllocationTypes,
    IReadOnlyList<int> RecentlyFreedAllocationTypes);

/// <summary>Progress for a DRED command list and the available operation codes around that position.</summary>
public sealed record D3D12Breadcrumb(uint Completed, uint Total, int? LastCompletedOperation, int? NextOperation);

/// <summary>The adapter and diagnostics observed by the render thread.</summary>
/// <remarks>Enablement flags indicate requested native settings available at startup. Debug queue results become
/// available when the render thread releases the GPU resources. Device removal may be reported without DRED data.</remarks>
public sealed record D3D12DiagnosticsSnapshot(D3D12AdapterInfo? Adapter, bool DebugLayerEnabled, bool DredEnabled,
    D3D12DebugMessages? DebugMessages, D3D12DeviceRemoval? DeviceRemoval);
