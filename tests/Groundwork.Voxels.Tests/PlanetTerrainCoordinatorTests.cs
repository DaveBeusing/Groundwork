using System.Numerics;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetTerrainCoordinatorTests
{
    private const double Radius = 64;
    private static readonly PlanetVector FirstCamera = new(Radius, 0, 0);
    private static readonly PlanetVector SecondCamera = new(0, 0, Radius);

    [TestMethod]
    public void TerrainCameraAndSupportSwitchTogetherOnlyAfterPresentation()
    {
        var coordinator = new PlanetTerrainCoordinator();
        var firstPlan = Select(FirstCamera);
        var firstFrame = PlanetLocalFrame.Create(FirstCamera);
        Assert.IsTrue(coordinator.TryStage(firstPlan, firstFrame));
        Assert.IsTrue(coordinator.TryReserveMesh(out var firstWork));
        Assert.IsFalse(coordinator.TryPreparePresentation(FirstCamera, out _));
        Assert.IsFalse(coordinator.TryReadActive(FirstCamera, out _));
        Assert.IsTrue(coordinator.CompleteMesh(firstWork, Build(firstWork.Tile)));
        CompleteAll(coordinator);

        var firstGeneration = Present(coordinator, FirstCamera);
        var walkState = new PlanetWalkState(SecondCamera, new PlanetVector(1, 0, 0), default);
        Assert.IsTrue(firstFrame.TryRebase(SecondCamera, out var secondFrame));
        var secondPlan = Select(SecondCamera);
        Assert.IsTrue(coordinator.TryStage(secondPlan, secondFrame));
        CompleteAll(coordinator);
        Assert.IsTrue(coordinator.TryReadActive(SecondCamera, out var active));
        using (active)
        {
            Assert.AreEqual(firstGeneration, active.Generation);
            Assert.AreEqual(firstFrame, active.Frame);
            Assert.AreEqual(firstFrame.ToLocal(SecondCamera), active.CameraPosition);
            Assert.IsTrue(active.TryGetSupport(FirstCamera, out _));
            Assert.IsFalse(active.TryGetSupport(SecondCamera, out _));
            var held = PlanetSurfaceWalker.Step(active, walkState, 1, 0);
            Assert.AreEqual(PlanetWalkStatus.CoverageUnavailable, held.Status);
            Assert.AreEqual(walkState, held.State);
            Assert.IsFalse(coordinator.AcknowledgePresented(active));
        }

        Assert.IsTrue(coordinator.TryPreparePresentation(SecondCamera, out var staged));
        using (staged)
        {
            Assert.AreEqual(secondFrame, staged.Frame);
            Assert.AreEqual(secondFrame.ToLocal(SecondCamera), staged.CameraPosition);
            Assert.AreNotEqual(firstGeneration, staged.Generation);
            Assert.AreEqual(secondPlan.Tiles.Length, staged.MeshCount);
            Assert.IsFalse(staged.TryGetSupport(SecondCamera, out _));

            var source = Build(secondPlan.Tiles[0]);
            var positions = staged.GetPositions(0);
            Assert.AreEqual(source.Positions.Length, positions.Length);
            for (var i = 0; i < positions.Length; i++)
                Assert.AreEqual(secondFrame.ToLocal(source.Positions[i]), positions[i]);
            Assert.IsTrue(source.Indices.SequenceEqual(staged.GetIndices(0)));
            Assert.IsTrue(coordinator.AcknowledgePresented(staged));
        }

        Assert.IsTrue(coordinator.TryReadActive(SecondCamera, out var published));
        using (published)
        {
            Assert.AreEqual(secondFrame, published.Frame);
            Assert.AreEqual(Vector3.Zero, published.CameraPosition);
            Assert.IsTrue(published.TryGetSupport(SecondCamera, out _));
            Assert.IsFalse(published.TryGetSupport(FirstCamera, out _));
            Assert.AreEqual(PlanetWalkStatus.Moved, PlanetSurfaceWalker.Step(published, walkState, 1, 0).Status);
        }
    }

    [TestMethod]
    public void SupersededReservationsKeepTheirSlotsUntilLateCompletion()
    {
        var coordinator = new PlanetTerrainCoordinator();
        var frame = PlanetLocalFrame.Create(FirstCamera);
        Assert.IsTrue(coordinator.TryStage(Select(FirstCamera), frame));
        Assert.IsTrue(coordinator.TryReserveMesh(out var oldFirst));
        Assert.IsTrue(coordinator.TryReserveMesh(out var oldSecond));
        Assert.IsFalse(coordinator.TryReserveMesh(out _));
        Assert.IsTrue(coordinator.TryStage(Select(SecondCamera), frame));
        Assert.IsFalse(coordinator.TryReserveMesh(out _));
        Assert.IsFalse(coordinator.CompleteMesh(oldFirst, Build(oldFirst.Tile)));
        Assert.IsTrue(coordinator.TryReserveMesh(out var current));
        Assert.AreNotEqual(oldSecond.Generation, current.Generation);
        Assert.IsFalse(coordinator.CompleteMesh(oldFirst, Build(oldFirst.Tile)));
        Assert.IsFalse(coordinator.TryReserveMesh(out _));
        Assert.IsFalse(coordinator.CompleteMesh(oldSecond, null));
        Assert.IsTrue(coordinator.CompleteMesh(current, Build(current.Tile)));
        CompleteAll(coordinator);

        Assert.AreEqual(current.Generation, Present(coordinator, SecondCamera));
    }

    [TestMethod]
    public void FailedReplacementKeepsActiveAndAbortsItsRemainingWork()
    {
        var coordinator = new PlanetTerrainCoordinator();
        var frame = PlanetLocalFrame.Create(FirstCamera);
        Assert.IsTrue(coordinator.TryStage(Select(FirstCamera), frame));
        CompleteAll(coordinator);
        var activeGeneration = Present(coordinator, FirstCamera);

        Assert.IsTrue(coordinator.TryStage(Select(SecondCamera), frame));
        Assert.IsTrue(coordinator.TryReserveMesh(out var failed));
        Assert.IsTrue(coordinator.TryReserveMesh(out var late));
        Assert.IsFalse(coordinator.CompleteMesh(failed, null));
        Assert.IsFalse(coordinator.TryPreparePresentation(SecondCamera, out _));
        Assert.IsFalse(coordinator.TryReserveMesh(out _));
        Assert.IsFalse(coordinator.CompleteMesh(late, Build(late.Tile)));
        Assert.IsTrue(coordinator.TryReadActive(FirstCamera, out var active));
        using (active)
        {
            Assert.AreEqual(activeGeneration, active.Generation);
            Assert.IsTrue(active.TryGetSupport(FirstCamera, out _));
            Assert.IsFalse(active.TryGetSupport(SecondCamera, out _));
        }
    }

    [TestMethod]
    public void SupersededPresentationCannotPublishAndStopWaitsForCustody()
    {
        var coordinator = new PlanetTerrainCoordinator();
        var frame = PlanetLocalFrame.Create(FirstCamera);
        Assert.IsTrue(coordinator.TryStage(Select(FirstCamera), frame));
        CompleteAll(coordinator);
        Assert.IsTrue(coordinator.TryPreparePresentation(FirstCamera, out var stale));
        using (stale)
        {
            Assert.IsTrue(coordinator.TryStage(Select(SecondCamera), frame));
            Assert.IsFalse(coordinator.AcknowledgePresented(stale));
            Assert.IsTrue(coordinator.TryReserveMesh(out var reserved));
            coordinator.Stop();
            Assert.IsFalse(coordinator.IsSettled);
            Assert.IsFalse(coordinator.TryStage(Select(FirstCamera), frame));
            Assert.IsFalse(coordinator.TryReserveMesh(out _));
            Assert.IsFalse(coordinator.AcknowledgePresented(stale));
            Assert.IsFalse(coordinator.CompleteMesh(reserved, Build(reserved.Tile)));
            Assert.IsFalse(coordinator.IsSettled);
        }

        Assert.IsTrue(coordinator.IsSettled);
    }

    private static PlanetTerrainPlan Select(PlanetVector position)
    {
        Assert.IsTrue(PlanetTerrainSelection.TrySelect(position, Radius, null, out var plan));
        Assert.IsNotNull(plan);
        return plan;
    }

    private static PlanetPatchMesh Build(PlanetTerrainTile tile) =>
        PlanetPatchMesh.Build(tile.Patch, Radius, static _ => 0, tile.Transitions);

    private static void CompleteAll(PlanetTerrainCoordinator coordinator)
    {
        while (coordinator.TryReserveMesh(out var work))
            Assert.IsTrue(coordinator.CompleteMesh(work, Build(work.Tile)));
    }

    private static ulong Present(PlanetTerrainCoordinator coordinator, PlanetVector camera)
    {
        Assert.IsTrue(coordinator.TryPreparePresentation(camera, out var lease));
        using (lease)
        {
            var generation = lease.Generation;
            Assert.IsTrue(coordinator.AcknowledgePresented(lease));
            return generation;
        }
    }
}
