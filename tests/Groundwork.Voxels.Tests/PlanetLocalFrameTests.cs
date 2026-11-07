using System.Numerics;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetLocalFrameTests
{
    [TestMethod]
    public void OriginUsesEvenGridTiesAndRebasesOnlyBeyondTwelveMeters()
    {
        var frame = PlanetLocalFrame.Create(new PlanetVector(4, 12, -12));
        Assert.AreEqual(new PlanetVector(0, 16, -16), frame.Origin);
        Assert.AreEqual(1UL, frame.Revision);
        Assert.IsFalse(frame.TryRebase(new PlanetVector(12, 16, -16), out var unchanged));
        Assert.AreEqual(frame, unchanged);
        Assert.IsTrue(frame.TryRebase(new PlanetVector(Math.BitIncrement(12), 16, -16), out var rebased));
        Assert.AreEqual(new PlanetVector(16, 16, -16), rebased.Origin);
        Assert.AreEqual(2UL, rebased.Revision);
    }

    [TestMethod]
    public void InvalidOrUnrepresentableFrameCoordinatesFailExplicitly()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetLocalFrame.Create(new PlanetVector(double.NaN, 0, 0)));
        var frame = PlanetLocalFrame.Create(default);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.ToLocal(new PlanetVector(double.MaxValue, 0, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.ToPlanet(new Vector3(float.PositiveInfinity, 0, 0)));
        Assert.ThrowsExactly<InvalidOperationException>(() => default(PlanetLocalFrame).ToLocal(default));
    }
}
