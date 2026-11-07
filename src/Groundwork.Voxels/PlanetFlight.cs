namespace Groundwork.Voxels;

/// <summary>A unit quaternion in planet axes, with double-precision components.</summary>
public readonly record struct PlanetFlightOrientation(double X, double Y, double Z, double W)
{
    public static PlanetFlightOrientation Identity => new(0, 0, 0, 1);

    internal bool IsUnit => Math.Abs(X * X + Y * Y + Z * Z + W * W - 1) <= 1e-9;
}

/// <summary>The craft's landing-foot position, velocity and orientation in planet axes.</summary>
public readonly record struct PlanetFlightState(
    PlanetVector Position, PlanetVector Velocity, PlanetFlightOrientation Orientation, bool Grounded = false);

/// <summary>Requested acceleration in planet axes and orientation for the next admitted tick.</summary>
public readonly record struct PlanetFlightInput(PlanetVector Thrust, PlanetFlightOrientation Orientation);

public enum PlanetFlightStatus
{
    Flying,
    Grounded,
    Touchdown,
    CoverageUnavailable,
    LandingBlocked,
    SweepLimitExceeded
}

/// <summary>A result of one proposed fixed step. Holds and blocked steps preserve the input state.</summary>
public readonly record struct PlanetFlightResult(PlanetFlightState State, PlanetFlightStatus Status);

/// <summary>Portable flight and sampled landing admission for the fixed WP09 reference planet.</summary>
public static class PlanetFlight
{
    public const double ReferenceRadius = 64;
    public const double GravitationalParameter = PlanetSurfaceWalker.GravityStrength * ReferenceRadius * ReferenceRadius;
    public const double MaximumThrust = 30;
    public const double MaximumLandingSweep = 0.5;
    private const double ApproachRadius = ReferenceRadius + 12;
    private const double MaximumSampleSpacing = PlanetSurfaceWalker.BodyRadius / 4;
    private const double MinimumNormalDot = 0.8660254037844386;
    private const double ContactTolerance = 0.001;
    private static readonly (double X, double Y)[] Footprint =
    [
        (1, 0), (0.7071067811865476, 0.7071067811865476), (0, 1), (-0.7071067811865476, 0.7071067811865476),
        (-1, 0), (-0.7071067811865476, -0.7071067811865476), (0, -1), (0.7071067811865476, -0.7071067811865476)
    ];

    /// <summary>Returns radial inverse-square acceleration in meters per second squared.</summary>
    public static PlanetVector Gravity(PlanetVector position)
    {
        var radius = RequireRadius(position, nameof(position));
        return GravityAt(position, radius);
    }

    /// <summary>Proposes a 60 Hz step against the currently confirmed landing coverage.</summary>
    /// <remarks>The surface must describe radial terrain contained below the 76 m approach radius.
    /// Outside that radius the craft coasts without surface queries. Near ground, this is a bounded
    /// sampled sweep of the fixed landing footprint. The caller advances its mission tick only
    /// for Flying, Grounded or Touchdown.</remarks>
    public static PlanetFlightResult Step(IPlanetSurface surface, PlanetFlightState state, PlanetFlightInput input)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var radius = RequireRadius(state.Position, nameof(state));
        if (!IsFinite(state.Velocity) || !state.Orientation.IsUnit || (state.Grounded && state.Velocity != default))
            throw new ArgumentOutOfRangeException(nameof(state), "Invalid flight velocity or orientation.");
        if (!IsFinite(input.Thrust) || !input.Orientation.IsUnit)
            throw new ArgumentOutOfRangeException(nameof(input), "Invalid thrust or orientation.");

        var acceleration = Add(GravityAt(state.Position, radius), LimitThrust(input.Thrust));
        if (state.Grounded)
        {
            if (radius >= ApproachRadius)
                return new(state, PlanetFlightStatus.LandingBlocked);
            var up = Scale(state.Position, 1 / radius);
            if (!TryFootprint(surface, up, radius, out var support, out var walkable))
                return new(state, PlanetFlightStatus.CoverageUnavailable);
            if (!walkable || Math.Abs(radius - support) > ContactTolerance)
                return new(state, PlanetFlightStatus.LandingBlocked);
            if (Dot(acceleration, up) <= 0)
                return new(state with { Orientation = input.Orientation }, PlanetFlightStatus.Grounded);
        }

        var velocity = Add(state.Velocity, Scale(acceleration, PlanetSurfaceWalker.StepSeconds));
        var delta = Scale(velocity, PlanetSurfaceWalker.StepSeconds);
        var position = Add(state.Position, delta);
        var length = Length(delta);
        if (!double.IsFinite(length) || !IsFinite(position))
            throw new ArgumentOutOfRangeException(nameof(state), "The proposed flight step is outside the numeric range.");

