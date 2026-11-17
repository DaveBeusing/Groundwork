namespace Groundwork.Voxels;

/// <summary>Elapsed <see cref="System.Diagnostics.Stopwatch"/> ticks in successful, newly generated bricks.</summary>
/// <param name="ReservationTicks">Input validation and store admission.</param>
/// <param name="AllocationTicks">Post-admission cancellation check and payload allocation.</param>
/// <param name="DensityTicks">Density row sampling and writes.</param>
/// <param name="MaterialTicks">Material row sampling, validation and writes.</param>
/// <param name="SharedVertexTicks">Shared face, edge and corner validation.</param>
/// <param name="PublishTicks">Store lock acquisition and publication, excluding shared vertex validation.</param>
public readonly record struct VolumeBrickGenerationTiming(long ReservationTicks, long AllocationTicks,
    long DensityTicks, long MaterialTicks, long SharedVertexTicks, long PublishTicks)
{
    public static VolumeBrickGenerationTiming operator +(VolumeBrickGenerationTiming left, VolumeBrickGenerationTiming right) =>
        checked(new(left.ReservationTicks + right.ReservationTicks, left.AllocationTicks + right.AllocationTicks,
            left.DensityTicks + right.DensityTicks, left.MaterialTicks + right.MaterialTicks,
            left.SharedVertexTicks + right.SharedVertexTicks, left.PublishTicks + right.PublishTicks));
}
