using System.Numerics;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed unsafe class DredTests
{
    [TestMethod]
    public void BreadcrumbsAndAllocationTypesHaveAFixedCaptureLimit()
    {
        const int limit = 16;
        var breadcrumbs = stackalloc D3D12_AUTO_BREADCRUMB_NODE[limit + 1];
        var allocations = stackalloc D3D12_DRED_ALLOCATION_NODE[limit + 1];
        for (var index = 0; index <= limit; index++)
        {
            breadcrumbs[index] = new D3D12_AUTO_BREADCRUMB_NODE
            {
                BreadcrumbCount = (uint)index,
                pNext = index < limit ? &breadcrumbs[index + 1] : null,
            };
            allocations[index] = new D3D12_DRED_ALLOCATION_NODE
            {
                AllocationType = (D3D12_DRED_ALLOCATION_TYPE)(index + 19),
                pNext = index < limit ? &allocations[index + 1] : null,
            };
        }

        var capturedBreadcrumbs = GpuDevice.ReadBreadcrumbs(breadcrumbs);
        var capturedAllocations = GpuDevice.ReadAllocationTypes(allocations);
        Assert.HasCount(limit, capturedBreadcrumbs);
        Assert.HasCount(limit, capturedAllocations);
        for (var index = 0; index < limit; index++)
        {
            Assert.AreEqual(new D3D12Breadcrumb(0, (uint)index, null, null), capturedBreadcrumbs[index]);
            Assert.AreEqual(index + 19, capturedAllocations[index]);
        }
    }

    [TestMethod]
    public void BreadcrumbProgressIncludesOnlyTheAvailableNeighboringOperations()
    {
        uint completed = 1;
        var history = stackalloc D3D12_AUTO_BREADCRUMB_OP[2]
        {
            D3D12_AUTO_BREADCRUMB_OP.D3D12_AUTO_BREADCRUMB_OP_DRAWINSTANCED,
            D3D12_AUTO_BREADCRUMB_OP.D3D12_AUTO_BREADCRUMB_OP_RESOURCEBARRIER,
        };
        var node = new D3D12_AUTO_BREADCRUMB_NODE
        {
            BreadcrumbCount = 2,
            pLastBreadcrumbValue = &completed,
            pCommandHistory = history,
        };

        Assert.AreEqual(new D3D12Breadcrumb(1, 2, (int)history[0], (int)history[1]), GpuDevice.ReadBreadcrumbs(&node)[0]);
        completed = 3;
        Assert.AreEqual(new D3D12Breadcrumb(2, 2, (int)history[1], null), GpuDevice.ReadBreadcrumbs(&node)[0]);
    }

    [TestMethod]
    public void BreadcrumbProgressDoesNotReportOverwrittenRingEntries()
    {
        const int capacity = 65536;
        uint completed = 1;
        var history = new D3D12_AUTO_BREADCRUMB_OP[capacity];
        history[0] = D3D12_AUTO_BREADCRUMB_OP.D3D12_AUTO_BREADCRUMB_OP_RESOURCEBARRIER;
        history[1] = D3D12_AUTO_BREADCRUMB_OP.D3D12_AUTO_BREADCRUMB_OP_DRAWINSTANCED;
        fixed (D3D12_AUTO_BREADCRUMB_OP* commands = history)
        {
            var node = new D3D12_AUTO_BREADCRUMB_NODE
            {
                BreadcrumbCount = capacity + 1,
                pLastBreadcrumbValue = &completed,
                pCommandHistory = commands,
            };

            Assert.AreEqual(new D3D12Breadcrumb(1, capacity + 1, null, (int)history[1]), GpuDevice.ReadBreadcrumbs(&node)[0]);
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12WarpDiagnostics")]
    public void RemovedWarpDeviceReturnsBoundedDredEvidence()
    {
        using var gpu = new GpuDevice(warp: true, enableDred: true);
        Assert.IsTrue(gpu.Adapter.IsSoftware);
        Assert.IsTrue(gpu.DredEnabled);
        var pixels = GpuDevice.RenderOffscreen(gpu, 16, 16, new RenderFrame(1, Vector4.One));
        Assert.AreEqual(1f, pixels.DepthAt(0, 0));
        Assert.IsNull(gpu.CaptureDred());

        // Remove this disposable software device without resetting a physical adapter.
        ID3D12Device5* removable = null;
        Assert.AreEqual(0, gpu.Device->QueryInterface(__uuidof<ID3D12Device5>(), (void**)&removable).Value);
        try
        {
            removable->RemoveDevice();
        }
        finally
        {
            removable->Release();
        }

        var removal = gpu.CaptureDred();
        Assert.IsNotNull(removal);
        Assert.IsLessThan(0, removal.Reason);
        Assert.AreEqual(gpu.RemovedReason, removal.Reason);
        Assert.AreEqual(0, removal.BreadcrumbResult);
        Assert.AreEqual(0, removal.PageFaultResult);
        Assert.IsLessThanOrEqualTo(16, removal.Breadcrumbs.Count);
        Assert.IsLessThanOrEqualTo(16, removal.ExistingAllocationTypes.Count);
        Assert.IsLessThanOrEqualTo(16, removal.RecentlyFreedAllocationTypes.Count);
        Console.WriteLine($"WARP removal 0x{removal.Reason:X8}, DRED breadcrumbs 0x{removal.BreadcrumbResult:X8} ({removal.Breadcrumbs.Count}), page fault 0x{removal.PageFaultResult:X8}.");
    }
}
