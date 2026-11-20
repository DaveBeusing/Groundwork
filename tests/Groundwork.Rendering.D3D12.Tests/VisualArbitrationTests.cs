using System.Collections.Concurrent;
using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReadyFirstUseTexturesPropsAndRefinementsShareDrawableTurns(bool warp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        using var run = new VolumeRun(openFloor: true);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork visual turns {Guid.NewGuid():N}",
            TerrainWidth, TerrainHeight, 32), runtime.UiWake, runtime.RuntimeOwnership);
        var textures = new D3D12TextureHandoff(new(4, 4, 4096, 4096, 4096, 512 * 1024, 512 * 1024, 4));
        var mesh = RenderMesh.Create([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], [0, 1, 2]);
        var props = new D3D12PropCells(new(1, 1, 1, 1, 2), 1, 42, 1,
            [new(new(1, 1, 2, 40, 20, 24), mesh, mesh)]);
        var propCell = new PropCellKey(0, 0, 0);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ready = new ManualResetEventSlim();
        var turns = new ConcurrentQueue<(ulong Frame, D3D12TextureTicket Ticket, int PropPayload)>();
        var generation = 0UL;
        var lastCaptured = 0UL;
        Action? stageDemotion = null;
        var refinable = SolidMipChain();
        var view = run.Terrain.View;
        D3D12Renderer? active = null;
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics, warp, TimeSpan.FromSeconds(15), null, null)
        {
            BeforeTextureUploadForTests = (ticket, _) => turns.Enqueue((0, ticket, 0)),
            VolumePresentReadbackForTests = (sequence, _, _) =>
            {
                if (sequence <= lastCaptured)
                    return;
                lastCaptured = sequence;
                if (sequence == 3)
                {
                    blocked.TrySetResult();
                    ready.Wait(token);
                    return;
                }
                if (sequence < 4 || sequence > 12)
                    return;

                var statistics = props.ReadStatistics();
                Assert.IsLessThanOrEqualTo(D3D12PropCells.MaximumUploadBytes, statistics.UploadBytes);
                turns.Enqueue((sequence, default, statistics.PayloadBytes));
                if (sequence == 12)
                {
                    finished.TrySetResult();
                    return;
                }
                if (statistics.PayloadBytes > 0)
                    StageProp();
                if (sequence == 7)
                    stageDemotion!();
                Assert.AreEqual(RenderSubmissionResult.Accepted,
                    active!.TrySubmitVolumeCamera(generation, sequence + 1, VolumeCameraAt(view, 32, 7)));
            },
        };
        active = renderer;
        renderer.AttachTextureHandoff(textures);
        renderer.AttachPropCells(props);
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 1, Vector4.Zero));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            using var installed = run.Terrain.TryRead();
            Assert.IsNotNull(installed);
            generation = installed.Info.Generation;
            var cut = new D3D12TextureView(run.Terrain.WorldIdentity, installed.Info.View,
                installed.Info.TerrainRevision, generation);
            props.Publish(new(cut.World, cut.View.RendererEpoch, cut.TerrainRevision, generation), [propCell]);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(generation, 2, VolumeCameraAt(view, 32, 7)));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 2, token);

            var stable = Target(1);
            var first = Target(2);
            var next = Target(3);
            Assert.IsTrue(textures.Publish(cut, 1, [stable, first, next]));
            var reservation = new D3D12TextureReservation(256, 256, 256, 128 * 1024, 128 * 1024);
            var policy = new D3D12TextureMipPolicy(0, 1, 10, 30);
            Assert.AreEqual(D3D12TextureResult.Accepted,
                textures.TryRequestTier(cut, stable, policy, 100, reservation, reservation, out var coarse));
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(coarse, refinable));
            await AwaitStatus(renderer,
                _ => textures.TryReadResidency(cut, stable, out var installedMip) && installedMip.ResidentMip == 1, token);

            Assert.AreEqual(RenderSubmissionResult.Accepted,
                renderer.TrySubmitVolumeCamera(generation, 3, VolumeCameraAt(view, 32, 7)));
            await blocked.Task.WaitAsync(token);
            StageProp();
            // The refinement has the oldest texture ticket. A first-use texture
            // must still win the first shared turn, followed by an eligible prop turn.
            Assert.AreEqual(D3D12TextureResult.Accepted,
                textures.TryRequestTier(cut, stable, policy, 0, reservation, reservation, out var refine));
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(refine, refinable));
            var firstTicket = StageFirstUse(first);
            var nextTicket = StageFirstUse(next);
            var demotion = default(D3D12TextureTicket);
            stageDemotion = () =>
            {
                Assert.IsTrue(textures.TryReadResidency(cut, stable, out var promoted));
                Assert.AreEqual(0, promoted.ResidentMip);
                Assert.AreEqual(D3D12TextureResult.Accepted,
                    textures.TryRequestTier(cut, stable, policy, 100, reservation, reservation, out demotion));
                Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(demotion, refinable));
            };
            Assert.AreEqual(RenderSubmissionResult.Accepted,
                renderer.TrySubmitVolumeCamera(generation, 4, VolumeCameraAt(view, 32, 7)));
            ready.Set();
            await finished.Task.WaitAsync(token);

            CollectionAssert.AreEqual(
                new (ulong, D3D12TextureTicket, int)[]
                {
                    (0, coarse, 0), (0, firstTicket, 0), (4, default, 0), (5, default, PropInstanceData.ByteSize),
                    (6, default, PropInstanceData.ByteSize), (0, refine, 0), (7, default, 0),
                    (8, default, PropInstanceData.ByteSize), (0, nextTicket, 0), (9, default, 0),
                    (10, default, PropInstanceData.ByteSize), (11, default, PropInstanceData.ByteSize),
                    (0, demotion, 0), (12, default, 0),
                }, turns.ToArray());
            Assert.AreEqual(12UL, renderer.Read().LastPresentedSequence);
            Assert.IsTrue(textures.TryReadResidency(cut, stable, out var final));
            Assert.AreEqual(1, final.ResidentMip);

            await renderer.StopAsync(token);
            Assert.AreEqual(default, textures.ReadUsage());
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, warp);
            AssertCleanDiagnostics(diagnostics, diagnosticsRequested: true);

            D3D12TextureTicket StageFirstUse(D3D12TextureTarget target)
            {
                Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryRequest(cut, target, reservation, out var ticket));
                Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(ticket, refinable));
                return ticket;
            }
        }
        finally
        {
            ready.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }

        void StageProp()
        {
            Assert.AreEqual(PropCellResult.Accepted, props.TryRequest(propCell, 1, out var request));
            Assert.AreEqual(PropCellResult.Accepted, props.TryStage(request, new(32, 32, 3),
                [new(0, 0, Vector3.Zero, Quaternion.Identity, 1, 0xffff00ff)]));
        }

        D3D12TextureTarget Target(ushort material) =>
            new(new(material, MaterialTextureSemantic.BaseColor), 1, new(MaterialTextureSemantic.BaseColor, refinable.AssetId));

        static CookedTexture SolidMipChain()
        {
            byte[] source = new byte[18 + 4 * 4 * 4];
            source[2] = 2;
            source[12] = source[14] = 4;
            source[16] = 32;
            source[17] = 0x28;
            for (var offset = 18; offset < source.Length; offset += 4)
            {
                source[offset + 2] = 255;
                source[offset + 3] = 255;
            }
            Assert.AreEqual(TextureCookError.None, TextureCooker.CookTga32(source,
                new(TextureColorSpace.Srgb, TextureSemantic.Color, 2), new(256, 256, 256, 16, 2), out var texture));
            Assert.IsNotNull(texture);
            return texture;
        }
    }
}
