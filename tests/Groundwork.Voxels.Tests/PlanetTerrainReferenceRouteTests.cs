namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetTerrainReferenceRouteTests
{
    private const double Radius = 64;

    [TestMethod]
    public void ReferenceRouteRefinesAndCoarsensWithoutLosingGuardedSeamSupport()
    {
        PlanetTerrainPlan? previous = null;
        var refined = false;
        var coarsened = false;
        for (var step = 0; step <= 88; step++)
        {
            var degrees = 35 + Math.Min(step, 88 - step) * 2.5;
            var angle = degrees * Math.PI / 180;
            var direction = new PlanetVector(Math.Cos(angle), 0, Math.Sin(angle));
            var position = Scale(direction, Radius + Height(direction));
            Assert.IsTrue(PlanetTerrainSelection.TrySelect(position, Radius, previous, out var plan), $"LOD at {degrees}°.");
            Assert.IsNotNull(plan);
            Assert.IsLessThanOrEqualTo(192, plan.Tiles.Length);

            if (previous is not null)
            {
                var before = previous.Tiles.ToArray().Select(tile => tile.Patch).ToHashSet();
                var after = plan.Tiles.ToArray().Select(tile => tile.Patch).ToHashSet();
                refined |= after.Any(key => HasAncestor(before, key));
                coarsened |= before.Any(key => HasAncestor(after, key));
            }

            if (degrees is 45 or 135)
                VerifyGuard(plan, angle);
            previous = plan;
        }

        Assert.IsTrue(refined && coarsened, "The reference route must exercise both LOD changes.");
    }

    private static void VerifyGuard(PlanetTerrainPlan plan, double angle)
    {
        var meshes = new List<PlanetPatchMesh>();
        foreach (var tile in plan.Tiles)
        {
            if (!tile.SupportsCollision)
                continue;
            Assert.AreEqual(PlanetCoordinates.MaximumLevel, tile.Patch.Level);
            Assert.AreEqual(PlanetTransitionEdges.None, tile.Transitions);
            meshes.Add(PlanetPatchMesh.Build(tile.Patch, Radius, static sample => Height(PlanetCoordinates.Direction(sample))));
        }

        var surface = new PlanetSurface(meshes.ToArray());
        var direction = new PlanetVector(Math.Cos(angle), 0, Math.Sin(angle));
        var tangent = new PlanetVector(-Math.Sin(angle), 0, Math.Cos(angle));
        // Cover the six-meter band plus the walker's radius and one movement tick.
        var distance = 6 + PlanetSurfaceWalker.BodyRadius + PlanetSurfaceWalker.WalkSpeed * PlanetSurfaceWalker.StepSeconds;
        for (var i = 0; i < 16; i++)
        {
            var bearing = i * Math.Tau / 16;
            var offset = Math.Sin(distance / Radius);
            var center = Scale(direction, Math.Cos(distance / Radius));
            var probe = new PlanetVector(center.X + offset * Math.Cos(bearing) * tangent.X,
                offset * Math.Sin(bearing), center.Z + offset * Math.Cos(bearing) * tangent.Z);
            Assert.IsTrue(surface.TryGetSupport(probe, out _), $"Missing seam guard at {angle}, bearing {bearing}.");
        }
    }

    private static bool HasAncestor(HashSet<PlanetPatchKey> leaves, PlanetPatchKey key)
    {
        while (key.Level > 0)
        {
            key = new PlanetPatchKey(key.Face, key.Level - 1, key.TileU / 2, key.TileV / 2);
            if (leaves.Contains(key))
                return true;
        }
        return false;
    }

    private static double Height(PlanetVector d) => Math.Round(1200 * d.X * d.Z + 600 * d.Y) / 1000;

    private static PlanetVector Scale(PlanetVector d, double scale) => new(d.X * scale, d.Y * scale, d.Z * scale);
}
