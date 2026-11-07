namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetFlightTests
{
    private static readonly PlanetFlightOrientation Identity = PlanetFlightOrientation.Identity;
    private static readonly PlanetFlightOrientation Turn = new(0, 1, 0, 0);
    private static readonly PlanetFlightInput Coast = new(default, Identity);

    [TestMethod]
    public void GravityPointsInwardAndFallsWithRadiusSquared()
    {
        var gravity = PlanetFlight.Gravity(new(0, -64, 0));
        Assert.AreEqual(new PlanetVector(0, 9.81, 0), gravity);
        var diagonal = PlanetFlight.Gravity(new(76.8, 0, 102.4));
        Assert.AreEqual(-9.81 / 4 * 0.6, diagonal.X, 1e-12);
        Assert.AreEqual(-9.81 / 4 * 0.8, diagonal.Z, 1e-12);
    }

    [TestMethod]
    public void ThrustCapsVectorMagnitudeBeforeVelocityFirstIntegration()
    {
        var initial = new PlanetFlightState(new(96, 0, 0), new(0, 2, 0), Identity);
        var result = PlanetFlight.Step(new NoQueries(), initial, new(new(0, 3e300, 4e300), Turn));
        var dt = PlanetSurfaceWalker.StepSeconds;
        Assert.AreEqual(PlanetFlightStatus.Flying, result.Status);
        Assert.AreEqual(Turn, result.State.Orientation);
        Assert.AreEqual(-PlanetFlight.GravitationalParameter / (96 * 96) * dt, result.State.Velocity.X, 1e-12);
        Assert.AreEqual(2 + 18 * dt, result.State.Velocity.Y, 1e-12);
        Assert.AreEqual(24 * dt, result.State.Velocity.Z, 1e-12);
        Assert.AreEqual(96 + result.State.Velocity.X * dt, result.State.Position.X, 1e-12);
        Assert.AreEqual(result.State.Velocity.Y * dt, result.State.Position.Y, 1e-12);
        Assert.AreEqual(result.State.Velocity.Z * dt, result.State.Position.Z, 1e-12);
    }

    [TestMethod]
    public void CircularCoastCompletesAFullProgradeOrbitWithBoundedDrift()
    {
        const double radius = 96;
        var mu = PlanetFlight.GravitationalParameter;
        var speed = Math.Sqrt(mu / radius);
        var period = Math.Tau * radius / speed;
        var state = new PlanetFlightState(new(radius, 0, 0), new(0, 0, speed), Identity);
        var surface = new NoQueries();
        var oldAngle = 0d;
        var sweep = 0d;
        var ticks = 0;
        var minimum = radius;
        var maximum = radius;
        while (sweep < Math.Tau && ticks < 1800)
        {
            var result = PlanetFlight.Step(surface, state, Coast);
            Assert.AreEqual(PlanetFlightStatus.Flying, result.Status);
            state = result.State;
            var angle = Math.Atan2(state.Position.Z, state.Position.X);
            var increment = Math.IEEERemainder(angle - oldAngle, Math.Tau);
            Assert.IsGreaterThan(0d, increment);
            sweep += increment;
            oldAngle = angle;
            var distance = Norm(state.Position);
            minimum = Math.Min(minimum, distance);
            maximum = Math.Max(maximum, distance);
            Assert.AreEqual(0d, state.Position.Y);
            ticks++;
        }

        Assert.IsGreaterThanOrEqualTo(Math.Tau, sweep);
        Assert.AreEqual(period, ticks * PlanetSurfaceWalker.StepSeconds, 0.04);
        Assert.IsGreaterThanOrEqualTo(95.5, minimum);
        Assert.IsLessThanOrEqualTo(96.5, maximum);
        var energy = 0.5 * Norm(state.Velocity) * Norm(state.Velocity) - mu / Norm(state.Position);
        var angularMomentum = state.Position.X * state.Velocity.Z - state.Position.Z * state.Velocity.X;
        Assert.AreEqual(-mu / (2 * radius), energy, 0.01);
        Assert.AreEqual(radius * speed, angularMomentum, 1e-8);
        Console.WriteLine(FormattableString.Invariant(
            $"Orbit {ticks} ticks, radius [{minimum:F6}, {maximum:F6}] m, energy {-mu / (2 * radius):F9} -> {energy:F9}, angular momentum {radius * speed:F9} -> {angularMomentum:F9}."));
    }

    [TestMethod]
    public void MissingFootprintCoveragePreservesWholeFlightStateUntilRetry()
    {
        var state = new PlanetFlightState(new(64.008, 0, 0), new(-0.5, 0, 0), Identity);
        var result = PlanetFlight.Step(new Sphere(coverage: static direction => direction == new PlanetVector(1, 0, 0)),
            state, new(default, Turn));
        Assert.AreEqual(new PlanetFlightResult(state, PlanetFlightStatus.CoverageUnavailable), result);

        var landed = PlanetFlight.Step(new Sphere(), result.State, new(default, Turn));
        Assert.AreEqual(PlanetFlightStatus.Touchdown, landed.Status);
        Assert.AreEqual(64d, landed.State.Position.X, 1e-12);
        Assert.AreEqual(0d, landed.State.Position.Y);
        Assert.AreEqual(0d, landed.State.Position.Z);
        Assert.AreEqual(default, landed.State.Velocity);
        Assert.AreEqual(Turn, landed.State.Orientation);
        Assert.IsTrue(landed.State.Grounded);
    }

    [TestMethod]
    public void TouchdownUsesConfirmedTriangulatedSupportAcrossACubeEdge()
    {
        var component = 1 / Math.Sqrt(2);
        var up = new PlanetVector(component, 0, component);
        var state = new PlanetFlightState(new(up.X * 64.006, 0, up.Z * 64.006),
            new(-up.X * 0.5, 0, -up.Z * 0.5), Identity);
        Assert.IsTrue(PlanetTerrainSelection.TrySelect(state.Position, 64, null, out var plan));
        Assert.IsNotNull(plan);
        var patches = plan.Tiles.ToArray().Where(tile => tile.SupportsCollision)
            .Select(tile => PlanetPatchMesh.Build(tile.Patch, 64, static _ => 0)).ToArray();
        var surface = new PlanetSurface(patches);
        var result = PlanetFlight.Step(surface, state, Coast);
        Assert.AreEqual(PlanetFlightStatus.Touchdown, result.Status);
        Assert.AreEqual(64d, Norm(result.State.Position), 1e-9);

        var partial = new PlanetSurface(patches.Where(patch => patch.Patch.Face == PlanetCoordinates.Project(up).Face).ToArray());
        Assert.AreEqual(new PlanetFlightResult(state, PlanetFlightStatus.CoverageUnavailable),
            PlanetFlight.Step(partial, state, Coast));
    }

    [TestMethod]
    [DataRow(-0.9, 0d, false, DisplayName = "Gravity raises descent speed past the limit")]
    [DataRow(-0.5, 0.6, false, DisplayName = "Tangential speed exceeds touchdown limit")]
    [DataRow(-0.5, 0d, true, DisplayName = "Steep contact normal")]
    public void UnsafeTouchdownPreservesLastSafeState(double descent, double tangent, bool steep)
    {
        var state = new PlanetFlightState(new(64.008, 0, 0), new(descent, tangent, 0), Identity);
        var surface = new Sphere(steep: steep);
        Assert.AreEqual(new PlanetFlightResult(state, PlanetFlightStatus.LandingBlocked),
            PlanetFlight.Step(surface, state, new(default, Turn)));
    }

    [TestMethod]
    public void GroundedCraftRestsAndCanLiftOffUnderThrust()
    {
        var surface = new Sphere();
        var grounded = new PlanetFlightState(new(64, 0, 0), default, Identity, Grounded: true);
        Assert.AreEqual(new PlanetFlightResult(grounded, PlanetFlightStatus.Grounded),
            PlanetFlight.Step(surface, grounded, Coast));
        var lifted = PlanetFlight.Step(surface, grounded, new(new(20, 0, 0), Turn));
        Assert.AreEqual(PlanetFlightStatus.Flying, lifted.Status);
        Assert.IsFalse(lifted.State.Grounded);
        Assert.IsGreaterThan(64d, lifted.State.Position.X);
        Assert.AreEqual((20 - 9.81) * PlanetSurfaceWalker.StepSeconds, lifted.State.Velocity.X, 1e-12);
    }

    [TestMethod]
    public void ExcessiveSweepCannotCrossApproachSphereWithBothEndpointsOutside()
    {
        var state = new PlanetFlightState(new(96, 0, 0), new(-12000, 0, 0), Identity);
        Assert.AreEqual(new PlanetFlightResult(state, PlanetFlightStatus.SweepLimitExceeded),
            PlanetFlight.Step(new NoQueries(), state, Coast));
    }

    [TestMethod]
    public void InvalidFlightStateAndInputAreRejectedBeforeTerrainQueries()
    {
        var surface = new NoQueries();
        var state = new PlanetFlightState(new(64, 0, 0), default, Identity);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetFlight.Step(surface, state with { Position = default }, Coast));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetFlight.Step(surface, state with { Velocity = new(double.NaN, 0, 0) }, Coast));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetFlight.Step(surface, state, Coast with { Orientation = new(0, 0, 0, 2) }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetFlight.Step(surface, state, Coast with { Thrust = new(double.PositiveInfinity, 0, 0) }));
    }

    private static double Norm(PlanetVector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private sealed class NoQueries : IPlanetSurface
    {
        public bool TryGetSupport(PlanetVector direction, out PlanetSupport support) =>
            throw new InvalidOperationException("This fixture must not query terrain.");
    }

    private sealed class Sphere(Func<PlanetVector, bool>? coverage = null, bool steep = false) : IPlanetSurface
    {
        public bool TryGetSupport(PlanetVector direction, out PlanetSupport support)
        {
            var radius = Norm(direction);
            var up = new PlanetVector(direction.X / radius, direction.Y / radius, direction.Z / radius);
            support = new(new(64 * up.X, 64 * up.Y, 64 * up.Z), steep ? new(0, 1, 0) : up);
            return coverage?.Invoke(up) ?? true;
        }
    }
}
