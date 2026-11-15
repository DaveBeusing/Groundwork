using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    internal bool HasResourceDiagnostics => _resourceDiagnostics is not null;

    internal void ObserveResource(ID3D12Resource* resource, D3D12ResourceOwner owner, ulong allocatedBytes = 0)
    {
        if (_resourceDiagnostics is not { } diagnostics)
            return;
        if (allocatedBytes == 0)
        {
            var description = resource->GetDesc();
            allocatedBytes = _device->GetResourceAllocationInfo(1, 1, &description).SizeInBytes;
        }
        diagnostics.Track((nint)resource, owner, allocatedBytes);
    }

    internal void ReleaseObservedResource(ID3D12Resource* resource)
    {
        Release(resource);
        _resourceDiagnostics?.Release((nint)resource);
    }

    internal void ObservePresentStart(ulong fence, GpuTarget target) =>
        _resourceDiagnostics?.BeginPresent(fence, (nint)target.Buffer);

    internal void ObservePresentSignal(ulong fence) => _resourceDiagnostics?.Signal(fence, draw: false);

    internal void ObservePresented(ulong fence) => _resourceDiagnostics?.Presented(fence);

    internal D3D12CustodyStatus ObserveTerminalCustody(bool confirmedRemoval)
    {
        var custody = ReadObservedCustody();
        if (confirmedRemoval)
            custody |= D3D12CustodyStatus.ConfirmedRemoval;
        _resourceDiagnostics?.NoteCustody(custody);
        return custody;
    }

    internal D3D12CustodyStatus ReadObservedCustody() =>
        (_retirement.AwaitingSignal ? D3D12CustodyStatus.UnassignedSubmission : D3D12CustodyStatus.None) |
        (_observedFenceSentinel ? D3D12CustodyStatus.FenceSentinelObserved : D3D12CustodyStatus.None);

    private void ObserveUse(ID3D12Resource* resource) => _resourceDiagnostics?.Use((nint)resource);

    private void RecordTransition(ID3D12GraphicsCommandList* commands, ID3D12Resource* resource,
        D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        Transition(commands, resource, before, after);
        _resourceDiagnostics?.Barrier();
    }
}
