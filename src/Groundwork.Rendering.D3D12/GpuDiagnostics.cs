using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Groundwork.Rendering.D3D12.GpuNative;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    // System debug settings apply to the process. Enabling the debug layer after
    // creating a device can remove that device.
    private static readonly object DiagnosticStartup = new();
    private static bool _openedDevice;
    private static int _activeDevices;

    internal static void CountDeviceReference() => Interlocked.Increment(ref _activeDevices);
    internal static void UncountDeviceReference() => Interlocked.Decrement(ref _activeDevices);
    private static bool _debugLayerEnabled;
    private static bool _dredEnabled;
    private const int DredEntryLimit = 16;
    private const uint BreadcrumbHistoryLength = 65536;
    private const int DebugMessageLimit = 1024;
    private const int DebugMessageBytes = 8192;

    internal D3D12AdapterInfo Adapter { get; private set; } = null!;
    internal bool DebugLayerEnabled { get; }
    internal bool DredEnabled { get; }
    internal D3D12DebugMessages? FinalDebugSummary { get; private set; }

    private static bool TryEnableDebugLayer()
    {
        ID3D12Debug* debug = null;
        try
        {
            if (GetDebugInterface(__uuidof<ID3D12Debug>(), (void**)&debug) < 0)
                return false;

            debug->EnableDebugLayer();
            return true;
        }
        finally
        {
            Release(debug);
        }
    }

    private static bool TryEnableDred()
    {
        ID3D12DeviceRemovedExtendedDataSettings* settings = null;
        try
        {
            if (GetDebugInterface(__uuidof<ID3D12DeviceRemovedExtendedDataSettings>(), (void**)&settings) < 0)
                return false;

            settings->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT.D3D12_DRED_ENABLEMENT_FORCED_ON);
            settings->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT.D3D12_DRED_ENABLEMENT_FORCED_ON);
            return true;
        }
        finally
        {
            Release(settings);
        }
    }

    private static D3D12AdapterInfo DescribeAdapter(IDXGIAdapter1* adapter, DXGI_ADAPTER_DESC1 description)
    {
        LARGE_INTEGER nativeVersion;
        var driverVersion = adapter->CheckInterfaceSupport(__uuidof<IDXGIDevice>(), &nativeVersion).Value >= 0
            ? FormatDriverVersion(unchecked((ulong)nativeVersion.QuadPart))
            : null;
        var name = new ReadOnlySpan<char>(&description.Description.e0, 128);
        var terminator = name.IndexOf('\0');
        if (terminator >= 0)
            name = name[..terminator];
        return new D3D12AdapterInfo(name.ToString(), description.VendorId, description.DeviceId,
            (description.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0, driverVersion);
    }

    private static string FormatDriverVersion(ulong version) =>
        FormattableString.Invariant($"{(ushort)(version >> 48)}.{(ushort)(version >> 32)}.{(ushort)(version >> 16)}.{(ushort)version}");

    private void ConfigureDebugQueue()
    {
        var queue = OpenDebugQueue();
        try
        {
            if (queue != null)
                queue->SetMessageCountLimit(DebugMessageLimit);
        }
        finally
        {
            Release(queue);
        }
    }

    private ID3D12InfoQueue* OpenDebugQueue()
    {
        if (!DebugLayerEnabled || _device == null)
            return null;

        ID3D12InfoQueue* queue = null;
        _device->QueryInterface(__uuidof<ID3D12InfoQueue>(), (void**)&queue);
        return queue;
    }

    private static D3D12DebugMessages ReadDebugSummary(ID3D12InfoQueue* queue)
    {
        ulong warnings = 0;
        ulong errors = 0;
        ulong corruption = 0;
        var count = queue->GetNumStoredMessagesAllowedByRetrievalFilter();
        var limit = Math.Min(count, DebugMessageLimit);
        var discarded = queue->GetNumMessagesDiscardedByMessageCountLimit() + (count - limit);
        nint* buffer = stackalloc nint[DebugMessageBytes / sizeof(nint)];
        var message = (D3D12_MESSAGE*)buffer;
        for (ulong index = 0; index < limit; index++)
        {
            nuint size = DebugMessageBytes;
            if (queue->GetMessage(index, message, &size).Value < 0)
            {
                discarded++;
                continue;
            }

            switch (message->Severity)
            {
                case D3D12_MESSAGE_SEVERITY.D3D12_MESSAGE_SEVERITY_CORRUPTION: corruption++; break;
                case D3D12_MESSAGE_SEVERITY.D3D12_MESSAGE_SEVERITY_ERROR: errors++; break;
                case D3D12_MESSAGE_SEVERITY.D3D12_MESSAGE_SEVERITY_WARNING: warnings++; break;
            }
        }

        return new D3D12DebugMessages(warnings, errors, corruption, discarded, null, null);
    }

    private void FinishDebugReport()
    {
        var queue = OpenDebugQueue();
        if (queue == null)
            return;

        ID3D12DebugDevice* debug = null;
        try
        {
            FinalDebugSummary = ReadDebugSummary(queue);
            if (_device->QueryInterface(__uuidof<ID3D12DebugDevice>(), (void**)&debug).Value < 0)
                return;

            var start = queue->GetNumStoredMessagesAllowedByRetrievalFilter();
            var discarded = queue->GetNumMessagesDiscardedByMessageCountLimit();
            var result = debug->ReportLiveDeviceObjects(D3D12_RLDO_FLAGS.D3D12_RLDO_SUMMARY |
                D3D12_RLDO_FLAGS.D3D12_RLDO_DETAIL | D3D12_RLDO_FLAGS.D3D12_RLDO_IGNORE_INTERNAL).Value;
            var retainedQueue = false;
            var count = result >= 0 && discarded == queue->GetNumMessagesDiscardedByMessageCountLimit()
                ? CountLiveChildObjects(queue, start, _session?.RetainedQueueName, out retainedQueue)
                : null;
            FinalDebugSummary = FinalDebugSummary with
            {
                LiveObjectReportResult = result,
                LiveChildObjects = count,
                RetainedSessionQueue = count.HasValue && retainedQueue,
            };
        }
        finally
        {
            Release(debug);
            Release(queue);
        }
    }

    private static int? CountLiveChildObjects(ID3D12InfoQueue* queue, ulong start, string? queueName, out bool retainedQueue)
    {
        retainedQueue = false;
        var end = queue->GetNumStoredMessagesAllowedByRetrievalFilter();
        nint* buffer = stackalloc nint[DebugMessageBytes / sizeof(nint)];
        var message = (D3D12_MESSAGE*)buffer;
        var count = 0;
        var sawReport = false;
        for (var index = start; index < end && index - start < DebugMessageLimit; index++)
        {
            nuint size = DebugMessageBytes;
            if (queue->GetMessage(index, message, &size).Value < 0)
                return null;
            if (message->ID is D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LIVE_OBJECT_SUMMARY or D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LIVE_DEVICE)
                sawReport = true;
            else if (message->ID.ToString().StartsWith("D3D12_MESSAGE_ID_LIVE_", StringComparison.Ordinal))
            {
                count++;
                if (queueName is not null && message->ID == D3D12_MESSAGE_ID.D3D12_MESSAGE_ID_LIVE_COMMANDQUEUE &&
                    message->pDescription != null && message->DescriptionByteLength <= DebugMessageBytes)
                {
                    var description = new ReadOnlySpan<byte>((byte*)message->pDescription, (int)message->DescriptionByteLength);
                    retainedQueue |= System.Text.Encoding.UTF8.GetString(description).Contains(queueName, StringComparison.Ordinal);
                }
            }
        }

        return sawReport && end - start <= DebugMessageLimit ? count : null;
    }

    internal D3D12DeviceRemoval? CaptureDred()
    {
        var reason = RemovedReason;
        if (reason >= 0)
            return null;

        ID3D12DeviceRemovedExtendedData* dred = null;
        try
        {
            if (!DredEnabled || _device->QueryInterface(__uuidof<ID3D12DeviceRemovedExtendedData>(), (void**)&dred).Value < 0)
                return new D3D12DeviceRemoval(reason, null, null, [], null, [], []);

            D3D12_DRED_AUTO_BREADCRUMBS_OUTPUT breadcrumbs;
            D3D12_DRED_PAGE_FAULT_OUTPUT pageFault;
            var breadcrumbResult = dred->GetAutoBreadcrumbsOutput(&breadcrumbs).Value;
            var pageFaultResult = dred->GetPageFaultAllocationOutput(&pageFault).Value;
            return new D3D12DeviceRemoval(reason, breadcrumbResult, pageFaultResult,
                breadcrumbResult >= 0 ? ReadBreadcrumbs(breadcrumbs.pHeadAutoBreadcrumbNode) : [],
                pageFaultResult >= 0 ? pageFault.PageFaultVA != 0 : null,
                pageFaultResult >= 0 ? ReadAllocationTypes(pageFault.pHeadExistingAllocationNode) : [],
                pageFaultResult >= 0 ? ReadAllocationTypes(pageFault.pHeadRecentFreedAllocationNode) : []);
        }
        finally
        {
            Release(dred);
        }
    }

    internal static IReadOnlyList<D3D12Breadcrumb> ReadBreadcrumbs(D3D12_AUTO_BREADCRUMB_NODE* head)
    {
        var result = new List<D3D12Breadcrumb>();
        for (var node = head; node != null && result.Count < DredEntryLimit; node = node->pNext)
        {
            var total = node->BreadcrumbCount;
            var completed = node->pLastBreadcrumbValue == null ? 0 : Math.Min(*node->pLastBreadcrumbValue, total);
            var history = node->pCommandHistory;
            // DRED retains at most the last 64 Ki operations in a ring.
            var firstRetained = total - Math.Min(total, BreadcrumbHistoryLength);
            result.Add(new D3D12Breadcrumb(completed, total,
                history != null && completed > firstRetained ? (int)history[(completed - 1) % BreadcrumbHistoryLength] : null,
                history != null && completed >= firstRetained && completed < total
                    ? (int)history[completed % BreadcrumbHistoryLength] : null));
        }
        return result.AsReadOnly();
    }

    internal static IReadOnlyList<int> ReadAllocationTypes(D3D12_DRED_ALLOCATION_NODE* head)
    {
        var result = new List<int>();
        for (var node = head; node != null && result.Count < DredEntryLimit; node = node->pNext)
            result.Add((int)node->AllocationType);
        return result.AsReadOnly();
    }
}
