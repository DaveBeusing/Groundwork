using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Groundwork.Rendering.Materials;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class TerrainAppearanceGeometryTests
{
    [TestMethod]
    public void PackedNearAndOrbitGeometryPreserveMixedMaterialsAndCanonicalPhaseAcrossRebase()
    {
        var descriptor = Descriptor(8);
        var field = new SurfaceField(descriptor);
        var volume = BuildVolume(field);
        var queries = field.Queries;
        var origin = PlanetLocalFrame.Create(new(0, 0, 8));
        Assert.IsTrue(origin.TryRebase(new(24, 0, 8), out var rebased));
        var near = PackVolume(volume, origin, appearance: true);
        var nearRebased = PackVolume(volume, rebased, appearance: true);
        var controls = new PlanetSurfaceMaterialSample[]
        {
            new(new(descriptor.WorldId, descriptor.PlanetId, new(4, 3, 7)), Int01Textures.Rock),
            new(new(descriptor.WorldId, descriptor.PlanetId, new(5, 3, 7)), Int01Textures.Ore),
        };
        using var lease = BuildOrbit(descriptor, origin, cube: true);
        using var secondLease = BuildOrbit(descriptor, rebased, cube: true);
        var orbit = D3D12Renderer.PackPlanetTerrainWithAppearance(lease, descriptor, controls, Int01Textures.OrbitProxy);
        var orbitRebased = D3D12Renderer.PackPlanetTerrainWithAppearance(secondLease, descriptor, controls, Int01Textures.OrbitProxy);
        Assert.IsTrue(near.TerrainAppearance.SequenceEqual(nearRebased.TerrainAppearance));
        Assert.IsTrue(orbit.Instances[0].Mesh.TerrainAppearance.SequenceEqual(orbitRebased.Instances[0].Mesh.TerrainAppearance));

        VerifyShared(4, Int01Textures.Rock);
        VerifyShared(5, Int01Textures.Ore);
        Assert.AreEqual(queries, field.Queries);

        void VerifyShared(int x, ushort material)
        {
            var local = origin.ToLocal(new(x, 3, 8));
            var nearVertex = Find(near, local);
            var farVertex = Find(orbit.Instances[0].Mesh, local);
            Assert.AreEqual(material, nearVertex.Material);
            Assert.AreEqual(material, farVertex.Material);
            Assert.IsTrue(nearVertex.HasMapping);
            Assert.IsTrue(farVertex.HasMapping);
            for (var axis = 0; axis < 3; axis++)
            {
                var period = PlanetTriplanarMapping.TexelsPerRepeat;
                var delta = Math.Abs(nearVertex.Texels[axis] - farVertex.Texels[axis]) % period;
                Assert.IsLessThanOrEqualTo(PlanetTriplanarMapping.PositionToleranceTexels, Math.Min(delta, period - delta));
            }
        }
    }

    [TestMethod]
    public void UnmappedOrbitPrimitivesKeepProxyMaterialAndNearGeometryKeepsLegacyCredits()
    {
        var descriptor = Descriptor(PlanetDescriptor.MaximumRadiusMeters);
        using var orbit = BuildOrbit(descriptor, PlanetLocalFrame.Create(new(0, 0, descriptor.RadiusMeters)), cube: false);
        var far = D3D12Renderer.PackPlanetTerrainWithAppearance(orbit, descriptor, [], Int01Textures.OrbitProxy);
        var legacyFar = D3D12Renderer.PackPlanetTerrain(orbit);
        var mesh = far.Instances[0].Mesh;
        var legacyMesh = legacyFar.Instances[0].Mesh;
        Assert.IsTrue(legacyMesh.TerrainAppearance.IsEmpty);
        Assert.AreEqual(legacyMesh.Indices.Length, mesh.TerrainAppearance.Length);
        for (var corner = 0; corner < mesh.TerrainAppearance.Length; corner++)
        {
            var sample = mesh.TerrainAppearance[corner];
            Assert.IsFalse(sample.HasMapping);
            Assert.AreEqual(Vector3.Zero, sample.Texels);
            Assert.AreEqual(Int01Textures.OrbitProxy, sample.Material);
            Assert.AreEqual(legacyMesh.Positions[(int)legacyMesh.Indices[corner]], mesh.Positions[corner]);
        }

        var volume = BuildVolume(new SurfaceField(Descriptor(8)));
        var origin = PlanetLocalFrame.Create(new(0, 0, 8));
        var legacy = PackVolume(volume, origin, appearance: false);
        var nativePayload = (long)legacy.Positions.Length * 24;
        var legacyNativeLimit = (nativePayload + 65535) & ~65535L;
        var legacyPackingLimit = nativePayload * 2;

        VerifyLegacy(PackVolume(volume, origin, appearance: true, nativeLimit: legacyNativeLimit));
        VerifyLegacy(PackVolume(volume, origin, appearance: true, packingLimit: legacyPackingLimit));
        Assert.AreNotEqual(0, PackVolume(volume, origin, appearance: true).TerrainAppearance.Length);

        void VerifyLegacy(RenderMesh candidate)
        {
            Assert.IsTrue(candidate.TerrainAppearance.IsEmpty);
            Assert.IsTrue(legacy.Positions.SequenceEqual(candidate.Positions));
            Assert.IsTrue(legacy.Indices.SequenceEqual(candidate.Indices));
            Assert.IsTrue(legacy.PackedNormals.SequenceEqual(candidate.PackedNormals));
            Assert.IsTrue(legacy.Colors.SequenceEqual(candidate.Colors));
        }
    }

    private static RenderMesh PackVolume(PlanetVolumeMesh mesh, PlanetLocalFrame origin, bool appearance,
        long nativeLimit = long.MaxValue, long packingLimit = long.MaxValue)
    {
        var region = D3D12Renderer.PackVolumeTerrain([mesh], origin, nativeLimit, packingLimit, null,
            out var rejected, includeTerrainAppearance: appearance);
        Assert.IsNotNull(region);
        Assert.IsFalse(rejected);
        return region.Instances[0].Mesh;
    }

    private static TerrainAppearanceVertex Find(RenderMesh mesh, Vector3 position)
    {
        for (var index = 0; index < mesh.Positions.Length; index++)
            if (Vector3.DistanceSquared(mesh.Positions[index], position) < 1e-8f)
                return mesh.TerrainAppearance[index];
        throw new InvalidOperationException($"Missing packed control {position}.");
    }

    internal static PlanetTerrainLease BuildOrbit(PlanetDescriptor descriptor, PlanetLocalFrame origin, bool cube)
    {
        var camera = new PlanetVector(0, 0, descriptor.RadiusMeters + 96);
        Assert.IsTrue(PlanetTerrainSelection.TrySelectFlight(camera, camera, 1, null, out var plan));
        Assert.IsNotNull(plan);
        var coordinator = new PlanetTerrainCoordinator();
        Assert.IsTrue(coordinator.TryStage(plan, origin));
        while (coordinator.TryReserveMesh(out var work))
        {
            var mesh = PlanetPatchMesh.Build(work.Tile.Patch, descriptor.RadiusMeters,
                Height, work.Tile.Transitions);
            Assert.IsTrue(coordinator.CompleteMesh(work, mesh));
        }
        Assert.IsTrue(coordinator.TryPreparePresentation(camera, out var lease));
        Assert.IsNotNull(lease);
        return lease;

        double Height(PlanetSampleKey key)
        {
            if (!cube)
                return 0;
            var ray = PlanetCoordinates.Direction(key);
            return descriptor.RadiusMeters / Math.Max(Math.Abs(ray.X), Math.Max(Math.Abs(ray.Y), Math.Abs(ray.Z))) - descriptor.RadiusMeters;
        }
    }

    internal static PlanetVolumeMesh BuildVolume(SurfaceField field)
    {
        const int bricks = 8;
        var store = new PlanetBrickStore(field, bricks * PlanetBrickStore.BrickPayloadBytes, bricks);
        var inputs = new VolumeMeshInput[bricks];
        try
        {
            for (var index = 0; index < bricks; index++)
            {
                var key = new LatticeBrick((index & 1) - 1, ((index >> 1) & 1) - 1, (index >> 2) - 1);
                Assert.IsTrue(store.TryGenerate(key));
                var lease = store.TryAcquire(key);
                Assert.IsNotNull(lease);
                inputs[index] = new(lease, 1);
            }
            var mesh = PlanetVolumeMesher.Build(inputs, 1, long.MaxValue);
            Assert.IsNotNull(mesh);
            return mesh;
        }
        finally
        {
            foreach (var input in inputs)
                input.Brick?.Dispose();
        }
    }

    internal static PlanetDescriptor Descriptor(double radius) => new(new("551ffcb9-db29-40c3-8093-91929e4d65a2"),
        new("38b17c17-dacf-46ad-85db-4b5cad78c393"), 42, 1, 1, 1, new string('a', 64), radius);

    internal sealed class SurfaceField(PlanetDescriptor descriptor, bool split = false) : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => descriptor;
        public int Queries { get; private set; }

        public long SampleDensity(LatticeCoordinate point)
        {
            Queries++;
            return (point.Z - 8) * 256;
        }

        public ushort SampleMaterial(LatticeCoordinate cell)
        {
            Queries++;
            return (split ? cell.X < 0 : (cell.X & 1) == 0) ? Int01Textures.Rock : Int01Textures.Ore;
        }
    }
}

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public void WarpCanonicalPlaneMappedPixelsMatchAcrossNearOrbitAndRebase() =>
        VerifyCanonicalPlaneMappedPixels(warp: true, requireDebug: false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void CanonicalPlaneMappedPixelsMatchWithDebugLayerAndDred(bool warp) =>
        VerifyCanonicalPlaneMappedPixels(warp, requireDebug: true);

    private static void VerifyCanonicalPlaneMappedPixels(bool warp, bool requireDebug)
    {
        const int edge = 512;
        const int rockX = 176, oreX = 336, sampleY = 239;
        var descriptor = TerrainAppearanceGeometryTests.Descriptor(8);
        var field = new TerrainAppearanceGeometryTests.SurfaceField(descriptor, split: true);
        var volume = TerrainAppearanceGeometryTests.BuildVolume(field);
        var queries = field.Queries;
        var origin = PlanetLocalFrame.Create(new(0, 0, 8));
        Assert.IsTrue(origin.TryRebase(new(24, 0, 8), out var rebased));
        PlanetSurfaceMaterialSample[] controls =
        [
            new(new(descriptor.WorldId, descriptor.PlanetId, new(-2, 0, 7)), Int01Textures.Rock),
            new(new(descriptor.WorldId, descriptor.PlanetId, new(1, 0, 7)), Int01Textures.Ore),
        ];
        using var orbitLease = TerrainAppearanceGeometryTests.BuildOrbit(descriptor, origin, cube: true);
        using var rebasedOrbitLease = TerrainAppearanceGeometryTests.BuildOrbit(descriptor, rebased, cube: true);
        var near = PackNear(origin);
        var orbit = D3D12Renderer.PackPlanetTerrainWithAppearance(orbitLease, descriptor, controls, Int01Textures.OrbitProxy);
        var assets = new Int01Textures();
        var proxyBase = new Vector3(.25f, .5f, .75f);
        var palette = new MaterialPalette(1,
        [
            new(Int01Textures.Rock, assets.Palette.Resolve(Int01Textures.Rock)),
            new(Int01Textures.Ore, assets.Palette.Resolve(Int01Textures.Ore)),
            new(Int01Textures.OrbitProxy, new(proxyBase, 0, 0.8f, 1, default, 1,
                assets.Palette.Resolve(Int01Textures.Rock).Textures)),
        ]);
        var targets = Targets(palette);
        var view = new D3D12TextureView(descriptor.WorldId, new(1, 1, origin), 1, 1);
        var stream = new D3D12TextureHandoff(new(16, 16, 16_384, 0, 16_384, 2 * 1024 * 1024, 4 * 1024 * 1024, 16));
        Assert.IsTrue(stream.PublishTerrainAppearance(view, palette, targets));
        var resident = new Dictionary<D3D12TextureTicket, GpuTerrainMap>(targets.Length);
        var eye = new PlanetVector(0, 0, 16);
        ulong sequence = 0;
        var gpu = new GpuDevice(warp, enableDebugLayer: requireDebug, enableDred: requireDebug, region: near);
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            if (requireDebug)
            {
                Assert.IsTrue(gpu.DebugLayerEnabled);
                Assert.IsTrue(gpu.DredEnabled);
            }
            foreach (var target in targets)
            {
                var texture = assets[target.Reference];
                Assert.IsTrue(gpu.TryGetTextureRequirements(texture, out var physical));
                var credit = new D3D12TextureReservation(512, 0, texture.CanonicalBytes.Length,
                    physical.StagingAllocationBytes, physical.TextureAllocationBytes);
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(view, target, credit, out var ticket));
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, texture));
                Assert.IsTrue(stream.TryTake(view, out var transfer));
                Assert.AreEqual(ticket, transfer.Ticket);
                Assert.IsTrue(gpu.TrySubmitTexture(transfer.Texture, view.View.RendererEpoch, physical, out var native));
                Assert.IsTrue(stream.MarkCopySubmitted(ticket));
                ObserveInt01Retirement(gpu);
                Assert.IsTrue(gpu.TryRetireTextureCopy(out var retired));
                Assert.AreEqual(native, retired);
                Assert.IsTrue(stream.TryInstall(ticket, view, out var predecessor));
                Assert.AreEqual(default, predecessor);
                Assert.IsTrue(gpu.TryGetTerrainMap(target, retired, view.View.RendererEpoch, out var map));
                resident.Add(ticket, map);
            }

            var bindings = new D3D12TerrainTextureBinding[targets.Length];
            Assert.IsTrue(stream.TryCaptureTerrainAppearance(view, bindings, out var captured, out var count));
            Assert.AreSame(palette, captured);
            Assert.AreEqual(targets.Length, count);
            var materials = new GpuTerrainMaterialSet(view, palette, bindings, resident.Values.ToArray());
            gpu.SelectTerrainMaterials(materials, stream);
            // The ambient-only probe isolates sampling and material selection. Azimuthal vertex
            // normals differ between the voxel surface and the radial orbit proxy.
            var nearPixels = Read(origin, eye);
            Install(orbit);
            var orbitPixels = Read(origin, eye);
            var assetFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
                targets.Select(static target => target.Reference.AssetId))))).ToLowerInvariant();
            Console.WriteLine($"INT-01 canonical seed={descriptor.Seed} nearPlan={volume.PlanIdentity} assets={assetFingerprint} " +
                $"nearRock={Pixel(nearPixels, rockX)} orbitRock={Pixel(orbitPixels, rockX)} " +
                $"nearOre={Pixel(nearPixels, oreX)} orbitOre={Pixel(orbitPixels, oreX)}");
            Assert.AreEqual("ba317c87879b5e42c34c0c760c211933588f37f4826d80b42ef4d4178cb11262", volume.PlanIdentity);
            Assert.AreEqual("b632b7c6b75329a73ee606a6f3a5c125230bcd11dd8eddf3f9a58e4890050170", assetFingerprint);
            VerifyGoldenProbe(nearPixels, rockX, [54, 15, 5, 255]);
            VerifyGoldenProbe(orbitPixels, rockX, [55, 16, 5, 255]);
            VerifyGoldenProbe(nearPixels, oreX, [2, 8, 14, 255]);
            VerifyGoldenProbe(orbitPixels, oreX, [2, 8, 14, 255]);
            Verify(nearPixels, orbitPixels, 2);

            Install(PackNear(rebased));
            Verify(nearPixels, Read(rebased, eye), 1);
            Install(D3D12Renderer.PackPlanetTerrainWithAppearance(rebasedOrbitLease, descriptor, controls, Int01Textures.OrbitProxy));
            Verify(orbitPixels, Read(rebased, eye), 1);
            Assert.AreEqual(queries, field.Queries);

            // These distant coarse triangles cannot share a canonical repeat anchor.
            // Their proxy material must ignore four resident map descriptors.
            var distant = TerrainAppearanceGeometryTests.Descriptor(PlanetDescriptor.MaximumRadiusMeters);
            var distantOrigin = PlanetLocalFrame.Create(new(0, 0, distant.RadiusMeters));
            using var distantLease = TerrainAppearanceGeometryTests.BuildOrbit(distant, distantOrigin, cube: false);
            Install(D3D12Renderer.PackPlanetTerrainWithAppearance(distantLease, distant, [], Int01Textures.OrbitProxy));
            var proxy = Read(distantOrigin, new(0, 0, distant.RadiusMeters + 8));
            var proxyPixel = proxy.ColorAt(edge / 2, edge / 2);
            Console.WriteLine($"INT-01 unmapped orbit radius={distant.RadiusMeters} proxy={Convert.ToHexString(proxyPixel)}");
            Assert.IsLessThan(1f, proxy.DepthAt(edge / 2, edge / 2));
            Assert.AreEqual(64f, proxyPixel[0], 1f);
            Assert.AreEqual(128f, proxyPixel[1], 1f);
            Assert.AreEqual(191f, proxyPixel[2], 1f);
            Assert.AreEqual(byte.MaxValue, proxyPixel[3]);

            gpu.SelectTerrainMaterials(null, null);
            foreach (var entry in resident)
            {
                Assert.IsTrue(gpu.TryReleaseTexture(entry.Value.Identity));
                Assert.IsTrue(stream.Release(entry.Key));
            }
            Assert.IsNull(gpu.CaptureDred());
        }

        Assert.AreEqual(default, stream.ReadUsage());
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), requireDebug);

        RenderMeshRegion PackNear(PlanetLocalFrame frame)
        {
            var packed = D3D12Renderer.PackVolumeTerrain([volume], frame, long.MaxValue, long.MaxValue, null,
                out var rejected, includeTerrainAppearance: true);
            Assert.IsNotNull(packed);
            Assert.IsFalse(rejected);
            return packed;
        }

        OffscreenFrame Read(PlanetLocalFrame frame, PlanetVector pose)
        {
            var scene = new RenderScene(Matrix4x4.Identity, Vector4.One,
                Matrix4x4.CreateLookTo(frame.ToLocal(pose), -Vector3.UnitZ, Vector3.UnitY),
                2 * MathF.Atan(0.5f), 0.1f, 100) { Sunlight = new(Vector3.UnitZ, Vector3.Zero, Vector3.One) };
            return GpuDevice.RenderOffscreen(gpu, edge, edge, new(++sequence, Vector4.Zero, scene));
        }

        void Install(RenderMeshRegion region)
        {
            var previous = gpu.ActiveMeshGeometry;
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(region);
            previous?.Dispose();
        }

        static string Pixel(OffscreenFrame pixels, int x) => Convert.ToHexString(pixels.ColorAt(x, sampleY));

        static void VerifyGoldenProbe(OffscreenFrame observed, int x, ReadOnlySpan<byte> golden)
        {
            var actual = observed.ColorAt(x, sampleY);
            for (var channel = 0; channel < 3; channel++)
                Assert.AreEqual((float)golden[channel], actual[channel], 1f, $"Golden probe {x}, channel {channel}.");
            Assert.AreEqual(golden[3], actual[3]);
        }

        static void Verify(OffscreenFrame a, OffscreenFrame b, float tolerance)
        {
            Compare(rockX);
            Compare(oreX);

            void Compare(int x)
            {
                Assert.IsLessThan(1f, a.DepthAt(x, sampleY));
                Assert.AreEqual(a.DepthAt(x, sampleY), b.DepthAt(x, sampleY), 0.000002f);
                var first = a.ColorAt(x, sampleY);
                var second = b.ColorAt(x, sampleY);
                for (var channel = 0; channel < 4; channel++)
                    Assert.AreEqual((float)first[channel], second[channel], tolerance, $"Probe {x}, channel {channel}.");
            }
        }
    }
}
