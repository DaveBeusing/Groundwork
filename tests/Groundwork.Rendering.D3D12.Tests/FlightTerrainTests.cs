using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private static readonly Vector3 FlightForward = Vector3.Normalize(new Vector3(-1, 0, -1));
    private static readonly Vector4 FlightTint = new(0, 1, 0, 1);
    private static readonly Vector4 FlightClear = new(0, 0, 0, 1);

    [TestMethod]
    public void WarpFlightTerrainReadbackSurvivesBothViewAndOriginTransitions() =>
        VerifyFlightReadback(useWarp: true, requireDebugLayer: false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void FlightTerrainReadbackWithDebugLayer(bool useWarp) =>
        VerifyFlightReadback(useWarp, requireDebugLayer: true);

    private static void VerifyFlightReadback(bool useWarp, bool requireDebugLayer)
    {
        var camera = AtRadius(96);
        var terrain = new PlanetTerrainCoordinator();
        var origin = PlanetLocalFrame.Create(AtRadius(64));
        var near = SelectFlight(79, 7, null, PlanetTerrainView.Near);
        var far = SelectFlight(96, 7, near, PlanetTerrainView.Far);
        var landing = SelectFlight(80, 8, far, PlanetTerrainView.Near);
        Assert.IsTrue(origin.TryRebase(camera, out var orbitalFrame));
        Assert.IsTrue(orbitalFrame.TryRebase(AtRadius(76), out var landingFrame));
        Assert.AreEqual(6, far.Tiles.Length);
        Assert.IsGreaterThan(far.Tiles.Length, near.Tiles.Length);

        using var first = StagePlanetPlan(terrain, near, origin, camera);
        var gpu = new GpuDevice(warp: useWarp, enableDebugLayer: true, enableDred: true, region: D3D12Renderer.PackPlanetTerrain(first));
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, useWarp);
            if (requireDebugLayer)
                Assert.IsTrue(gpu.DebugLayerEnabled);
            Assert.IsTrue(gpu.DredEnabled);
            var nearPixels = ReadFlight(gpu, first, 1);
            Assert.IsTrue(terrain.AcknowledgePresented(first));
            first.Dispose();
            using var orbit = StagePlanetPlan(terrain, far, orbitalFrame, camera);
            InstallFlightReadback(gpu, orbit);
            var farPixels = ReadFlight(gpu, orbit, 2);
            Assert.IsTrue(terrain.AcknowledgePresented(orbit));
            Assert.IsFalse(orbit.TryGetSupport(AtRadius(64), out _));
            orbit.Dispose();
            using var approach = StagePlanetPlan(terrain, landing, landingFrame, camera);
            InstallFlightReadback(gpu, approach);
            var approachPixels = ReadFlight(gpu, approach, 3);
            VerifyMixedPlanetSeam(gpu, approach);
            // The view looks straight at the +X/+Z seam. Sample both sides
            // away from the silhouette in all three terrain generations.
            for (var x = 76; x <= 83; x++)
            for (var y = 45; y <= 50; y++)
            {
                Assert.AreEqual(byte.MaxValue, nearPixels.ColorAt(x, y)[1]);
                Assert.AreEqual(byte.MaxValue, farPixels.ColorAt(x, y)[1]);
                Assert.AreEqual(byte.MaxValue, approachPixels.ColorAt(x, y)[1]);
                Assert.IsLessThan(1f, farPixels.DepthAt(x, y));
                Assert.AreEqual(nearPixels.DepthAt(x, y), farPixels.DepthAt(x, y), 0.0002f);
                Assert.AreEqual(nearPixels.DepthAt(x, y), approachPixels.DepthAt(x, y), 0.000002f);
            }
        }

        AssertFlightGpuReleased(gpu);
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    public async Task FlightTransitionRetainsGpuResourcesUntilStopCanSettle(bool useWarp, bool stopBeforeInstall)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var observed = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseUpload = new ManualResetEventSlim();
        var holdRetirement = !stopBeforeInstall ? 1 : 0;
        var uploads = 0;
        var hooks = new GpuRetirementTestHooks(CompletedValue: (gpu, completed) =>
        {
            if (Volatile.Read(ref holdRetirement) == 0 || completed == ulong.MaxValue)
                return completed;
            if (completed >= 6)
                observed.TrySetResult(gpu);
            return Math.Min(completed, 4UL);
        });
        await using var window = new WindowsHostModule(new WindowsHostOptions($"WP09 Flight {Guid.NewGuid():N}", 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, QualificationDiagnostics, useWarp,
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), hooks, gpu =>
            {
                if (++uploads != 3 || !stopBeforeInstall)
                    return;
                observed.TrySetResult(gpu);
                if (!releaseUpload.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The flight fixture did not release the staged upload.");
            });
        var terrain = new PlanetTerrainCoordinator();
        PlanetTerrainLease? approach = null;
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

            var near = SelectFlight(79, 7, null, PlanetTerrainView.Near);
            var far = SelectFlight(96, 7, near, PlanetTerrainView.Far);
            var landing = SelectFlight(80, 8, far, PlanetTerrainView.Near);
            var origin = PlanetLocalFrame.Create(AtRadius(64));
            using (var departure = StagePlanetPlan(terrain, near, origin, AtRadius(79)))
                await PresentFlightAsync(renderer, terrain, departure, 1, token);
            Assert.IsTrue(origin.TryRebase(AtRadius(96), out var orbitFrame));
            using (var orbit = StagePlanetPlan(terrain, far, orbitFrame, AtRadius(96)))
            {
                await PresentFlightAsync(renderer, terrain, orbit, 2, token);
                Assert.IsFalse(orbit.TryGetSupport(AtRadius(64), out _));
            }

            Assert.IsTrue(orbitFrame.TryRebase(AtRadius(76), out var returnFrame));
            approach = StagePlanetPlan(terrain, landing, returnFrame, AtRadius(76));
            Assert.IsFalse(approach.TryGetSupport(AtRadius(64), out _));
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame, SubmitPlanetCamera(renderer, approach, 3));
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, SubmitFlight(renderer, approach, 3));
            var device = await observed.Task.WaitAsync(token);
            if (stopBeforeInstall)
            {
                Assert.IsFalse(renderer.TryReadPresentedPlanetTerrain(approach, 3, out _));
                Assert.AreEqual(2UL, renderer.Read().LastPresentedSequence);
                Assert.IsTrue(terrain.TryReadActive(AtRadius(76), out var old));
                using (old)
                {
                    Assert.IsNotNull(old);
                    Assert.AreEqual(PlanetTerrainView.Far, old.Stamp.View);
                    Assert.IsFalse(old.TryGetSupport(AtRadius(64), out _));
                }
            }
            else
            {
                await VerifyFlightPresentationAsync(renderer, terrain, approach, 3, token);
                Assert.IsTrue(approach.TryGetSupport(AtRadius(64), out _));
                Assert.IsFalse(terrain.TryStage(near, returnFrame));
            }

            var stop = renderer.StopAsync(token).AsTask();
            await AwaitStatus(renderer, state => state.Lifecycle == RendererLifecycle.Stopping, token);
            Assert.IsFalse(stop.IsCompleted);
            Assert.AreEqual(stopBeforeInstall ? 2 : 1, device.LiveMeshBuffers);
            var held = device.LiveMeshBytes;
            Assert.IsGreaterThan(0L, held);
            Assert.IsLessThanOrEqualTo(GpuDevice.MaximumNativeMeshBytes, held);
            Console.WriteLine($"Stop held {device.LiveMeshBuffers} mesh buffer(s), {held} bytes, before installation={stopBeforeInstall}.");
            Volatile.Write(ref holdRetirement, 0);
            releaseUpload.Set();
            await stop;
            if (stopBeforeInstall)
                Assert.AreEqual(2UL, renderer.Read().LastPresentedSequence);
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            AssertFlightGpuReleased(device);
        }
        finally
        {
            Volatile.Write(ref holdRetirement, 0);
            releaseUpload.Set();
            terrain.Stop();
            approach?.Dispose();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private static PlanetTerrainPlan SelectFlight(double radius, ulong editRevision, PlanetTerrainPlan? previous, PlanetTerrainView expected)
    {
        Assert.IsTrue(PlanetTerrainSelection.TrySelectFlight(AtRadius(radius), AtRadius(64), editRevision, previous, out var plan));
        Assert.IsNotNull(plan);
        Assert.AreEqual(expected, plan.View);
        Assert.AreEqual(editRevision, plan.EditRevision);
        return plan;
    }

    private static async Task PresentFlightAsync(D3D12Renderer renderer, PlanetTerrainCoordinator terrain, PlanetTerrainLease lease,
        ulong sequence, CancellationToken token)
    {
        Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, SubmitFlight(renderer, lease, sequence));
        await VerifyFlightPresentationAsync(renderer, terrain, lease, sequence, token);
    }

    private static async Task VerifyFlightPresentationAsync(D3D12Renderer renderer, PlanetTerrainCoordinator terrain, PlanetTerrainLease lease,
        ulong sequence, CancellationToken token)
    {
        await AwaitStatus(renderer, snapshot => renderer.TryReadPresentedPlanetTerrain(lease, sequence, out _), token);
        Assert.IsTrue(renderer.TryReadPresentedPlanetTerrain(lease, sequence, out var stamp));
        Assert.AreEqual(lease.Stamp, stamp);
        Assert.IsFalse(renderer.TryReadPresentedPlanetTerrain(lease, sequence + 1, out _));
        Assert.IsTrue(terrain.AcknowledgePresented(lease));
    }

    private static PlanetVector AtRadius(double radius) => new(radius / Math.Sqrt(2), 0, radius / Math.Sqrt(2));

    private static MeshReplacementSubmissionResult SubmitFlight(D3D12Renderer renderer, PlanetTerrainLease lease, ulong sequence) =>
        renderer.TrySubmitPlanetTerrain(lease, sequence, FlightForward, Vector3.UnitY, MathF.PI / 3, NearPlane, FarPlane, FlightTint, FlightClear);

    private static OffscreenFrame ReadFlight(GpuDevice gpu, PlanetTerrainLease lease, ulong sequence) =>
        GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, D3D12Renderer.CreatePlanetFrame(lease, sequence,
            FlightForward, Vector3.UnitY, MathF.PI / 3, NearPlane, FarPlane, FlightTint, FlightClear));

    private static void InstallFlightReadback(GpuDevice gpu, PlanetTerrainLease lease)
    {
        Assert.IsTrue(gpu.IsIdle);
        var old = gpu.ActiveMeshGeometry;
        gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(D3D12Renderer.PackPlanetTerrain(lease));
        old?.Dispose();
        Assert.AreEqual(1, gpu.LiveMeshBuffers);
    }

    private static void AssertFlightGpuReleased(GpuDevice gpu)
    {
        Assert.AreEqual(0, gpu.LiveMeshBuffers);
        Assert.AreEqual(0L, gpu.LiveMeshBytes);
        AssertCleanDiagnostics(new D3D12DiagnosticsSnapshot(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled,
            gpu.FinalDebugSummary, null), diagnosticsRequested: true);
    }
}
