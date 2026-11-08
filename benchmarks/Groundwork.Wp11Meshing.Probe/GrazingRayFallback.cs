using System.Numerics;

sealed class GrazingRayFallback(Func<Vector3, float> field, float extent)
{
    private const float MinimumInterval = 1f / 4096;
    private const int SampleBudget = 4096;
    internal const float SeamHalfWidth = 1f;

    // The caller supplies a unit direction and an empty mesh hit list.
    // Only complete entry/exit pairs outside the protected seam are eligible.
    internal (List<float> Hits, bool Exhausted, int Samples) Trace(Vector3 origin, Vector3 direction)
    {
        Span<(float Start, float End)> ranges = stackalloc (float, float)[2];
        var count = 0;
        var mid = extent / 2;
        if (direction.X == 0)
        {
            if (MathF.Abs(origin.X - mid) > SeamHalfWidth)
                ranges[count++] = (0, extent);
        }
        else
        {
            var a = (mid - SeamHalfWidth - origin.X) / direction.X;
            var b = (mid + SeamHalfWidth - origin.X) / direction.X;
            ranges[count++] = (0, Math.Min(extent, Math.Min(a, b)));
            ranges[count++] = (Math.Max(0, Math.Max(a, b)), extent);
        }
        return SearchRanges(origin, direction, ranges[..count], true, SampleBudget);
    }

    internal (List<float> Hits, bool Exhausted, int Samples) TraceInterval(
        Vector3 origin, Vector3 direction, float start, float end, int budget)
    {
        ReadOnlySpan<(float, float)> range = [(start, end)];
        return SearchRanges(origin, direction, range, false, budget);
    }

    private (List<float> Hits, bool Exhausted, int Samples) SearchRanges(Vector3 origin, Vector3 direction,
        ReadOnlySpan<(float Start, float End)> ranges, bool requireOutside, int budget)
    {
        var hits = new List<float>();
        var remaining = budget;
        var exhausted = false;
        foreach (var range in ranges)
            Search(range.Start, range.End);
        return exhausted ? ([], true, budget - remaining) : (hits, false, budget - remaining);
        void Search(float start, float end)
        {
            if (start >= end || exhausted)
                return;
            if (remaining < 2)
            {
                exhausted = true;
                return;
            }
            var a = Sample(start);
            var b = Sample(end);
            if (!requireOutside || a >= 0 && b >= 0)
                Visit(start, a, end, b);
        }

        void Visit(float a, float da, float b, float db)
        {
            var width = b - a;
            var signChange = (da < 0) != (db < 0);
            // An exact signed-distance field is 1-Lipschitz. Its endpoint
            // magnitudes cannot sum to more than the interval if it has a zero.
            if (exhausted || !signChange && MathF.Abs(da) + MathF.Abs(db) > width + 1e-6f)
                return;
            if (width <= MinimumInterval)
            {
                if (signChange)
                    hits.Add(a + width * da / (da - db));
                return;
            }
            if (remaining == 0)
            {
                exhausted = true;
                return;
            }
            var middle = (a + b) * 0.5f;
            var density = Sample(middle);
            Visit(a, da, middle, density);
            Visit(middle, density, b, db);
        }

        float Sample(float t)
        {
            remaining--;
            return field(origin + direction * t);
        }
    }
}
