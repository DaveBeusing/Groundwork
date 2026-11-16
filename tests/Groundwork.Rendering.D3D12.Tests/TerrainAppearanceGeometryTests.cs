using System.Numerics;
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

    private static PlanetTerrainLease BuildOrbit(PlanetDescriptor descriptor, PlanetLocalFrame origin, bool cube)
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

    private static PlanetVolumeMesh BuildVolume(SurfaceField field)
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

    private static PlanetDescriptor Descriptor(double radius) => new(new("551ffcb9-db29-40c3-8093-91929e4d65a2"),
        new("38b17c17-dacf-46ad-85db-4b5cad78c393"), 42, 1, 1, 1, new string('a', 64), radius);

    private sealed class SurfaceField(PlanetDescriptor descriptor) : IPlanetBrickSource
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
            return (cell.X & 1) == 0 ? Int01Textures.Rock : Int01Textures.Ore;
        }
    }
}
