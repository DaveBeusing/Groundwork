using System.Numerics;

namespace Groundwork.Rendering;

/// <summary>Linear RGB gains and a unit world direction toward the sun.</summary>
public readonly record struct RenderSunlight(Vector3 Direction, Vector3 Direct, Vector3 Ambient)
{
    internal bool IsValid => MathF.Abs(Direction.LengthSquared() - 1) <= 0.0001f &&
        Gain(Direct) && Gain(Ambient);

    private static bool Gain(Vector3 value) =>
        value.X is >= 0 and <= 4 && value.Y is >= 0 and <= 4 && value.Z is >= 0 and <= 4;
}