        // Test the full segment against the approach sphere. A large step can cross it
        // even when both endpoints are outside.
        var along = length == 0 ? default : Divide(delta, length);
        var closest = Math.Clamp(-Dot(state.Position, along), 0, length);
        if (Length(Add(state.Position, Scale(along, closest))) > ApproachRadius)
            return new(new(position, velocity, input.Orientation), PlanetFlightStatus.Flying);
        if (length > MaximumLandingSweep)
            return new(state, PlanetFlightStatus.SweepLimitExceeded);

        var intervals = Math.Max(1, (int)Math.Ceiling(length / MaximumSampleSpacing));
        for (var i = state.Grounded ? 1 : 0; i <= intervals; i++)
        {
            var sample = Add(state.Position, Scale(delta, (double)i / intervals));
            var sampleRadius = Length(sample);
            if (sampleRadius > ApproachRadius)
                continue;
            if (sampleRadius <= PlanetSurfaceWalker.BodyRadius)
                return new(state, PlanetFlightStatus.LandingBlocked);
            var up = Scale(sample, 1 / sampleRadius);
            if (!TryFootprint(surface, up, sampleRadius, out var support, out var walkable))
                return new(state, PlanetFlightStatus.CoverageUnavailable);
            if (sampleRadius > support)
                continue;

            var radialSpeed = Dot(velocity, up);
            var tangentSpeed = Length(Add(velocity, Scale(up, -radialSpeed)));
            if (!walkable || radialSpeed is < -1 or > 0 || tangentSpeed > 0.5 ||
                (i == 0 && support - sampleRadius > ContactTolerance))
                return new(state, PlanetFlightStatus.LandingBlocked);

            return new(new(Scale(up, support), default, input.Orientation, Grounded: true), PlanetFlightStatus.Touchdown);
        }

        return new(new(position, velocity, input.Orientation), PlanetFlightStatus.Flying);
    }

    private static bool TryFootprint(IPlanetSurface surface, PlanetVector up, double radius, out double support, out bool walkable)
    {
        support = 0;
        walkable = true;
        if (!surface.TryGetSupport(up, out var center))
            return false;
        support = Dot(center.Position, up);
        walkable = Dot(center.Normal, up) >= MinimumNormalDot;

        var axis = Math.Abs(up.Y) < 0.9 ? new PlanetVector(0, 1, 0) : new PlanetVector(1, 0, 0);
        var tangent = Cross(axis, up);
        tangent = Scale(tangent, 1 / Length(tangent));
        var sideways = Cross(up, tangent);
        foreach (var (x, y) in Footprint)
        {
            var offset = Add(Scale(tangent, x), Scale(sideways, y));
            var direction = Add(up, Scale(offset, PlanetSurfaceWalker.BodyRadius / radius));
            direction = Scale(direction, 1 / Length(direction));
            if (!surface.TryGetSupport(direction, out var hit))
                return false;
            support = Math.Max(support, Dot(hit.Position, up));
            walkable &= Dot(hit.Normal, direction) >= MinimumNormalDot;
        }

        return double.IsFinite(support) && support > PlanetSurfaceWalker.BodyRadius && support < ApproachRadius;
    }

    private static PlanetVector LimitThrust(PlanetVector thrust)
    {
        var component = Math.Max(Math.Abs(thrust.X), Math.Max(Math.Abs(thrust.Y), Math.Abs(thrust.Z)));
        if (component == 0)
            return thrust;
        var scaled = new PlanetVector(thrust.X / component, thrust.Y / component, thrust.Z / component);
        var cap = MaximumThrust / Length(scaled);
        return component <= cap ? thrust : Scale(scaled, cap);
    }

    private static double RequireRadius(PlanetVector position, string name)
    {
        var radius = Length(position);
        return double.IsFinite(radius) && radius > PlanetSurfaceWalker.BodyRadius ? radius :
            throw new ArgumentOutOfRangeException(name, "A finite craft radius above the body radius is required.");
    }

    private static PlanetVector GravityAt(PlanetVector position, double radius) =>
        Scale(Scale(position, 1 / radius), -GravitationalParameter / radius / radius);

    private static bool IsFinite(PlanetVector value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static PlanetVector Add(PlanetVector a, PlanetVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static PlanetVector Scale(PlanetVector v, double scale) => new(v.X * scale, v.Y * scale, v.Z * scale);
    private static PlanetVector Divide(PlanetVector v, double divisor) => new(v.X / divisor, v.Y / divisor, v.Z / divisor);
    private static double Dot(PlanetVector a, PlanetVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static PlanetVector Cross(PlanetVector a, PlanetVector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static double Length(PlanetVector v)
    {
        var max = Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));
        if (max == 0)
            return 0;
        var unitScale = Divide(v, max);
        return max * Math.Sqrt(Dot(unitScale, unitScale));
    }
}
