using System.Numerics;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public void WarpShadowReadbackCoversDirectionSplitRebaseAndReplacement() => VerifyShadows(true, false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void ShadowReadbackWithDebugLayerAndDred(bool warp) => VerifyShadows(warp, true);

    private static void VerifyShadows(bool warp, bool debug)
    {
        var origin = PlanetLocalFrame.Create(default);
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, region: Region(origin, true));
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DredEnabled);
            if (debug)
                Assert.IsTrue(gpu.DebugLayerEnabled);
            var positive = Vector3.Normalize(new Vector3(1, 0, 1));
            var negative = Vector3.Normalize(new Vector3(-1, 0, 1));
            var left = Read(origin, positive, 0);
            var right = Read(origin, negative, 0);
            Assert.IsGreaterThan(left.ColorAt(65, 48)[0] + 80, left.ColorAt(95, 48)[0]);
            Assert.IsGreaterThan(right.ColorAt(95, 48)[0] + 80, right.ColorAt(65, 48)[0]);

            var fogged = Read(origin, positive, 0, fog: true);
            Assert.IsGreaterThan((byte)40, fogged.ColorAt(65, 48)[2]);
            Assert.IsGreaterThan(left.ColorAt(95, 48)[2], fogged.ColorAt(95, 48)[2]);

            // Move the same receiver through 22, 24 and 26 m of positive view depth.
            foreach (var depth in new[] { 21.9, 22.1, 24.0, 25.9, 26.1 })
            {
                var image = Read(origin, positive, depth - 24);
                Assert.IsGreaterThan(image.ColorAt(65, 48)[0] + 80, image.ColorAt(95, 48)[0], $"Depth {depth}");
            }

            Assert.IsTrue(origin.TryRebase(new PlanetVector(32, -16, 8), out var shifted));
            Replace(Region(shifted, true));
            var rebased = Read(shifted, positive, 0);
            for (var x = 60; x <= 100; x++)
                Assert.AreEqual((float)left.ColorAt(x, 48)[0], rebased.ColorAt(x, 48)[0], 1f, $"x={x}");

            // Only the replacement's buffers are available to either depth pass.
            Replace(Region(shifted, false));
            var edited = Read(shifted, positive, 0);
            Assert.IsGreaterThan(left.ColorAt(65, 48)[0] + 80, edited.ColorAt(65, 48)[0]);
            Assert.IsNull(gpu.CaptureDred());
        }
        Assert.AreEqual(0, gpu.LiveMeshBuffers);
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);

        void Replace(RenderMeshRegion region)
        {
            var old = gpu.ActiveMeshGeometry;
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(region);
            old?.Dispose();
        }

        OffscreenFrame Read(PlanetLocalFrame local, Vector3 sun, double eyeZ, bool fog = false)
        {
            var camera = new VolumeCamera(new(1, 1, local), new(0, 0, eyeZ), -Vector3.UnitZ, Vector3.UnitY,
                0.65f, 0.1f, 100, Vector4.One, Vector4.Zero)
            {
                Sunlight = new(sun, new(0.85f), new(0.12f)), TerrainShadows = true,
                Fog = fog ? new(Vector3.UnitZ, 0, 64, 1) : null,
            };
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 1, out var frame));
            return GpuDevice.RenderOffscreen(gpu, 160, 96, frame);
        }

        static RenderMeshRegion Region(PlanetLocalFrame local, bool caster)
        {
            var translation = local.ToLocal(default);
            var floor = new RenderMeshInstance(Quad(8, 5, -24), translation);
            return new(caster ? [floor, new(Quad(1, 1, -21.5f), translation)] : [floor]);
        }

        static RenderMesh Quad(float x, float y, float z)
        {
            var normal = PackedTerrainNormal.Encode(Vector3.UnitZ);
            return RenderMesh.CreateWithTerrainNormals([new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z)],
                [0, 1, 2, 0, 2, 3], [], [normal, normal, normal, normal]);
        }
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TerrainEditRebuildsShadowFromPublishedMeshesWithDebugLayerAndDred(bool warp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var run = new VolumeRun(editable: true);
        var view = run.Terrain.ChangeView(PlanetLocalFrame.Create(default), 1);
        Assert.IsTrue(run.Terrain.ConfirmMask(view));
        await run.Publish(1, cut: false);
        using var previous = run.Terrain.TryRead();
        Assert.IsNotNull(previous);
        using var registration = run.Terrain.TryRegisterFrame(previous.Info.Generation, view, 1);
        Assert.IsNotNull(registration);
        Assert.IsTrue(run.Terrain.AcknowledgeFrame(registration));
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true);
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DebugLayerEnabled && gpu.DredEnabled);
            var sun = Vector3.Normalize(new Vector3(1, 0.25f, 0.6f));
            var before = Read(previous.Meshes, view, sun);
            var next = await run.CutFloor(null, timeout.Token,
                new VolumeBrushIntent(1, 1, 1, new(32, 30, 2), new(35, 34, 4), VolumeBrushOperation.Add, 1));
            using var edited = run.Terrain.TryRead();
            Assert.IsNotNull(edited);
            Assert.AreEqual(previous.Info.TerrainRevision + 1, edited.Info.TerrainRevision);
            Assert.AreNotEqual(previous.Info.Generation, edited.Info.Generation);
            var after = Read(edited.Meshes, next, sun);
            var changedOnSameSurface = 0;
            for (var y = 0; y < TerrainHeight; y++)
            for (var x = 0; x < TerrainWidth; x++)
            {
                if (before.Shadowed.DepthAt(x, y) >= 1 ||
                    Math.Abs(before.Shadowed.DepthAt(x, y) - after.Shadowed.DepthAt(x, y)) > 0.000001f ||
                    !before.Lit.ColorAt(x, y).SequenceEqual(after.Lit.ColorAt(x, y)))
                    continue;
                if (Math.Abs(before.Shadowed.ColorAt(x, y)[0] - after.Shadowed.ColorAt(x, y)[0]) > 4)
                    changedOnSameSurface++;
            }
            Assert.IsGreaterThan(0, changedOnSameSurface, "The edit must change visibility on unchanged receivers.");
            Assert.IsNull(gpu.CaptureDred());

            (OffscreenFrame Shadowed, OffscreenFrame Lit) Read(ReadOnlySpan<PlanetVolumeMesh> meshes,
                VolumeViewIdentity identity, Vector3 direction)
            {
                var region = D3D12Renderer.PackVolumeTerrain(meshes, identity.Frame, VolumeRun.NativeBytes);
                Assert.IsNotNull(region);
                // Isolate the cavity floor from its roof so the added solid can cast a new shadow.
                var instances = region.Instances.ToArray();
                for (var batch = 0; batch < instances.Length; batch++)
                {
                    var source = instances[batch].Mesh;
                    var positions = new List<Vector3>();
                    var normals = new List<uint>();
                    var colors = new List<uint>();
                    for (var index = 0; index < source.Positions.Length; index += 3)
                    {
                        if (identity.Frame.ToPlanet(source.Positions[index]).Z >= 6 ||
                            identity.Frame.ToPlanet(source.Positions[index + 1]).Z >= 6 ||
                            identity.Frame.ToPlanet(source.Positions[index + 2]).Z >= 6)
                            continue;
                        for (var vertex = index; vertex < index + 3; vertex++)
                        {
                            positions.Add(source.Positions[vertex]);
                            normals.Add(source.PackedNormals[vertex]);
                            colors.Add(source.Colors[vertex]);
                        }
                    }
                    var indices = Enumerable.Range(0, positions.Count).Select(i => (uint)i).ToArray();
                    instances[batch] = new(RenderMesh.CreateWithTerrainNormals(positions.ToArray(), indices,
                        colors.ToArray(), normals.ToArray()), Vector3.Zero);
                }
                region = new RenderMeshRegion(instances);
                var old = gpu.ActiveMeshGeometry;
                gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(region);
                old?.Dispose();
                var camera = VolumeCameraAt(identity, 32, 7) with
                {
                    Sunlight = new(direction, Vector3.One, new(0.12f)),
                    TerrainShadows = true,
                };
                Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 1, out var frame));
                var shadowed = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
                var lit = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight,
                    frame with { Scene = frame.Scene!.Value with { ShadowOrigin = null } });
                return (shadowed, lit);
            }
        }
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);
    }

    [TestMethod]
    public void ShadowCascadeFittingHandlesClippedBandsAndCameraTurns()
    {
        var scene = new RenderScene(Matrix4x4.Identity, Vector4.One, Matrix4x4.Identity, 0.7f, 28, 100)
            { ShadowOrigin = new(-4096, 512, -16) };
        var basis = ShadowBasis.Create(Vector3.Normalize(new(0.02f, 1, 0.03f)));
        Assert.IsFalse(basis.Fit(scene, 16f / 9, 0, 26).Valid);
        var straight = basis.Fit(scene, 16f / 9, 22, 64);
        var turned = basis.Fit(scene with { View = Matrix4x4.CreateRotationY(1.4f) }, 16f / 9, 22, 64);
        Assert.IsTrue(straight.Valid && turned.Valid);
        Assert.AreEqual(straight.Area.Z, turned.Area.Z);
        Assert.IsFalse(basis.Fit(scene with { FarPlane = 20, NearPlane = 0.1f }, 1, 22, 64).Valid);
    }
}
