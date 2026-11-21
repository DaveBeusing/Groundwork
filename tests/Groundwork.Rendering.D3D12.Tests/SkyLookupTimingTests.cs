using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private static readonly ulong[] ExpectedLookupSplitSequences = [1, 2];
    [TestMethod]
    public void WarpSkyLookupSplitMarkersRetireOnceAndExactlyPartitionOriginalIntervals()
    {
        var owner = Environment.CurrentManagedThreadId;
        var exposeCompletion = true;
        var cpu = new List<(ulong Sequence, ulong Fence, double Compute, double Draw)>();
        var gpuRows = new List<(ulong Sequence, ulong Fence, double Compute, double Draw)>();
        var overallCpu = new Dictionary<ulong, double>();
        var overallGpu = new Dictionary<ulong, double>();
        var hooks = new GpuRetirementTestHooks(
            CompletedValue: (_, actual) => exposeCompletion ? actual : 0,
            RejectRecordedPlan: sequence => sequence == 3,
            AfterDeviceInitialize: device => device.EnableSkyTimingForTests(),
            AfterSkyRecordMeasured: (_, fence, time, _, _) => overallCpu.Add(fence, time),
            AfterSkyTimingRetired: (_, fence, _, time, _) => overallGpu.Add(fence, time),
            AfterSkyLookupRecordMeasured: (sequence, fence, compute, draw) =>
            {
                Assert.AreEqual(owner, Environment.CurrentManagedThreadId);
                cpu.Add((sequence, fence, compute, draw));
            },
            AfterSkyLookupGpuTimingRetired: (sequence, fence, compute, draw) =>
            {
                Assert.AreEqual(owner, Environment.CurrentManagedThreadId);
                gpuRows.Add((sequence, fence, compute, draw));
            });
        using var device = new GpuDevice(true, enableDebugLayer: true, enableDred: true,
            retirementHooks: hooks, skyLookupPrototype: true);
        using var target = device.CreateSkyTimingTargetForTests(256, 128);
        var frame = new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() };
        exposeCompletion = false;
        Assert.AreEqual(GpuFrameSubmitResult.Submitted, device.TryRenderSkyTimingForTests(target, frame));
        Assert.HasCount(1, cpu);
        Assert.IsEmpty(gpuRows, "The split must not map queries before native retirement is observed.");
        exposeCompletion = true;
        Retire();
        Assert.HasCount(1, gpuRows);
        Assert.IsTrue(device.IsIdle);
        Assert.IsTrue(device.IsIdle);
        Assert.HasCount(1, gpuRows, "Repeated idle reads must not repeat a retired split.");

        Assert.AreEqual(GpuFrameSubmitResult.Submitted,
            device.TryRenderSkyTimingForTests(target, frame with { Sequence = 2, Sky = null }));
        Retire();
        Assert.HasCount(1, cpu, "A G-off frame must not publish a record split.");
        Assert.HasCount(1, gpuRows, "A G-off frame must not publish a GPU split.");
        Assert.AreEqual(GpuFrameSubmitResult.Submitted,
            device.TryRenderSkyTimingForTests(target, frame with { Sequence = 2 }));
        Retire();
        Assert.AreEqual(GpuFrameSubmitResult.Rejected,
            device.TryRenderSkyTimingForTests(target, frame with { Sequence = 3 }));
        Assert.IsTrue(device.IsIdle);
        CollectionAssert.AreEqual(ExpectedLookupSplitSequences, cpu.Select(row => row.Sequence).ToArray());
        CollectionAssert.AreEqual(cpu.Select(row => (row.Sequence, row.Fence)).ToArray(),
            gpuRows.Select(row => (row.Sequence, row.Fence)).ToArray());
        Assert.AreEqual(2, gpuRows.Select(row => row.Fence).Distinct().Count());
        foreach (var row in cpu)
        {
            Assert.IsTrue(double.IsFinite(row.Compute) && double.IsFinite(row.Draw));
            Assert.IsGreaterThanOrEqualTo(0d, row.Compute);
            Assert.IsGreaterThanOrEqualTo(0d, row.Draw);
            Assert.AreEqual(overallCpu[row.Fence], row.Compute + row.Draw, 1e-9,
                "CPU components must partition the unchanged complete record interval.");
        }
        foreach (var row in gpuRows)
        {
            Assert.IsTrue(double.IsFinite(row.Compute) && double.IsFinite(row.Draw));
            Assert.IsGreaterThanOrEqualTo(0d, row.Compute);
            Assert.IsGreaterThanOrEqualTo(0d, row.Draw);
            Assert.AreEqual(overallGpu[row.Fence], row.Compute + row.Draw, 1e-9,
                "GPU components must partition the unchanged complete sky interval.");
        }
        var evidence = Path.GetFullPath(Path.Combine("artifacts", "sky-lookup-marker-tests", Guid.NewGuid().ToString("N") + ".json"));
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        File.WriteAllText(evidence, JsonSerializer.Serialize(new
        {
            DiagnosticOnly = true,
            Cpu = cpu.Select(row => new { row.Sequence, row.Fence, row.Compute, row.Draw }),
            Gpu = gpuRows.Select(row => new { row.Sequence, row.Fence, row.Compute, row.Draw }),
            OverallCpu = overallCpu, OverallGpu = overallGpu,
            CpuScope = "Sky record start through dispatch; dispatch through original record end including depth restoration",
            GpuScope = "SkyStart through dispatch marker; marker through original SkyEnd including all barriers",
        }, SkyLookupEvidenceOptions));
        target.Dispose();
        device.Dispose();
        AssertCleanDiagnostics(new(device.Adapter, device.DebugLayerEnabled, device.DredEnabled, device.FinalDebugSummary, null), true);
        Console.WriteLine("Lookup split marker evidence: " + evidence);

        void Retire()
        {
            var start = Stopwatch.GetTimestamp();
            while (!device.IsIdle)
            {
                if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(10))
                    throw new TimeoutException("Lookup marker test did not retire within its functional deadline.");
                Thread.Sleep(1);
            }
        }
    }
}
