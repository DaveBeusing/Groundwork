using System.Numerics;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetSurfaceMovementTests
{
    private const double Radius = 64;

    [TestMethod]
    [DataRow(44.9, 1)]
    [DataRow(45.1, -1)]
    public void WalkingAcrossACubeSeamKeepsGroundContactAndTangentHeading(double startAngle, int travelDirection)
    {
        var surface = SeamSurface();
        var angle = startAngle * Math.PI / 180;
        var heading = new PlanetVector(-Math.Sin(angle) * travelDirection, 0, Math.Cos(angle) * travelDirection);
        Assert.IsTrue(surface.TryGetSupport(new PlanetVector(Math.Cos(angle), 0, Math.Sin(angle)), out var support));
        var state = new PlanetWalkState(support.Position, heading, default);
        var startFace = PlanetCoordinates.Project(state.Position).Face;

        for (var tick = 0; tick < 4; tick++)
        {
            var result = PlanetSurfaceWalker.Step(surface, state, 1, 0);
            Assert.AreEqual(PlanetWalkStatus.Moved, result.Status);
            Assert.IsTrue(result.Grounded);
            state = result.State;

            Assert.IsTrue(surface.TryGetSupport(state.Position, out var contact));
            Assert.AreEqual(0, Length(Difference(state.Position, contact.Position)), 0.001);
            var radial = Normalize(state.Position);
            var expectedHeading = new PlanetVector(-radial.Z * travelDirection, 0, radial.X * travelDirection);
            Assert.AreEqual(0, Dot(radial, state.Heading), 1e-10);
            Assert.AreEqual(1, Dot(expectedHeading, state.Heading), 1e-10);
        }

        Assert.AreEqual(travelDirection > 0 ? PlanetFace.PositiveX : PlanetFace.PositiveZ, startFace);
        Assert.AreEqual(travelDirection > 0 ? PlanetFace.PositiveZ : PlanetFace.PositiveX,
            PlanetCoordinates.Project(state.Position).Face);
    }

    [TestMethod]
    public void MissingNeighborAcrossACubeSeamBlocksAValidCenterContact()
    {
        var surface = new PlanetSurface(
        [
            Build(PlanetFace.PositiveX, 0, 15),
            Build(PlanetFace.PositiveX, 0, 16)
        ]);
        var angle = 44.9 * Math.PI / 180;
        Assert.IsTrue(surface.TryGetSupport(new PlanetVector(Math.Cos(angle), 0, Math.Sin(angle)), out var support));
        var heading = new PlanetVector(-Math.Sin(angle), 0, Math.Cos(angle));
        var state = new PlanetWalkState(support.Position, heading, default);

        var blocked = PlanetSurfaceWalker.Step(surface, state, 1, 0);
        Assert.AreEqual(PlanetWalkStatus.CoverageUnavailable, blocked.Status);
        Assert.AreEqual(state, blocked.State);
    }

    [TestMethod]
    public void GravityIsRadialAndAnAirborneTickFallsTowardTheSurface()
    {
        var gravity = PlanetSurfaceWalker.Gravity(new PlanetVector(3, 4, 0));
        Assert.AreEqual(-5.886, gravity.X, 1e-12);
        Assert.AreEqual(-7.848, gravity.Y, 1e-12);
        Assert.AreEqual(0, gravity.Z, 1e-12);

        var state = new PlanetWalkState(new PlanetVector(Radius + 1, 0, 0), new PlanetVector(0, 0, 1), default);
        var result = PlanetSurfaceWalker.Step(new SphereSurface(), state, 0, 0);
        Assert.IsFalse(result.Grounded);
        Assert.IsLessThan(state.Position.X, result.State.Position.X);
        Assert.AreEqual(-9.81 / 60, result.State.Velocity.X, 1e-12);
        Assert.AreEqual(0, result.State.Velocity.Y, 1e-12);
        Assert.AreEqual(0, result.State.Velocity.Z, 1e-12);
    }

    [TestMethod]
    public void MissingFootprintCoverageHoldsTheEntireStateWithoutAccumulatingGravity()
    {
        var state = new PlanetWalkState(new PlanetVector(Radius, 0, 0), new PlanetVector(0, 0, 1), default);
        var missingFootprint = new SphereSurface(maximumY: 0);
        Assert.IsTrue(missingFootprint.TryGetSupport(state.Position, out _));

        var held = state;
        for (var tick = 0; tick < 20; tick++)
        {
            var result = PlanetSurfaceWalker.Step(missingFootprint, held, 1, 0);
            Assert.AreEqual(PlanetWalkStatus.CoverageUnavailable, result.Status);
            Assert.AreEqual(state, result.State);
            held = result.State;
        }

        var available = new SphereSurface();
        var resumed = PlanetSurfaceWalker.Step(available, held, 1, 0);
        Assert.AreEqual(PlanetSurfaceWalker.Step(available, state, 1, 0), resumed);
        Assert.AreEqual(PlanetWalkStatus.Moved, resumed.Status);
        Assert.IsTrue(resumed.Grounded);
    }

    [TestMethod]
    public void LeavingADownwardLedgeBeginsFallingWithoutSnappingToTheLowerSurface()
    {
        var surface = new SteppedSurface(Radius - 2);
        var state = StartBeforeStep(surface, -0.02);

        for (var tick = 0; tick < 12; tick++)
        {
            var result = PlanetSurfaceWalker.Step(surface, state, 1, 0);
            Assert.AreEqual(PlanetWalkStatus.Moved, result.Status);
            state = result.State;
            if (result.Grounded)
                continue;

            // The foot can descend by one cap radius while its outer edge still
            // contacts the ledge. The first airborne tick only adds a gravity step.
            Assert.IsGreaterThan(Radius - PlanetSurfaceWalker.BodyRadius - 0.01, Length(state.Position));
            Assert.IsLessThan(0, Dot(Normalize(state.Position), state.Velocity));
            return;
        }

        Assert.Fail("The walker stayed grounded after leaving the upper ledge.");
    }

    [TestMethod]
    public void ASteepRiseInTheSweptFootprintHoldsTheEntireState()
    {
        var surface = new SteppedSurface(Radius + 2);
        var state = StartBeforeStep(surface, -0.27);

        var blocked = PlanetSurfaceWalker.Step(surface, state, 1, 0);
        Assert.AreEqual(PlanetWalkStatus.SteepTerrain, blocked.Status);
        Assert.AreEqual(state, blocked.State);
    }

    [TestMethod]
    public void RebaseUsesAnEightMeterGridAndKeepsMillimeterAccuracyAtPlanetaryScale()
    {
        var initialPosition = new PlanetVector(6_400_004, 12, -4);
        var original = PlanetLocalFrame.Create(initialPosition);
        Assert.AreEqual(new PlanetVector(6_400_000, 16, 0), original.Origin);
        Assert.IsFalse(original.TryRebase(new PlanetVector(6_400_012, 16, 0), out var same));
        Assert.AreEqual(original, same);

        var position = new PlanetVector(6_400_016.00125, 16.00075, -0.00025);
        Assert.IsTrue(original.TryRebase(position, out var rebased));
        Assert.AreEqual(new PlanetVector(6_400_016, 16, 0), rebased.Origin);
        Assert.AreEqual(original.Revision + 1, rebased.Revision);

        var before = original.ToPlanet(original.ToLocal(position));
        var after = rebased.ToPlanet(rebased.ToLocal(position));
        Assert.AreEqual(0, Length(Difference(position, before)), 0.001);
        Assert.AreEqual(0, Length(Difference(position, after)), 0.001);
        Assert.AreEqual(0, Length(Difference(before, after)), 0.001);

        var heading = new PlanetVector(0, 0, 1);
        var velocity = new PlanetVector(0, 0, 4);
        Assert.AreEqual(new Vector3(0, 0, 1), LocalOffset(original, position, heading));
        Assert.AreEqual(new Vector3(0, 0, 1), LocalOffset(rebased, position, heading));
        Assert.AreEqual(new Vector3(0, 0, 4), LocalOffset(original, position, velocity));
        Assert.AreEqual(new Vector3(0, 0, 4), LocalOffset(rebased, position, velocity));
    }

    private static PlanetSurface SeamSurface() => new(
    [
        Build(PlanetFace.PositiveX, 0, 15),
        Build(PlanetFace.PositiveX, 0, 16),
        Build(PlanetFace.PositiveZ, 31, 15),
        Build(PlanetFace.PositiveZ, 31, 16)
    ]);

    private static PlanetPatchMesh Build(PlanetFace face, int u, int v) =>
        PlanetPatchMesh.Build(new PlanetPatchKey(face, PlanetCoordinates.MaximumLevel, u, v), Radius, static _ => 0);

    private static PlanetWalkState StartBeforeStep(IPlanetSurface surface, double z)
    {
        Assert.IsTrue(surface.TryGetSupport(new PlanetVector(Radius, 0, z), out var support));
        var heading = Normalize(new PlanetVector(-support.Position.Z, 0, support.Position.X));
        return new PlanetWalkState(support.Position, heading, default);
    }

    private static Vector3 LocalOffset(PlanetLocalFrame frame, PlanetVector position, PlanetVector offset) =>
        frame.ToLocal(new PlanetVector(position.X + offset.X, position.Y + offset.Y, position.Z + offset.Z)) -
        frame.ToLocal(position);

    private static PlanetVector Normalize(PlanetVector vector)
    {
        var length = Length(vector);
        return new PlanetVector(vector.X / length, vector.Y / length, vector.Z / length);
    }

    private static double Length(PlanetVector vector) => Math.Sqrt(Dot(vector, vector));

    private static double Dot(PlanetVector a, PlanetVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static PlanetVector Difference(PlanetVector a, PlanetVector b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private sealed class SphereSurface(double maximumY = double.PositiveInfinity) : IPlanetSurface
    {
        public bool TryGetSupport(PlanetVector direction, out PlanetSupport support)
        {
            var unit = Normalize(direction);
            support = default;
            if (unit.Y > maximumY)
                return false;
            support = new PlanetSupport(new PlanetVector(Radius * unit.X, Radius * unit.Y, Radius * unit.Z), unit);
            return true;
        }
    }

    private sealed class SteppedSurface(double positiveZRadius) : IPlanetSurface
    {
        public bool TryGetSupport(PlanetVector direction, out PlanetSupport support)
        {
            var unit = Normalize(direction);
            var radius = unit.Z >= 0 ? positiveZRadius : Radius;
            support = new PlanetSupport(new PlanetVector(radius * unit.X, radius * unit.Y, radius * unit.Z), unit);
            return true;
        }
    }
}
