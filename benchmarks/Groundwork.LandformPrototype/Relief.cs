using Groundwork.Voxels;

// Experimental +Y patch. Only the transect fixture projects to the reference sphere.
// Parameters and seeded streams are local to P00 and carry no persisted identity.
internal readonly struct Relief(ulong seed)
{
    private const int Q = 256;
    private readonly int _phase = (int)(Mix(seed) & 127);

    internal (long Ridge, long Main, long Tributary) Centres(long z)
    {
        var main = Wave(z + _phase, 128, 12);
        return (main - 58 + Wave(z + 2L * _phase, 256, 8), main,
            main + Math.Clamp(-z, 0, 128) / 3);
    }

    internal long Delta(LatticeCoordinate vertex, bool smooth)
    {
        if (vertex.Y <= 0 || Math.Abs(vertex.X) > 256 || Math.Abs(vertex.Z) > 192)
            return 0;
        return Profile(vertex.X, vertex.Z, smooth);
    }

    internal void FillLinearColumns(LatticeBrick brick, Span<long> columns)
    {
        const int edge = PlanetBrickStore.VertexEdge;
        var origin = brick.Vertex(0, 0, 0);
        for (var z = 0; z < edge; z++)
        for (var x = 0; x < edge; x++)
            columns[z * edge + x] = Delta(new(origin.X + x, 1, origin.Z + z), false);
    }

    internal (long Linear, long Smooth) SampleProfile(long x, long z) => (Profile(x, z, false), Profile(x, z, true));

    private long Profile(long x, long z, bool smooth)
    {
        var (ridge, main, tributary) = Centres(z);
        var crest = Band(x - ridge, 26, 22 * Q, smooth);
        var valley = Band(x - main, 19, 16 * Q, smooth);
        var branch = z is >= -160 and <= 0 ? Band(x - tributary, 12, 10 * Q, smooth)
            * Math.Min(32, Math.Min(z + 160, -z)) / 32 : 0;
        var envelope = Math.Min(32, Math.Max(0, 192 - Math.Abs(z)));
        var cut = Band(Math.Abs(x - ridge + 9) + Math.Abs(z - 40), 12, 4 * Q, smooth);
        return (crest - Math.Max(valley, branch) - cut) * envelope / 32;
    }

    internal static LatticeCoordinate Vertex(long x, long z) =>
        new(x, (long)Math.Round(Math.Sqrt(4096L * 4096 - x * x - z * z), MidpointRounding.ToEven), z);

    private static long Band(long offset, int width, int amplitude, bool smooth)
    {
        var t = Math.Max(0, width - Math.Abs(offset)) * Q / width;
        if (smooth)
            t = t * t * (3 * Q - 2 * t) / (Q * Q);
        return amplitude * t / Q;
    }

    private static long Wave(long value, int period, int amplitude)
    {
        var part = value & (period - 1);
        return (period / 2 - Math.Abs(part - period / 2)) * (4 * amplitude) / period - amplitude;
    }

    private static ulong Mix(ulong value)
    {
        value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111ebUL);
        return value ^ (value >> 31);
    }
}
