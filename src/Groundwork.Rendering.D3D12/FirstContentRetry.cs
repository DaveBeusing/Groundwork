using System.Diagnostics;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12;

/// <summary>Only a registered first-content frame that has never executed can occupy this slot.</summary>
internal readonly record struct FirstContentRetry(VolumeFrameRegistration Frame, long Started, int Rejections)
{
    internal const int MaximumRejections = 3;

    internal FirstContentRetry RejectAgain() => this with { Rejections = Rejections + 1 };

    internal bool IsExpired(long now, TimeSpan limit) =>
        Rejections >= MaximumRejections || Stopwatch.GetElapsedTime(Started, now) >= limit;

    internal int WaitMilliseconds(long now, TimeSpan limit)
    {
        var remaining = limit - Stopwatch.GetElapsedTime(Started, now);
        return remaining <= TimeSpan.Zero
            ? 0
            : (int)Math.Min(int.MaxValue, Math.Ceiling(remaining.TotalMilliseconds));
    }
}
