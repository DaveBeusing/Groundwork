using System.Numerics;
using Groundwork.Core;
using Groundwork.Core.Tests;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public async Task WarpVolumeReadbackCoversTransitionAndCavityAcrossRebase() =>
        await VerifyVolumeReadback(true, false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task VolumeReadbackWithDebugLayerAndDred(bool warp) =>
        await VerifyVolumeReadback(warp, true);

    private static async Task VerifyVolumeReadback(bool warp, bool requireDebug)
    {
        using var run = new VolumeRun();
        var origin = PlanetLocalFrame.Create(default);
        var view = run.Terrain.ChangeView(origin, 1);
        Assert.IsTrue(run.Terrain.ConfirmMask(view));
        await run.Publish(1, cut: false);
        using var first = run.Terrain.TryRead();
        Assert.IsNotNull(first);
        Assert.IsTrue(first.Meshes.ToArray().Any(mesh => mesh.Triangles.ToArray().Any(t => t.IsTransition)));
        Assert.IsNull(D3D12Renderer.PackVolumeTerrain(first.Meshes, origin, 65535));
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true,
            region: D3D12Renderer.PackVolumeTerrain(first.Meshes, origin, VolumeRun.NativeBytes));
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DredEnabled);
            if (requireDebug)
                Assert.IsTrue(gpu.DebugLayerEnabled);
            // The camera sits inside a cavity: solid floor and roof coexist on one ray.
            var camera = VolumeCameraAt(view, 32, 4);
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 1, out var frame));
            var before = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            var hits = new VolumeRayHit[4];
            Assert.AreEqual(2, first.Raycast(new(32, 0, -4), new(32, 0, 12), hits).Count);
            Assert.IsTrue(hits[0].IsTransition, "The floor readback is centered on actual transition geometry.");
            AssertVolumeFloor(before);
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera with { Forward = Vector3.UnitZ }, 1, out var roofFrame));
            AssertVolumeFloor(GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, roofFrame));

            Assert.IsTrue(origin.TryRebase(new PlanetVector(32, 0, 0), out var rebased));
            view = run.Terrain.ChangeView(rebased, 1);
            Assert.IsTrue(run.Terrain.ConfirmMask(view));
            await run.Publish(2, cut: true);
            using var second = run.Terrain.TryRead();
            Assert.IsNotNull(second);
            var packed = D3D12Renderer.PackVolumeTerrain(second.Meshes, rebased, VolumeRun.NativeBytes);
            Assert.IsNotNull(packed);
            var old = gpu.ActiveMeshGeometry;
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(packed, VolumeRun.NativeBytes);
            old?.Dispose();
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(VolumeCameraAt(view, 32, 4), 2, out frame));
            var after = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            AssertVolumeFloor(after);
            Assert.IsLessThan(after.DepthAt(80, 48), before.DepthAt(80, 48));
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(VolumeCameraAt(view, 32, 6), 3, out frame));
            var far = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            AssertVolumeFloor(far);
            Assert.IsLessThan(far.DepthAt(80, 48), after.DepthAt(80, 48));
            Assert.AreEqual(2UL, second.Info.TerrainRevision);
            Assert.AreEqual(1UL, first.Info.TerrainRevision);
            Assert.AreEqual(1, gpu.LiveMeshBuffers);
            Assert.IsNull(gpu.CaptureDred());
        }
        Assert.AreEqual(0L, gpu.LiveMeshBytes);
        AssertCleanDiagnostics(new D3D12DiagnosticsSnapshot(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled,
            gpu.FinalDebugSummary, null), diagnosticsRequested: true);
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task VolumePresentRetainsGenerationUntilFenceAndRejectsStaleView(bool warp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = timeout.Token;
        using var run = new VolumeRun();
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        var observed = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = 1;
        var hooks = new GpuRetirementTestHooks(CompletedValue: (gpu, completed) =>
        {
            if (Volatile.Read(ref hold) == 0 || completed == ulong.MaxValue)
                return completed;
            if (completed >= 4)
                observed.TrySetResult(gpu);
            return Math.Min(completed, 2UL);
        });
        var title = $"Groundwork volume {Guid.NewGuid():N}";
        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1, QualificationDiagnostics,
            warp, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), hooks);
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, warp);
            Assert.IsTrue(diagnostics.DebugLayerEnabled);
            Assert.IsTrue(diagnostics.DredEnabled);

            var view = run.Terrain.View;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 1, Vector4.Zero));
            await AwaitStatus(renderer, s => s.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            var generation = ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Terrain.Generation;
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(generation, 2, VolumeCameraAt(view, 32, 4)));
            var device = await observed.Task.WaitAsync(token);
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual(1, run.Terrain.Statistics.Readers);
            Assert.AreEqual(1, device.LiveMeshBuffers);
            Assert.AreEqual(MeshReplacementSubmissionResult.DuplicateGeneration,
                renderer.TrySubmitVolumeTerrain(generation, 3, VolumeCameraAt(view, 32, 4)));

            var next = run.Terrain.ChangeView(PlanetLocalFrame.Create(default), 1);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(next, 3, Vector4.Zero));
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame, renderer.TrySubmitVolumeCamera(generation, 4, VolumeCameraAt(view, 32, 4)));
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
            var hwnd = FindWindow(null, title);
            Assert.AreNotEqual(nint.Zero, hwnd);
            var initialWidth = renderer.Read().AppliedWidth;
            Assert.IsTrue(SetWindowPos(hwnd, 0, 0, 0, 480, 340, 0x0016));
            await AwaitStatus(renderer, _ => window.Read().ClientWidth != initialWidth, token);
            Assert.AreEqual(initialWidth, renderer.Read().AppliedWidth, "Resize must wait for the post-Present fence.");
            Volatile.Write(ref hold, 0);
            await AwaitStatus(renderer, s => s.LastPresentedSequence == 3, token);
            Assert.AreEqual(window.Read().ClientWidth, renderer.Read().AppliedWidth);
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            await run.Publish(2, cut: true, renderer);
            generation = ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Terrain.Generation;
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(generation, 4, VolumeCameraAt(next, 32, 4)));
            await AwaitStatus(renderer, s => s.LastPresentedSequence == 4, token);
            await run.OpenMovement();
            Assert.IsFalse(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual(1, run.Terrain.Statistics.Readers);
            Assert.IsTrue(PostMessage(hwnd, 0x0010, 0, 0)); // WM_CLOSE
            await AwaitStatus(renderer, s => s.Lifecycle == RendererLifecycle.Stopped, token);
            await renderer.StopAsync(token);
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual(0, run.Terrain.Statistics.Readers);
            Assert.AreEqual(0L, device.LiveMeshBytes);
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
        }
        finally
        {
            Volatile.Write(ref hold, 0);
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task VolumeFailedPresentSignalRetainsRegistrationUntilConfirmedRemoval()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var returnSignal = new ManualResetEventSlim();
        using var remove = new ManualResetEventSlim();
        var token = timeout.Token;
        using var run = new VolumeRun();
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        var reached = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = 0;
        var hooks = new GpuRetirementTestHooks(SignalFailure: (gpu, value) =>
        {
            if (value != 4)
                return null;
            reached.TrySetResult(gpu);
            if (!returnSignal.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the Present signal.");
            return unchecked((int)0x80004005);
        }, CompletedValue: (gpu, completed) =>
        {
            if (!reached.Task.IsCompleted)
                return completed;
            held.TrySetResult(gpu.RemovedReason);
            if (remove.IsSet && removed == 0)
                removed = RemoveWarpRetirementDevice(gpu);
            return ulong.MaxValue;
        });
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork volume fault {Guid.NewGuid():N}", 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1, QualificationDiagnostics,
            true, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), hooks);
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            var view = run.Terrain.View;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 1, Vector4.Zero));
            await AwaitStatus(renderer, s => s.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            var generation = ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Terrain.Generation;
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(generation, 2, VolumeCameraAt(view, 32, 4)));
            var gpu = await reached.Task.WaitAsync(token);
            Assert.AreEqual(1, run.Terrain.Statistics.Readers);
            Assert.AreEqual(1, gpu.LiveMeshBuffers);
            returnSignal.Set();
            Assert.AreEqual(0, await held.Task.WaitAsync(token), "A synthetic fence sentinel is not device removal.");
            while (renderer.Read().FirstFault is null)
                await Task.Delay(10, token);
            Assert.AreEqual(new RenderFaultSummary((uint)RendererStage.Present, unchecked((int)0x80004005), null, 2),
                renderer.Read().FirstFault);
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual(1, run.Terrain.Statistics.Readers);
            Assert.AreEqual(1, gpu.LiveMeshBuffers);

            remove.Set();
            while (renderer.Read().Lifecycle != RendererLifecycle.Stopped)
                await Task.Delay(10, token);
            Assert.AreEqual(0, run.Terrain.Statistics.Readers);
            Assert.AreEqual(0L, gpu.LiveMeshBytes);
            var diagnostics = renderer.ReadDiagnostics();
            Assert.IsTrue(diagnostics.DebugLayerEnabled);
            Assert.IsTrue(diagnostics.DredEnabled);
            Assert.IsNotNull(diagnostics.DeviceRemoval);
            Assert.AreEqual(removed, diagnostics.DeviceRemoval.Reason);
            Console.WriteLine($"Volume Present signal failed; registration retained through sentinel and released after WARP removal 0x{removed:X8}.");
        }
        finally
        {
            returnSignal.Set();
            remove.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private static VolumeCamera VolumeCameraAt(VolumeViewIdentity view, double x, double z) =>
        new(view, new(x, 0, z), -Vector3.UnitZ, Vector3.UnitY, MathF.PI / 4, 0.1f, 100, Vector4.One, Vector4.Zero);

    private static void AssertVolumeFloor(OffscreenFrame image)
    {
        for (var y = 44; y <= 52; y++)
        for (var x = 74; x <= 86; x++)
        {
            Assert.IsLessThan(1f, image.DepthAt(x, y));
            Assert.AreEqual((byte)0x5a, image.ColorAt(x, y)[0]);
        }
    }

    private sealed class VolumeClock : CoreRuntimeFixture.TestClock { }

    private sealed class VolumeRun : IDisposable
    {
        internal const long NativeBytes = 4 * 1024 * 1024;
        private const long MeshBytes = 8 * 1024 * 1024;
        private readonly VolumeStreamingSelection _selection;
        private readonly VolumeResources _unit;
        private ulong _tick;
        internal CoreRuntimeFixture Fixture { get; } = new(new VolumeClock());
        internal WorldSafetyPorts World { get; }
        internal VolumeStreamingCoordinator Terrain { get; }

        internal VolumeRun()
        {
            World = Fixture.OpenWorld();
            var descriptor = new PlanetDescriptor(new Guid("00112233-4455-6677-8899-aabbccddeeff"),
                new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"), 42, 1, 1, 1, new string('a', 64), 128);
            _selection = VolumeStreamingSelection.Select(descriptor, default, new PlanetVector(-1, 0, 0));
            _unit = new(VolumeStreamingCoordinator.RequiredCpuBytes(_selection, MeshBytes),
                NativeBytes, NativeBytes, NativeBytes, 1, 1, 1, 64, 1);
            var baseline = _unit.Retained + _unit.Retained;
            baseline = baseline with { LeaseSlots = baseline.LeaseSlots + 8 };
            Terrain = new(World, descriptor, new VolumeStreamingLimits(baseline + _unit + _unit + _unit + _unit,
                baseline, _unit, _unit, _unit, _unit, 12, 8, 128));
        }

        internal async Task Publish(ulong revision, bool cut, D3D12Renderer? renderer = null)
        {
            Assert.AreEqual(VolumeAdmission.Accepted, Terrain.TryStage(VolumeWorkClass.Movement, _selection,
                revision - 1, revision, MeshBytes, _unit, new VolumeFixtureSource(_selection.Descriptor, cut), out var work));
            Assert.IsNotNull(work);
            using (work)
            {
                Assert.IsTrue(work.Execute());
                await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, ++_tick, BarrierKind.BeginTick);
                var candidates = await CoreRuntimeFixture.SealAsync(World, _tick, BarrierKind.CommitResults);
                var prepared = renderer is null
                    ? Terrain.TryPreparePublication(work.Generation, candidates, [])
                    : renderer.TryPrepareVolumePublication(work.Generation, candidates, [], out _);
                Assert.IsNotNull(prepared);
                var outcome = World.Publication.Finalization.TryFinalize(prepared);
                Assert.AreEqual(BarrierStatus.Committed, outcome.Status);
                CoreRuntimeFixture.PublishObserver(World, outcome.Stamp!.Value, (int)_tick);
            }
        }

        internal async Task OpenMovement()
        {
            var begin = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, ++_tick, BarrierKind.BeginTick);
            Assert.IsTrue(Terrain.BeginTick(begin));
            var commit = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, _tick, BarrierKind.CommitResults);
            CoreRuntimeFixture.PublishObserver(World, commit, (int)_tick);
        }

        public void Dispose()
        {
            Terrain.Stop();
            Fixture.Dispose();
        }
    }

    private sealed class VolumeFixtureSource(PlanetDescriptor descriptor, bool cut) : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => descriptor;
        public long SampleDensity(LatticeCoordinate p) => Math.Min(cut ? p.Z + 1 : p.Z, 8 - p.Z);
        public ushort SampleMaterial(LatticeCoordinate p) => 1;
    }
}
