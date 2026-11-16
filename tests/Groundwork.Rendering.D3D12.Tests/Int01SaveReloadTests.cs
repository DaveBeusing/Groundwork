using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Groundwork.Rendering.Textures;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Int01SavedEditRestoresCanonicalGeometryAndTexturedPixels(bool warp)
    {
        const int width = 160, height = 96;
        const int y = height / 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        using var run = new VolumeRun(editable: true, openFloor: true, material: BaseMaterial);
        var initialView = run.Terrain.View;
        Assert.IsTrue(run.Terrain.ConfirmMask(initialView));
        await run.Publish(1, cut: false);
        using var original = run.Terrain.TryRead();
        Assert.IsNotNull(original);

        var assets = new Int01Textures();
        var targets = Targets(assets.Palette);
        var stream = new D3D12TextureHandoff(new(targets.Length, targets.Length, 8192, 0, 8192,
            targets.Length * 65_536, (targets.Length + 1) * 65_536, targets.Length));
        var initialCut = Cut(run, original);
        Assert.IsTrue(stream.PublishTerrainAppearance(initialCut, assets.Palette, targets));
        var native = new GpuTerrainMap[targets.Length];
        var bindings = new D3D12TerrainTextureBinding[targets.Length];
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true);
        ulong sequence = 0;
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DebugLayerEnabled);
            Assert.IsTrue(gpu.DredEnabled);
            for (var i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                CookedTexture cooked = assets[target.Reference];
                Assert.IsTrue(gpu.TryGetTextureRequirements(cooked, out var physical));
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(initialCut, target,
                    new(512, 0, cooked.CanonicalBytes.Length, physical.StagingAllocationBytes, physical.TextureAllocationBytes), out var ticket));
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, cooked));
                Assert.IsTrue(stream.TryTake(initialCut, out var transfer));
                Assert.AreEqual(ticket, transfer.Ticket);
                Assert.IsTrue(gpu.TrySubmitTexture(transfer.Texture, initialView.RendererEpoch, physical, out var texture));
                Assert.IsTrue(stream.MarkCopySubmitted(ticket));
                var started = Stopwatch.GetTimestamp();
                while (!gpu.TryRetireTextureCopy(out var retired) || retired != texture)
                {
                    token.ThrowIfCancellationRequested();
                    Assert.IsLessThan(TimeSpan.FromSeconds(10), Stopwatch.GetElapsedTime(started));
                    Thread.Sleep(1);
                }
                Assert.IsTrue(stream.TryInstall(ticket, initialCut, out var predecessor));
                Assert.AreEqual(default, predecessor);
                Assert.IsTrue(gpu.TryGetTerrainMap(target, texture, initialView.RendererEpoch, out native[i]));
                bindings[i] = new(target, ticket);
            }

            var before = Render(run, original, Pack(original));
            using var firstFrame = run.Terrain.TryRegisterFrame(original.Info.Generation, initialView, 1);
            Assert.IsNotNull(firstFrame);
            Assert.IsTrue(run.Terrain.AcknowledgeFrame(firstFrame));
            var brush = new VolumeBrushIntent(1, 1, 1, new(30, 30, -1), new(32, 34, 2),
                VolumeBrushOperation.Add, Int01Textures.Ore);
            var editView = await run.CutFloor(null, token, brush);
            using var edited = run.Terrain.TryRead();
            Assert.IsNotNull(edited);
            Assert.AreEqual(original.Info.TerrainRevision + 1, edited.Info.TerrainRevision);
            var editedGeometry = Pack(edited);
            var editPixels = Render(run, edited, editedGeometry);

            var written = OverlayBytes(edited);
            Assert.IsGreaterThan(8, written.Length, "The checkpoint must contain the added ore cells.");
            var checkpoint = new VolumeRun.SavedOverlay(run.Descriptor, edited.Info.TerrainRevision,
                run.World.Publication.Committed.Read().Stamp.Tick, written,
                static material => material is Int01Textures.Rock or Int01Textures.Ore);
            using var reload = new VolumeRun(editable: true, openFloor: true, material: BaseMaterial, savedOverlay: checkpoint);
            var restoredView = reload.Terrain.ChangeView(editView.Frame, editView.RendererEpoch);
            Assert.IsTrue(reload.Terrain.ConfirmMask(restoredView));
            await reload.Publish(checkpoint.TerrainRevision, cut: false, sourceRevision: 0);
            using var restored = reload.Terrain.TryRead();
            Assert.IsNotNull(restored);
            Assert.AreEqual(edited.Info.TerrainRevision, restored.Info.TerrainRevision);
            Assert.AreEqual(edited.Info.PlanIdentity, restored.Info.PlanIdentity);
            CollectionAssert.AreEqual(written, OverlayBytes(restored));
            var savedGeometry = Pack(restored);
            AssertSameGeometry(editedGeometry, savedGeometry);
            var restoredPixels = Render(reload, restored, savedGeometry);

            var editedProbeCount = 0;
            for (var x = width / 8; x < width * 7 / 8; x++)
            {
                Assert.AreEqual(editPixels.DepthAt(x, y), restoredPixels.DepthAt(x, y), 1e-6f, $"Reloaded depth at x={x}.");
                var editedColor = editPixels.ColorAt(x, y).ToArray();
                CollectionAssert.AreEqual(editedColor, restoredPixels.ColorAt(x, y).ToArray(), $"Reloaded color at x={x}.");
                var prior = before.ColorAt(x, y).ToArray();
                if (x < width / 2 && before.DepthAt(x, y) - editPixels.DepthAt(x, y) > 1e-4f &&
                    prior[0] > prior[2] + 2 && editedColor[2] > editedColor[0] + 2)
                    editedProbeCount++;
            }
            Assert.IsGreaterThan(0, editedProbeCount, "The test view must show closer ore geometry in pixels that originally displayed rock.");

            if (Int01VisualCapture.RequestedDirectory(warp, diagnostics: true) is { } captures)
            {
                Int01VisualCapture.Write(Path.Combine(captures, "save-reload"), width, height, new
                {
                    Seed = 42,
                    CheckpointRevision = checkpoint.TerrainRevision,
                    OverlaySha256 = Convert.ToHexStringLower(SHA256.HashData(written)),
                    GeometryPlan = restored.Info.PlanIdentity,
                    CameraPosition = new { X = 32, Y = 32, Z = 7 },
                    VerticalFieldOfView = MathF.PI / 4,
                    NearPlane = 0.1f,
                    FarPlane = 100f,
                    SunDirection = new { X = 0, Y = 0, Z = 1 },
                    DirectPerChannel = 0.45f,
                    AmbientPerChannel = 0.85f,
                    gpu.Adapter,
                    Assets = targets.Select(target => new { target.Slot.Material, target.Slot.Semantic, target.Reference.AssetId }),
                }, ("before-ore-edit", before), ("saved-edit", editPixels), ("restored-edit", restoredPixels));
            }

            gpu.SelectTerrainMaterials(null, null);
            for (var i = 0; i < native.Length; i++)
            {
                Assert.IsTrue(gpu.TryReleaseTexture(native[i].Identity));
                Assert.IsTrue(stream.Release(bindings[i].Resident!.Value));
            }
            Assert.IsNull(gpu.CaptureDred());
        }
        Assert.AreEqual(default, stream.ReadUsage());
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);

        OffscreenFrame Render(VolumeRun owner, VolumeStreamingLease lease, RenderMeshRegion geometry)
        {
            var old = gpu.ActiveMeshGeometry;
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(geometry, VolumeRun.NativeBytes);
            old?.Dispose();
            var camera = VolumeCameraAt(lease.Info.View, 32, 7) with
            {
                Sunlight = new(Vector3.UnitZ, new(0.45f), new(0.85f)),
            };
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, ++sequence, out var frame));
            // The auxiliary offscreen route reuses the unchanged fixture assets when a fresh coordinator is loaded.
            gpu.SelectTerrainMaterials(new(Cut(owner, lease), assets.Palette, bindings, native), stream);
            return GpuDevice.RenderOffscreen(gpu, width, height, frame);
        }

        static ushort BaseMaterial(LatticeCoordinate cell) => cell.X < 32 ? Int01Textures.Rock : Int01Textures.Ore;
        static D3D12TextureView Cut(VolumeRun owner, VolumeStreamingLease lease) =>
            new(owner.Terrain.WorldIdentity, lease.Info.View, lease.Info.TerrainRevision, lease.Info.Generation);
        static byte[] OverlayBytes(VolumeStreamingLease lease)
        {
            var bytes = new byte[lease.EditOverlayBytes];
            Assert.IsTrue(lease.TryWriteEditOverlay(bytes, out var written));
            Assert.AreEqual(bytes.Length, written);
            return bytes;
        }
        static RenderMeshRegion Pack(VolumeStreamingLease lease)
        {
            var region = D3D12Renderer.PackVolumeTerrain(lease.Meshes, lease.Info.View.Frame,
                VolumeRun.NativeBytes, VolumeRun.NativeBytes * 2, null, out var rejected, includeTerrainAppearance: true);
            Assert.IsFalse(rejected);
            Assert.IsNotNull(region);
            foreach (var mesh in region.Instances)
                Assert.IsFalse(mesh.Mesh.TerrainAppearance.IsEmpty);
            return region;
        }
        static void AssertSameGeometry(RenderMeshRegion edited, RenderMeshRegion restored)
        {
            Assert.AreEqual(edited.Instances.Length, restored.Instances.Length);
            for (var i = 0; i < edited.Instances.Length; i++)
            {
                var before = edited.Instances[i].Mesh;
                var after = restored.Instances[i].Mesh;
                Assert.IsTrue(before.Positions.SequenceEqual(after.Positions), "Reloaded mesh positions changed.");
                Assert.IsTrue(before.Indices.SequenceEqual(after.Indices), "Reloaded mesh indices changed.");
                Assert.IsTrue(before.Colors.SequenceEqual(after.Colors), "Reloaded legacy material colors changed.");
                Assert.IsTrue(before.PackedNormals.SequenceEqual(after.PackedNormals), "Reloaded packed normals changed.");
                Assert.IsTrue(before.TerrainAppearance.SequenceEqual(after.TerrainAppearance), "Reloaded material IDs or texels changed.");
            }
        }
    }
}
