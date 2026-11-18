using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SharedSessionPresentsTexturedSurfaceOrbitAndReturn(bool warp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var token = timeout.Token;
        using var run = new VolumeRun(editable: true, openFloor: true,
            material: static point => point.X < 32 ? Int01Textures.Rock : Int01Textures.Ore);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        var diagnostics = QualificationDiagnostics with { EnableResourceSnapshots = true };
        using var session = new D3D12DeviceSession(diagnostics, warp, reuseDirectQueue: true);
        await using var window = new WindowsHostModule(
            new WindowsHostOptions("Groundwork shared session terrain appearance", 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        var assets = new Int01Textures();
        var targets = Targets(assets.Palette);
        var expectedUploadBytes = (ulong)(targets.Length * 8 * 8 * 4);
        var light = new RenderSunlight(Vector3.UnitZ, new(0.45f), new(0.85f));
        var colorSamples = 0;
        var nearView = run.Terrain.View;

        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            var firstCut = await PresentSurfaceAsync(nearView, 1, 0);
            var afterSurface = run.World.Publication.Committed.Read();

            var positions = new Vector3[] { new(-2, -2, 8), new(2, -2, 8), new(2, 2, 8), new(-2, 2, 8) };
            var source = RenderMesh.Create(positions, [0, 1, 2, 0, 2, 3]);
            var canonical = Array.ConvertAll(positions, static point => new PlanetVector(point.X, point.Y, point.Z));
            Assert.IsTrue(D3D12Renderer.TryBuildOrbitalTerrainMesh(source, canonical,
                [Int01Textures.Rock, Int01Textures.Ore, Int01Textures.Ore, Int01Textures.Rock], out var mapped));
            Assert.IsNotNull(mapped);
            var planet = new RenderMeshRegion([new RenderMeshInstance(mapped, Vector3.Zero)]);
            var orbitView = new VolumeViewIdentity(2, 1, PlanetLocalFrame.Create(new(0, 0, 8)));
            var orbitCut = new D3D12TextureView(firstCut.World, orbitView, firstCut.TerrainRevision, 1);
            var orbitStream = NewStream();
            var orbitRuntime = CreateRuntime(timing);
            await using (var orbit = D3D12Renderer.CreateWithSessionTexturedMeshes(
                window, orbitRuntime.RuntimeOwnership, planet, session, orbitStream, orbitCut))
            {
                Assert.IsFalse(orbitStream.PublishTerrainAppearance(
                    orbitCut with { CoverageGeneration = orbitCut.CoverageGeneration + 1 }, assets.Palette, targets));
                StageTextures(orbitStream, orbitCut);
                await StartRendererAsync(orbit, orbitRuntime);
                Assert.AreEqual(RenderSubmissionResult.Accepted, orbit.TrySubmit(new RenderFrame(3, Vector4.Zero)));
                await AwaitStatus(orbit, state => state.LastPresentedSequence == 3, token);
                Assert.AreEqual(0UL, orbit.ReadTextureDiagnostics().UploadedBytes);
                Assert.AreEqual(RenderSubmissionResult.Accepted, orbit.TrySubmit(new RenderFrame(4, Vector4.Zero,
                    new RenderScene(Matrix4x4.Identity, Vector4.One,
                        Matrix4x4.CreateLookTo(new(0, 0, 16), -Vector3.UnitZ, Vector3.UnitY), MathF.PI / 3, 0.1f, 100)
                    { Sunlight = light })));
                // Color, depth, one mesh, material constants, one input per material texture.
                await AwaitTexturedAsync(orbit, orbitStream, orbitCut, 4, 4 + targets.Length);
                Assert.AreSame(afterSurface.Root, run.World.Publication.Committed.Read().Root);
                Assert.AreEqual(afterSurface.Stamp, run.World.Publication.Committed.Read().Stamp);
                await StopRendererAsync(orbit, orbitStream, orbitRuntime);
            }

            var returnView = run.Terrain.ChangeView(nearView.Frame, 3);
            Assert.AreEqual(3UL, returnView.RendererEpoch);
            var returned = await PresentSurfaceAsync(returnView, 5, firstCut.TerrainRevision);
            Assert.AreEqual(firstCut.TerrainRevision, returned.TerrainRevision);
            Assert.AreNotEqual(firstCut.CoverageGeneration, returned.CoverageGeneration);
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }

        async Task<D3D12TextureView> PresentSurfaceAsync(VolumeViewIdentity view, ulong mask, ulong sourceRevision)
        {
            var stream = NewStream();
            var samplesBefore = Volatile.Read(ref colorSamples);
            var graphicsRuntime = CreateRuntime(timing);
            await using var renderer = D3D12Renderer.CreateWithTexturedSessionVolumeTerrain(
                window, graphicsRuntime.RuntimeOwnership, run.Terrain, view.RendererEpoch, session,
                colorEvaluator: (_, _) =>
                {
                    Interlocked.Increment(ref colorSamples);
                    return 0xff808080;
                });
            renderer.AttachTextureHandoff(stream);
            await StartRendererAsync(renderer, graphicsRuntime);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, mask, Vector4.Zero));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == mask, token);
            await run.Publish(1, cut: false, renderer, sourceRevision);
            using var installed = run.Terrain.TryRead();
            Assert.IsNotNull(installed);
            Assert.AreEqual(view, installed.Info.View);
            var generation = installed.Info.Generation;
            var cut = new D3D12TextureView(run.Terrain.WorldIdentity, view, installed.Info.TerrainRevision, generation);
            var sequence = mask + 1;
            var committed = run.World.Publication.Committed.Read();
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TrySubmitVolumeTerrain(generation, sequence,
                VolumeCameraAt(view, 32, 7) with { Sunlight = light }));
            var baselineUses = 0;
            await AwaitStatus(renderer, state => state.LastPresentedSequence == sequence &&
                renderer.TryReadResourceDiagnostics(out var snapshot) &&
                snapshot.TryGetPass(GpuPassPlan.Terrain, out var pass) && (baselineUses = pass.ResourceUses) > 0, token);
            StageTextures(stream, cut);
            await AwaitTexturedAsync(renderer, stream, cut, sequence, baselineUses + targets.Length);
            Assert.IsGreaterThan(samplesBefore, Volatile.Read(ref colorSamples));
            Assert.AreSame(committed.Root, run.World.Publication.Committed.Read().Root);
            Assert.AreEqual(committed.Stamp, run.World.Publication.Committed.Read().Stamp);

            var registered = 0;
            var accepted = 0;
            while (run.Terrain.TryReadEvent(out var item))
            {
                if (item.Generation != generation || item.Kind is not (VolumeEventKind.Registered or VolumeEventKind.Accepted))
                    continue;
                Assert.AreEqual(sequence, item.Frame);
                if (item.Kind == VolumeEventKind.Registered)
                    registered++;
                else
                    accepted++;
            }
            Assert.AreEqual(1, registered);
            Assert.AreEqual(1, accepted);
            await StopRendererAsync(renderer, stream, graphicsRuntime);
            return cut;
        }

        D3D12TextureHandoff NewStream() => new(new(targets.Length, targets.Length, 8192, 0, 8192,
            targets.Length * 65_536, (targets.Length + 1) * 65_536, targets.Length));

        void StageTextures(D3D12TextureHandoff stream, D3D12TextureView cut)
        {
            Assert.IsTrue(stream.PublishTerrainAppearance(cut, assets.Palette, targets));
            foreach (var target in targets)
            {
                Assert.AreEqual(D3D12TextureResult.Accepted,
                    stream.TryRequest(cut, target, new(512, 0, 1024, 65_536, 65_536), out var ticket));
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, assets[target.Reference]));
            }
        }

        async Task StartRendererAsync(D3D12Renderer renderer, RuntimeSafetyAuthority graphicsRuntime)
        {
            var graphicsContext = new EngineModuleContext(TimeProvider.System, timing, graphicsRuntime.Reporter, graphicsRuntime.StopControl);
            await renderer.InitializeAsync(graphicsContext, token);
            await renderer.StartAsync(token);
            AssertPlanetAdapter(renderer.ReadDiagnostics().Adapter, warp);
        }

        async Task AwaitTexturedAsync(D3D12Renderer renderer, D3D12TextureHandoff stream, D3D12TextureView cut,
            ulong sequence, int minimumResourceUses)
        {
            var presented = await AwaitStatus(renderer, state => state.LastPresentedSequence == sequence &&
                renderer.ReadTextureDiagnostics().UploadedBytes == expectedUploadBytes &&
                targets.All(target => stream.TryReadResidency(cut, target, out var resident) && resident.ResidentMip == 0) &&
                renderer.TryReadResourceDiagnostics(out var snapshot) &&
                snapshot.RetiredPresentSequence == sequence &&
                snapshot.TryGetPass(GpuPassPlan.Terrain, out var pass) && pass.ResourceUses >= minimumResourceUses, token);
            Assert.AreEqual(sequence, presented.LastAcceptedSequence);
        }

        async Task StopRendererAsync(D3D12Renderer renderer, D3D12TextureHandoff stream, RuntimeSafetyAuthority graphicsRuntime)
        {
            await renderer.StopAsync(token);
            Assert.AreEqual(ShutdownSettlement.Released, graphicsRuntime.SettleStop());
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            var observed = renderer.ReadDiagnostics();
            Assert.IsTrue(observed.DebugLayerEnabled);
            Assert.IsTrue(observed.DredEnabled);
            Assert.IsNull(observed.DeviceRemoval);
            var debug = observed.DebugMessages;
            Assert.IsNotNull(debug);
            Assert.AreEqual(0UL, debug.Errors);
            Assert.AreEqual(0UL, debug.Corruption);
            Assert.AreEqual(0UL, debug.Discarded);
            Assert.AreEqual(0, debug.LiveObjectReportResult);
            Assert.AreEqual(1, debug.LiveChildObjects);
            Assert.IsTrue(debug.RetainedSessionQueue);
            Assert.AreEqual(default, stream.ReadUsage());
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task SessionOrbitDoesNotExecuteARecordedFallbackAfterRuntimeStop()
    {
        const ulong sceneSequence = 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var windowRuntime = CreateRuntime(timing);
        var rendererRuntime = CreateRuntime(timing);
        var windowContext = new EngineModuleContext(TimeProvider.System, timing, windowRuntime.Reporter, windowRuntime.StopControl);
        var rendererContext = new EngineModuleContext(TimeProvider.System, timing, rendererRuntime.Reporter, rendererRuntime.StopControl);
        using var session = new D3D12DeviceSession(QualificationDiagnostics, useWarp: true);
        await using var window = new WindowsHostModule(
            new WindowsHostOptions("Groundwork shared orbit stop before execute", 360, 280, 32),
            windowRuntime.UiWake, windowRuntime.RuntimeOwnership);
        var pose = PlanetLocalFrame.Create(new(0, 0, 8));
        var cut = new D3D12TextureView(Guid.NewGuid(), new(1, 1, pose), 1, 1);
        var stream = new D3D12TextureHandoff(new(1, 1, 512, 0, 1024, 65_536, 2 * 65_536, 1));
        var region = new RenderMeshRegion([new RenderMeshInstance(MappedQuad(-2, 2, 2, 8, Int01Textures.Rock), Vector3.Zero)]);
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = 0;
        var hooks = new GpuRetirementTestHooks(
            BeforeValidateRecordedBindings: (sequence, _) =>
            {
                if (sequence != sceneSequence)
                    return;
                rendererRuntime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
                recorded.TrySetResult();
            },
            BeforeExecute: sequence =>
            {
                if (sequence == sceneSequence)
                    Interlocked.Increment(ref executed);
            });
        await using var renderer = new D3D12Renderer(window, rendererRuntime.RuntimeOwnership, region, session, stream, cut,
            startupTimeout: null, operationTimeout: null, retirementHooks: hooks);
        try
        {
            await window.InitializeAsync(windowContext, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(rendererContext, token);
            await renderer.StartAsync(token);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmit(new RenderFrame(1, Vector4.Zero)));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 1, token);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmit(new RenderFrame(sceneSequence, Vector4.Zero,
                new RenderScene(Matrix4x4.Identity, Vector4.One,
                    Matrix4x4.CreateLookTo(new(0, 0, 16), -Vector3.UnitZ, Vector3.UnitY), MathF.PI / 3, 0.1f, 100))));
            await recorded.Task.WaitAsync(token);
            await renderer.StopAsync(token);
            Assert.AreEqual(ShutdownSettlement.Released, rendererRuntime.SettleStop());
            Assert.AreEqual(HostWindowPhase.Open, window.Read().Phase);
            Assert.AreEqual(0, Volatile.Read(ref executed));
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
            Assert.AreEqual(default, stream.ReadUsage());
        }
        finally
        {
            rendererRuntime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            windowRuntime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }
}
