namespace Groundwork.Rendering.Textures;

internal static class TextureMipFilter
{
    // IEC sRGB EOTF, round-to-nearest linear values on [0, 65535].
    private static ReadOnlySpan<ushort> SrgbToLinear =>
    [
        0, 20, 40, 60, 80, 99, 119, 139, 159, 179, 199, 219, 241, 264, 288, 313,
        340, 367, 396, 427, 458, 491, 526, 562, 599, 637, 677, 718, 761, 805, 851, 898,
        947, 997, 1048, 1101, 1156, 1212, 1270, 1330, 1391, 1453, 1517, 1583, 1651, 1720, 1790, 1863,
        1937, 2013, 2090, 2170, 2250, 2333, 2418, 2504, 2592, 2681, 2773, 2866, 2961, 3058, 3157, 3258,
        3360, 3464, 3570, 3678, 3788, 3900, 4014, 4129, 4247, 4366, 4488, 4611, 4736, 4864, 4993, 5124,
        5257, 5392, 5530, 5669, 5810, 5953, 6099, 6246, 6395, 6547, 6700, 6856, 7014, 7174, 7335, 7500,
        7666, 7834, 8004, 8177, 8352, 8528, 8708, 8889, 9072, 9258, 9445, 9635, 9828, 10022, 10219, 10417,
        10619, 10822, 11028, 11235, 11446, 11658, 11873, 12090, 12309, 12530, 12754, 12980, 13209, 13440, 13673, 13909,
        14146, 14387, 14629, 14874, 15122, 15371, 15623, 15878, 16135, 16394, 16656, 16920, 17187, 17456, 17727, 18001,
        18277, 18556, 18837, 19121, 19407, 19696, 19987, 20281, 20577, 20876, 21177, 21481, 21787, 22096, 22407, 22721,
        23038, 23357, 23678, 24002, 24329, 24658, 24990, 25325, 25662, 26001, 26344, 26688, 27036, 27386, 27739, 28094,
        28452, 28813, 29176, 29542, 29911, 30282, 30656, 31033, 31412, 31794, 32179, 32567, 32957, 33350, 33745, 34143,
        34544, 34948, 35355, 35764, 36176, 36591, 37008, 37429, 37852, 38278, 38706, 39138, 39572, 40009, 40449, 40891,
        41337, 41785, 42236, 42690, 43147, 43606, 44069, 44534, 45002, 45473, 45947, 46423, 46903, 47385, 47871, 48359,
        48850, 49344, 49841, 50341, 50844, 51349, 51858, 52369, 52884, 53401, 53921, 54445, 54971, 55500, 56032, 56567,
        57105, 57646, 58190, 58737, 59287, 59840, 60396, 60955, 61517, 62082, 62650, 63221, 63795, 64372, 64952, 65535,
    ];

    internal static void Generate(Span<byte> payload, TextureMip previous, TextureMip next, TextureCookRecipe recipe)
    {
        ReadOnlySpan<byte> source = payload.Slice(previous.Offset, previous.ByteLength);
        Span<byte> destination = payload.Slice(next.Offset, next.ByteLength);
        bool srgb = recipe.ColorSpace == TextureColorSpace.Srgb;
        long totalWeight = (long)previous.Width * previous.Height;

        for (int y = 0; y < next.Height; y++)
        {
            long top = (long)y * previous.Height;
            long bottom = top + previous.Height;
            int firstY = (int)(top / next.Height);
            int lastY = (int)((bottom - 1) / next.Height);
            for (int x = 0; x < next.Width; x++)
            {
                long left = (long)x * previous.Width;
                long right = left + previous.Width;
                int firstX = (int)(left / next.Width);
                int lastX = (int)((right - 1) / next.Width);
                long red = 0, green = 0, blue = 0, alpha = 0;

                // Coordinates are scaled by the destination size for exact fractional coverage.
                for (int sy = firstY; sy <= lastY; sy++)
                {
                    long rowTop = (long)sy * next.Height;
                    long vertical = Math.Min(bottom, rowTop + next.Height) - Math.Max(top, rowTop);
                    for (int sx = firstX; sx <= lastX; sx++)
                    {
                        long columnLeft = (long)sx * next.Width;
                        long horizontal = Math.Min(right, columnLeft + next.Width) - Math.Max(left, columnLeft);
                        long weight = vertical * horizontal;
                        int index = sy * previous.RowBytes + sx * 4;
                        red += weight * (srgb ? SrgbToLinear[source[index]] : source[index]);
                        green += weight * (srgb ? SrgbToLinear[source[index + 1]] : source[index + 1]);
                        blue += weight * (srgb ? SrgbToLinear[source[index + 2]] : source[index + 2]);
                        alpha += weight * source[index + 3];
                    }
                }

                int output = y * next.RowBytes + x * 4;
                if (recipe.Semantic == TextureSemantic.Normal)
                {
                    long zero = 255 * totalWeight;
                    EncodeNormal(destination.Slice(output, 3), 2 * red - zero, 2 * green - zero, 2 * blue - zero);
                }
                else
                {
                    destination[output] = srgb ? EncodeSrgb(red, totalWeight) : RoundMean(red, totalWeight);
                    destination[output + 1] = srgb ? EncodeSrgb(green, totalWeight) : RoundMean(green, totalWeight);
                    destination[output + 2] = srgb ? EncodeSrgb(blue, totalWeight) : RoundMean(blue, totalWeight);
                }
                destination[output + 3] = RoundMean(alpha, totalWeight);
            }
        }
    }

    private static byte RoundMean(long sum, long weight) => (byte)((sum + weight / 2) / weight);

    private static byte EncodeSrgb(long sum, long weight)
    {
        int low = 0;
        int high = 255;
        while (low < high)
        {
            int candidate = (low + high + 1) / 2;
            long midpoint = weight * (SrgbToLinear[candidate - 1] + SrgbToLinear[candidate]);
            if (2 * sum >= midpoint)
                low = candidate;
            else
                high = candidate - 1;
        }

        return (byte)low;
    }

    private static void EncodeNormal(Span<byte> destination, long x, long y, long z)
    {
        Int128 squaredLength = (Int128)x * x + (Int128)y * y + (Int128)z * z;
        if (squaredLength == 0)
        {
            destination[0] = 128;
            destination[1] = 128;
            destination[2] = 255;
            return;
        }

        destination[0] = EncodeNormalComponent(x, squaredLength);
        destination[1] = EncodeNormalComponent(y, squaredLength);
        destination[2] = EncodeNormalComponent(z, squaredLength);
    }

    private static byte EncodeNormalComponent(long component, Int128 squaredLength)
    {
        // Test exact half-step thresholds for round(127.5 + 127.5 * component / length).
        // Squaring avoids floating-point and is valid after comparing signs.
        Int128 scaled = (Int128)component * 255;
        Int128 squared = scaled * scaled;
        int low = 0;
        int high = 255;
        while (low < high)
        {
            int candidate = (low + high + 1) / 2;
            int threshold = 2 * candidate - 256;
            Int128 squaredThreshold = squaredLength * threshold * threshold;
            bool roundsUp = component >= 0
                ? threshold <= 0 || squared >= squaredThreshold
                : threshold < 0 && squared <= squaredThreshold;
            if (roundsUp)
                low = candidate;
            else
                high = candidate - 1;
        }

        return (byte)low;
    }
}
