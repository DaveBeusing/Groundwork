using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Rendering.Materials;

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
        var first = new TaskCompletionSource<OffscreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var painted = new TaskCompletionSource<(ulong Id, ulong Sequence, ulong Generation, OffscreenFrame Pixels)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var copyStarts = 0;

        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics, warp, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), null,
            includeTerrainAppearance: true)
        {
            BeforeTextureUploadForTests = (_, _) => Interlocked.Increment(ref copyStarts),
            VolumePresentReadbackForTests = (sequence, _, pixels) =>
            {
                if (sequence == 2)
                    first.TrySetResult(pixels);
            },
            AppearancePresentReadbackForTests = (id, sequence, generation, pixels) =>
            {
                if (Volatile.Read(ref copyStarts) == requests.Length)
                    painted.TrySetResult((id, sequence, generation, pixels));
            },
        };
        renderer.AttachTextureHandoff(stream);
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
            var numeric = await first.Task.WaitAsync(token);
            var mapped = await painted.Task.WaitAsync(token);
            Assert.IsGreaterThan(0UL, mapped.Id);
            Assert.AreEqual(2UL, mapped.Sequence);
            Assert.AreEqual(generation, mapped.Generation);
            Assert.AreEqual(width, renderer.Read().AppliedWidth);
            Assert.AreEqual(height, renderer.Read().AppliedHeight);
            Assert.AreEqual(2UL, renderer.Read().LastAcceptedSequence);
            Assert.AreEqual(2UL, renderer.Read().LastPresentedSequence);
            Assert.AreSame(committed.Root, run.World.Publication.Committed.Read().Root);
            Assert.AreEqual(committed.Stamp, run.World.Publication.Committed.Read().Stamp);
            Assert.AreEqual((ulong)(requests.Length * 8 * 8 * 4), renderer.ReadTextureDiagnostics().UploadedBytes);

            Assert.IsLessThan(1f, numeric.DepthAt(leftX, y));
            Assert.IsLessThan(1f, numeric.DepthAt(rightX, y));
            Assert.AreEqual(numeric.DepthAt(leftX, y), mapped.Pixels.DepthAt(leftX, y));
            Assert.AreEqual(numeric.DepthAt(rightX, y), mapped.Pixels.DepthAt(rightX, y));
            var rock = mapped.Pixels.ColorAt(leftX, y).ToArray();
            var ore = mapped.Pixels.ColorAt(rightX, y).ToArray();
            Assert.IsGreaterThan(rock[2] + 2, rock[0]);
            Assert.IsGreaterThan(ore[0] + 2, ore[2]);

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
