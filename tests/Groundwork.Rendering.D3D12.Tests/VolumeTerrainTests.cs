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

    [TestMethod]
    public async Task WarpVolumeColorPolicyReadbackAcrossLodSeamAndRebase() =>
        await VerifyVolumeReadback(true, false, TestVolumeColor);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task VolumeColorPolicyReadbackWithDebugLayerAndDred(bool warp) =>
        await VerifyVolumeReadback(warp, true, TestVolumeColor);

    private static uint? TestVolumeColor(PlanetVector position, ushort material) =>
        0xff0000ccu | ((uint)material & 0xff) << 16 | ((uint)Math.Floor(position.X) & 0xff) << 8;

    private static async Task VerifyVolumeReadback(bool warp, bool requireDebug, VolumeTerrainColorEvaluator? colors = null)
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
        var initial = Pack(first.Meshes, origin);
        if (colors is not null)
        {
            Assert.IsTrue(origin.TryRebase(new PlanetVector(32, 0, 0), out var shifted));
            var rebuilt = Pack(first.Meshes, shifted);
            Assert.AreEqual(initial.Instances.Length, rebuilt.Instances.Length);
            var vertex = 0;
            foreach (var mesh in first.Meshes.ToArray())
            foreach (var triangle in mesh.Triangles.ToArray())
            {
                Check(triangle.A);
                Check(triangle.B);
                Check(triangle.C);

                void Check(VolumeVertex source)
                {
                    var cell = mesh.Descriptor.CellSizeMeters;
                    var position = new PlanetVector((mesh.Origin.X + (double)source.Position.X) * cell,
                        (mesh.Origin.Y + (double)source.Position.Y) * cell, (mesh.Origin.Z + (double)source.Position.Z) * cell);
                    var expected = TestVolumeColor(position, source.Material).GetValueOrDefault();
                    var batch = vertex / RenderMesh.MaximumVertexCount;
                    var index = vertex++ % RenderMesh.MaximumVertexCount;
                    Assert.AreEqual(expected, initial.Instances[batch].Mesh.Colors[index]);
                    Assert.AreEqual(expected, rebuilt.Instances[batch].Mesh.Colors[index]);
                    var packedNormal = initial.Instances[batch].Mesh.PackedNormals[index];
                    Assert.AreEqual(packedNormal, rebuilt.Instances[batch].Mesh.PackedNormals[index]);
                    var decoded = DecodeTerrainNormal(packedNormal);
                    Assert.IsGreaterThan(0.99999f, Vector3.Dot(Vector3.Normalize(source.Normal), decoded));
                }
            }
        }
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, region: initial);
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
            Assert.AreEqual(2, first.Raycast(new(32, 32, -4), new(32, 32, 12), hits).Count);
            Assert.IsTrue(hits[0].IsTransition, "The floor readback is centered on actual transition geometry.");
            Assert.AreEqual(PlanetFace.PositiveY, PlanetCoordinates.Project(new(31, 32, 0)).Face);
            Assert.AreEqual(PlanetFace.PositiveX, PlanetCoordinates.Project(new(33, 32, 0)).Face);
            AssertVolumeFloor(before, colors is null ? (byte)0x5a : (byte)0xcc);
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera with { Forward = Vector3.UnitZ }, 1, out var roofFrame));
            AssertVolumeFloor(GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, roofFrame), colors is null ? (byte)0x5a : (byte)0xcc);

            Assert.IsTrue(origin.TryRebase(new PlanetVector(32, 0, 0), out var rebased));
            view = run.Terrain.ChangeView(rebased, 1);
            Assert.IsTrue(run.Terrain.ConfirmMask(view));
            await run.Publish(2, cut: true);
            using var second = run.Terrain.TryRead();
            Assert.IsNotNull(second);
            var packed = Pack(second.Meshes, rebased);
            Assert.IsNotNull(packed);
            var old = gpu.ActiveMeshGeometry;
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(packed, VolumeRun.NativeBytes);
            old?.Dispose();
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(VolumeCameraAt(view, 32, 4), 2, out frame));
            var after = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            AssertVolumeFloor(after, colors is null ? (byte)0x5a : (byte)0xcc);
            Assert.IsLessThan(after.DepthAt(80, 48), before.DepthAt(80, 48));
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(VolumeCameraAt(view, 32, 6), 3, out frame));
            var far = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            AssertVolumeFloor(far, colors is null ? (byte)0x5a : (byte)0xcc);
            Assert.IsLessThan(far.DepthAt(80, 48), after.DepthAt(80, 48));
            Assert.AreEqual(2UL, second.Info.TerrainRevision);
            Assert.AreEqual(1UL, first.Info.TerrainRevision);
            Assert.AreEqual(1, gpu.LiveMeshBuffers);
            Assert.IsNull(gpu.CaptureDred());
        }
        Assert.AreEqual(0L, gpu.LiveMeshBytes);
        AssertCleanDiagnostics(new D3D12DiagnosticsSnapshot(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled,
            gpu.FinalDebugSummary, null), diagnosticsRequested: true);

        RenderMeshRegion Pack(ReadOnlySpan<PlanetVolumeMesh> meshes, PlanetLocalFrame frame)
        {
            var region = D3D12Renderer.PackVolumeTerrain(meshes, frame, VolumeRun.NativeBytes, long.MaxValue, colors, out var rejected);
            Assert.IsFalse(rejected);
            Assert.IsNotNull(region);
            return region;
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task VolumePresentRetainsGenerationUntilFenceAndRejectsStaleView(bool warp)
    {
        var measure = Environment.GetEnvironmentVariable("GROUNDWORK_Q01_MEASURE") == "1";
        var phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = timeout.Token;
        using var run = new VolumeRun(readers: 2);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        var observed = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = 1;
        var holdNext = 0;
        var nextObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new GpuRetirementTestHooks(CompletedValue: (gpu, completed) =>
        {
            if (completed == ulong.MaxValue)
                return completed;
            if (Volatile.Read(ref holdNext) != 0 && completed >= 8)
            {
                nextObserved.TrySetResult();
                return 6;
            }
            if (Volatile.Read(ref hold) == 0)
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
            Assert.IsFalse(run.Terrain.ConfirmMask(view), "The bound renderer owns mask evidence.");
            Assert.IsNull(run.Terrain.TryBindRenderer(1));
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
            Sample("first-fence-held", device);
            Assert.AreEqual(MeshReplacementSubmissionResult.DuplicateGeneration,
                renderer.TrySubmitVolumeTerrain(generation, 3, VolumeCameraAt(view, 32, 4)));

            Assert.IsTrue(view.Frame.TryRebase(new PlanetVector(32, 0, 0), out var rebased));
            var next = run.Terrain.ChangeView(rebased, 1);
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
            Volatile.Write(ref holdNext, 1);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(generation, 4, VolumeCameraAt(next, 32, 4)));
            await nextObserved.Task.WaitAsync(token);
            Sample("replacement-fence-held", device);
            var alreadyFinalized = await run.BeginHeldTick();
            Volatile.Write(ref holdNext, 0);
            await AwaitStatus(renderer, s => s.LastPresentedSequence == 4, token);
            Assert.IsFalse(run.Terrain.BeginTick(alreadyFinalized), "An ACK cannot release a previously finalized tick.");
            await run.FinishHeldTick();
            await run.OpenMovement();
            Assert.IsFalse(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual(RenderSubmissionResult.Accepted,
                renderer.TrySubmitVolumeCamera(generation, 5, VolumeCameraAt(next, 32, 6)));
            await AwaitStatus(renderer, s => s.LastPresentedSequence == 5, token);
            var registeredCamera = false;
            while (run.Terrain.TryReadEvent(out var record))
                registeredCamera |= record.Kind == VolumeEventKind.Registered && record.Frame == 5;
            Assert.IsTrue(registeredCamera);
            Assert.AreEqual(1, run.Terrain.Statistics.Readers);
            Assert.IsTrue(PostMessage(hwnd, 0x0010, 0, 0)); // WM_CLOSE
            await AwaitStatus(renderer, s => s.Lifecycle == RendererLifecycle.Stopped, token);
            await renderer.StopAsync(token);
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual(0, run.Terrain.Statistics.Readers);
            Assert.AreEqual(0L, device.LiveMeshBytes);
            Sample("shutdown", device);
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
        }
        finally
        {
            Volatile.Write(ref hold, 0);
            Volatile.Write(ref holdNext, 0);
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
        static object ResourceVector(VolumeResources resources) => new
        {
            resources.CpuBytes, resources.NativeBytes, resources.UploadBytes, resources.RetirementBytes,
            resources.DensityJobs, resources.MeshJobs, resources.QueueSlots, resources.LeaseSlots, resources.RetirementSlots,
        };
        void Sample(string phase, GpuDevice device)
        {
            if (!measure)
                return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var stats = run.Terrain.Statistics;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Console.WriteLine("Q01 " + System.Text.Json.JsonSerializer.Serialize(new
            {
                route = warp ? "warp-fence" : "hardware-fence",
                phase,
                elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart, now).TotalMilliseconds,
                used = ResourceVector(stats.Used),
                peak = ResourceVector(stats.Peak),
                stats.Readers,
                stats.Generations,
                stats.MovementHeld,
                device.LiveMeshBytes,
                device.LiveMeshBuffers,
                process.WorkingSet64,
                process.PrivateMemorySize64,
            }));
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(0)] // Watchdog expiry with an unobserved fence.
    [DataRow(3)] // Draw Signal.
    [DataRow(4)] // Present Signal.
    [DataRow(5)] // Hard Present HRESULT.
    public async Task VolumeFaultRetainsRegistrationUntilConfirmedRemoval(int failurePoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var returnSignal = new ManualResetEventSlim();
        using var remove = new ManualResetEventSlim();
        var token = timeout.Token;
        using var run = new VolumeRun(readers: 1);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        var reached = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = 0;
        var presents = 0;
        const int injectedFailure = unchecked((int)0x80004005);
        var hooks = new GpuRetirementTestHooks(SignalFailure: (gpu, value) =>
        {
            if (failurePoint is not (3 or 4) || value != (ulong)failurePoint)
                return null;
            reached.TrySetResult(gpu);
            if (!returnSignal.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the Present signal.");
            return injectedFailure;
        }, CompletedValue: (gpu, completed) =>
        {
            if (failurePoint == 0 && completed >= 4)
                reached.TrySetResult(gpu);
            if (!reached.Task.IsCompleted)
                return completed;
            held.TrySetResult(gpu.RemovedReason);
            if (remove.IsSet && removed == 0)
                removed = RemoveWarpRetirementDevice(gpu);
            return ulong.MaxValue;
        }, PresentFailure: gpu =>
        {
            if (failurePoint != 5 || ++presents != 2)
                return null;
            reached.TrySetResult(gpu);
            return injectedFailure;
        });
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork volume fault {Guid.NewGuid():N}", 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1, QualificationDiagnostics,
            true, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(failurePoint == 0 ? 1 : 15), hooks);
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
            var expectedStage = failurePoint switch
            {
                0 => RendererStage.Retire,
                3 => RendererStage.Submit,
                _ => RendererStage.Present
            };
            var fault = renderer.Read().FirstFault!.Value;
            Assert.AreEqual((uint)expectedStage, fault.Stage);
            Assert.AreEqual(2UL, fault.FrameSequence);
            Assert.AreEqual(failurePoint == 0 ? new TimeoutException().HResult : injectedFailure, fault.ErrorCode);
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual(1, run.Terrain.Statistics.Readers);
            Assert.AreEqual(1, gpu.LiveMeshBuffers);

            if (failurePoint == 0)
            {
                runtime.StopControl.RequestStop(HostStopReason.RuntimeFault);
                run.Fixture.Clock.Advance(TimeSpan.FromSeconds(15));
                await Assert.ThrowsAsync<IncompleteCleanupException>(() => renderer.StopAsync(token).AsTask());
                Assert.AreEqual(1, run.Terrain.Statistics.Readers);
            }
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
            Console.WriteLine($"Volume fault at {expectedStage}; registration retained through sentinel and released after WARP removal 0x{removed:X8}.");
        }
        finally
        {
            returnSignal.Set();
            remove.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidatedRendererCannotCommitAfterLossOrReopenItsEpoch(bool swapFirst)
    {
        using var run = new VolumeRun();
        await run.VerifyInvalidationOrder(swapFirst);
    }

    private static VolumeCamera VolumeCameraAt(VolumeViewIdentity view, double x, double z) =>
        new(view, new(x, 32, z), -Vector3.UnitZ, Vector3.UnitY, MathF.PI / 4, 0.1f, 100, Vector4.One, Vector4.Zero);

    private static void AssertVolumeFloor(OffscreenFrame image, byte red = 0x5a)
    {
        for (var y = 44; y <= 52; y++)
        for (var x = 74; x <= 86; x++)
        {
            Assert.IsLessThan(1f, image.DepthAt(x, y));
            Assert.AreEqual(red, image.ColorAt(x, y)[0]);
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task WarpEditedTerrainRebaseRetiresTheMatchingCollisionRevision() =>
        await VerifyEditedTerrainRebase(true, false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task VolumeColorPolicyRejectsPrivateCandidateAndSurvivesEdit(bool warp) =>
        await VerifyEditedTerrainRebase(warp, true);

    private static async Task VerifyEditedTerrainRebase(bool warp, bool customColors)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        using var run = new VolumeRun(editable: true);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork edited volume {Guid.NewGuid():N}", 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        var calls = 0;
        var reject = 0;
        uint? Evaluate(PlanetVector position, ushort material)
        {
            calls++;
            return reject switch
            {
                1 => null,
                2 => throw new InvalidOperationException("test policy failure"),
                _ => TestVolumeColor(position, material)
            };
        }

        await using var renderer = customColors
            ? D3D12Renderer.CreateWithColoredVolumeTerrain(window, runtime.RuntimeOwnership, run.Terrain, 1,
                Evaluate, QualificationDiagnostics, useWarp: warp, startupTimeout: TimeSpan.FromSeconds(15))
            : D3D12Renderer.CreateWithVolumeTerrain(window, runtime.RuntimeOwnership, run.Terrain, 1,
                QualificationDiagnostics, useWarp: warp, startupTimeout: TimeSpan.FromSeconds(15));
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            var view = run.Terrain.View;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 1, Vector4.Zero));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            using var old = run.Terrain.TryRead();
            Assert.IsNotNull(old);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(old.Info.Generation, 2, VolumeCameraAt(view, 32, 4)));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 2, token);

            if (customColors)
            {
                Assert.IsGreaterThan(0, calls);
                var packedCalls = calls;
                var fog = new RenderFog(Vector3.UnitZ, 1, 8, 0.5f);
                var camera = VolumeCameraAt(view, 32, 4) with { Fog = fog };
                Assert.AreEqual(RenderSubmissionResult.InvalidFrame,
                    renderer.TrySubmitVolumeCamera(old.Info.Generation, 3, camera with { Fog = fog with { EndDepthMeters = 1 } }));
                Assert.AreEqual(2UL, renderer.Read().LastAcceptedSequence);
                Assert.AreEqual(RenderSubmissionResult.Accepted,
                    renderer.TrySubmitVolumeCamera(old.Info.Generation, 3, camera));
                await AwaitStatus(renderer, state => state.LastPresentedSequence == 3, token);
                Assert.AreEqual(packedCalls, calls);
                using var sameTerrain = run.Terrain.TryRead();
                Assert.IsNotNull(sameTerrain);
                Assert.AreEqual(old.Info, sameTerrain.Info);
                using var rejected = run.Stage(VolumeWorkClass.Movement, 1, 1, cut: false);
                Assert.IsTrue(rejected.Execute());
                foreach (var mode in new[] { 1, 2 })
                {
                    reject = mode;
                    Assert.AreEqual(MeshReplacementSubmissionResult.InvalidRegion, renderer.TryStageVolumeTerrain(rejected.Generation));
                    Assert.IsFalse(renderer.IsVolumeTerrainReady(rejected.Generation));
                    using var active = run.Terrain.TryRead();
                    Assert.IsNotNull(active);
                    Assert.AreEqual(old.Info, active.Info);
                }
                reject = 0;
            }

            var mask = customColors ? 4UL : 3UL;
            var next = await run.CutFloor(renderer, token, maskSequence: mask);
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            using var edited = run.Terrain.TryRead();
            Assert.IsNotNull(edited);
            Assert.AreEqual(2UL, edited.Info.TerrainRevision);
            Assert.AreNotEqual(view.Frame, next.Frame);
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame,
                renderer.TrySubmitVolumeCamera(old.Info.Generation, mask + 1, VolumeCameraAt(view, 32, 4)));
            var editedCamera = VolumeCameraAt(next, 32, 4);
            if (customColors)
            {
                var fog = new RenderFog(Vector3.UnitZ, 1, 8, 0.5f);
                Assert.AreEqual(MeshReplacementSubmissionResult.InvalidRegion,
                    renderer.TrySubmitVolumeTerrain(edited.Info.Generation, mask + 1, editedCamera with { Fog = fog with { MaxOpacity = float.NaN } }));
                Assert.AreEqual(mask, renderer.Read().LastAcceptedSequence);
                editedCamera = editedCamera with { Fog = fog };
            }
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(edited.Info.Generation, mask + 1, editedCamera));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == mask + 1, token);
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            await run.OpenMovement();
            Assert.IsFalse(run.Terrain.Statistics.MovementHeld);

            var beforeHits = new VolumeRayHit[4];
            var afterHits = new VolumeRayHit[4];
            Assert.AreEqual(1, old.Raycast(new(32, 32, 4), new(32, 32, -8), beforeHits).Count);
            Assert.AreEqual(1, edited.Raycast(new(32, 32, 4), new(32, 32, -8), afterHits).Count);
            Assert.IsLessThan(afterHits[0].Distance, beforeHits[0].Distance);
            await renderer.StopAsync(token);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, warp);
            AssertCleanDiagnostics(diagnostics, diagnosticsRequested: true);

            using var gpu = new GpuDevice(true, enableDebugLayer: true, enableDred: true);
            var oldRegion = D3D12Renderer.PackVolumeTerrain(old.Meshes, view.Frame, VolumeRun.NativeBytes);
            Assert.IsNotNull(oldRegion);
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(oldRegion);
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(VolumeCameraAt(view, 32, 4), 1, out var frame));
            var before = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            gpu.ActiveMeshGeometry.Dispose();
            var editedRegion = D3D12Renderer.PackVolumeTerrain(edited.Meshes, next.Frame, VolumeRun.NativeBytes);
            Assert.IsNotNull(editedRegion);
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(editedRegion);
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(VolumeCameraAt(next, 32, 4), 2, out frame));
            var after = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            for (var y = 45; y <= 51; y++)
            for (var x = 75; x <= 85; x++)
            {
                Assert.IsLessThan(1f, after.DepthAt(x, y));
                Assert.IsLessThan(after.DepthAt(x, y), before.DepthAt(x, y));
            }
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private sealed class VolumeClock : CoreRuntimeFixture.TestClock { }

    private sealed class VolumeRun : IDisposable
    {
        internal const long NativeBytes = 4 * 1024 * 1024;
        internal const long CaveNativeBytes = 32 * 1024 * 1024;
        private readonly long _meshBytes;
        private readonly PlanetVector _camera;
        private readonly PlanetVector _forward;
        private readonly PlanetVector _displacement;
        private readonly VolumeStreamingSelection _selection;
        private readonly VolumeResources _unit;
        private readonly IPlanetBrickSource? _editSource;
        private ulong _tick;
        internal CoreRuntimeFixture Fixture { get; } = new(new VolumeClock());
        internal WorldSafetyPorts World { get; }
        internal VolumeStreamingCoordinator Terrain { get; }

        internal VolumeRun(int readers = 8, bool editable = false,
            PlanetVector? camera = null, PlanetVector? forward = null,
            PlanetGeologyField? geology = null, PlanetVector displacement = default)
        {
            World = Fixture.OpenWorld();
            var descriptor = geology?.Descriptor ?? new PlanetDescriptor(new Guid("00112233-4455-6677-8899-aabbccddeeff"),
                new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"), 42, 1, 1, 1, new string('a', 64), 128);
            _camera = camera ?? new PlanetVector(0, 32, 0);
            _forward = forward ?? new PlanetVector(-1, 0, 0);
            _displacement = displacement;
            _selection = geology is null ? VolumeStreamingSelection.Select(descriptor, _camera, _forward) :
                SelectCave(descriptor);
            var overlayRows = geology is null ? 1024 : 4096;
            _editSource = (IPlanetBrickSource?)geology ?? (editable ? new VolumeFixtureSource(descriptor, false) : null);
            _meshBytes = geology is null ? 8 * 1024 * 1024 : 32 * 1024 * 1024;
            var native = geology is null ? NativeBytes : CaveNativeBytes;
            _unit = new(VolumeStreamingCoordinator.RequiredCpuBytes(_selection, _meshBytes) + (_editSource is not null ? 64L * overlayRows + 4096 : 0),
                native, native, native, 1, 1, 1, 64, 1);
            var baseline = _unit.Retained + _unit.Retained;
            baseline = baseline with { LeaseSlots = baseline.LeaseSlots + 8 };
            var limits = new VolumeStreamingLimits(baseline + _unit + _unit + _unit + _unit,
                baseline, _unit, _unit, _unit, _unit, 12, readers, 128);
            Terrain = _editSource is null ? new(World, descriptor, limits) : new(World, _editSource, limits, overlayRows);
        }

        private VolumeStreamingSelection SelectCave(PlanetDescriptor descriptor, VolumeEditDependencies? edit = null) =>
            VolumeStreamingSelection.SelectCave(descriptor, _camera, _forward, new(_camera, _camera with { Z = _camera.Z + 1 }, 0.5),
                _displacement, edit);

        internal PlanetDescriptor Descriptor => _selection.Descriptor;

        internal VolumeStreamingWork Stage(VolumeWorkClass kind, ulong sourceRevision, ulong targetRevision, bool cut, IPlanetBrickSource? source = null)
        {
            Assert.AreEqual(VolumeAdmission.Accepted, Terrain.TryStage(kind, _selection, sourceRevision,
                targetRevision, _meshBytes, _unit, source ?? _editSource ?? new VolumeFixtureSource(_selection.Descriptor, cut), out var work));
            Assert.IsNotNull(work);
            return work;
        }
        internal async Task Publish(ulong revision, bool cut, D3D12Renderer? renderer = null)
        {
            Assert.AreEqual(VolumeAdmission.Accepted, Terrain.TryStage(VolumeWorkClass.Movement, _selection,
                revision - 1, revision, _meshBytes, _unit, _editSource ?? new VolumeFixtureSource(_selection.Descriptor, cut), out var work));
            Assert.IsNotNull(work);
            using (work)
            {
                Assert.IsTrue(work.Execute());
                await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, ++_tick, BarrierKind.BeginTick);
                var candidates = await CoreRuntimeFixture.SealAsync(World, _tick, BarrierKind.CommitResults);
                PreparedBarrierPublication? prepared;
                if (renderer is null)
                    prepared = Terrain.TryPreparePublication(work.Generation, candidates, []);
                else
                {
                    Assert.IsNull(renderer.TryPrepareVolumePublication(work.Generation, candidates, [], out var premature));
                    Assert.AreEqual(MeshReplacementSubmissionResult.Backpressure, premature);
                    Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TryStageVolumeTerrain(work.Generation));
                    using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await AwaitStatus(renderer, _ => renderer.IsVolumeTerrainReady(work.Generation), readyDeadline.Token);
                    prepared = renderer.TryPrepareVolumePublication(work.Generation, candidates, [], out var result);
                    Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, result);
                }
                Assert.IsNotNull(prepared);
                var outcome = World.Publication.Finalization.TryFinalize(prepared);
                Assert.AreEqual(BarrierStatus.Committed, outcome.Status);
                CoreRuntimeFixture.PublishObserver(World, outcome.Stamp!.Value, (int)_tick);
            }
        }

        internal async Task<VolumeViewIdentity> CutFloor(D3D12Renderer? renderer, CancellationToken token,
            VolumeBrushIntent? brush = null, PlanetVector? rebase = null, ulong maskSequence = 3)
        {
            var begin = await BeginHeldTick();
            Assert.IsTrue(Terrain.BeginTick(begin));
            var edit = brush is { } requested ? requested with { Tick = begin.Tick } :
                new VolumeBrushIntent(begin.Tick, 1, 1, new(28, 28, -2), new(36, 36, 1), VolumeBrushOperation.Remove);
            var selection = _selection;
            if (_editSource is PlanetGeologyField && VolumeStreamingCoordinator.TryGetEditDependencies(edit, out var footprint))
                selection = SelectCave(_selection.Descriptor, footprint);
            Assert.AreEqual(VolumeAdmission.Accepted, Terrain.SealEditTick(begin.Tick, _selection.Descriptor, [edit]));
            var view = Terrain.View;
            Assert.IsTrue(view.Frame.TryRebase(rebase ?? new PlanetVector(32, 32, 0), out var frame));
            var next = Terrain.ChangeView(frame, view.RendererEpoch);
            if (renderer is null)
                Assert.IsTrue(Terrain.ConfirmMask(next));
            else
                Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(next, maskSequence, Vector4.Zero));
            Assert.AreEqual(VolumeAdmission.Accepted, Terrain.TryStageEdit(begin.Tick, edit.Id, selection, _meshBytes, _unit, out var work));
            Assert.IsNotNull(work);
            using (work)
            {
                Assert.IsTrue(work.Execute(token));
                if (renderer is not null)
                {
                    Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TryStageVolumeTerrain(work.Generation));
                    await AwaitStatus(renderer, state => state.LastPresentedSequence == maskSequence && renderer.IsVolumeTerrainReady(work.Generation), token);
                    var nativeReady = false;
                    while (Terrain.TryReadEvent(out var evidence))
                    {
                        if (evidence.Kind != VolumeEventKind.NativeReady)
                            continue;
                        Assert.AreEqual(edit, evidence.Edit);
                        Assert.AreEqual(next, evidence.View);
                        Assert.AreEqual(begin, evidence.Stamp);
                        nativeReady = true;
                    }
                    Assert.IsTrue(nativeReady);
                }
                var sealedCut = await CoreRuntimeFixture.SealAsync(World, _tick, BarrierKind.CommitResults);
                Assert.IsNull(renderer is null ? Terrain.TryPreparePublication(work.Generation, sealedCut, []) :
                    renderer.TryPrepareVolumePublication(work.Generation, sealedCut, [], out _));
                var unchanged = World.Publication.Finalization.TryFinalize(CoreRuntimeFixture.Prepare(World, sealedCut, []));
                Assert.AreEqual(BarrierStatus.Unchanged, unchanged.Status);
                CoreRuntimeFixture.PublishObserver(World, unchanged.Stamp!.Value, (int)_tick);
                await BeginHeldTick();
                var candidates = await CoreRuntimeFixture.SealAsync(World, _tick, BarrierKind.CommitResults);
                PreparedBarrierPublication? prepared;
                if (renderer is null)
                    prepared = Terrain.TryPreparePublication(work.Generation, candidates, []);
                else
                {
                    prepared = renderer.TryPrepareVolumePublication(work.Generation, candidates, [], out var result);
                    Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, result);
                }
                Assert.IsNotNull(prepared);
                var committed = World.Publication.Finalization.TryFinalize(prepared);
                Assert.AreEqual(BarrierStatus.Committed, committed.Status);
                CoreRuntimeFixture.PublishObserver(World, committed.Stamp!.Value, (int)_tick);
            }
            return next;
        }

        internal async Task VerifyInvalidationOrder(bool swapFirst)
        {
            var authority = Terrain.TryBindRenderer(1);
            Assert.IsNotNull(authority);
            var view = Terrain.View;
            Assert.IsFalse(Terrain.ConfirmMask(view));
            Assert.IsTrue(Terrain.ConfirmMask(view, authority, 1));
            Assert.AreEqual(VolumeAdmission.Accepted, Terrain.TryStage(VolumeWorkClass.Movement, _selection, 0, 1,
                _meshBytes, _unit, new VolumeFixtureSource(_selection.Descriptor, false), out var work));
            Assert.IsNotNull(work);
            using (work)
            {
                Assert.IsTrue(work.Execute());
                using var reader = Terrain.TryAcquirePreparedGeometry(authority, work.Generation, out _, out _);
                Assert.IsNotNull(reader);
                var entry = World.Publication.Committed.Read().Root;
                await BeginHeldTick();
                var candidates = await CoreRuntimeFixture.SealAsync(World, _tick, BarrierKind.CommitResults);
                Assert.IsNull(Terrain.TryPreparePublication(work.Generation, candidates, []));
                var prepared = Terrain.TryPreparePublication(work.Generation, candidates, [], null, authority);
                Assert.IsNotNull(prepared);
                if (!swapFirst)
                    Terrain.InvalidateRenderer(1);
                var outcome = World.Publication.Finalization.TryFinalize(prepared);
                if (swapFirst)
                    Terrain.InvalidateRenderer(1);
                Assert.AreEqual(swapFirst ? BarrierStatus.Committed : BarrierStatus.Unchanged, outcome.Status);
                Assert.AreEqual(!swapFirst, ReferenceEquals(entry, World.Publication.Committed.Read().Root));
                Assert.IsFalse(Terrain.IsCurrentPresentationCandidate(reader.Info));
                Assert.IsFalse(Terrain.ConfirmMask(Terrain.View, authority, 2));
                Assert.IsNull(Terrain.TryBindRenderer(1));
                Assert.IsTrue(Terrain.Statistics.MovementHeld);
                var next = Terrain.ChangeView(view.Frame, 2);
                var replacement = Terrain.TryBindRenderer(2);
                Assert.IsNotNull(replacement);
                Assert.IsTrue(Terrain.ConfirmMask(next, replacement, 3));
            }
        }

        internal Task<BarrierStamp> BeginHeldTick() =>
            CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, ++_tick, BarrierKind.BeginTick);

        internal async Task FinishHeldTick()
        {
            var commit = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, _tick, BarrierKind.CommitResults);
            CoreRuntimeFixture.PublishObserver(World, commit, (int)_tick);
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
