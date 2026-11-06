using System.Numerics;
using Groundwork.Voxels;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class ChunkSnapshotTests
{
    [TestMethod]
    public void SnapshotCopiesCellsInXFastestOrder()
    {
        var cells = new byte[ChunkSnapshot.CellCount];
        cells[1 + 16 * (2 + 16 * 3)] = 1;
        var coordinate = new ChunkCoordinate(-1024, 1023, -1);
        var snapshot = ChunkSnapshot.Create(coordinate, 7, cells);

        Array.Clear(cells);

        Assert.AreEqual((byte)1, snapshot[1, 2, 3]);
        Assert.AreEqual((byte)0, snapshot[3, 2, 1]);
        Assert.AreEqual(coordinate, snapshot.Coordinate);
        Assert.AreEqual(7UL, snapshot.Revision);
        Assert.AreEqual(new Vector3(-16384, 16368, -16), snapshot.Coordinate.GetOrigin());
    }

    [TestMethod]
    [DataRow(-1025, 0, 0)]
    [DataRow(0, 1024, 0)]
    [DataRow(0, 0, int.MaxValue)]
    public void InvalidCoordinatesAreRejectedBeforeOriginArithmetic(int x, int y, int z)
    {
        var coordinate = new ChunkCoordinate(x, y, z);
        Assert.ThrowsExactly<ArgumentException>(() => coordinate.GetOrigin());
        Assert.ThrowsExactly<ArgumentException>(() => ChunkSnapshot.Create(coordinate, 1, new byte[ChunkSnapshot.CellCount]));
    }

    [TestMethod]
    public void InvalidLengthRevisionAndCellValueAreRejected()
    {
        var cells = new byte[ChunkSnapshot.CellCount];
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ChunkSnapshot.Create(default, 0, cells));
        Assert.ThrowsExactly<ArgumentException>(() => ChunkSnapshot.Create(default, 1, cells.AsSpan(1)));
        cells[^1] = 2;
        Assert.ThrowsExactly<ArgumentException>(() => ChunkSnapshot.Create(default, 1, cells));
    }

    [TestMethod]
    [DataRow(16, 0, 0)]
    [DataRow(0, -1, 0)]
    [DataRow(0, 0, 16)]
    public void SnapshotRejectsLocalCoordinatesOutsideTheChunk(int x, int y, int z)
    {
        var snapshot = ChunkSnapshot.Create(default, 1, new byte[ChunkSnapshot.CellCount]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = snapshot[x, y, z]);
    }
}
