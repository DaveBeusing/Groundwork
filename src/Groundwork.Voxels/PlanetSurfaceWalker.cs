namespace Groundwork.Voxels;

public enum PlanetWalkStatus
{
    Moved,
    Held,
    CoverageUnavailable,
    SteepTerrain
}

/// <summary>The capsule foot, tangent heading and velocity in planet-centered meters.</summary>
public readonly record struct PlanetWalkState(PlanetVector Position, PlanetVector Heading, PlanetVector Velocity);

/// <summary>
/// The accepted state and contact result. A rejected sweep reports known contact
/// at the original footprint if that footprint was verified before movement.
/// </summary>
public readonly record struct PlanetWalkResult(PlanetWalkState State, PlanetWalkStatus Status, bool Grounded);

/// <summary>Fixed-step walking and falling over a single radial terrain surface.</summary>
public static class PlanetSurfaceWalker
{
    public const double StepSeconds = 1.0 / 60;
    public const double BodyRadius = 0.25;
    public const double BodyHeight = 1.7;
    public const double WalkSpeed = 4;
    public const double GravityStrength = 9.81;
    private const double ContactTolerance = 0.001;
    private const double MinimumWalkableNormal = 0.7071067811865476;
    private const double MaximumStepHeight = 0.25;
    private const double MaximumRadialSpeed = 60;
    private const double MaximumSweepSpacing = BodyRadius / 4;

    private static readonly (double X, double Y)[] Footprint =
    [
        (1, 0), (MinimumWalkableNormal, MinimumWalkableNormal), (0, 1), (-MinimumWalkableNormal, MinimumWalkableNormal),
        (-1, 0), (-MinimumWalkableNormal, -MinimumWalkableNormal), (0, -1), (MinimumWalkableNormal, -MinimumWalkableNormal)
    ];

    public static PlanetVector Gravity(PlanetVector position) => Scale(Unit(position, nameof(position)), -GravityStrength);

    /// <summary>
    /// Advances one 60 Hz tick with analog forward and right input in [-1, 1].
    /// Terrain coverage and capsule base clearance are sampled at the center and on two
    /// footprint rings along the sweep. This is a bounded sampled radial-surface query
    /// rather than a continuous triangle/capsule intersection solver.
    /// </summary>
    public static PlanetWalkResult Step(IPlanetSurface surface, PlanetWalkState state, double forward, double right)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ValidateInput(forward, nameof(forward));
        ValidateInput(right, nameof(right));
        var up = Unit(state.Position, nameof(state));
        var radius = Length(state.Position);
        if (!double.IsFinite(radius) || radius <= BodyRadius)
            throw new ArgumentOutOfRangeException(nameof(state), "The foot radius must exceed the body radius.");

        var heading = TangentHeading(Unit(state.Heading, nameof(state)), up);
        var sideways = Cross(heading, up);
        var radialSpeed = Dot(state.Velocity, up);
        if (!IsFinite(state.Velocity) || !double.IsFinite(radialSpeed))
            throw new ArgumentOutOfRangeException(nameof(state), "Velocity must be finite.");

        if (!TryFootSupport(surface, up, heading, radius, out var support, out var walkable))
            return Blocked(state, PlanetWalkStatus.CoverageUnavailable);

        var grounded = radius <= support + ContactTolerance && radialSpeed <= 0;
        if (grounded && !walkable)
            return Blocked(state, PlanetWalkStatus.SteepTerrain);
        var originalGrounded = grounded;

        // The command bounds each horizontal sweep to at most two intervals.
        var inputLength = Math.Sqrt(forward * forward + right * right);
        var speedScale = WalkSpeed / Math.Max(1, inputLength);
        var tangentVelocity = Scale(Add(Scale(heading, forward), Scale(sideways, right)), speedScale);
        var travel = Length(tangentVelocity) * StepSeconds;
        var subdivisions = Math.Max(1, (int)Math.Ceiling(travel / MaximumSweepSpacing));
        var dt = StepSeconds / subdivisions;
        radialSpeed = grounded ? 0 : Math.Clamp(radialSpeed - GravityStrength * StepSeconds, -MaximumRadialSpeed, MaximumRadialSpeed);

        for (var i = 0; i < subdivisions; i++)
        {
            var candidateUp = Unit(Add(up, Scale(tangentVelocity, dt / radius)), nameof(state));
            var candidateHeading = TangentHeading(Transport(heading, up, candidateUp), candidateUp);
            if (!TryFootSupport(surface, candidateUp, candidateHeading, radius, out var candidateSupport, out walkable))
                return Blocked(state, PlanetWalkStatus.CoverageUnavailable, originalGrounded);

            // A large drop ends ground following. Start gravity over the remaining
            // portion of this tick instead of snapping to the lower terrain.
            var followsSupport = grounded && radius - candidateSupport <= MaximumStepHeight;
            if (grounded && !followsSupport)
                radialSpeed = -GravityStrength * (subdivisions - i) * dt;

            var freeRadius = radius + radialSpeed * dt;
            var contacts = followsSupport || freeRadius <= candidateSupport + ContactTolerance;
            if (contacts && (!walkable || candidateSupport - radius > MaximumStepHeight))
                return Blocked(state, PlanetWalkStatus.SteepTerrain, originalGrounded);

            grounded = contacts;
            radius = grounded ? candidateSupport : freeRadius;
            radialSpeed = grounded ? 0 : radialSpeed;
            tangentVelocity = Transport(tangentVelocity, up, candidateUp);
            up = candidateUp;
            heading = candidateHeading;
        }

