using System.Numerics;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class SessionOrbitGeometryTests
{
    private static readonly PlanetVector[] Points =
        [new(-2, -1, 4096), new(2, -1, 4096), new(2, 1, 4096), new(-2, 1, 4096)];

    [TestMethod]
    public void OrbitalVerticesKeepCanonicalMaterialColorsAndWinding()
    {
        uint[] colors = [0xff010203, 0xff040506, 0xff070809, 0xff0a0b0c];
        var source = RenderMesh.Create([new(-2, -1, 4096), new(2, -1, 4096), new(2, 1, 4096), new(-2, 1, 4096)],
            [0, 1, 2, 0, 2, 3], colors);

        Assert.IsTrue(D3D12Renderer.TryBuildOrbitalTerrainMesh(source, Points, [7, 19, 19, 7], out var mesh));
        Assert.IsNotNull(mesh);
        CollectionAssert.AreEqual(new uint[] { 1, 2, 0, 3, 4, 5 }, mesh.Indices.ToArray());
        CollectionAssert.AreEqual(new[] { colors[0], colors[1], colors[2], colors[0], colors[2], colors[3] },
            mesh.Colors.ToArray());
        CollectionAssert.AreEqual(new ushort[] { 7, 19, 19, 7, 19, 7 },
            mesh.TerrainAppearance.ToArray().Select(static vertex => vertex.Material).ToArray());
        Assert.AreEqual(source.Positions[0], mesh.Positions[0]);
        Assert.AreEqual(mesh.Positions[0], mesh.Positions[3]);
        Assert.AreEqual(mesh.PackedNormals[0], mesh.PackedNormals[3]);

        var a = PlanetTriplanarMapping.FromCanonical(Points[0]);
        var b = PlanetTriplanarMapping.FromCanonical(Points[1]);
        var c = PlanetTriplanarMapping.FromCanonical(Points[2]);
        Assert.IsTrue(PlanetTriplanarMapping.TryGetTriangleAnchor(a, b, c, out var anchor));
        Assert.AreEqual(PlanetTriplanarMapping.ToLocalTexels(a, anchor), mesh.TerrainAppearance[0].Texels);
        Assert.IsTrue(mesh.TerrainAppearance[0].HasMapping);
    }

    [TestMethod]
    public void WideOrbitalTriangleUsesTheExistingMaterialFallback()
    {
        var source = RenderMesh.Create([new(-1250, 0, 4096), new(1250, 0, 4096), new(0, 5, 4096)], [0, 1, 2]);
        Assert.IsTrue(D3D12Renderer.TryBuildOrbitalTerrainMesh(source,
            [new(-1250, 0, 4096), new(1250, 0, 4096), new(0, 5, 4096)], [7, 7, 7], out var mesh));
        Assert.IsNotNull(mesh);
        foreach (var vertex in mesh.TerrainAppearance)
        {
            Assert.AreEqual((ushort)7, vertex.Material);
            Assert.IsFalse(vertex.HasMapping);
            Assert.AreEqual(Vector3.Zero, vertex.Texels);
        }
    }

    [TestMethod]
    public void OrbitalSourceRejectsGeometryFromADifferentCanonicalCut()
    {
        var source = RenderMesh.Create([new(-1, 0, 4096), new(1, 0, 4096), new(0, 1, 4096)], [0, 1, 2]);
        Assert.ThrowsExactly<ArgumentException>(() => D3D12Renderer.TryBuildOrbitalTerrainMesh(source,
            [new(-1, 0, 4096), new(1, 0, 4096), new(0, 1, 4097)], [7, 7, 7], out _));
    }

    [TestMethod]
    public void OrbitalExpansionDeclinesMeshesAboveTheVertexLimit()
    {
        var source = RenderMesh.Create([new(-1, 0, 4096), new(1, 0, 4096), new(0, 1, 4096)],
            new uint[RenderMesh.MaximumVertexCount + 3]);
        Assert.IsFalse(D3D12Renderer.TryBuildOrbitalTerrainMesh(source,
            [new(-1, 0, 4096), new(1, 0, 4096), new(0, 1, 4096)], [7, 7, 7], out var mesh));
        Assert.IsNull(mesh);
    }
}
