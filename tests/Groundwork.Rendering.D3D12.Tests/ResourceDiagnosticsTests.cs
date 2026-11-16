using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class ResourceDiagnosticsTests
{
    [TestMethod]
    public void TextureCopyAndDrawTrackDistinctResourcesBeyondTheOriginalCapacity()
    {
        var diagnostics = new ResourceDiagnostics();
        for (var i = 1; i <= 65; i++)
            diagnostics.Track(i, i == 65 ? D3D12ResourceOwner.Texture : D3D12ResourceOwner.Terrain, 65536);
        diagnostics.Track(66, D3D12ResourceOwner.TextureStaging, 131072);

        diagnostics.BeginRecording();
        diagnostics.BeginPass(GpuPassPlan.Terrain);
        diagnostics.Use(1);
        diagnostics.BeginDraw(7, 1);
        diagnostics.Signal(1, draw: true);
        diagnostics.Complete(1);
        diagnostics.BeginTextureCopy(2, 66, 65);
        Assert.IsTrue(diagnostics.TryRead(out var copying));
        Assert.AreEqual(64, copying.GetBalance(D3D12ResourceOwner.Terrain).Live);
        Assert.AreEqual(0, copying.GetBalance(D3D12ResourceOwner.Terrain).Retained);
        Assert.AreEqual(1, copying.GetBalance(D3D12ResourceOwner.Texture).Retained);
        Assert.AreEqual(1, copying.GetBalance(D3D12ResourceOwner.TextureStaging).Retained);
        diagnostics.SignalTextureCopy(2);
        diagnostics.Complete(2);
        diagnostics.Release(66);
        Assert.IsTrue(diagnostics.TryRead(out var copied));
        Assert.AreEqual(7UL, copied.FrameSequence);
        Assert.AreEqual(1UL, copied.DrawFence);
        Assert.AreEqual(2UL, copied.TextureCopyFence);
        Assert.AreEqual(copied.CompletedFence, copied.SubmittedFence);
        Assert.IsTrue(copied.TryGetPass(GpuPassPlan.Terrain, out var previousDraw));
        Assert.AreEqual(1, previousDraw.ResourceUses);

        diagnostics.BeginRecording();
        diagnostics.BeginPass(GpuPassPlan.Terrain);
        diagnostics.Use(1);
        diagnostics.Use(65);
        diagnostics.Use(65);
        diagnostics.BeginDraw(8, 3);
        Assert.IsTrue(diagnostics.TryRead(out var drawing));
        Assert.AreEqual(1, drawing.GetBalance(D3D12ResourceOwner.Terrain).Retained);
        Assert.AreEqual(1, drawing.GetBalance(D3D12ResourceOwner.Texture).Retained);
        diagnostics.Signal(3, draw: true);
        diagnostics.BeginPresent(4, 0);
        diagnostics.Signal(4, draw: false);
        diagnostics.Complete(3);
        Assert.IsTrue(diagnostics.TryRead(out var presented));
        Assert.AreEqual(1, presented.GetBalance(D3D12ResourceOwner.Texture).Retained);
        diagnostics.Complete(4);
        Assert.IsTrue(diagnostics.TryRead(out var finished));
        Assert.AreEqual(1, finished.GetBalance(D3D12ResourceOwner.Texture).Live);
        Assert.IsTrue(finished.TryGetPass(GpuPassPlan.Terrain, out var texturedDraw));
        Assert.AreEqual(2, texturedDraw.ResourceUses);
        Assert.IsFalse(finished.LedgerOverflowed);
    }

    [TestMethod]
    public void LedgerCountsExactRetirementWithoutReusingAResourceAfterSentinel()
    {
        var diagnostics = new ResourceDiagnostics();
        Assert.IsFalse(diagnostics.TryRead(out _));
        diagnostics.Track(1, D3D12ResourceOwner.Terrain, 65536);
        diagnostics.Track(2, D3D12ResourceOwner.Terrain, 131072);
        diagnostics.BeginRecording();
        diagnostics.BeginPass(GpuPassPlan.Terrain);
        diagnostics.Use(1);
        diagnostics.Use(1);
        diagnostics.Barrier();
        diagnostics.BeginDraw(12, 1);
        Assert.IsTrue(diagnostics.TryRead(out var pending));
        Assert.AreEqual(12UL, pending.UnassignedFrameSequence);
        Assert.AreEqual(new D3D12ResourceBalance(1, 131072, 1, 65536, 0, 0), pending.GetBalance(D3D12ResourceOwner.Terrain));
        diagnostics.Complete(ulong.MaxValue);
        Assert.IsTrue(diagnostics.TryRead(out var sentinel));
        Assert.AreEqual(D3D12CustodyStatus.UnassignedSubmission | D3D12CustodyStatus.FenceSentinelObserved, sentinel.Custody);
        Assert.AreEqual(0UL, sentinel.CompletedFence);
        Assert.AreEqual(1, sentinel.GetBalance(D3D12ResourceOwner.Terrain).Retained);
        diagnostics.NoteCustody(D3D12CustodyStatus.ConfirmedRemoval);
        diagnostics.Release(1);
        diagnostics.Release(2);
        Assert.IsTrue(diagnostics.TryRead(out var released));
        Assert.AreEqual(new D3D12ResourceBalance(0, 0, 0, 0, 2, 196608), released.GetBalance(D3D12ResourceOwner.Terrain));
        Assert.AreEqual(0UL, released.SubmittedFence);
        Assert.IsFalse(released.TryGetPass(GpuPassPlan.Terrain, out _));
    }

    [TestMethod]
    public void WarpPassObservationsPublishOnlyForSubmittedCommandLists()
    {
        var diagnostics = new ResourceDiagnostics();
        D3D12ResourceSnapshot beforeExecute = default;
        var sampledBeforeExecute = false;
        var hooks = new GpuRetirementTestHooks(RejectRecordedPlan: sequence => sequence == 2,
            BeforeExecute: sequence =>
            {
                if (sequence == 1)
                    sampledBeforeExecute = diagnostics.TryRead(out beforeExecute);
            });
        var gpu = new GpuDevice(warp: true, retirementHooks: hooks, resourceDiagnostics: diagnostics);
        using (gpu)
        {
            var clear = new RenderFrame(1, new Vector4(0.125f, 0.25f, 0.5f, 1));
            var first = GpuDevice.RenderOffscreen(gpu, 143, 89, clear);
            Assert.IsTrue(sampledBeforeExecute);
            Assert.AreEqual(D3D12CustodyStatus.UnassignedSubmission, beforeExecute.Custody);
            Assert.AreEqual(1UL, beforeExecute.UnassignedFrameSequence);
            Assert.AreEqual(1UL, beforeExecute.UnassignedFence);
            Assert.AreEqual(0UL, beforeExecute.SubmittedFence);
            Assert.AreEqual(1, beforeExecute.GetBalance(D3D12ResourceOwner.Presentation).Retained);
            Assert.AreEqual(1, beforeExecute.GetBalance(D3D12ResourceOwner.Depth).Retained);
            Assert.AreEqual(1, beforeExecute.GetBalance(D3D12ResourceOwner.Readback).Retained);
            Assert.IsTrue(diagnostics.TryRead(out var accepted));
            Assert.AreEqual(1UL, accepted.FrameSequence);
            Assert.AreEqual(1UL, accepted.DrawFence);
            Assert.AreEqual(1UL, accepted.SubmittedFence);
            Assert.AreEqual(1UL, accepted.CompletedFence);
            Assert.AreEqual(0UL, accepted.PresentedSequence);
            Assert.IsTrue(accepted.TryGetPass(GpuPassPlan.Clear, out var clearObservation));
            Assert.AreEqual(new D3D12PassObservation(1, 2), clearObservation);
            Assert.IsTrue(accepted.TryGetPass(GpuPassPlan.Copy, out var copyObservation));
            Assert.AreEqual(new D3D12PassObservation(4, 3), copyObservation);
            Assert.IsTrue(accepted.TryGetPass(GpuPassPlan.Readback, out _));
            Assert.IsFalse(accepted.TryGetPass(GpuPassPlan.Props, out _));
            Assert.IsFalse(accepted.TryGetPass(default, out _));
            Assert.AreEqual(1, accepted.GetBalance(D3D12ResourceOwner.Depth).Live);
            Assert.AreEqual(1UL, accepted.GetBalance(D3D12ResourceOwner.Readback).Released);
            Assert.IsGreaterThan(143UL * 89 * 8, accepted.GetBalance(D3D12ResourceOwner.Readback).ReleasedBytes);

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                GpuDevice.RenderOffscreen(gpu, 143, 89, clear with { Sequence = 2, ClearColor = Vector4.One }));
            Assert.IsTrue(diagnostics.TryRead(out var rejected));
            Assert.AreEqual(1UL, rejected.FrameSequence);
            Assert.AreEqual(1UL, rejected.SubmittedFence);
            Assert.AreEqual(2UL, rejected.GetBalance(D3D12ResourceOwner.Readback).Released);

            var resumed = GpuDevice.RenderOffscreen(gpu, 163, 97, clear with { Sequence = 3 });
            Assert.IsTrue(resumed.ColorAt(0, 0).SequenceEqual(first.ColorAt(0, 0)));
            Assert.AreEqual(first.DepthAt(0, 0), resumed.DepthAt(0, 0));
            Assert.IsTrue(diagnostics.TryRead(out var resized));
            Assert.AreEqual(3UL, resized.FrameSequence);
            Assert.AreEqual(2UL, resized.SubmittedFence);
            Assert.AreEqual(1UL, resized.GetBalance(D3D12ResourceOwner.Depth).Released);
            Assert.AreEqual(1, resized.GetBalance(D3D12ResourceOwner.Depth).Live);
        }
        Assert.IsTrue(diagnostics.TryRead(out var disposed));
        Assert.AreEqual(2UL, disposed.GetBalance(D3D12ResourceOwner.Depth).Released);
        Assert.AreEqual(3UL, disposed.GetBalance(D3D12ResourceOwner.Presentation).Released);
        Assert.AreEqual(D3D12CustodyStatus.None, disposed.Custody);
        Assert.IsFalse(disposed.LedgerOverflowed);
        foreach (var owner in Enum.GetValues<D3D12ResourceOwner>())
        {
            var balance = disposed.GetBalance(owner);
            Assert.AreEqual(0, balance.Live + balance.Retained);
        }
    }

    [TestMethod]
    public void LedgerFlagsUnknownSizesAndSaturatesLargeTotals()
    {
        var diagnostics = new ResourceDiagnostics();
        diagnostics.Track(1, D3D12ResourceOwner.Props, ulong.MaxValue);
        diagnostics.Track(2, D3D12ResourceOwner.Props, 0);
        diagnostics.Track(3, D3D12ResourceOwner.Props, 65536);
        Assert.IsTrue(diagnostics.TryRead(out var unknown));
        Assert.IsTrue(unknown.LedgerHasUnknownSizes);
        Assert.IsFalse(unknown.LedgerOverflowed);
        Assert.AreEqual(new D3D12ResourceBalance(3, 65536, 0, 0, 0, 0), unknown.GetBalance(D3D12ResourceOwner.Props));
        diagnostics.Track(4, D3D12ResourceOwner.Props, ulong.MaxValue - 65535);
        Assert.IsTrue(diagnostics.TryRead(out var saturated));
        Assert.IsTrue(saturated.LedgerOverflowed);
        Assert.AreEqual(ulong.MaxValue, saturated.GetBalance(D3D12ResourceOwner.Props).LiveBytes);
        for (var pointer = 1; pointer <= 4; pointer++)
            diagnostics.Release(pointer);
        Assert.IsTrue(diagnostics.TryRead(out var released));
        Assert.AreEqual(new D3D12ResourceBalance(0, 0, 0, 0, 4, ulong.MaxValue), released.GetBalance(D3D12ResourceOwner.Props));
    }

    [TestMethod]
    public void WarpClearOnlyCascadeAndTerrainWithoutDrawReportActualUses()
    {
        var diagnostics = new ResourceDiagnostics();
        var nearMesh = RenderMesh.Create([new(-0.2f, -0.2f, -4), new(0.2f, -0.2f, -4), new(0, 0.2f, -4)], [0, 1, 2]);
        using var gpu = new GpuDevice(warp: true, region: new([new(nearMesh, Vector3.Zero)]), resourceDiagnostics: diagnostics);
        var camera = new VolumeCamera(new(1, 1, PlanetLocalFrame.Create(default)), default, -Vector3.UnitZ,
            Vector3.UnitY, 0.1f, 0.1f, 100, Vector4.One, Vector4.Zero)
        {
            Sunlight = new(Vector3.UnitY, new(0.85f), new(0.12f)),
            TerrainShadows = true,
        };
        Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 1, out var frame));
        GpuDevice.RenderOffscreen(gpu, 64, 64, frame);
        Assert.IsTrue(diagnostics.TryRead(out var first));
        Assert.IsTrue(first.TryGetPass(GpuPassPlan.NearShadow, out var nearShadow));
        Assert.IsTrue(first.TryGetPass(GpuPassPlan.FarShadow, out var farShadow));
        Assert.AreEqual(new D3D12PassObservation(2, 2), nearShadow);
        Assert.AreEqual(new D3D12PassObservation(2, 1), farShadow);

        var emptyDraw = RenderMesh.Create([], []);
        var previous = gpu.ActiveMeshGeometry;
        gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(new RenderMeshRegion([new(emptyDraw, Vector3.Zero)]));
        previous!.Dispose();
        GpuDevice.RenderOffscreen(gpu, 64, 64, frame with { Sequence = 2 });
        Assert.IsTrue(diagnostics.TryRead(out var second));
        Assert.IsFalse(second.TryGetPass(GpuPassPlan.NearShadow, out _));
        Assert.IsTrue(second.TryGetPass(GpuPassPlan.Terrain, out var terrain));
        Assert.AreEqual(new D3D12PassObservation(0, 0), terrain);
        Assert.IsTrue(second.TryGetPass(GpuPassPlan.Clear, out var clear));
        Assert.AreEqual(new D3D12PassObservation(1, 2), clear);
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void MeasureResourceDiagnosticsOverhead(bool warp)
    {
        if (Environment.GetEnvironmentVariable("GROUNDWORK_RPA03_MEASURE") != "1")
            return;

        const int warmup = 24;
        const int frames = 128;
        const int reads = 10000;
        foreach (var enableSnapshots in new[] { false, true })
        {
            var diagnostics = enableSnapshots ? new ResourceDiagnostics() : null;
            var frameTimings = new double[frames];
            var recordTimings = new double[frames];
            var frameBytes = 0L;
            var recordBytes = 0L;
            var recordStart = 0L;
            var recordStartBytes = 0L;
            var sample = -warmup;
            var hooks = new GpuRetirementTestHooks(
                RejectPreparedPlan: _ =>
                {
                    recordStart = Stopwatch.GetTimestamp();
                    recordStartBytes = GC.GetAllocatedBytesForCurrentThread();
                    return false;
                },
                RejectRecordedPlan: _ =>
                {
                    if (sample >= 0)
                    {
                        recordTimings[sample] = Stopwatch.GetElapsedTime(recordStart).TotalMicroseconds;
                        recordBytes += GC.GetAllocatedBytesForCurrentThread() - recordStartBytes;
                    }
                    return false;
                });
            using var gpu = new GpuDevice(warp, retirementHooks: hooks, resourceDiagnostics: diagnostics);
            var scene = new RenderScene(Matrix4x4.CreateTranslation(0, 0, -3), Vector4.One,
                Matrix4x4.Identity, 1, 0.2f, 10);
            var clear = new RenderFrame(1, new Vector4(0.125f, 0.25f, 0.5f, 1), scene);
            for (; sample < frames; sample++)
            {
                var start = Stopwatch.GetTimestamp();
                var frameStartBytes = GC.GetAllocatedBytesForCurrentThread();
                GpuDevice.RenderOffscreen(gpu, 143, 89, clear with { Sequence = (ulong)(sample + warmup + 1) });
                if (sample >= 0)
                {
                    frameTimings[sample] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                    frameBytes += GC.GetAllocatedBytesForCurrentThread() - frameStartBytes;
                }
            }

            var started = Stopwatch.GetTimestamp();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            int readCount = 0;
            for (var i = 0; i < reads; i++)
                if (diagnostics?.TryRead(out _) == true)
                    readCount++;
            var readMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            var readBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.AreEqual(enableSnapshots ? reads : 0, readCount);
            Array.Sort(frameTimings);
            Array.Sort(recordTimings);
            static string Percentiles(double[] times) => FormattableString.Invariant(
                $"{Percentile(times, .5):F2}/{Percentile(times, .95):F2}/{Percentile(times, .99):F2}");
            static double Percentile(double[] times, double fraction) => times[(int)Math.Ceiling(fraction * times.Length) - 1];
            Console.WriteLine(FormattableString.Invariant(
                $"RPA03 adapter={gpu.Adapter.Description}; driver={gpu.Adapter.DriverVersion}; snapshots={enableSnapshots}; n={frames}; warmup={warmup}; frame_us={Percentiles(frameTimings)}; frame_B/op={(double)frameBytes / frames:F2}; record_us={Percentiles(recordTimings)}; record_B/op={(double)recordBytes / frames:F2}; snapshot_read_us/op={readMicroseconds / reads:F3}; snapshot_read_B/op={(double)readBytes / reads:F2}; snapshot_value_B={Unsafe.SizeOf<D3D12ResourceSnapshot>()}"));
        }
    }
}
