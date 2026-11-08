using System.Numerics;

sealed class GrazingRayFallback(Func<Vector3, float> field, float extent)
{
    private const float MinimumInterval = 1f / 4096;
    private const int SampleBudget = 4096;
    internal const float SeamHalfWidth = 1f;

    // The caller supplies a unit direction and an empty mesh hit list.
    // Only complete entry/exit pairs outside the protected seam are eligible.
    internal (List<float> Hits, bool Exhausted) Trace(Vector3 origin, Vector3 direction)
    {
        var hits = new List<float>();
        var remaining = SampleBudget;
        var exhausted = false;
        var mid = extent / 2;
        if (direction.X == 0)
        {
            if (MathF.Abs(origin.X - mid) > SeamHalfWidth)
                Search(0, extent);
        }
        else
        {
            var a = (mid - SeamHalfWidth - origin.X) / direction.X;
            var b = (mid + SeamHalfWidth - origin.X) / direction.X;
            Search(0, Math.Min(extent, Math.Min(a, b)));
            Search(Math.Max(0, Math.Max(a, b)), extent);
        }
        return exhausted ? ([], true) : (hits, false);

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
            if (a >= 0 && b >= 0)
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
