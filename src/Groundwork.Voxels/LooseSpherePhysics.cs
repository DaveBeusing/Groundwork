using System.Numerics;

namespace Groundwork.Voxels;

public readonly record struct LooseSphereState(PlanetVector Position, Quaternion Orientation,
    PlanetVector Velocity, PlanetVector AngularVelocity, int RestTicks = 0)
{
    public bool Sleeping => RestTicks == LooseSpherePhysics.TicksToSleep;
}

public readonly record struct LooseSphereSettings(double Radius, double Friction, double Restitution);

public readonly record struct LooseSphereStep(LooseSphereState State, VolumeQueryStatus Status, int Sweeps);

/// <summary>Portable proposal for one fixed sphere step. Terrain and the caller's committed state stay immutable.</summary>
public static class LooseSpherePhysics
{
    public const double StepSeconds = 1d / 60;
    public const int TicksToSleep = 30;
    private const double RestSpeed = 0.05;
    private const double BounceThreshold = 0.35;

    public static LooseSphereStep Step(scoped VolumePhysicsRead terrain, LooseSphereState state,
        LooseSphereSettings settings, VolumeQueryLimits? limits = null)
    {
        var queryLimits = limits ?? VolumeQueryLimits.Default;
        var position = new CollisionVector(state.Position);
        var velocity = new CollisionVector(state.Velocity);
        var spin = new CollisionVector(state.AngularVelocity);
        var radius = position.Length;
        if (!queryLimits.Valid || !double.IsFinite(radius) || radius <= 0 ||
            !double.IsFinite(velocity.Length) || !double.IsFinite(spin.Length) || spin.Length > float.MaxValue ||
            !float.IsFinite(state.Orientation.LengthSquared()) || Math.Abs(state.Orientation.LengthSquared() - 1) > 1e-5 ||
            !double.IsFinite(settings.Radius) || settings.Radius <= 0 || settings.Radius > 32 * terrain.CellSizeMeters ||
            !(settings.Friction >= 0 && settings.Friction <= 1 && settings.Restitution >= 0 && settings.Restitution <= 1) ||
            state.RestTicks is < 0 or > TicksToSleep)
            return new(state, VolumeQueryStatus.Invalid, 0);
        if (state.Sleeping)
            return new(state, VolumeQueryStatus.Clear, 0);

        var up = position / radius;
        velocity -= up * (PlanetSurfaceWalker.GravityStrength * StepSeconds);
        var remaining = StepSeconds;
        var supported = false;
        var status = VolumeQueryStatus.Clear;
        var sweeps = 0;
        while (sweeps < 2 && remaining > 0)
        {
            var delta = velocity * remaining;
            var hit = terrain.Sweep(new(position.Planet, position.Planet, settings.Radius), delta.Planet, queryLimits);
            sweeps++;
            if (hit.Status == VolumeQueryStatus.Clear)
            {
                position += delta;
                break;
            }
            if (hit.Status != VolumeQueryStatus.Contact)
                return new(state, hit.Status, sweeps);

            status = VolumeQueryStatus.Contact;
            position += delta * hit.Fraction;
            remaining *= 1 - hit.Fraction;
            var normal = new CollisionVector(hit.Normal);
            var inward = CollisionVector.Dot(velocity, normal);
            supported |= CollisionVector.Dot(normal, up) >= 0.7 && -inward <= BounceThreshold;
            if (inward >= 0)
                break;
            var restitution = -inward > BounceThreshold ? settings.Restitution : 0;
            var normalImpulse = -(1 + restitution) * inward;
            velocity += normal * normalImpulse;

            // Unit-mass solid sphere, moment of inertia 2r²/5. Limit friction by the normal impulse.
            var arm = normal * -settings.Radius;
            var slip = velocity + CollisionVector.Cross(spin, arm);
            var tangent = slip - normal * CollisionVector.Dot(slip, normal);
            var slipSpeed = tangent.Length;
            if (slipSpeed > 0)
            {
                var impulse = tangent / slipSpeed * -Math.Min(slipSpeed / 3.5, settings.Friction * normalImpulse);
                velocity += impulse;
                spin += CollisionVector.Cross(normal * -1, impulse) * (2.5 / settings.Radius);
            }
        }

        var speed = spin.Length;
        if (!double.IsFinite(velocity.Length) || !double.IsFinite(speed) || speed > float.MaxValue)
            return new(state, VolumeQueryStatus.Invalid, sweeps);
        var rest = supported && velocity.Length <= RestSpeed && speed <= RestSpeed ? state.RestTicks + 1 : 0;
        var orientation = state.Orientation;
        if (speed > 0)
        {
            var axis = spin / speed;
            var rotation = Quaternion.CreateFromAxisAngle(new Vector3((float)axis.X, (float)axis.Y, (float)axis.Z),
                (float)(speed * StepSeconds));
            orientation = Quaternion.Normalize(rotation * orientation);
        }
        if (rest == TicksToSleep)
        {
            velocity = default;
            spin = default;
        }
        return new(new(position.Planet, orientation, velocity.Planet, spin.Planet, rest), status, sweeps);
    }
}
