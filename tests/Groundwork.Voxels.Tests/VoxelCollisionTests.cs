using Groundwork.Voxels;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class VoxelCollisionTests
{
    [TestMethod]
    public void FullSweepClampsToNearestWallAndAllowsTangentialMovement()
    {
        var occupancy = new Occupancy((x, y, z) => y <= 3 || (x == 2 && z is >= -1 and <= 0 && y < 7)
            ? VoxelOccupancy.Solid : VoxelOccupancy.Air);
        var start = new VoxelPose(-640, 4_096, -512);

        var hit = VoxelCollision.Sweep(occupancy, start, 8_192, 0, 0);
        var slide = VoxelCollision.Sweep(occupancy, hit.Pose, 0, 0, 1_024);

        Assert.AreEqual(new VoxelPose(1_792, 4_096, -512), hit.Pose);
        Assert.AreEqual(new VoxelPose(1_792, 4_096, 512), slide.Pose);
        Assert.IsTrue(hit.Grounded);
        Assert.IsTrue(slide.Grounded);
        Assert.IsFalse(hit.StreamingBlocked);
    }

    [TestMethod]
    public void NegativeSweepUsesTheRightFaceOfTheNegativeCell()
    {
        var occupancy = new Occupancy((x, y, z) => y <= 3 || (x == -3 && z == -1 && y < 7)
            ? VoxelOccupancy.Solid : VoxelOccupancy.Air);

        var hit = VoxelCollision.Sweep(occupancy, new(-512, 4_096, -512), -8_192, 0, 0);

        Assert.AreEqual(new VoxelPose(-1_792, 4_096, -512), hit.Pose);
        Assert.IsTrue(hit.Grounded);
    }

    [TestMethod]
    public void SweepsXBeforeZAndResolvesTheFloorAfterBoth()
    {
        var occupancy = new Occupancy((x, y, z) => (x == 1 && z == 1 && y is >= 4 and <= 7) || y <= 3
            ? VoxelOccupancy.Solid : VoxelOccupancy.Air);

        var hit = VoxelCollision.Sweep(occupancy, new(512, 4_224, 512), 1_024, -128, 1_024);

        Assert.AreEqual(new VoxelPose(1_536, 4_096, 768), hit.Pose);
        Assert.IsTrue(hit.Grounded);
    }

    [TestMethod]
    public void NegativeZSeamBlocksUntilTheNextColumnIsAvailable()
    {
        var nextColumnReady = false;
        var occupancy = new Occupancy((_, y, z) => z < -16 && !nextColumnReady
            ? VoxelOccupancy.Unavailable : Floor(y));
        var state = new VoxelMotionState(new(512, 4_096, -16_000));

        var touch = VoxelCollision.Step(occupancy, state, 0, -128);
        var held = VoxelCollision.Step(occupancy, touch.State, 0, -128);
        nextColumnReady = true;
        var resumed = VoxelCollision.Step(occupancy, held.State, 0, -128);

        Assert.AreEqual(-16_128, touch.State.Pose.Z);
        Assert.IsFalse(touch.StreamingBlocked);
        Assert.AreEqual(touch.State, held.State);
        Assert.IsTrue(held.StreamingBlocked);
        Assert.IsTrue(held.Grounded);
        Assert.AreEqual(-16_256, resumed.State.Pose.Z);
        Assert.IsFalse(resumed.StreamingBlocked);
        Assert.IsTrue(resumed.Grounded);
    }

    [TestMethod]
    public void MissingSupportFreezesMovementAndNeverSuppliesGrounding()
    {
        var floorReady = false;
        var occupancy = new Occupancy((_, y, _) => y == 3 && !floorReady
            ? VoxelOccupancy.Unavailable : Floor(y));
        var start = new VoxelMotionState(new(512, 4_096, 512), -128);

        Assert.IsFalse(VoxelCollision.HasCoverage(occupancy, start.Pose));
        var held = VoxelCollision.Step(occupancy, start, 128, 0);
        Assert.AreEqual(start.Pose, held.State.Pose);
        Assert.AreEqual(0, held.State.VerticalVelocity);
        Assert.IsFalse(held.Grounded);
        Assert.IsTrue(held.StreamingBlocked);

        floorReady = true;
        Assert.IsTrue(VoxelCollision.HasCoverage(occupancy, start.Pose));
        var resumed = VoxelCollision.Step(occupancy, held.State, 128, 0);
        Assert.AreEqual(new VoxelPose(640, 4_096, 512), resumed.State.Pose);
        Assert.AreEqual(0, resumed.State.VerticalVelocity);
        Assert.IsTrue(resumed.Grounded);
        Assert.IsFalse(resumed.StreamingBlocked);
    }

    [TestMethod]
    public void UnavailableFloorClampsTheSweepAndPreventsGravityDebt()
    {
        var occupancy = new Occupancy((_, y, _) => y <= 3 ? VoxelOccupancy.Unavailable : VoxelOccupancy.Air);
        var state = new VoxelMotionState(new(512, 4_200, 512), -256);

        var blocked = VoxelCollision.Step(occupancy, state, 0, 0);
        var stillBlocked = VoxelCollision.Step(occupancy, blocked.State, 128, 0);

        Assert.AreEqual(4_096, blocked.State.Pose.Y);
        Assert.AreEqual(0, blocked.State.VerticalVelocity);
        Assert.IsFalse(blocked.Grounded);
        Assert.IsTrue(blocked.StreamingBlocked);
        Assert.AreEqual(blocked, stillBlocked);
    }

    [TestMethod]
    public void SweepRejectsInvalidRangeBeforeReadingOccupancy()
    {
        var occupancy = new Occupancy((_, _, _) => throw new InvalidOperationException("Unexpected cell query."));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VoxelCollision.Sweep(occupancy, new(512, 4_096, 512), 8_193, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VoxelCollision.Sweep(occupancy, new(512, int.MaxValue, 512), 0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VoxelCollision.Sweep(occupancy, new(int.MinValue + 1_024, 4_096, 512), -1_024, 0, 0));
    }

    [TestMethod]
    public void SweepRejectsAPoseThatStartsInsideSolidTerrain()
    {
        var occupancy = new Occupancy((_, y, _) => Floor(y));

        Assert.ThrowsExactly<ArgumentException>(() =>
            VoxelCollision.Sweep(occupancy, new(512, 4_095, 512), 0, 0, 0));
    }

    private static VoxelOccupancy Floor(int y) => y <= 3 ? VoxelOccupancy.Solid : VoxelOccupancy.Air;

    private sealed class Occupancy(Func<int, int, int, VoxelOccupancy> cell) : IVoxelOccupancy
    {
        public VoxelOccupancy GetCell(int x, int y, int z) => cell(x, y, z);
    }
}
