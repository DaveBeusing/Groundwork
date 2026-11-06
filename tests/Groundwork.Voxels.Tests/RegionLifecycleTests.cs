using Groundwork.Voxels;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class RegionLifecycleTests
{
    private const int Unit = 1024;
    private static readonly VoxelPose Origin = new(Unit / 2, 4 * Unit, Unit / 2);
    private static readonly VoxelPose East = new(16 * Unit + Unit / 2, 4 * Unit, Unit / 2);
    private static readonly VoxelPose West = new(-16 * Unit + Unit / 2, 4 * Unit, Unit / 2);

    [TestMethod]
    public void DelayedObsoleteLoadsKeepBothReservationsUntilTheySettle()
    {
        var region = new WalkableRegion(new RegionBounds(-4, 4, -2, 2));
        var initial = new VoxelPose(-48 * Unit + Unit / 2, 4 * Unit, Unit / 2);
        var latest = new VoxelPose(48 * Unit + Unit / 2, 4 * Unit, Unit / 2);
        Assert.IsFalse(region.PublishTick(initial, published: true));
        var first = RequireLoad(region);
        var second = RequireLoad(region);
        Assert.AreEqual(new ChunkCoordinate(-3, 0, 0), first.Coordinate);
        Assert.AreEqual(new ChunkCoordinate(-4, 0, -1), second.Coordinate);
        Assert.AreNotEqual(first.LoadIdentity, second.LoadIdentity);
        Assert.IsNull(region.TryTakeLoad());
        Assert.IsNull(region.TryTakeMesh());

        Assert.IsFalse(region.PublishTick(latest, published: true));
        Assert.IsTrue(first.IsCancellationRequested);
        Assert.IsTrue(second.IsCancellationRequested);
        Assert.AreEqual(2, region.Usage.Loads);
        Assert.IsNull(region.TryTakeLoad());
        Assert.IsFalse(region.CompleteLoad(second, RegionTestTerrain.Generate(second.Coordinate)));
        var latestFirst = RequireLoad(region);
        Assert.AreEqual(new ChunkCoordinate(3, 0, 0), latestFirst.Coordinate);
        Assert.IsGreaterThan(second.LoadIdentity, latestFirst.LoadIdentity);
        Assert.IsFalse(region.FailLoad(first, new InvalidOperationException("Obsolete load failed.")));
        Assert.IsNull(region.Failure);
        var latestSecond = RequireLoad(region);
        Assert.AreEqual(new ChunkCoordinate(2, 0, -1), latestSecond.Coordinate);
        Assert.IsTrue(region.CompleteLoad(latestSecond, RegionTestTerrain.Generate(latestSecond.Coordinate)));
        Assert.IsFalse(region.CompleteLoad(latestSecond, RegionTestTerrain.Generate(latestSecond.Coordinate)));
        Assert.IsTrue(region.CompleteLoad(latestFirst, RegionTestTerrain.Generate(latestFirst.Coordinate)));
        Assert.IsNull(region.TryRead());
        DrainLoads(region);
        var mesh = RequireMesh(region);
        Assert.IsTrue(region.CompleteMesh(mesh, mesh.Build()));
        Assert.IsNull(region.TryRead());
        Assert.IsTrue(region.PublishTick(latest, published: true));
        using (var read = region.TryRead())
        {
            Assert.IsNotNull(read);
            Assert.AreEqual(new ChunkCoordinate(3, 0, 0), read.Anchor);
            Assert.IsTrue(read.TryGetLoadIdentity(latestFirst.Coordinate, out var identity));
            Assert.AreEqual(latestFirst.LoadIdentity, identity);
            Assert.IsFalse(read.TryGetLoadIdentity(first.Coordinate, out _));
        }
        Assert.AreEqual(2, region.HighWater.Loads);
        Assert.IsLessThanOrEqualTo(50, region.HighWater.SnapshotSlots);
        AssertSettledAfterStop(region);
    }

    [TestMethod]
    public void AFailedWorldPublicationKeepsTheActiveAndDesiredRegions()
    {
        var region = CreatePublishedRegion();
        var activeGeneration = region.DesiredGeneration;
        Assert.IsFalse(region.PublishTick(East, published: true));
        var eastGeneration = region.DesiredGeneration;
        Assert.IsGreaterThan(activeGeneration, eastGeneration);
        DrainLoads(region);
        var mesh = RequireMesh(region);
        Assert.IsTrue(region.CompleteMesh(mesh, mesh.Build()));

        Assert.IsFalse(region.PublishTick(West, published: false));
        Assert.AreEqual(new ChunkCoordinate(1, 0, 0), region.DesiredAnchor);
        Assert.AreEqual(eastGeneration, region.DesiredGeneration);
        using (var read = region.TryRead())
        {
            Assert.IsNotNull(read);
            Assert.AreEqual(default(ChunkCoordinate), read.Anchor);
            Assert.AreEqual(activeGeneration, read.Generation);
        }
        Assert.IsTrue(region.PublishTick(East, published: true));
        using (var read = region.TryRead())
        {
            Assert.IsNotNull(read);
            Assert.AreEqual(eastGeneration, read.Generation);
            Assert.AreEqual(new ChunkCoordinate(1, 0, 0), read.Anchor);
        }
        AssertSettledAfterStop(region);
    }

    [TestMethod]
    public void FinishedMeshCannotPublishAfterANewerAnchorSupersedesIt()
    {
        var region = CreatePublishedRegion();
        var activeGeneration = region.DesiredGeneration;
        Assert.IsFalse(region.PublishTick(East, published: true));
        DrainLoads(region);
        var superseded = RequireMesh(region);
        var stale = superseded.Build();
        Assert.IsNotNull(stale);
        Assert.IsFalse(region.PublishTick(West, published: true));
        Assert.IsTrue(superseded.IsCancellationRequested);
        Assert.AreEqual(1, region.Usage.MeshWork);
        DrainLoads(region);
        Assert.IsNull(region.TryTakeMesh());
        Assert.IsFalse(region.CompleteMesh(superseded, stale));
        Assert.IsFalse(region.PublishTick(West, published: true));
        using (var read = region.TryRead())
        {
            Assert.IsNotNull(read);
            Assert.AreEqual(activeGeneration, read.Generation);
        }

        var current = RequireMesh(region);
        Assert.IsGreaterThan(superseded.Generation, current.Generation);
        Assert.IsTrue(region.CompleteMesh(current, current.Build()));
        Assert.IsTrue(region.PublishTick(West, published: true));
        using (var read = region.TryRead())
        {
            Assert.IsNotNull(read);
            Assert.AreEqual(current.Generation, read.Generation);
            Assert.AreEqual(new ChunkCoordinate(-1, 0, 0), read.Anchor);
            Assert.AreEqual(9, read.MeshCount);
            for (var index = 0; index < read.MeshCount; index++)
            {
                Assert.AreEqual(new ChunkCoordinate(-2 + index / 3, 0, -1 + index % 3), read.GetMeshCoordinate(index));
                Assert.IsFalse(read.GetMeshPositions(index).IsEmpty);
                Assert.IsFalse(read.GetMeshIndices(index).IsEmpty);
            }
        }
        Assert.AreEqual(1, region.HighWater.MeshWork);
        AssertSettledAfterStop(region);
    }

    [TestMethod]
    public void PermanentLoadFailureReportsItsReservationAndClosesFurtherAdmission()
    {
        var region = CreatePublishedRegion();
        Assert.IsFalse(region.PublishTick(East, published: true));
        var failed = RequireLoad(region);
        var draining = RequireLoad(region);
        var error = new InvalidOperationException("Terrain generation failed.");

        Assert.IsTrue(region.FailLoad(failed, error));
        Assert.AreEqual(new RegionFailure(failed.Coordinate, failed.Generation, error), region.Failure);
        Assert.IsTrue(draining.IsCancellationRequested);
        Assert.IsFalse(region.IsSettled);
        Assert.AreEqual(1, region.Usage.Loads);
        Assert.IsNull(region.TryTakeLoad());
        Assert.IsNull(region.TryTakeMesh());
        using (var fallback = region.TryRead())
        {
            Assert.IsNotNull(fallback);
            Assert.AreEqual(default(ChunkCoordinate), fallback.Anchor);
        }
        region.Stop();
        Assert.IsNull(region.TryRead());
        Assert.IsFalse(region.CompleteLoad(draining, null));
        Assert.IsTrue(region.IsSettled);
        Assert.AreEqual(default, region.Usage);
    }

    [TestMethod]
    public void PermanentMeshFailureReportsCurrentAnchorAndPreservesAnExistingReadUntilRelease()
    {
        var region = CreatePublishedRegion();
        using var read = region.TryRead();
        Assert.IsNotNull(read);
        Assert.IsFalse(region.PublishTick(East, published: true));
        DrainLoads(region);
        var work = RequireMesh(region);
        var error = new InvalidOperationException("Meshing failed.");

        Assert.IsTrue(region.FailMesh(work, error));
        Assert.AreEqual(new RegionFailure(new ChunkCoordinate(1, 0, 0), work.Generation, error), region.Failure);
        Assert.IsFalse(region.IsSettled);
        Assert.AreEqual(0, region.Usage.MeshWork);
        Assert.AreEqual(default(ChunkCoordinate), read.Anchor);
        Assert.AreEqual(VoxelOccupancy.Solid, read.GetCell(0, 3, 0));
        region.Stop();
        Assert.IsFalse(region.IsSettled);
        read.Dispose();
        Assert.IsTrue(region.IsSettled);
        Assert.AreEqual(default, region.Usage);
        Assert.IsNull(region.TryRead());
    }

    [TestMethod]
    public void ObsoleteMeshFailureSettlesTheWorkWithoutClosingTheCurrentIntent()
    {
        var region = CreatePublishedRegion();
        Assert.IsFalse(region.PublishTick(East, published: true));
        DrainLoads(region);
        var obsolete = RequireMesh(region);
        Assert.IsFalse(region.PublishTick(West, published: true));
        Assert.IsTrue(obsolete.IsCancellationRequested);

        Assert.IsFalse(region.FailMesh(obsolete, new InvalidOperationException("Obsolete meshing failed.")));
        Assert.IsNull(region.Failure);
        Assert.AreEqual(0, region.Usage.MeshWork);
        DrainLoads(region);
        var current = RequireMesh(region);
        Assert.IsTrue(region.CompleteMesh(current, current.Build()));
        Assert.IsTrue(region.PublishTick(West, published: true));
        AssertSettledAfterStop(region);
    }

    [TestMethod]
    public void AReadyRegionDoesNotPublishWithThePlayerInsideSolidTerrain()
    {
        var region = CreatePublishedRegion();
        Assert.IsFalse(region.PublishTick(East, published: true));
        DrainLoads(region);
        var work = RequireMesh(region);
        Assert.IsTrue(region.CompleteMesh(work, work.Build()));

        var penetrating = East with { Y = 3 * Unit };
        Assert.IsFalse(region.PublishTick(penetrating, published: true));
        using (var read = region.TryRead())
        {
            Assert.IsNotNull(read);
            Assert.AreEqual(default(ChunkCoordinate), read.Anchor);
        }
        Assert.IsTrue(region.PublishTick(East, published: true));
        AssertSettledAfterStop(region);
    }

    [TestMethod]
    public void InvalidPoseCannotAdvanceTheDesiredRegion()
    {
        var region = CreatePublishedRegion();
        var generation = region.DesiredGeneration;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            region.PublishTick(East with { Y = int.MaxValue }, published: true));
        Assert.AreEqual(generation, region.DesiredGeneration);
        Assert.IsNull(region.TryTakeLoad());
        AssertSettledAfterStop(region);
    }

    [TestMethod]
    public void StopDuringGenerationWaitsForLateLoadSettlement()
    {
        var region = new WalkableRegion(new RegionBounds(-4, 4, -2, 2));
        Assert.IsFalse(region.PublishTick(Origin, published: true));
        var work = RequireLoad(region);
        Assert.AreEqual(1, region.Usage.Loads);
        region.Stop();
        Assert.IsTrue(work.IsCancellationRequested);
        Assert.IsFalse(region.IsSettled);
        Assert.AreEqual(1, region.Usage.SnapshotSlots);
        Assert.IsNull(region.TryTakeLoad());
        Assert.IsFalse(region.PublishTick(Origin, published: true));
        Assert.IsFalse(region.CompleteLoad(work, RegionTestTerrain.Generate(work.Coordinate)));
        Assert.IsTrue(region.IsSettled);
        Assert.AreEqual(default, region.Usage);
    }

    [TestMethod]
    public void SnapshotCapacityIsNotReusedWhileAnOldRegionIsStillLeased()
    {
        var region = new WalkableRegion(new RegionBounds(-10, 10, -10, 10));
        var farWest = new VoxelPose(-80 * Unit + Unit / 2, 4 * Unit, Unit / 2);
        Assert.IsFalse(region.PublishTick(farWest, published: true));
        DrainLoads(region);
        var initial = RequireMesh(region);
        Assert.IsTrue(region.CompleteMesh(initial, initial.Build()));
        Assert.IsTrue(region.PublishTick(farWest, published: true));
        using var retained = region.TryRead();
        Assert.IsNotNull(retained);

        Assert.IsFalse(region.PublishTick(Origin, published: true));
        DrainLoads(region);
        var replacement = RequireMesh(region);
        Assert.IsTrue(region.CompleteMesh(replacement, replacement.Build()));
        Assert.IsTrue(region.PublishTick(Origin, published: true));
        Assert.AreEqual(50, region.Usage.SnapshotSlots);
        Assert.AreEqual(50, region.HighWater.SnapshotSlots);

        Assert.IsFalse(region.PublishTick(East, published: true));
        Assert.IsNull(region.TryTakeLoad());
        Assert.IsNull(region.TryTakeMesh());
        Assert.AreEqual(50, region.Usage.SnapshotSlots);
        Assert.AreEqual(new ChunkCoordinate(-5, 0, 0), retained.Anchor);
        Assert.AreEqual(VoxelOccupancy.Solid, retained.GetCell(-80, 3, 0));
        retained.Dispose();
        Assert.AreEqual(25, region.Usage.SnapshotSlots);

        DrainLoads(region);
        var current = RequireMesh(region);
        Assert.IsTrue(region.CompleteMesh(current, current.Build()));
        Assert.IsTrue(region.PublishTick(East, published: true));
        Assert.AreEqual(50, region.HighWater.SnapshotSlots);
        AssertSettledAfterStop(region);
    }

    [TestMethod]
    public void StopDuringMeshingKeepsWorkAndReadersUntilTheyReleaseTheirOwnership()
    {
        var region = CreatePublishedRegion();
        Assert.IsFalse(region.PublishTick(East, published: true));
        DrainLoads(region);
        var work = RequireMesh(region);
        using var read = region.TryRead();
        Assert.IsNotNull(read);
        Assert.AreEqual(VoxelOccupancy.Solid, read.GetCell(0, 3, 0));
        region.Stop();
        Assert.IsTrue(work.IsCancellationRequested);
        Assert.IsFalse(region.IsSettled);
        Assert.AreEqual(1, region.Usage.MeshWork);
        Assert.IsGreaterThanOrEqualTo(25, region.Usage.SnapshotSlots);
        Assert.IsNull(region.TryTakeMesh());
        Assert.IsNull(region.TryRead());
        Assert.IsFalse(region.CompleteMesh(work, null));
        Assert.IsFalse(region.IsSettled);
        Assert.AreEqual(VoxelOccupancy.Solid, read.GetCell(0, 3, 0));
        read.Dispose();
        Assert.IsTrue(region.IsSettled);
        Assert.AreEqual(default, region.Usage);
    }

    [TestMethod]
    public void HaloDataIsUnavailableForCollisionUntilItEntersTheInnerRegion()
    {
        var region = CreatePublishedRegion();
        using (var read = region.TryRead())
        {
            Assert.IsNotNull(read);
            Assert.IsTrue(read.TryGetLoadIdentity(new ChunkCoordinate(2, 0, 0), out _));
            Assert.AreEqual(VoxelOccupancy.Unavailable, read.GetCell(32, 3, 0));
            Assert.AreEqual(VoxelOccupancy.Solid, read.GetCell(80, 4, 0));
            Assert.AreEqual(VoxelOccupancy.Air, read.GetCell(32, 16, 0));
            var start = new VoxelPose(31 * Unit, 4 * Unit, Unit / 2);
            var sweep = VoxelCollision.Sweep(read, start, 2 * Unit, -8, 0);
            Assert.AreEqual(new VoxelPose(32 * Unit - Unit / 4, 4 * Unit, Unit / 2), sweep.Pose);
            Assert.IsTrue(sweep.StreamingBlocked);
            Assert.IsTrue(sweep.Grounded);
        }
        Assert.IsFalse(region.PublishTick(East, published: true));
        DrainLoads(region);
        var work = RequireMesh(region);
        Assert.IsTrue(region.CompleteMesh(work, work.Build()));
        Assert.IsTrue(region.PublishTick(East, published: true));
        using (var read = region.TryRead())
        {
            Assert.IsNotNull(read);
            Assert.AreEqual(VoxelOccupancy.Solid, read.GetCell(32, 3, 0));
            var start = new VoxelPose(31 * Unit, 4 * Unit, Unit / 2);
            var sweep = VoxelCollision.Sweep(read, start, 2 * Unit, -8, 0);
            Assert.AreEqual(new VoxelPose(33 * Unit, 4 * Unit, Unit / 2), sweep.Pose);
            Assert.IsFalse(sweep.StreamingBlocked);
            Assert.IsTrue(sweep.Grounded);
        }
        AssertSettledAfterStop(region);
    }

    private static WalkableRegion CreatePublishedRegion()
    {
        var region = new WalkableRegion(new RegionBounds(-4, 4, -2, 2));
        Assert.IsFalse(region.PublishTick(Origin, published: true));
        DrainLoads(region);
        var mesh = RequireMesh(region);
        Assert.IsTrue(region.CompleteMesh(mesh, mesh.Build()));
        Assert.IsTrue(region.PublishTick(Origin, published: true));
        return region;
    }

    private static void DrainLoads(WalkableRegion region)
    {
        while (region.TryTakeLoad() is { } load)
            Assert.IsTrue(region.CompleteLoad(load, RegionTestTerrain.Generate(load.Coordinate)));
    }

    private static RegionLoadWork RequireLoad(WalkableRegion region)
    {
        var work = region.TryTakeLoad();
        Assert.IsNotNull(work);
        return work;
    }

    private static RegionMeshWork RequireMesh(WalkableRegion region)
    {
        var work = region.TryTakeMesh();
        Assert.IsNotNull(work);
        return work;
    }

    private static void AssertSettledAfterStop(WalkableRegion region)
    {
        region.Stop();
        Assert.IsTrue(region.IsSettled);
        Assert.AreEqual(default, region.Usage);
    }
}
