using System.Numerics;

namespace Groundwork.Rendering.Tests;

[TestClass]
public sealed class RenderMeshTests
{
    [TestMethod]
    public void MeshOwnsThePositionsAndIndicesAfterCreation()
    {
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        uint[] indices = [0, 1, 2];
        var mesh = RenderMesh.Create(positions, indices);

        positions[1] = Vector3.One;
        indices[2] = 0;

        CollectionAssert.AreEqual(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, mesh.Positions.ToArray());
        CollectionAssert.AreEqual(new uint[] { 0, 1, 2 }, mesh.Indices.ToArray());
    }

    [TestMethod]
    public void MeshSupportsEmptyGeometryAndRejectsOnlyOneEmptyBuffer()
    {
        var empty = RenderMesh.Create([], []);
        Assert.IsTrue(empty.Positions.IsEmpty);
        Assert.IsTrue(empty.Indices.IsEmpty);

        Assert.ThrowsExactly<ArgumentException>(() => RenderMesh.Create([Vector3.Zero], []));
        Assert.ThrowsExactly<ArgumentException>(() => RenderMesh.Create([], [0, 0, 0]));
    }

    [TestMethod]
    public void MeshOwnsOptionalColorsAndRequiresOneColorPerPosition()
    {
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        uint[] indices = [0, 1, 2];
        uint[] colors = [0xFF0000FF, 0xFF00FF00, 0xFFFF0000];
        var mesh = RenderMesh.Create(positions, indices, colors);
        colors[0] = 0;

        Assert.IsTrue(mesh.Colors.SequenceEqual([0xFF0000FF, 0xFF00FF00, 0xFFFF0000]));
        Assert.IsTrue(RenderMesh.Create(positions, indices).Colors.IsEmpty);
        Assert.ThrowsExactly<ArgumentException>(() => RenderMesh.Create(positions, indices, [0]));
        Assert.ThrowsExactly<ArgumentException>(() => RenderMesh.Create([], [], [0]));
    }

    [TestMethod]
    public void IndicesMustFormTrianglesAndAddressExistingPositions()
    {
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        Assert.ThrowsExactly<ArgumentException>(() => RenderMesh.Create(positions, [0, 1]));
        Assert.ThrowsExactly<ArgumentException>(() => RenderMesh.Create(positions, [0, 1, 3]));
        Assert.ThrowsExactly<ArgumentException>(() => RenderMesh.Create(positions, [0, 1, uint.MaxValue]));
    }

    [TestMethod]
    [DataRow(float.NaN, 0f, 0f)]
    [DataRow(0f, float.PositiveInfinity, 0f)]
    [DataRow(0f, 0f, float.NegativeInfinity)]
    public void MeshPositionsAndInstanceTranslationsMustBeFinite(float x, float y, float z)
    {
        var invalid = new Vector3(x, y, z);
        var mesh = RenderMesh.Create([], []);
        Assert.ThrowsExactly<ArgumentException>(() => RenderMesh.Create([invalid], [0, 0, 0]));
        Assert.ThrowsExactly<ArgumentException>(() => new RenderMeshInstance(mesh, invalid));
    }

    [TestMethod]
    public void MeshLimitsAcceptExactBoundsAndRejectLargerBuffers()
    {
        var positions = new Vector3[98_304];
        var indices = new uint[147_456];
        indices[^1] = (uint)(positions.Length - 1);

        var mesh = RenderMesh.Create(positions, indices);

        Assert.AreEqual(positions.Length, mesh.Positions.Length);
        Assert.AreEqual(indices.Length, mesh.Indices.Length);
        Assert.AreEqual(indices[^1], mesh.Indices[^1]);
        Assert.ThrowsExactly<ArgumentException>(() => RenderMesh.Create(new Vector3[98_305], indices));
        Assert.ThrowsExactly<ArgumentException>(() => RenderMesh.Create(positions, new uint[147_459]));
    }

    [TestMethod]
    public void PairPreservesBothInstancesAndAcceptsEmptyMeshes()
    {
        var empty = RenderMesh.Create([], []);
        var mesh = RenderMesh.Create([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2]);
        var first = new RenderMeshInstance(empty, new Vector3(-16, 0, 0));
        var second = new RenderMeshInstance(mesh, Vector3.Zero);
        var pair = new RenderMeshPair(first, second);

        Assert.AreEqual(first, pair.First);
        Assert.AreEqual(second, pair.Second);

        var emptyPair = new RenderMeshPair(first, new RenderMeshInstance(empty, Vector3.Zero));
        Assert.IsTrue(emptyPair.First.Mesh.Indices.IsEmpty);
        Assert.IsTrue(emptyPair.Second.Mesh.Indices.IsEmpty);
    }

    [TestMethod]
    public void PairRejectsDefaultInstances()
    {
        var valid = new RenderMeshInstance(RenderMesh.Create([], []), Vector3.Zero);
        Assert.ThrowsExactly<ArgumentNullException>(() => new RenderMeshInstance(null!, Vector3.Zero));
        Assert.ThrowsExactly<ArgumentException>(() => new RenderMeshPair(default, valid));
        Assert.ThrowsExactly<ArgumentException>(() => new RenderMeshPair(valid, default));
    }
}
