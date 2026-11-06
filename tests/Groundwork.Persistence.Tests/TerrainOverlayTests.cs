using Groundwork.Voxels;

namespace Groundwork.Persistence.Tests;

[TestClass]
public sealed class TerrainOverlayTests
{
    private static readonly SaveWorldRecipe Recipe = new(1, 1, 0x57505535, new RegionBounds(-4, 4, -2, 2));

    [TestMethod]
    public void ReconstructsBothSidesOfASignedSeamOnEachLoad()
    {
        var left = new ChunkCoordinate(-1, 0, 0);
        var right = new ChunkCoordinate(0, 0, 0);
        TerrainCellDelta[] leftEdits = [new(1087, 0)];
        TerrainCellDelta[] rightEdits = [new(1088, 1)];
        TerrainChunkDelta[] overlay = [new(left, leftEdits), new(right, rightEdits)];

        TerrainOverlay.Validate(Recipe, overlay, FloorGenerator.Instance);

        var leftSnapshot = TerrainOverlay.CreateSnapshot(Recipe, left, 5, leftEdits, FloorGenerator.Instance);
        var rightSnapshot = TerrainOverlay.CreateSnapshot(Recipe, right, 6, rightEdits, FloorGenerator.Instance);
        var reloaded = TerrainOverlay.CreateSnapshot(Recipe, left, 19, leftEdits, FloorGenerator.Instance);

        Assert.AreEqual(left, leftSnapshot.Coordinate);
        Assert.AreEqual(right, rightSnapshot.Coordinate);
        Assert.AreEqual(5UL, leftSnapshot.Revision);
        Assert.AreEqual(6UL, rightSnapshot.Revision);
        Assert.AreEqual(19UL, reloaded.Revision);
        Assert.AreEqual((byte)0, leftSnapshot[15, 3, 4]);
        Assert.AreEqual((byte)1, leftSnapshot[14, 3, 4]);
        Assert.AreEqual((byte)1, rightSnapshot[0, 3, 4]);
        Assert.AreEqual((byte)1, rightSnapshot[0, 4, 4]);
        Assert.AreEqual((byte)0, rightSnapshot[1, 4, 4]);
        Assert.AreEqual(leftSnapshot[15, 3, 4], reloaded[15, 3, 4]);
    }

    [TestMethod]
    public void EmptyOverlayReconstructsTheGeneratorBase()
    {
        var snapshot = TerrainOverlay.CreateSnapshot(Recipe, new ChunkCoordinate(-4, 0, -2), 1, [], FloorGenerator.Instance);

        Assert.AreEqual((byte)1, snapshot[15, 3, 4]);
        Assert.AreEqual((byte)0, snapshot[15, 4, 4]);
        Assert.AreEqual((byte)0, snapshot[15, 15, 15]);
    }

    [TestMethod]
    public void ValidationRejectsRedundantAndNoncanonicalOverlayRecords()
    {
        var left = new ChunkCoordinate(-1, 0, 0);
        var right = new ChunkCoordinate(0, 0, 0);
        var leftEdit = new TerrainChunkDelta(left, [new(1087, 0)]);
        var rightEdit = new TerrainChunkDelta(right, [new(1088, 1)]);
        var redundant = new TerrainChunkDelta(left, [new(1087, 1)]);
        var empty = new TerrainChunkDelta(left, []);

        Assert.ThrowsExactly<InvalidDataException>(() => TerrainOverlay.Validate(Recipe, [redundant], FloorGenerator.Instance));
        Assert.ThrowsExactly<InvalidDataException>(() => TerrainOverlay.Validate(Recipe, [empty], FloorGenerator.Instance));
        Assert.ThrowsExactly<InvalidDataException>(() => TerrainOverlay.Validate(Recipe, [leftEdit, leftEdit], FloorGenerator.Instance));
        Assert.ThrowsExactly<InvalidDataException>(() => TerrainOverlay.Validate(Recipe, [rightEdit, leftEdit], FloorGenerator.Instance));
    }

    [TestMethod]
    public void SnapshotRejectsRedundantOutOfBoundsAndOutOfRangeEdits()
    {
        var outside = new ChunkCoordinate(-5, 0, 0);
        var left = new ChunkCoordinate(-1, 0, 0);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            TerrainOverlay.CreateSnapshot(Recipe, outside, 1, [], FloorGenerator.Instance));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            TerrainOverlay.CreateSnapshot(Recipe, left, 1, [new(1087, 1)], FloorGenerator.Instance));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            TerrainOverlay.CreateSnapshot(Recipe, left, 1, [new(4096, 0)], FloorGenerator.Instance));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            TerrainOverlay.CreateSnapshot(Recipe, left, 1, [new(1087, 2)], FloorGenerator.Instance));
    }

    [TestMethod]
    public void RejectsIncompleteGeneratorOutputAsAGeneratorFailure()
    {
        var coordinate = new ChunkCoordinate(-1, 0, 0);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            TerrainOverlay.CreateSnapshot(Recipe, coordinate, 1, [new(1087, 0)], new IncompleteGenerator()));
    }

    private sealed class IncompleteGenerator : ISaveTerrainGenerator
    {
        public void Generate(SaveWorldRecipe recipe, ChunkCoordinate coordinate, Span<byte> cells) => cells[1087] = 1;
    }
}
