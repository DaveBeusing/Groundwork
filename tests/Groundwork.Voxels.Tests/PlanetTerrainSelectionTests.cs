namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetTerrainSelectionTests
{
    private const double Radius = 64;
    private const int FaceWidth = 1 << PlanetCoordinates.MaximumLevel;
    private static readonly PlanetEdge[] Edges = Enum.GetValues<PlanetEdge>();
    private static readonly PlanetFace[] Faces = Enum.GetValues<PlanetFace>();

    [TestMethod]
    public void SelectionCoversEachFaceExactlyAndStitchesOnlyTowardCoarserNeighbors()
    {
        var plan = Select(AtLongitude(45));
        var owners = new Dictionary<PlanetPatchKey, PlanetTerrainTile>();
        foreach (var tile in plan.Tiles)
        {
            var stride = 1 << (PlanetCoordinates.MaximumLevel - tile.Patch.Level);
            for (var u = tile.Patch.TileU * stride; u < (tile.Patch.TileU + 1) * stride; u++)
            for (var v = tile.Patch.TileV * stride; v < (tile.Patch.TileV + 1) * stride; v++)
                Assert.IsTrue(owners.TryAdd(Finest(tile.Patch.Face, u, v), tile), $"Overlapping leaf at {tile.Patch}.");
        }

        Assert.HasCount(Faces.Length * FaceWidth * FaceWidth, owners);
        foreach (var face in Faces)
        for (var u = 0; u < FaceWidth; u++)
        for (var v = 0; v < FaceWidth; v++)
        {
            var key = Finest(face, u, v);
            var owner = owners[key];
            foreach (var edge in Edges)
            {
                var neighbor = owners[PlanetCoordinates.Neighbor(key, edge).Patch];
                if (owner.Patch == neighbor.Patch)
                    continue;

                Assert.IsLessThanOrEqualTo(1, Math.Abs(owner.Patch.Level - neighbor.Patch.Level));
                Assert.AreEqual(neighbor.Patch.Level < owner.Patch.Level,
                    (owner.Transitions & Transition(edge)) != 0, $"Transition at {owner.Patch}/{edge}.");
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GuardBandIsFinestAndUnstitchedAcrossChartSeams(bool corner)
    {
        var center = corner ? Unit(new PlanetVector(1, 1, 1)) : Unit(new PlanetVector(1, 0, 1));
        var tangent = Unit(new PlanetVector(1, 0, -1));
        var bitangent = new PlanetVector(
            center.Y * tangent.Z - center.Z * tangent.Y,
            center.Z * tangent.X - center.X * tangent.Z,
            center.X * tangent.Y - center.Y * tangent.X);
        var plan = Select(Scale(center, Radius));
        var supportedFaces = new HashSet<PlanetFace>();
        var band = 6 + PlanetSurfaceWalker.BodyRadius + PlanetSurfaceWalker.WalkSpeed * PlanetSurfaceWalker.StepSeconds;
        for (var ring = 0; ring <= 4; ring++)
        for (var index = 0; index < 16; index++)
        {
            var angle = index * Math.Tau / 16;
            var arc = band * ring / (4 * Radius);
            var lateral = Add(Scale(tangent, Math.Cos(angle)), Scale(bitangent, Math.Sin(angle)));
            var direction = Add(Scale(center, Math.Cos(arc)), Scale(lateral, Math.Sin(arc)));
            var (face, u, v) = PlanetCoordinates.Project(direction);
            var expected = Finest(face, Tile(u), Tile(v));
            var found = false;
            foreach (var tile in plan.Tiles)
            {
                if (tile.Patch != expected)
                    continue;

                Assert.IsTrue(tile.SupportsCollision, $"Missing collision at {expected}.");
                Assert.AreEqual(PlanetTransitionEdges.None, tile.Transitions);
                supportedFaces.Add(face);
                found = true;
                break;
            }
            Assert.IsTrue(found, $"Finest patch absent at {expected}.");
        }

        Assert.HasCount(corner ? 3 : 2, supportedFaces);
    }

    [TestMethod]
    public void HysteresisPreservesARefinedPlanWhenAFreshPlanWouldCoarsen()
    {
        var prior = Select(AtLongitude(35));
        var preserved = false;
        for (var degree = 36; degree <= 145; degree++)
        {
            var position = AtLongitude(degree);
            var retained = Select(position, prior);
            var fresh = Select(position);
            Assert.IsTrue(retained.Tiles.SequenceEqual(Select(position, retained).Tiles),
                $"Plan changes with stationary input at {degree} degrees.");
            if (retained.Tiles.Length > fresh.Tiles.Length)
                preserved = true;
            prior = retained;
        }

        Assert.IsTrue(preserved, "The route should cross a threshold where prior refinement is retained.");
    }

    private static PlanetTerrainPlan Select(PlanetVector position, PlanetTerrainPlan? prior = null)
    {
        Assert.IsTrue(PlanetTerrainSelection.TrySelect(position, Radius, prior, out var plan), $"Selection failed at {position}.");
        Assert.IsNotNull(plan);
        return plan;
    }

    private static PlanetPatchKey Finest(PlanetFace face, int u, int v) => new(face, PlanetCoordinates.MaximumLevel, u, v);

    private static int Tile(double coordinate) => Math.Min(FaceWidth - 1, (int)((coordinate + 1) * FaceWidth / 2));

    private static PlanetTransitionEdges Transition(PlanetEdge edge) => edge switch
    {
        PlanetEdge.Bottom => PlanetTransitionEdges.Bottom,
        PlanetEdge.Right => PlanetTransitionEdges.Right,
        PlanetEdge.Top => PlanetTransitionEdges.Top,
        PlanetEdge.Left => PlanetTransitionEdges.Left,
        _ => throw new ArgumentOutOfRangeException(nameof(edge))
    };

    private static PlanetVector AtLongitude(double degrees)
    {
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        return new PlanetVector(Radius * cos, 0, Radius * sin);
    }

    private static PlanetVector Add(PlanetVector a, PlanetVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static PlanetVector Scale(PlanetVector v, double scale) => new(v.X * scale, v.Y * scale, v.Z * scale);

    private static PlanetVector Unit(PlanetVector v) => Scale(v, 1 / Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z));
}