        var position = Scale(up, radius);
        var velocity = Add(tangentVelocity, Scale(up, radialSpeed));
        var status = travel == 0 && grounded && Length(Subtract(position, state.Position)) <= ContactTolerance
            ? PlanetWalkStatus.Held : PlanetWalkStatus.Moved;
        return new PlanetWalkResult(new PlanetWalkState(position, heading, velocity), status, grounded);
    }

    private static PlanetWalkResult Blocked(PlanetWalkState state, PlanetWalkStatus status, bool grounded = false) =>
        new(state, status, grounded);

    private static bool TryFootSupport(IPlanetSurface surface, PlanetVector up, PlanetVector heading,
        double radius, out double supportRadius, out bool walkable)
    {
        supportRadius = 0;
        walkable = true;
        if (!surface.TryGetSupport(up, out var center))
            return false;
        supportRadius = Dot(center.Position, up);
        walkable = IsWalkable(center.Normal, up);
        var sideways = Cross(heading, up);
        for (var ring = 1; ring <= 2; ring++)
        {
            var offsetRadius = BodyRadius * ring / 2;
            foreach (var (x, y) in Footprint)
            {
                var offset = Add(Scale(heading, x * offsetRadius), Scale(sideways, y * offsetRadius));
                var direction = Unit(Add(up, Scale(offset, 1 / radius)), nameof(radius));
                if (!surface.TryGetSupport(direction, out var support))
                    return false;

                // Convert the radial hit into the local body frame. The lower cap begins at
                // the foot and rises to the radius at the outer edge of the footprint.
                var height = Dot(support.Position, up);
                var lateral = Length(Subtract(support.Position, Scale(up, height)));
                var capRise = BodyRadius - Math.Sqrt(Math.Max(0, BodyRadius * BodyRadius - lateral * lateral));
                supportRadius = Math.Max(supportRadius, height - capRise);
                walkable &= IsWalkable(support.Normal, direction);
            }
        }
        return true;
    }

    private static bool IsWalkable(PlanetVector normal, PlanetVector up) => Dot(normal, up) >= MinimumWalkableNormal;

    // Rodrigues transport with the cross product scaled by sin(angle). Every
    // accepted substep is small enough that the old and new up cannot be antipodal.
    private static PlanetVector Transport(PlanetVector vector, PlanetVector from, PlanetVector to)
    {
        var axis = Cross(from, to);
        var first = Cross(axis, vector);
        var second = Scale(Cross(axis, first), 1 / (1 + Dot(from, to)));
        return Add(vector, Add(first, second));
    }

    private static PlanetVector TangentHeading(PlanetVector heading, PlanetVector up)
    {
        var tangent = Subtract(heading, Scale(up, Dot(heading, up)));
        if (Length(tangent) < 1e-9)
            throw new ArgumentOutOfRangeException(nameof(heading), "Heading must have a tangent component.");
        return Unit(tangent, nameof(heading));
    }

    private static void ValidateInput(double input, string name)
    {
        if (!double.IsFinite(input) || Math.Abs(input) > 1)
            throw new ArgumentOutOfRangeException(name);
    }

    private static PlanetVector Unit(PlanetVector vector, string name)
    {
        var scale = Math.Max(Math.Abs(vector.X), Math.Max(Math.Abs(vector.Y), Math.Abs(vector.Z)));
        if (!double.IsFinite(scale) || scale == 0)
            throw new ArgumentOutOfRangeException(name, "Vector must be finite and nonzero.");
        var scaled = new PlanetVector(vector.X / scale, vector.Y / scale, vector.Z / scale);
        return Scale(scaled, 1 / Math.Sqrt(Dot(scaled, scaled)));
    }

    private static bool IsFinite(PlanetVector vector) =>
        double.IsFinite(vector.X) && double.IsFinite(vector.Y) && double.IsFinite(vector.Z);

    private static double Length(PlanetVector vector)
    {
        var scale = Math.Max(Math.Abs(vector.X), Math.Max(Math.Abs(vector.Y), Math.Abs(vector.Z)));
        if (scale == 0)
            return 0;
        var scaled = new PlanetVector(vector.X / scale, vector.Y / scale, vector.Z / scale);
        return scale * Math.Sqrt(Dot(scaled, scaled));
    }

    private static double Dot(PlanetVector a, PlanetVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static PlanetVector Add(PlanetVector a, PlanetVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static PlanetVector Subtract(PlanetVector a, PlanetVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static PlanetVector Scale(PlanetVector vector, double scale) => new(vector.X * scale, vector.Y * scale, vector.Z * scale);
    private static PlanetVector Cross(PlanetVector a, PlanetVector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
