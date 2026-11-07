using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private static readonly PlanetVector PlanetFocus = new(64, 0, 0);
    private static readonly PlanetVector PlanetNextFocus = new(45.2548339959, 0, 45.2548339959);
    private static readonly PlanetVector PlanetCamera = new(80, 0, 0);

    [TestMethod]
    [TestCategory("D3D12PlanetWarp")]
    public void WarpPlanetTerrainReadbackSurvivesLodAndOriginChange() =>
        VerifyPlanetReadback(useWarp: true, requireDebugLayer: false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void PlanetTerrainReadbackWithDebugLayer(bool useWarp) =>
        VerifyPlanetReadback(useWarp, requireDebugLayer: true);

    private static void VerifyPlanetReadback(bool useWarp, bool requireDebugLayer)
    {
        var terrain = new PlanetTerrainCoordinator();
        var frame = PlanetLocalFrame.Create(PlanetFocus);
        using var first = StagePlanet(terrain, PlanetFocus, frame, PlanetCamera, null, out var firstPlan);
        var gpu = new GpuDevice(warp: useWarp, enableDebugLayer: true, enableDred: true,
            region: D3D12Renderer.PackPlanetTerrain(first));
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, useWarp);
            Assert.IsTrue(gpu.DredEnabled);
            if (requireDebugLayer)
                Assert.IsTrue(gpu.DebugLayerEnabled);

            var firstView = PlanetView(first, 1);
            var before = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, firstView);
            Assert.IsTrue(terrain.AcknowledgePresented(first));
            first.Dispose();

            Assert.IsTrue(frame.TryRebase(PlanetCamera, out var rebased));
            using var second = StagePlanet(terrain, PlanetNextFocus, rebased, PlanetCamera, firstPlan, out var secondPlan);
            AssertLodChange(firstPlan, secondPlan);
            var secondView = PlanetView(second, 2);
            var old = gpu.ActiveMeshGeometry;
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(D3D12Renderer.PackPlanetTerrain(second));
            old?.Dispose();
            var after = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, secondView);
            for (var x = 74; x <= 86; x++)
            for (var y = 44; y <= 52; y++)
            {
                Assert.IsLessThan(1f, before.DepthAt(x, y));
                Assert.IsLessThan(1f, after.DepthAt(x, y));
                Assert.AreEqual(before.DepthAt(x, y), after.DepthAt(x, y), 0.0002f);
                Assert.IsTrue(before.ColorAt(x, y).SequenceEqual(after.ColorAt(x, y)));
                Assert.AreNotEqual((byte)0, after.ColorAt(x, y)[1]);
            }

            VerifyMixedPlanetSeam(gpu, second);
            Assert.AreEqual(1, gpu.LiveMeshBuffers);
            Assert.IsNull(gpu.CaptureDred());
        }

        Assert.AreEqual(0, gpu.LiveMeshBuffers);
        Assert.AreEqual(0L, gpu.LiveMeshBytes);
        AssertCleanDiagnostics(new D3D12DiagnosticsSnapshot(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled,
            gpu.FinalDebugSummary, null), diagnosticsRequested: true);
    }

    private static void VerifyMixedPlanetSeam(GpuDevice gpu, PlanetTerrainLease lease)
    {
        var tile = lease.Tiles.ToArray().First(tile => tile.Transitions != PlanetTransitionEdges.None);
        const int middle = PlanetCoordinates.IntervalsPerPatch / 2;
        const int edge = PlanetCoordinates.IntervalsPerPatch;
        var (u, v) = tile.Transitions switch
        {
            var flags when flags.HasFlag(PlanetTransitionEdges.Bottom) => (middle, 0),
            var flags when flags.HasFlag(PlanetTransitionEdges.Right) => (edge, middle),
            var flags when flags.HasFlag(PlanetTransitionEdges.Top) => (middle, edge),
            _ => (0, middle)
        };
        var direction = PlanetCoordinates.Direction(PlanetCoordinates.Sample(tile.Patch, u, v));
        var surface = lease.Frame.ToLocal(new PlanetVector(direction.X * 64, direction.Y * 64, direction.Z * 64));
        var radial = new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z);
        var tangent = Vector3.Normalize(Vector3.Cross(radial, Vector3.UnitY));
        var up = Vector3.Cross(tangent, radial);
        foreach (var offset in new[] { radial * 3, radial + tangent * 6 })
        {
            var scene = new RenderScene(Matrix4x4.Identity, new Vector4(0, 1, 0, 1),
                Matrix4x4.CreateLookTo(surface + offset, -offset, up), MathF.PI / 4, NearPlane, FarPlane);
            var pixels = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, new RenderFrame(3, new Vector4(0, 0, 0, 1), scene));
            for (var x = 77; x <= 82; x++)
            for (var y = 45; y <= 50; y++)
            {
                Assert.IsLessThan(1f, pixels.DepthAt(x, y));
                Assert.AreEqual(byte.MaxValue, pixels.ColorAt(x, y)[1]);
            }
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PlanetTerrainPresentsMatchingCameraAndRetainsGpuMeshThroughStop(bool useWarp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var title = $"Groundwork planet terrain {Guid.NewGuid():N}";
        var observed = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = 1;
        var hooks = new GpuRetirementTestHooks(CompletedValue: (gpu, completed) =>
        {
            if (Volatile.Read(ref hold) == 0 || completed == ulong.MaxValue)
                return completed;
            if (completed >= 6)
                observed.TrySetResult(gpu);
            return Math.Min(completed, 4UL);
        });
        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, QualificationDiagnostics,
            useWarp, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), hooks);
        var terrain = new PlanetTerrainCoordinator();
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, useWarp);
            Assert.IsTrue(diagnostics.DebugLayerEnabled);
            Assert.IsTrue(diagnostics.DredEnabled);
            var origin = PlanetLocalFrame.Create(PlanetFocus);
            using (var first = StagePlanet(terrain, PlanetFocus, origin, PlanetCamera, null, out _))
            {
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, SubmitPlanet(renderer, first, 10));
                await AwaitStatus(renderer, _ => renderer.WasPlanetTerrainPresented(first, 10), token);
                Assert.IsTrue(terrain.AcknowledgePresented(first));
                Assert.IsTrue(terrain.TryReadActive(new PlanetVector(80, 0, 1), out var active));
                using (active)
                {
                    Assert.IsNotNull(active);
                    Assert.AreEqual(RenderSubmissionResult.Accepted, SubmitPlanetCamera(renderer, active, 11));
                    await AwaitStatus(renderer, state => state.LastPresentedSequence == 11, token);
                    Assert.IsTrue(renderer.WasPlanetTerrainPresented(first, 10));
                    Assert.IsFalse(renderer.WasPlanetTerrainPresented(active, 10));
                }
            }

            Assert.IsTrue(origin.TryRebase(PlanetCamera, out var rebased));
            using var second = StagePlanet(terrain, PlanetNextFocus, rebased, PlanetCamera, null, out _);
            Assert.IsFalse(renderer.WasPlanetTerrainPresented(second, 10));
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame, SubmitPlanetCamera(renderer, second, 19));
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame, renderer.TrySubmit(PlanetView(second, 30)));
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, SubmitPlanet(renderer, second, 20));
            Assert.IsTrue(PlanetTerrainSelection.TrySelect(PlanetFocus, 64, null, out var supersedingPlan));
            Assert.IsNotNull(supersedingPlan);
            Assert.IsFalse(terrain.TryStage(supersedingPlan, rebased));
            await AwaitStatus(renderer, _ => renderer.WasPlanetTerrainPresented(second, 20), token);
            Assert.IsFalse(renderer.WasPlanetTerrainPresented(second, 10));
            Assert.IsTrue(terrain.AcknowledgePresented(second));
            var device = await observed.Task.WaitAsync(token);
            var stop = renderer.StopAsync(token).AsTask();
            await AwaitStatus(renderer, state => state.Lifecycle == RendererLifecycle.Stopping, token);
            Assert.IsFalse(stop.IsCompleted);
            Assert.AreEqual(1, device.LiveMeshBuffers);
            var heldBytes = device.LiveMeshBytes;
            Assert.IsGreaterThan(0L, heldBytes);
            Assert.IsLessThanOrEqualTo(GpuDevice.MaximumNativeMeshBytes, heldBytes);
            Console.WriteLine($"Stopped with {heldBytes} native bytes in custody at the post-Present fence.");

            Volatile.Write(ref hold, 0);
            await stop;
            Assert.AreEqual(0, device.LiveMeshBuffers);
            Assert.AreEqual(0L, device.LiveMeshBytes);
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
        }
        finally
        {
            Volatile.Write(ref hold, 0);
            terrain.Stop();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private static void AssertPlanetAdapter(D3D12AdapterInfo? adapter, bool useWarp)
    {
        AssertAdapter(adapter, useWarp);
        if (!useWarp)
            Assert.AreEqual(0x10DEu, adapter!.VendorId, "This qualification requires NVIDIA.");
    }

    private static PlanetTerrainLease StagePlanet(PlanetTerrainCoordinator terrain, PlanetVector focus, PlanetLocalFrame frame,
        PlanetVector camera, PlanetTerrainPlan? previous, out PlanetTerrainPlan plan)
    {
        Assert.IsTrue(PlanetTerrainSelection.TrySelect(focus, 64, previous, out var selected));
        Assert.IsNotNull(selected);
        plan = selected;
        return StagePlanetPlan(terrain, plan, frame, camera);
    }

    private static PlanetTerrainLease StagePlanetPlan(PlanetTerrainCoordinator terrain, PlanetTerrainPlan plan,
        PlanetLocalFrame frame, PlanetVector camera)
    {
        Assert.IsTrue(terrain.TryStage(plan, frame));
        while (terrain.TryReserveMesh(out var work))
        {
            var mesh = PlanetPatchMesh.Build(work.Tile.Patch, 64, static _ => 0, work.Tile.Transitions);
            Assert.AreEqual(plan.EditRevision, work.EditRevision);
            Assert.IsTrue(terrain.CompleteMesh(work, mesh));
        }

        Assert.IsTrue(terrain.TryPreparePresentation(camera, out var lease));
        Assert.IsNotNull(lease);
        return lease;
    }

    private static void AssertLodChange(PlanetTerrainPlan before, PlanetTerrainPlan after)
    {
        var previous = before.Tiles.ToArray().Select(tile => tile.Patch).ToHashSet();
        Assert.IsTrue(after.Tiles.ToArray().Any(tile => IsDescendantOf(tile.Patch, previous)));
        var next = after.Tiles.ToArray().Select(tile => tile.Patch).ToHashSet();
        Assert.IsTrue(before.Tiles.ToArray().Any(tile => IsDescendantOf(tile.Patch, next)));
    }

    private static bool IsDescendantOf(PlanetPatchKey patch, HashSet<PlanetPatchKey> candidates)
    {
        for (var delta = 1; delta <= patch.Level; delta++)
        {
            if (candidates.Contains(new PlanetPatchKey(patch.Face, patch.Level - delta, patch.TileU >> delta, patch.TileV >> delta)))
                return true;
        }

        return false;
    }

    private static RenderFrame PlanetView(PlanetTerrainLease lease, ulong sequence) =>
        D3D12Renderer.CreatePlanetFrame(lease, sequence, -Vector3.UnitX, Vector3.UnitY,
            MathF.PI / 3, NearPlane, FarPlane, new Vector4(0, 1, 0, 1), new Vector4(0, 0, 0, 1));

    private static MeshReplacementSubmissionResult SubmitPlanet(D3D12Renderer renderer, PlanetTerrainLease lease, ulong sequence) =>
        renderer.TrySubmitPlanetTerrain(lease, sequence, -Vector3.UnitX, Vector3.UnitY,
            MathF.PI / 3, NearPlane, FarPlane, new Vector4(0, 1, 0, 1), new Vector4(0, 0, 0, 1));

    private static RenderSubmissionResult SubmitPlanetCamera(D3D12Renderer renderer, PlanetTerrainLease lease, ulong sequence) =>
        renderer.TrySubmitPlanetCamera(lease, sequence, -Vector3.UnitX, Vector3.UnitY,
            MathF.PI / 3, NearPlane, FarPlane, new Vector4(0, 1, 0, 1), new Vector4(0, 0, 0, 1));
}
