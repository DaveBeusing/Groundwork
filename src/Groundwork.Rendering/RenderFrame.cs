using System.Numerics;

namespace Groundwork.Rendering;

/// <summary>Optional terrain fog in linear RGB and positive camera-forward depth in metres.</summary>
public readonly record struct RenderFog(Vector3 Color, float StartDepthMeters, float EndDepthMeters, float MaxOpacity)
{
    private const float MinimumDepthSpan = 1.17549435E-38f;

    internal bool IsValid => RenderFrame.IsColor(new Vector4(Color, MaxOpacity)) &&
        StartDepthMeters >= 0 && float.IsFinite(EndDepthMeters) &&
        EndDepthMeters - StartDepthMeters >= MinimumDepthSpan;
}

/// <summary>One optional unit cube in a right-handed, row-vector scene.</summary>
public readonly record struct RenderScene(
    Matrix4x4 World,
    Vector4 Tint,
    Matrix4x4 View,
    float VerticalFieldOfView,
    float NearPlane,
    float FarPlane)
{
    /// <summary>Null preserves the original vertex color and tint path.</summary>
    public RenderFog? Fog { get; init; }

    internal bool IsValid => (Fog is null || Fog.Value.IsValid) && IsAffine(World) && IsRigid(View) && RenderFrame.IsColor(Tint) &&
        VerticalFieldOfView > 0 && VerticalFieldOfView < MathF.PI &&
        NearPlane > 0 && FarPlane > NearPlane && float.IsFinite(FarPlane);

    private static bool IsAffine(in Matrix4x4 value) =>
        value.M14 == 0 && value.M24 == 0 && value.M34 == 0 && value.M44 == 1 &&
        IsFinite(value.M11, value.M12, value.M13) && IsFinite(value.M21, value.M22, value.M23) &&
        IsFinite(value.M31, value.M32, value.M33) && IsFinite(value.M41, value.M42, value.M43);

    private static bool IsFinite(float x, float y, float z) =>
        float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);

    private static bool IsRigid(in Matrix4x4 value)
    {
        if (!IsAffine(value))
        {
            return false;
        }

        var x = new Vector3(value.M11, value.M12, value.M13);
        var y = new Vector3(value.M21, value.M22, value.M23);
        var z = new Vector3(value.M31, value.M32, value.M33);
        return Near(x.LengthSquared(), 1) && Near(y.LengthSquared(), 1) && Near(z.LengthSquared(), 1) &&
            Near(Vector3.Dot(x, y), 0) && Near(Vector3.Dot(y, z), 0) && Near(Vector3.Dot(z, x), 0) &&
            Near(Vector3.Dot(Vector3.Cross(x, y), z), 1);
    }

    // Allow ordinary floating-point error from System.Numerics camera construction.
    private static bool Near(float value, float expected) => MathF.Abs(value - expected) <= 0.0001f;
}

/// <summary>Fixed-size frame data that contains no caller-owned references.</summary>
public readonly record struct RenderFrame(ulong Sequence, Vector4 ClearColor, RenderScene? Scene = null)
{
    /// <summary>Optional sky, including for frames that hold terrain publication.</summary>
    public RenderSky? Sky { get; init; }

    /// <summary>Checks frame values independently of admission and sequence history.</summary>
    public bool IsValid => Sequence != 0 && IsColor(ClearColor) && (Sky is null || Sky.Value.IsValid) && (Scene is null || Scene.Value.IsValid);

    internal static bool IsColor(Vector4 value) =>
        value.X is >= 0 and <= 1 && value.Y is >= 0 and <= 1 &&
        value.Z is >= 0 and <= 1 && value.W is >= 0 and <= 1;
}
