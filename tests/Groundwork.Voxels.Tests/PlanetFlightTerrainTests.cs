namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetFlightTerrainTests
{
    [TestMethod]
    public void ViewHysteresisUsesPresentedPlanAndNearCoverageFollowsLandingDirection()
    {
        var near = Select(83, null);
        Assert.AreEqual(PlanetTerrainView.Near, near.View);
        var far = Select(84, near);
        Assert.AreEqual(PlanetTerrainView.Far, far.View);
        Assert.AreEqual(6, far.Tiles.Length);
        foreach (var tile in far.Tiles)
        {
            Assert.AreEqual(0, tile.Patch.Level);
            Assert.AreEqual(PlanetTransitionEdges.None, tile.Transitions);
            Assert.IsFalse(tile.SupportsCollision);
        }

        Assert.AreEqual(PlanetTerrainView.Far, Select(83, far).View);
        Assert.AreEqual(PlanetTerrainView.Far, Select(81, far).View);
        var landing = Select(80, far);
        Assert.AreEqual(PlanetTerrainView.Near, landing.View);
        var support = landing.Tiles.ToArray().Where(tile => tile.SupportsCollision).ToArray();
        Assert.IsNotEmpty(support);
        Assert.IsTrue(support.All(tile => tile.Patch.Face == PlanetFace.PositiveZ &&
            tile.Patch.Level == PlanetCoordinates.MaximumLevel && tile.Transitions == PlanetTransitionEdges.None));

        static PlanetTerrainPlan Select(double radius, PlanetTerrainPlan? previous)
        {
            Assert.IsTrue(PlanetTerrainSelection.TrySelectFlight(new(radius, 0, 0), new(0, 0, 1), 1, previous, out var plan));
            Assert.IsNotNull(plan);
            return plan;
        }
    }
}
