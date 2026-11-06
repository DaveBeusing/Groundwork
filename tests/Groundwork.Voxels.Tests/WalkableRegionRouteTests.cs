using Groundwork.Voxels;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class WalkableRegionRouteTests
{
    private const int Unit = 1024;
    private const int HalfWidth = Unit / 4;
    private const int Height = 7 * Unit / 4;
    private static readonly ChunkCoordinate WestEdge = new(-4, 0, 0);
    private static readonly ChunkCoordinate EastEdge = new(4, 0, 0);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SeededRouteCrossesFourSeamsEachWayAndReloadsTheWestEdgeDeterministically()
    {
        var first = RunRoute();
        var second = RunRoute();
        CollectionAssert.AreEqual(first, second);
        TestContext.WriteLine(string.Join(Environment.NewLine, first));
    }

    private static string[] RunRoute()
    {
        var region = new WalkableRegion(new RegionBounds(-4, 4, -2, 2));
        var state = new VoxelMotionState(new VoxelPose(-20 * Unit - Unit / 2, 4 * Unit, 7 * Unit + Unit / 2));
        var anchors = new List<int>();
        var trace = new List<string>();
        var westLoads = new List<(ulong Identity, byte[] Cells)>();
        var motionTicks = 0;
        var holdTicks = 0;
        var waiting = System.Diagnostics.Stopwatch.StartNew();
        PublishAndHold();
        var westIdentity = RequireIdentity(WestEdge);
        Assert.AreEqual(westLoads[0].Identity, westIdentity);

        Walk(128, 0, 424, new VoxelPose(32 * Unit + Unit / 2, 4 * Unit, 7 * Unit + Unit / 2));
        AssertNoIdentity(WestEdge);
        var eastIdentity = RequireIdentity(EastEdge);
        Assert.AreNotEqual(0UL, eastIdentity);
        Assert.HasCount(1, westLoads);

        Walk(128, 0, 24, new VoxelPose(33 * Unit + 3 * Unit / 4, 4 * Unit, 7 * Unit + Unit / 2), clampedTicks: 14);
        Walk(0, 128, 32, new VoxelPose(33 * Unit + 3 * Unit / 4, 4 * Unit, 11 * Unit + Unit / 2));
        Walk(-128, 0, 434, new VoxelPose(-20 * Unit - Unit / 2, 4 * Unit, 11 * Unit + Unit / 2));
        Assert.AreEqual(914, motionTicks);
        CollectionAssert.AreEqual(new[] { -2, -1, 0, 1, 2, 1, 0, -1, -2 }, anchors);
        AssertNoIdentity(EastEdge);
        Assert.HasCount(2, westLoads);
        Assert.AreNotEqual(westIdentity, westLoads[1].Identity);
        Assert.AreEqual(westLoads[1].Identity, RequireIdentity(WestEdge));
        CollectionAssert.AreEqual(westLoads[0].Cells, westLoads[1].Cells);

        var highWater = region.HighWater;
        Assert.IsLessThanOrEqualTo(50, highWater.SnapshotSlots);
        Assert.IsLessThanOrEqualTo(2, highWater.Loads);
        Assert.IsLessThanOrEqualTo(1, highWater.MeshWork);
        Assert.IsLessThanOrEqualTo(96L * 1024 * 1024, highWater.MeshBytes);
        trace.Add($"seed=0x{RegionTestTerrain.Seed:X8} version={RegionTestTerrain.Version} anchors={string.Join(",", anchors)} " +
            $"motion={motionTicks} holds={holdTicks} west-loads={westIdentity},{westLoads[1].Identity} " +
            $"high-water={highWater}");
        region.Stop();
        Assert.IsTrue(region.IsSettled);
        Assert.AreEqual(default, region.Usage);
        return [.. trace];

        void Walk(int dx, int dz, int count, VoxelPose endpoint, int clampedTicks = 0)
        {
            PublishAndHold();
            for (var tick = 0; tick < count; tick++)
            {
                var before = state.Pose;
                Move(dx, dz);
                var shouldClamp = tick >= count - clampedTicks;
                Assert.AreEqual(shouldClamp ? before.X : before.X + dx, state.Pose.X);
                Assert.AreEqual(before.Z + dz, state.Pose.Z);
                motionTicks++;
                PublishAndHold();
            }
            Assert.AreEqual(endpoint, state.Pose);
            trace.Add($"tick={motionTicks} pose={state.Pose} generation={region.DesiredGeneration}");
        }

        void PublishAndHold()
        {
            region.PublishTick(state.Pose, published: true);
            var desired = region.DesiredAnchor!.Value;
            waiting.Restart();
            while (!IsActive(desired))
            {
                Assert.IsTrue(waiting.Elapsed < TimeSpan.FromSeconds(10), $"Anchor {desired} did not settle.");
                using (var read = region.TryRead())
                {
                    if (read is not null)
                        CheckStep(read, 0, 0);
                }
                holdTicks++;
                if (region.TryTakeLoad() is { } load)
                {
                    var cells = RegionTestTerrain.GenerateCells(load.Coordinate);
                    if (load.Coordinate == WestEdge)
                        westLoads.Add((load.LoadIdentity, cells));
                    Assert.IsTrue(region.CompleteLoad(load, ChunkSnapshot.Create(load.Coordinate, 1, cells)));
                }
                else
                {
                    var mesh = region.TryTakeMesh();
                    Assert.IsNotNull(mesh);
                    var result = mesh.Build();
                    Assert.IsNotNull(result);
                    Assert.IsTrue(region.CompleteMesh(mesh, result));
                }
                region.PublishTick(state.Pose, published: true);
            }
            if (anchors.Count == 0 || anchors[^1] != desired.X)
                anchors.Add(desired.X);
        }

        bool IsActive(ChunkCoordinate desired)
        {
            using var read = region.TryRead();
            return read?.Anchor == desired;
        }

        void Move(int dx, int dz)
        {
            using var read = region.TryRead();
            Assert.IsNotNull(read);
            CheckStep(read, dx, dz);
        }

        void CheckStep(RegionReadLease read, int dx, int dz)
        {
            var step = VoxelCollision.Step(read, state, dx, dz);
            Assert.IsFalse(step.StreamingBlocked);
            Assert.IsTrue(step.Grounded);
            Assert.AreEqual(0, step.State.VerticalVelocity);
            state = step.State;
            Assert.AreEqual(4 * Unit, state.Pose.Y);
            AssertClearVolumeAndFloor(read, state.Pose);
        }

        ulong RequireIdentity(ChunkCoordinate coordinate)
        {
            using var read = region.TryRead();
            Assert.IsNotNull(read);
            Assert.IsTrue(read.TryGetLoadIdentity(coordinate, out var id));
            return id;
        }

        void AssertNoIdentity(ChunkCoordinate coordinate)
        {
            using var read = region.TryRead();
            Assert.IsNotNull(read);
            Assert.IsFalse(read.TryGetLoadIdentity(coordinate, out _));
        }
    }

    private static void AssertClearVolumeAndFloor(RegionReadLease occupancy, VoxelPose pose)
    {
        for (var z = FloorCell(pose.Z - HalfWidth); z <= FloorCell(pose.Z + HalfWidth - 1); z++)
        for (var x = FloorCell(pose.X - HalfWidth); x <= FloorCell(pose.X + HalfWidth - 1); x++)
        {
            Assert.AreEqual(VoxelOccupancy.Solid, occupancy.GetCell(x, 3, z));
            for (var y = FloorCell(pose.Y); y <= FloorCell(pose.Y + Height - 1); y++)
                Assert.AreEqual(VoxelOccupancy.Air, occupancy.GetCell(x, y, z));
        }
    }

    private static int FloorCell(int fixedPoint) => fixedPoint >> 10;
}

internal static class RegionTestTerrain
{
    internal const uint Seed = 0x57505535;
    internal const int Version = 1;

    internal static ChunkSnapshot Generate(ChunkCoordinate coordinate) =>
        ChunkSnapshot.Create(coordinate, Version, GenerateCells(coordinate));

    internal static byte[] GenerateCells(ChunkCoordinate coordinate)
    {
        const int size = ChunkSnapshot.Size;
        var cells = new byte[ChunkSnapshot.CellCount];
        for (var z = 0; z < size; z++)
        for (var x = 0; x < size; x++)
        {
            var worldX = coordinate.X * size + x;
            var worldZ = coordinate.Z * size + z;
            var markerHash = unchecked(Seed ^ (uint)worldX * 73_856_093U ^ (uint)worldZ * 19_349_663U);
            var height = worldX == 34 && worldZ is >= 7 and <= 9 ? 8
                : worldZ >= 13 && markerHash % 17 == 0 ? 6
                : 4;
            for (var y = 0; y < height; y++)
                cells[x + size * (y + size * z)] = 1;
        }
        return cells;
    }
}
