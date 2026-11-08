using System.Numerics;

sealed class SubtractiveRayRepair(Func<Vector3, float> field, CollisionSphere[] brushes, float extent, bool acrossSeam)
{
    private const int SampleBudget = 4096;
    private const float Padding = .5f;
    private const float RadialPadding = .125f;
    private const float SeamMissPadding = 1f;
    private const float EndpointTolerance = 1e-5f;
    private readonly GrazingRayFallback _search = new(field, extent);
    internal bool AllowsEmptyMesh => acrossSeam;

    internal (List<float> Hits, int Intervals, bool Exhausted) Trace(
        Vector3 origin, Vector3 direction, List<float> meshHits)
    {
        // A transition can miss grazing terrain even just beyond the edit.
        // Expand only empty mesh queries in the certified seam fixture.
        var radialPadding = acrossSeam && meshHits.Count == 0 ? SeamMissPadding : RadialPadding;
        var ranges = new List<(float Start, float End)>();
        foreach (var brush in brushes)
        {
            // A sampled cut can protrude slightly beyond the analytic brush.
            if (brush.Roots(origin, direction, radialPadding) is not { } roots || roots.Enter == roots.Exit ||
                roots.Exit < 0 || roots.Enter > extent)
                continue;
            var start = Math.Max(0, roots.Enter - Padding);
            var end = Math.Min(extent, roots.Exit + Padding);
            var left = brush.Center.X < extent / 2;
            if (!acrossSeam && direction.X != 0)
            {
                var seam = extent / 2 + (left ? -1 : 1) * GrazingRayFallback.SeamHalfWidth;
                var boundary = (seam - origin.X) / direction.X;
                if (left == (direction.X > 0))
                    end = Math.Min(end, boundary);
                else
                    start = Math.Max(start, boundary);
            }
            if (start < end)
                ranges.Add((start, end));
        }
        if (ranges.Count == 0 || field(origin) <= 0)
            return (meshHits, 0, false);

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        List<float>? corrected = null;
        var intervals = 0;
        var budget = SampleBudget - 1;
        var next = 0;
        while (next < ranges.Count)
        {
            var (start, end) = ranges[next++];
            while (next < ranges.Count && ranges[next].Start <= end)
                end = Math.Max(end, ranges[next++].End);

            // Splice only where sampled-field and mesh occupancy agree on
            // both sides. This preserves the parity of the unmodified ray.
            if (budget < 2)
                return (meshHits, 0, true);
            budget -= 2;
            if (!Matches(start) || !Matches(end))
                continue;
            var (hits, exhausted, samples) = _search.TraceInterval(origin, direction, start, end, budget);
            if (exhausted)
                return (meshHits, 0, true);
            budget -= samples;
            corrected ??= new List<float>(meshHits);
            corrected.RemoveAll(hit => hit > start && hit < end);
            corrected.AddRange(hits);
            intervals++;
        }

        corrected?.Sort();
        return (corrected ?? meshHits, intervals, false);

        bool Matches(float t)
        {
            var density = field(origin + direction * t);
            if (MathF.Abs(density) <= EndpointTolerance ||
                meshHits.Any(hit => MathF.Abs(hit - t) <= EndpointTolerance))
                return false;
            var insideMesh = (meshHits.Count(hit => hit < t) & 1) != 0;
            return insideMesh == (density < 0);
        }
    }
}
