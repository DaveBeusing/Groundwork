namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetSupportTests
{
    [TestMethod]
    public void EdgeAndCornerCanUseAnIncidentPatchWhenDominantCoverageIsMissing()
    {
        var edgePatch = Build(new PlanetPatchKey(PlanetFace.PositiveZ, 5, 31, 16));
        var edge = new PlanetSurface([edgePatch]);
        Assert.IsTrue(edge.TryGetSupport(new PlanetVector(1, 1.0 / 32, 1), out var contact));
        Assert.AreEqual(64, Length(contact.Position), 1e-10);
        Assert.IsFalse(edge.TryGetSupport(new PlanetVector(1.00001, 1.0 / 32, 1), out _));

        var cornerPatch = Build(new PlanetPatchKey(PlanetFace.PositiveZ, 5, 31, 31));
        var corner = new PlanetSurface([cornerPatch]);
        Assert.IsTrue(corner.TryGetSupport(new PlanetVector(1, 1, 1), out contact));
        Assert.AreEqual(64, Length(contact.Position), 1e-10);
        Assert.AreEqual(1, Length(contact.Normal), 1e-12);
    }

    [TestMethod]
    public void SupportUsesTheTrianglePlaneBetweenSamples()
    {
        var patch = Build(new PlanetPatchKey(PlanetFace.PositiveX, 5, 16, 16));
        var surface = new PlanetSurface([patch]);
        var a = patch.Positions[0];
        var b = patch.Positions[1];
        var c = patch.Positions[18];
        var interior = new PlanetVector((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
        Assert.IsTrue(surface.TryGetSupport(interior, out var support));
        Assert.AreEqual(interior.X, support.Position.X, 1e-12);
        Assert.AreEqual(interior.Y, support.Position.Y, 1e-12);
        Assert.AreEqual(interior.Z, support.Position.Z, 1e-12);
        Assert.IsLessThan(64.0, Length(support.Position));
    }

    [TestMethod]
    public void CoarseStitchedAndDuplicateMeshesCannotBecomeSupport()
    {
        var fineKey = new PlanetPatchKey(PlanetFace.PositiveX, 5, 0, 0);
        var fine = Build(fineKey);
        var coarse = Build(fineKey with { Level = 4 });
        var stitched = PlanetPatchMesh.Build(fineKey, 64, static _ => 0, PlanetTransitionEdges.Left);
        Assert.ThrowsExactly<ArgumentException>(() => new PlanetSurface([coarse]));
        Assert.ThrowsExactly<ArgumentException>(() => new PlanetSurface([stitched]));
        Assert.ThrowsExactly<ArgumentException>(() => new PlanetSurface([fine, fine]));
    }

    private static PlanetPatchMesh Build(PlanetPatchKey patch) => PlanetPatchMesh.Build(patch, 64, static _ => 0);

    private static double Length(PlanetVector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
}
