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
    public async Task PreviewSkyWindowHandoffReportsOnlyNamedSessionObjectsBeforeFinalStop(bool warp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var title = $"Groundwork Preview sky session handoff {Guid.NewGuid():N}";
        var empty = new RenderMeshInstance(RenderMesh.Create([], []), Vector3.Zero);
        var geometry = new RenderMeshPair(empty, empty);
        var firstFrame = new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() };
        const string preview = "GROUNDWORK_TWILIGHT_PREVIEW";
        const string lookup = "GROUNDWORK_SKY_LOOKUP_PROTOTYPE";
        var priorPreview = Environment.GetEnvironmentVariable(preview);
        var priorLookup = Environment.GetEnvironmentVariable(lookup);
        using var session = new D3D12DeviceSession(QualificationDiagnostics, useWarp: warp, reuseDirectQueue: true);
        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        try
        {
            Environment.SetEnvironmentVariable(preview, "1");
            Environment.SetEnvironmentVariable(lookup, null);
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await using (var near = NewOwner())
            {
                await PresentSky(near);
                await near.StopAsync(token);
                var handoff = near.ReadDiagnostics();
                Assert.IsTrue(handoff.DebugLayerEnabled && handoff.DredEnabled);
                Assert.IsNull(handoff.DeviceRemoval);
                var report = handoff.DebugMessages;
                Assert.IsNotNull(report);
                Assert.AreEqual(0, report.LiveObjectReportResult);
                Assert.AreEqual(0UL, report.Errors);
                Assert.AreEqual(0UL, report.Corruption);
                Assert.AreEqual(0UL, report.Discarded);
                Assert.IsTrue(report.RetainedSessionQueue);
                Assert.AreEqual(2, report.RetainedSessionPreviewObjects);
                Assert.AreEqual(3, report.LiveChildObjects);
            }

            await using (var orbit = NewOwner())
            {
                await PresentSky(orbit);
                session.Dispose();
                await orbit.StopAsync(token);
                var final = orbit.ReadDiagnostics();
                AssertCleanDiagnostics(final, diagnosticsRequested: true);
                Assert.AreEqual(0, final.DebugMessages!.RetainedSessionPreviewObjects);
                Assert.IsFalse(final.DebugMessages.RetainedSessionQueue);
            }
            await window.StopAsync(token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(lookup, priorLookup);
            Environment.SetEnvironmentVariable(preview, priorPreview);
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }

        D3D12Renderer NewOwner() =>
            D3D12Renderer.CreateWithSessionMeshes(window, runtime.RuntimeOwnership, geometry, session,
                startupTimeout: TimeSpan.FromSeconds(10), operationTimeout: TimeSpan.FromSeconds(5));

        async Task PresentSky(D3D12Renderer renderer)
        {
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            AssertPlanetAdapter(renderer.ReadDiagnostics().Adapter, warp);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmit(firstFrame));
            var presented = await renderer.WaitForPresentedAsync(firstFrame.Sequence, token);
            Assert.AreEqual(firstFrame.Sequence, presented.LastPresentedSequence);
        }
    }

    // Filter: FullyQualifiedName~CanonicalWindowPresentsTheSameMixedMaterialsAcrossNearAndOrbit
    // This test owns a standalone presentation lease. The WP11 and planet renderer
    // authority tests separately verify their paired publication and receipt rules.
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CanonicalWindowPresentsTheSameMixedMaterialsAcrossNearAndOrbit(bool warp)
    {
        const int width = 1920, height = 1080;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var title = $"Groundwork canonical handoff {Guid.NewGuid():N}";
        await using var window = new WindowsHostModule(new WindowsHostOptions(title, width, height, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        // The standalone native fixture owns the presentation lease. A non-volume
        // owner selects the existing back-buffer readback submission entry point.
        await using var frameOwner = new D3D12Renderer(window, runtime.RuntimeOwnership);
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            SizeInt01Client(window, title, width, height);
            while (window.Read() is not { ClientWidth: width, ClientHeight: height })
                await Task.Delay(10, token);
            using var target = window.TryAcquirePresentationTarget();
            Assert.IsNotNull(target);
            VerifyCanonicalHandoff(target, frameOwner, warp, token);
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private static void VerifyCanonicalHandoff(WindowsPresentationTargetLease target, D3D12Renderer frameOwner,
        bool warp, CancellationToken token)
    {
        const int width = 1920, height = 1080;
        const int rockX = 792, oreX = 1128, sampleY = 506;
        const int middle = width / 2, middleY = height / 2;
        const float nearPlane = 0.1f, farPlane = 100;
        var fov = 2 * MathF.Atan(0.5f);
        var descriptor = TerrainAppearanceGeometryTests.Descriptor(8);
        var original = TerrainAppearanceGeometryTests.BuildVolume(new(descriptor, split: true));
        var cut = TerrainAppearanceGeometryTests.BuildVolume(new(descriptor, split: true, openTrench: true), revision: 2);
        var origin = PlanetLocalFrame.Create(new(0, 0, 8));
        var eye = new PlanetVector(0, 0, 16);
        var scene = new RenderScene(Matrix4x4.Identity, Vector4.One,
            Matrix4x4.CreateLookTo(origin.ToLocal(eye), -Vector3.UnitZ, Vector3.UnitY), fov, nearPlane, farPlane)
        { Sunlight = new(Vector3.UnitZ, Vector3.Zero, Vector3.One) };
        PlanetSurfaceMaterialSample[] controls =
        [
            new(new(descriptor.WorldId, descriptor.PlanetId, new(-2, 0, 7)), Int01Textures.Rock),
            new(new(descriptor.WorldId, descriptor.PlanetId, new(1, 0, 7)), Int01Textures.Ore),
        ];
        using var orbit = TerrainAppearanceGeometryTests.BuildOrbit(descriptor, origin, cube: true);
        var near = Pack(original);
        var orbitRegion = D3D12Renderer.PackPlanetTerrainWithAppearance(orbit, descriptor, controls, Int01Textures.OrbitProxy);
        var edited = Pack(cut);
        var assets = new Int01Textures();
        var targets = Targets(assets.Palette);
        var view = new D3D12TextureView(descriptor.WorldId, new(1, 1, origin), 1, 1);
        var stream = new D3D12TextureHandoff(new(16, 16, 16_384, 0, 16_384, 2 * 1024 * 1024, 4 * 1024 * 1024, 16));
        Assert.IsTrue(stream.PublishTerrainAppearance(view, assets.Palette, targets));
        var nativeMaps = new Dictionary<D3D12TextureTicket, GpuTerrainMap>();
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, region: near);
        using var ownerWake = new AutoResetEvent(false);
        WindowsSwapChain? chain = null;
        nint hwnd = 0;
        try
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DebugLayerEnabled);
            Assert.IsTrue(gpu.DredEnabled);
            using (var attach = target.TryBeginOperation())
            {
                Assert.IsNotNull(attach);
                hwnd = attach.WindowHandle;
                Assert.AreNotEqual(nint.Zero, hwnd);
                Assert.AreEqual(width, attach.Snapshot.ClientWidth);
                Assert.AreEqual(height, attach.Snapshot.ClientHeight);
                chain = new WindowsSwapChain(gpu, attach, width, height, ownerWake);
            }

            foreach (var textureTarget in targets)
            {
                var texture = assets[textureTarget.Reference];
                Assert.IsTrue(gpu.TryGetTextureRequirements(texture, out var physical));
                var reservation = new D3D12TextureReservation(512, 0, texture.CanonicalBytes.Length,
                    physical.StagingAllocationBytes, physical.TextureAllocationBytes);
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(view, textureTarget, reservation, out var ticket));
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, texture));
                Assert.IsTrue(stream.TryTake(view, out var transfer));
                Assert.AreEqual(ticket, transfer.Ticket);
                Assert.IsTrue(gpu.TrySubmitTexture(transfer.Texture, view.View.RendererEpoch, physical, out var binding));
                Assert.IsTrue(stream.MarkCopySubmitted(ticket));
                ObserveInt01Retirement(gpu);
                Assert.IsTrue(gpu.TryRetireTextureCopy(out var retired));
                Assert.AreEqual(binding, retired);
                Assert.IsTrue(stream.TryInstall(ticket, view, out var predecessor));
                Assert.AreEqual(default, predecessor);
                Assert.IsTrue(gpu.TryGetTerrainMap(textureTarget, binding, view.View.RendererEpoch, out var map));
                nativeMaps.Add(ticket, map);
            }

            var bindings = new D3D12TerrainTextureBinding[targets.Length];
            Assert.IsTrue(stream.TryCaptureTerrainAppearance(view, bindings, out var palette, out var count));
            Assert.AreSame(assets.Palette, palette);
            Assert.AreEqual(targets.Length, count);
            var materials = new GpuTerrainMaterialSet(view, assets.Palette, bindings, nativeMaps.Values.ToArray());
            gpu.SelectTerrainMaterials(materials, stream);
            var uploadedBytes = gpu.TextureStatistics.UploadedBytes;
            Assert.AreEqual((ulong)(targets.Length * 8 * 8 * 4), uploadedBytes);
            var first = Present(1);
            Install(orbitRegion);
            var fromOrbit = Present(2);
            Install(near);
            var returned = Present(3);
            Install(edited);
            var openTrench = Present(4);
            Assert.AreEqual(uploadedBytes, gpu.TextureStatistics.UploadedBytes);

            VerifyControl(first, fromOrbit, rockX, 2);
            VerifyControl(first, fromOrbit, oreX, 2);
            VerifyControl(first, returned, rockX, 1);
            VerifyControl(first, returned, oreX, 1);
            Assert.IsGreaterThan(first.ColorAt(rockX, sampleY)[2], first.ColorAt(rockX, sampleY)[0]);
            Assert.IsGreaterThan(first.ColorAt(oreX, sampleY)[0], first.ColorAt(oreX, sampleY)[2]);
            Assert.IsGreaterThan(first.DepthAt(middle, middleY) + .002f, openTrench.DepthAt(middle, middleY));
            Assert.IsLessThan(1f, openTrench.DepthAt(middle, middleY));
            Console.WriteLine($"INT-01 1920×1080 same HWND and swap chain, four retired Presents. " +
                $"Near/Orbit/Return Rock {Color(first, rockX)}/{Color(fromOrbit, rockX)}/{Color(returned, rockX)} and " +
                $"Ore {Color(first, oreX)}/{Color(fromOrbit, oreX)}/{Color(returned, oreX)}. " +
                $"Center depth {first.DepthAt(middle, middleY):R} → {openTrench.DepthAt(middle, middleY):R}. Upload bytes {uploadedBytes}.");

            if (Int01VisualCapture.RequestedDirectory(warp, diagnostics: true) is { } capture)
                Int01VisualCapture.Write(Path.Combine(capture, "canonical-window-handoff"), width, height, new
                {
                    Adapter = gpu.Adapter, Descriptor = descriptor, InitialPlan = original.PlanIdentity, EditedPlan = cut.PlanIdentity,
                    Eye = eye, Origin = origin, Projection = new { FieldOfViewRadians = fov, NearMetres = nearPlane, FarMetres = farPlane },
                    CameraForward = new { X = 0, Y = 0, Z = -1 }, CameraUp = new { X = 0, Y = 1, Z = 0 },
                    Light = new { TowardSun = new { X = 0, Y = 0, Z = 1 }, Direct = new { X = 0, Y = 0, Z = 0 }, Ambient = new { X = 1, Y = 1, Z = 1 } },
                    SharedControls = controls, UploadedBytes = uploadedBytes,
                    Assets = targets.Select(item => new { item.Slot, item.Reference.AssetId }),
                    PresentSequence = new { Near = 1, Orbit = 2, NearReturn = 3, EditedNear = 4 },
                }, ("near", first), ("orbit", fromOrbit), ("near-return", returned), ("near-open-trench", openTrench));

            gpu.SelectTerrainMaterials(null, null);
            foreach (var map in nativeMaps)
            {
                Assert.IsTrue(gpu.TryReleaseTexture(map.Value.Identity));
                Assert.IsTrue(stream.Release(map.Key));
            }
            Assert.IsNull(gpu.CaptureDred());
        }
        finally
        {
            if (gpu.IsIdle || gpu.IsRemoved)
            {
                chain?.Dispose();
                gpu.Dispose();
            }
        }

        Assert.AreEqual(default, stream.ReadUsage());
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);

        RenderMeshRegion Pack(PlanetVolumeMesh volume)
        {
            var packed = D3D12Renderer.PackVolumeTerrain([volume], origin, long.MaxValue, long.MaxValue, null,
                out var rejected, includeTerrainAppearance: true);
            Assert.IsNotNull(packed);
            Assert.IsFalse(rejected);
            return packed;
        }

        void Install(RenderMeshRegion region)
        {
            Assert.IsTrue(gpu.IsIdle);
            var previous = gpu.ActiveMeshGeometry;
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(region);
            previous?.Dispose();
        }

        OffscreenFrame Present(ulong sequence)
        {
            while (!chain!.CanBeginFrame())
            {
                token.ThrowIfCancellationRequested();
                chain.WaitForFrameSlot(10);
            }

            using (var operation = target.TryBeginOperation())
            {
                Assert.IsNotNull(operation);
                Assert.AreEqual(hwnd, operation.WindowHandle);
                Assert.AreEqual(width, operation.Snapshot.ClientWidth);
                Assert.AreEqual(height, operation.Snapshot.ClientHeight);
                Assert.AreEqual(GpuFrameSubmitResult.Submitted,
                    gpu.TryRenderWithPresentReadback(chain.CurrentBuffer, new(sequence, Vector4.Zero, scene), frameOwner));
                Assert.AreEqual(PresentResult.Presented, chain.Present(testOnly: false));
            }
            ObserveInt01Retirement(gpu);
            return gpu.ReadRetiredPresent();
        }

        static void VerifyControl(OffscreenFrame nearPixels, OffscreenFrame orbitPixels, int x, float tolerance)
        {
            var a = nearPixels.ColorAt(x, sampleY);
            var b = orbitPixels.ColorAt(x, sampleY);
            for (var channel = 0; channel < 4; channel++)
                Assert.AreEqual((float)a[channel], b[channel], tolerance);
            Assert.IsLessThan(1f, nearPixels.DepthAt(x, sampleY));
            Assert.AreEqual(nearPixels.DepthAt(x, sampleY), orbitPixels.DepthAt(x, sampleY), 0.000002f);
        }

        static string Color(OffscreenFrame frame, int x) => Convert.ToHexString(frame.ColorAt(x, sampleY));
    }
}
