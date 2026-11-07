using System.Numerics;

namespace Groundwork.Voxels;

/// <summary>
/// Translation from authoritative planet coordinates to a float render origin.
/// The planet axes and all direction vectors are shared by both frames.
/// </summary>
public readonly record struct PlanetLocalFrame
{
    public const double GridMeters = 8;
    public const double RebaseDistanceMeters = 12;

    private PlanetLocalFrame(PlanetVector origin, ulong revision)
    {
        Origin = origin;
        Revision = revision;
    }

    public PlanetVector Origin { get; }
    public ulong Revision { get; }

    public static PlanetLocalFrame Create(PlanetVector position) => new(Snap(position), 1);

    /// <summary>Returns a new revision if the position lies outside the published origin's radius.</summary>
    public bool TryRebase(PlanetVector position, out PlanetLocalFrame next)
    {
        EnsureInitialized();
        var (x, y, z) = Offset(position);
        if (x * x + y * y + z * z <= RebaseDistanceMeters * RebaseDistanceMeters)
        {
            next = this;
            return false;
        }

        next = new PlanetLocalFrame(Snap(position), checked(Revision + 1));
        return true;
    }

    public Vector3 ToLocal(PlanetVector position)
    {
        EnsureInitialized();
        var (x, y, z) = Offset(position);
        var result = new Vector3((float)x, (float)y, (float)z);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z))
            throw new ArgumentOutOfRangeException(nameof(position), "Position is outside the float render frame.");
        return result;
    }

    public PlanetVector ToPlanet(Vector3 local)
    {
        EnsureInitialized();
        var result = new PlanetVector(Origin.X + local.X, Origin.Y + local.Y, Origin.Z + local.Z);
        if (!IsFinite(result))
            throw new ArgumentOutOfRangeException(nameof(local), "Position must reconstruct to a finite planet coordinate.");
        return result;
    }

    private PlanetVector Offset(PlanetVector position)
    {
        if (!IsFinite(position))
            throw new ArgumentOutOfRangeException(nameof(position), "Position must be finite.");
        return new PlanetVector(position.X - Origin.X, position.Y - Origin.Y, position.Z - Origin.Z);
    }

    private static PlanetVector Snap(PlanetVector position)
    {
        if (!IsFinite(position))
            throw new ArgumentOutOfRangeException(nameof(position), "Position must be finite.");
        return new PlanetVector(SnapAxis(position.X), SnapAxis(position.Y), SnapAxis(position.Z));
    }

    private static double SnapAxis(double axis) => Math.Round(axis / GridMeters, MidpointRounding.ToEven) * GridMeters;

    private static bool IsFinite(PlanetVector vector) =>
        double.IsFinite(vector.X) && double.IsFinite(vector.Y) && double.IsFinite(vector.Z);

    private void EnsureInitialized()
    {
        if (Revision == 0)
            throw new InvalidOperationException("Create a local frame before using it.");
    }
}
