using System.Numerics;

namespace Groundwork.Rendering.D3D12;

internal static class PackedTerrainNormal
{
    // Three signed normalized 10-bit channels. Zero is a missing or invalid normal.
    internal static uint Encode(Vector3 normal)
    {
        var length = normal.LengthSquared();
        if (!float.IsFinite(length) || length <= 0)
            return 0;
        normal /= MathF.Sqrt(length);
        return EncodeChannel(normal.X) | EncodeChannel(normal.Y) << 10 | EncodeChannel(normal.Z) << 20;
    }

    private static uint EncodeChannel(float value) => unchecked((uint)(int)MathF.Round(value * 511)) & 1023;
}
