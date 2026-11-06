using System.Numerics;
using Groundwork.Voxels;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class ChunkMesherTests
{
    private static readonly ChunkCoordinate HighCoordinate = new(1, 0, 0);

    [TestMethod]
    [DataRow("empty", 0, 0)]
    [DataRow("touching", 5, 5)]
    [DataRow("removed", 6, 0)]
    [DataRow("full", 1280, 1280)]
    [DataRow("checkerboard", 12288, 12288)]
    public void SurfaceCountsMatchTheContract(string shape, int lowQuads, int highQuads)
    {
        var low = new byte[ChunkSnapshot.CellCount];
        var high = new byte[ChunkSnapshot.CellCount];
        switch (shape)
        {
            case "touching":
                high[Index(0, 8, 8)] = 1;
                goto case "removed";
            case "removed":
                low[Index(15, 8, 8)] = 1;
                break;
            case "full":
                Array.Fill(low, (byte)1);
                Array.Fill(high, (byte)1);
                break;
            case "checkerboard":
                for (var i = 0; i < low.Length; i++)
                {
                    var parity = (i % 16 + i / 16 % 16 + i / 256) % 2;
                    low[i] = high[i] = (byte)(1 - parity);
                }
                break;
        }

        var pair = ChunkMesher.BuildPair(ChunkSnapshot.Create(default, 1, low), ChunkSnapshot.Create(HighCoordinate, 2, high));

        AssertQuads(pair.Low, lowQuads);
        AssertQuads(pair.High, highQuads);
        if (shape is "full" or "touching")
        {
            Assert.AreEqual(0, CountPlaneQuads(pair.Low, 0, 16));
            Assert.AreEqual(0, CountPlaneQuads(pair.High, 0, 0));
        }
        else if (shape == "removed")
        {
            Assert.AreEqual(1, CountPlaneQuads(pair.Low, 0, 16));
        }
    }

    [TestMethod]
    public void RevisionChangeInvalidatesBothMeshesAndExposesTheUnchangedNeighbor()
    {
        var cells = new byte[ChunkSnapshot.CellCount];
        Array.Fill(cells, (byte)1);
        var low = ChunkSnapshot.Create(default, 7, cells);
        var high = ChunkSnapshot.Create(HighCoordinate, 9, cells);
        var original = ChunkMesher.BuildPair(low, high);
        cells[Index(0, 8, 8)] = 0;
        var edited = ChunkSnapshot.Create(HighCoordinate, 10, cells);

        Assert.IsTrue(original.Matches(low, high));
        Assert.IsTrue(original.Matches(high, low));
        Assert.IsFalse(original.Matches(low, edited));
        Assert.IsFalse(original.Matches(low, null));

        var replacement = ChunkMesher.BuildPair(low, edited);
        Assert.IsTrue(replacement.Matches(low, edited));
        Assert.AreEqual(low.Revision, replacement.Low.Revision);
        Assert.AreEqual(edited.Revision, replacement.High.Revision);
        AssertQuads(replacement.Low, 1281);
        AssertQuads(replacement.High, 1285);
        Assert.AreEqual(1, CountPlaneQuads(replacement.Low, 0, 16));
        Assert.AreEqual(0, CountPlaneQuads(replacement.High, 0, 0));
    }

    [TestMethod]
    public void TerrainBoundaryEditAndRestoreRebuildTheUnchangedNeighbor()
    {
        const int size = ChunkSnapshot.Size;
        var lowCells = new byte[ChunkSnapshot.CellCount];
        var highCells = new byte[ChunkSnapshot.CellCount];
        // Match the WP03 WYU terrain, including the raised back half of the high chunk.
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < 4; y++)
                lowCells.AsSpan(Index(0, y, z), size).Fill(1);

            var height = z < 8 ? 4 : 8;
            for (var y = 0; y < height; y++)
                highCells.AsSpan(Index(0, y, z), size).Fill(1);
        }

        var low = ChunkSnapshot.Create(default, 1, lowCells);
        var high = ChunkSnapshot.Create(HighCoordinate, 1, highCells);
        var original = ChunkMesher.BuildPair(low, high);

        var editIndex = Index(15, 3, 4);
        lowCells[editIndex] = 0;
        var edited = ChunkSnapshot.Create(default, 2, lowCells);
        var removed = ChunkMesher.BuildPair(edited, high);

        lowCells[editIndex] = 1;
        var restored = ChunkSnapshot.Create(default, 3, lowCells);
        var replacement = ChunkMesher.BuildPair(restored, high);

        Assert.IsTrue(original.Matches(low, high));
        Assert.IsTrue(removed.Matches(edited, high));
        Assert.IsTrue(replacement.Matches(restored, high));
        Assert.IsFalse(original.Matches(edited, high));
        Assert.IsFalse(removed.Matches(restored, high));
        Assert.IsFalse(original.Matches(restored, high));

        const int raisedSeamQuads = 4 * 8;
        Assert.AreEqual(raisedSeamQuads, CountPlaneQuads(original.High, 0, 0));
        Assert.AreEqual(raisedSeamQuads + 1, CountPlaneQuads(removed.High, 0, 0));
        Assert.AreEqual(raisedSeamQuads, CountPlaneQuads(replacement.High, 0, 0));
        Assert.AreEqual(high.Revision, removed.High.Revision);

        ReadOnlySpan<Vector3> exposedFace = [new(0, 3, 4), new(0, 3, 5), new(0, 4, 5), new(0, 4, 4)];
        var matches = 0;
        for (var i = 0; i < removed.High.Positions.Length; i += exposedFace.Length)
        {
            if (removed.High.Positions.Slice(i, exposedFace.Length).SequenceEqual(exposedFace))
                matches++;
        }

        Assert.AreEqual(1, matches);
        Assert.IsTrue(original.Low.Positions.SequenceEqual(replacement.Low.Positions));
        Assert.IsTrue(original.Low.Indices.SequenceEqual(replacement.Low.Indices));
        Assert.IsTrue(original.High.Positions.SequenceEqual(replacement.High.Positions));
        Assert.IsTrue(original.High.Indices.SequenceEqual(replacement.High.Indices));
    }

    [TestMethod]
    [DataRow(-1, 0, 0, 0)]
    [DataRow(0, -1, 0, 1)]
    [DataRow(0, 0, -1, 2)]
    public void SignedSeamsOnEveryAxisHaveCanonicalIdenticalOutput(int x, int y, int z, int axis)
    {
        var lowCells = new byte[ChunkSnapshot.CellCount];
        var highCells = new byte[ChunkSnapshot.CellCount];
        var stride = axis switch { 0 => 1, 1 => 16, _ => 256 };
        var highCell = Index(axis == 0 ? 0 : 8, axis == 1 ? 0 : 8, axis == 2 ? 0 : 8);
        lowCells[highCell + 15 * stride] = highCells[highCell] = 1;
        var coordinate = new ChunkCoordinate(x, y, z);
        var low = ChunkSnapshot.Create(coordinate, 4, lowCells);
        var high = ChunkSnapshot.Create(default, 6, highCells);

        var pair = ChunkMesher.BuildPair(low, high);
        var reversed = ChunkMesher.BuildPair(high, low);

        Assert.AreEqual(coordinate, reversed.Low.Coordinate);
        Assert.AreEqual(default, reversed.High.Coordinate);
        AssertQuads(pair.Low, 5);
        AssertQuads(pair.High, 5);
        Assert.AreEqual(0, CountPlaneQuads(pair.Low, axis, 16));
        Assert.AreEqual(0, CountPlaneQuads(pair.High, axis, 0));
        Assert.IsTrue(pair.Low.Positions.SequenceEqual(reversed.Low.Positions));
        Assert.IsTrue(pair.Low.Indices.SequenceEqual(reversed.Low.Indices));
        Assert.IsTrue(pair.High.Positions.SequenceEqual(reversed.High.Positions));
        Assert.IsTrue(pair.High.Indices.SequenceEqual(reversed.High.Indices));
    }

    [TestMethod]
    public void QuadIndicesAndOutwardWindingFollowFaceOrder()
    {
        var cells = new byte[ChunkSnapshot.CellCount];
        cells[Index(3, 4, 5)] = 1;
        var pair = ChunkMesher.BuildPair(ChunkSnapshot.Create(default, 1, cells),
            ChunkSnapshot.Create(HighCoordinate, 1, new byte[ChunkSnapshot.CellCount]));
        var mesh = pair.Low;
        AssertQuads(mesh, 6);
        Vector3[] normals = [-Vector3.UnitX, Vector3.UnitX, -Vector3.UnitY, Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ];
        for (var face = 0; face < normals.Length; face++)
        {
            var positions = mesh.Positions.Slice(4 * face, 4);
            var indices = mesh.Indices.Slice(6 * face, 6);
            var first = (uint)(4 * face);
            CollectionAssert.AreEqual(new uint[] { first, first + 1, first + 2, first, first + 2, first + 3 }, indices.ToArray());
            Assert.AreEqual(normals[face], Vector3.Cross(positions[1] - positions[0], positions[2] - positions[0]));
            Assert.AreEqual(normals[face], Vector3.Cross(positions[2] - positions[0], positions[3] - positions[0]));
        }
    }

    [TestMethod]
    [DataRow(0, 0, 0)]
    [DataRow(1, 1, 0)]
    [DataRow(0, 0, -2)]
    public void PairRequiresExactlyOneSharedFace(int x, int y, int z)
    {
        var cells = new byte[ChunkSnapshot.CellCount];
        var first = ChunkSnapshot.Create(default, 1, cells);
        var second = ChunkSnapshot.Create(new(x, y, z), 1, cells);
        Assert.ThrowsExactly<ArgumentException>(() => ChunkMesher.BuildPair(first, second));
    }

    private static void AssertQuads(ChunkMesh mesh, int expected)
    {
        Assert.AreEqual(expected * 4, mesh.Positions.Length);
        Assert.AreEqual(expected * 6, mesh.Indices.Length);
        Assert.IsLessThanOrEqualTo(ChunkMesher.MaximumVerticesPerChunk, mesh.Positions.Length);
        Assert.IsLessThanOrEqualTo(ChunkMesher.MaximumIndicesPerChunk, mesh.Indices.Length);
    }

    private static int CountPlaneQuads(ChunkMesh mesh, int axis, float plane)
    {
        var count = 0;
        for (var i = 0; i < mesh.Positions.Length; i += 4)
        {
            var quad = mesh.Positions.Slice(i, 4);
            if (quad[0][axis] == plane && quad[1][axis] == plane && quad[2][axis] == plane && quad[3][axis] == plane)
            {
                count++;
            }
        }

        return count;
    }

    private static int Index(int x, int y, int z) => x + 16 * (y + 16 * z);
}
