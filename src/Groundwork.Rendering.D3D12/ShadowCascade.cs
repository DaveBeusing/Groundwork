using System.Numerics;

namespace Groundwork.Rendering.D3D12;

internal readonly record struct ShadowCascade(Vector4 Area, bool Valid);

internal readonly record struct ShadowBasis(Vector3 X, Vector3 Y, Vector3 Z)
{
    internal static ShadowBasis Create(Vector3 sun)
    {
        var z = -Vector3.Normalize(sun);
        var x = Vector3.Normalize(Vector3.Cross(MathF.Abs(z.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX, z));
        return new(x, Vector3.Cross(z, x), z);
    }

    internal Vector3 Project(Vector3 value) => new(Vector3.Dot(value, X), Vector3.Dot(value, Y), Vector3.Dot(value, Z));

    internal ShadowCascade Fit(in RenderScene scene, float aspect, float start, float end)
    {
        start = Math.Max(start, scene.NearPlane);
        end = Math.Min(end, scene.FarPlane);
        if (end <= start)
            return default;
        var view = scene.View;
        var vx = new Vector3(view.M11, view.M21, view.M31);
        var vy = new Vector3(view.M12, view.M22, view.M32);
        var vz = new Vector3(view.M13, view.M23, view.M33);
        var eye = -view.M41 * vx - view.M42 * vy - view.M43 * vz;
        var center = eye - vz * ((start + end) * 0.5f);
        var farSide = end * Math.Tan(scene.VerticalFieldOfView * 0.5);
        var halfDepth = (end - start) * 0.5;
        var radius = Math.Sqrt(farSide * farSide * (1 + (double)aspect * aspect) + halfDepth * halfDepth) * (1024d / 1020);
        var texel = radius / 512;
        var origin = scene.ShadowOrigin!.Value;
        double Snap(Vector3 axis)
        {
            var offset = origin.X * axis.X + origin.Y * axis.Y + origin.Z * axis.Z;
            var canonical = offset + (double)center.X * axis.X + (double)center.Y * axis.Y + (double)center.Z * axis.Z;
            return Math.Round(canonical / texel, MidpointRounding.ToEven) * texel - offset;
        }
        return new(new((float)Snap(X), (float)Snap(Y), (float)(1 / radius), 1), true);
    }
}
