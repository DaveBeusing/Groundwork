using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Rendering.Materials;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Int01MixedVoxelMaterialsDrawAt1080pWithoutAnotherTerrainFrame(bool warp)
    {
        const int width = 1920;
        const int height = 1080;
        const int leftX = width * 3 / 8;
        const int rightX = width * 5 / 8;
        const int y = height / 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var token = timeout.Token;
        using var run = new VolumeRun(editable: true, openFloor: true,
            material: static p => p.X < 32 ? Int01Textures.Rock : Int01Textures.Ore);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        var title = $"Groundwork INT-01 fixture {Guid.NewGuid():N}";
        await using var window = new WindowsHostModule(new WindowsHostOptions(title,
            width, height, 32), runtime.UiWake, runtime.RuntimeOwnership);

        var assets = new Int01Textures();
        var requests = Targets(assets.Palette.Resolve(Int01Textures.Rock).Textures, Int01Textures.Rock)
            .Concat(Targets(assets.Palette.Resolve(Int01Textures.Ore).Textures, Int01Textures.Ore)).ToArray();
        var stream = new D3D12TextureHandoff(new(requests.Length, requests.Length, 8192, 0, 8192,
            requests.Length * 65_536, (requests.Length + 1) * 65_536, requests.Length));
        var first = new TaskCompletionSource<(OffscreenFrame Pixels, D3D12PropStatistics Props)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEdited = new TaskCompletionSource<(ulong Generation, OffscreenFrame Pixels)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var painted = new TaskCompletionSource<(ulong Id, ulong Sequence, ulong Generation, OffscreenFrame Pixels,
            int RecordedPlans, D3D12PropStatistics Props)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var copyStarts = 0;
        var recordedPlans = 0;
        const uint propTint = 0xffff00ff;
        var propMesh = RenderMesh.Create([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], [0, 1, 2]);
        var props = new D3D12PropCells(new(1, 1, 1, 1, 2), 1, 42, 1,
            [new(new(1, 1, 2, 40, 20, 24), propMesh, propMesh)]);
        PropCellKey propCell = default;
        PropPublication propPublication = default;
        var hooks = new GpuRetirementTestHooks(RejectRecordedPlan: sequence =>
        {
            if (sequence != 2 || Volatile.Read(ref copyStarts) != requests.Length ||
                Interlocked.Increment(ref recordedPlans) != 1)
                return false;

            lock (props.Gate)
            {
                var resident = new VisibleProp(new(42, 1, propCell, 0), 1, 1, 0, 0, 0);
                Assert.IsTrue(props.Admission.TryReadFrame(resident, out var recordedProp, out _));
                Assert.AreEqual(propTint, recordedProp.Tint);
                var recordedVersion = props.ViewVersion;
                props.Publish(propPublication with { CoverageGeneration = propPublication.CoverageGeneration + 1 }, []);
                Assert.AreEqual(recordedVersion + 1, props.ViewVersion);
                Assert.AreEqual(0, props.Admission.Usage.ResidentInstances);
            }

            // Let submission admission see the actual stale prop cut. Publish does not wake the owner.
            return false;
        });

        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics, warp, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), hooks,
            includeTerrainAppearance: true)
        {
            BeforeTextureUploadForTests = (_, _) => Interlocked.Increment(ref copyStarts),
            VolumePresentReadbackForTests = (sequence, generation, pixels) =>
            {
                if (sequence == 2)
                    first.TrySetResult((pixels, props.ReadStatistics()));
                if (sequence == 4)
                    firstEdited.TrySetResult((generation, pixels));
            },
            AppearancePresentReadbackForTests = (id, sequence, generation, pixels) =>
            {
                if (Volatile.Read(ref copyStarts) == requests.Length)
                    painted.TrySetResult((id, sequence, generation, pixels, Volatile.Read(ref recordedPlans), props.ReadStatistics()));
            },
        };
        renderer.AttachTextureHandoff(stream);
        renderer.AttachPropCells(props);
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            SizeInt01Client(window, title, width, height);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            await AwaitStatus(renderer, state => state.AppliedWidth == width && state.AppliedHeight == height, token);
            var view = run.Terrain.View;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 1, Vector4.Zero));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            using var installed = run.Terrain.TryRead();
            Assert.IsNotNull(installed);
            var generation = installed.Info.Generation;
            var cut = new D3D12TextureView(run.Terrain.WorldIdentity, installed.Info.View, installed.Info.TerrainRevision, generation);
            propPublication = new(cut.World, view.RendererEpoch, cut.TerrainRevision, generation);
            props.Publish(propPublication, [propCell]);
            Assert.AreEqual(PropCellResult.Accepted, props.TryRequest(propCell, 1, out var propRequest));
            Assert.AreEqual(PropCellResult.Accepted, props.TryStage(propRequest, new(32, 32, 3),
                [new(0, 0, Vector3.Zero, Quaternion.Identity, 1, propTint)]));
            Assert.IsTrue(stream.PublishTerrainAppearance(cut, assets.Palette, requests));
            foreach (var request in requests)
            {
                Assert.AreEqual(D3D12TextureResult.Accepted,
                    stream.TryRequest(cut, request, new(512, 0, 1024, 65_536, 65_536), out var ticket));
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, assets[request.Reference]));
            }

            var camera = VolumeCameraAt(view, 32, 7) with
            {
                Sunlight = new(Vector3.UnitZ, new(0.45f), new(0.85f)),
            };
            var committed = run.World.Publication.Committed.Read();
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TrySubmitVolumeTerrain(generation, 2, camera));
            var baseline = await first.Task.WaitAsync(token);
            var numeric = baseline.Pixels;
            var mapped = await painted.Task.WaitAsync(token);
            Assert.AreEqual(1, baseline.Props.Draws);
            Assert.AreEqual(1, baseline.Props.Instances);
            Assert.AreEqual(1, mapped.RecordedPlans);
            Assert.AreEqual(0, mapped.Props.Draws);
            Assert.IsLessThan(mapped.Pixels.DepthAt(width / 2, y), numeric.DepthAt(width / 2, y));
            Assert.IsGreaterThan(0UL, mapped.Id);
            Assert.AreEqual(2UL, mapped.Sequence);
            Assert.AreEqual(generation, mapped.Generation);
            Assert.AreEqual(width, renderer.Read().AppliedWidth);
            Assert.AreEqual(height, renderer.Read().AppliedHeight);
            Assert.AreEqual(2UL, renderer.Read().LastAcceptedSequence);
            Assert.AreEqual(2UL, renderer.Read().LastPresentedSequence);
            Assert.AreSame(committed.Root, run.World.Publication.Committed.Read().Root);
            Assert.AreEqual(committed.Stamp, run.World.Publication.Committed.Read().Stamp);
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual((ulong)(requests.Length * 8 * 8 * 4), renderer.ReadTextureDiagnostics().UploadedBytes);
            var registrations = 0;
            var acknowledgements = 0;
            while (run.Terrain.TryReadEvent(out var authorityEvent))
            {
                if (authorityEvent.Generation != generation ||
                    authorityEvent.Kind is not (VolumeEventKind.Registered or VolumeEventKind.Accepted))
                    continue;
                Assert.AreEqual(2UL, authorityEvent.Frame);
                if (authorityEvent.Kind == VolumeEventKind.Registered)
                    registrations++;
                else
                    acknowledgements++;
            }
            Assert.AreEqual(1, registrations);
            Assert.AreEqual(1, acknowledgements);

            Assert.IsLessThan(1f, numeric.DepthAt(leftX, y));
            Assert.IsLessThan(1f, numeric.DepthAt(rightX, y));
            Assert.AreEqual(numeric.DepthAt(leftX, y), mapped.Pixels.DepthAt(leftX, y));
            Assert.AreEqual(numeric.DepthAt(rightX, y), mapped.Pixels.DepthAt(rightX, y));
            var rock = mapped.Pixels.ColorAt(leftX, y).ToArray();
            var ore = mapped.Pixels.ColorAt(rightX, y).ToArray();
            Assert.IsGreaterThan(rock[2] + 2, rock[0]);
            Assert.IsGreaterThan(ore[0] + 2, ore[2]);

            var editedView = await run.CutFloor(renderer, token, maskSequence: 3);
            using var edited = run.Terrain.TryRead();
            Assert.IsNotNull(edited);
            Assert.AreNotEqual(generation, edited.Info.Generation);
            Assert.AreEqual(cut.TerrainRevision + 1, edited.Info.TerrainRevision);
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame, renderer.TrySubmitVolumeCamera(generation, 4, camera));
            var editedCut = new D3D12TextureView(cut.World, editedView, edited.Info.TerrainRevision, edited.Info.Generation);
            Assert.IsTrue(stream.PublishTerrainAppearance(editedCut, assets.Palette, requests));
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(edited.Info.Generation, 4, VolumeCameraAt(editedView, 32, 7) with
                { Sunlight = camera.Sunlight }));
            var editedPixels = await firstEdited.Task.WaitAsync(token);
            Assert.AreEqual(edited.Info.Generation, editedPixels.Generation);
            Assert.AreEqual(4UL, renderer.Read().LastPresentedSequence);
            Assert.IsGreaterThan(mapped.Pixels.DepthAt(width / 2, y), editedPixels.Pixels.DepthAt(width / 2, y));
            Assert.AreEqual(requests.Length, Volatile.Read(ref copyStarts));

            if (Int01VisualCapture.RequestedDirectory(warp, diagnostics: true) is { } captures)
            {
                Int01VisualCapture.Write(Path.Combine(captures, "near-present"), width, height, new
                {
                    Seed = 42,
                    OriginalCut = cut,
                    EditedCut = editedCut,
                    CameraPosition = camera.Position,
                    CameraForward = new[] { camera.Forward.X, camera.Forward.Y, camera.Forward.Z },
                    CameraUp = new[] { camera.Up.X, camera.Up.Y, camera.Up.Z },
                    VerticalFieldOfView = camera.FieldOfView,
                    camera.NearPlane,
                    camera.FarPlane,
                    SunDirection = new { X = 0, Y = 0, Z = 1 },
                    DirectPerChannel = 0.45f,
                    AmbientPerChannel = 0.85f,
                    Adapter = renderer.ReadDiagnostics().Adapter,
                    Assets = requests.Select(request => new { request.Slot.Material, request.Slot.Semantic, request.Reference.AssetId }),
                }, ("near-first-numeric", numeric), ("near-textured", mapped.Pixels), ("near-first-edited", editedPixels.Pixels));
            }

            await renderer.StopAsync(token);
            AssertPlanetAdapter(renderer.ReadDiagnostics().Adapter, warp);
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private static D3D12TextureTarget[] Targets(MaterialTextureSet textures, ushort material) =>
    [
        new(new(material, MaterialTextureSemantic.BaseColor), 1, textures.BaseColor!),
        new(new(material, MaterialTextureSemantic.Normal), 1, textures.Normal!),
        new(new(material, MaterialTextureSemantic.Roughness), 1, textures.Roughness!),
        new(new(material, MaterialTextureSemantic.Metallic), 1, textures.Metallic!),
    ];
}
