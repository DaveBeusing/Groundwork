using System.Collections.Immutable;
using Groundwork.Core;

namespace Groundwork.Voxels;

/// <summary>Relocation of an existing walker onto a staged cut of the current terrain revision.</summary>
/// <param name="SourceTerrain">The exact committed terrain publication under the walker.</param>
/// <param name="SourcePlayer">The exact committed pose, including heading and velocity.</param>
/// <param name="Destination">The requested foot in planet-centered metres, up to one metre above support.</param>
public readonly record struct VolumeRelocationRequest(VolumePublicationInfo SourceTerrain,
    PlanetWalkState SourcePlayer, PlanetVector Destination)
{
    public const double MaximumDistanceMeters = 512;
    public const double MaximumSettleMeters = 1;
}

public enum VolumeRelocationStatus
{
    Unavailable,
    StaleSource,
    InvalidDestination,
    Obstructed,
    Unsupported,
    Prepared,
}

public sealed partial class VolumeStreamingCoordinator
{
    /// <summary>Prepares one root containing the requested terrain and a verified destination pose.</summary>
    /// <remarks>
    /// The destination is provisional until Core commits the returned publication. Prepare on a fresh masked view with
    /// the ordinary terrain staging protocol. On rejection the committed source remains readable, but movement stays held
    /// until the host restores and presents source coverage. A bound renderer must use its own preparation entry point.
    /// If provided, the static sweep must use immutable obstacles from the current committed gameplay state, return
    /// unit contact normals, and be bounded, side-effect-free and non-reentrant.
    /// </remarks>
    public PreparedBarrierPublication? TryPrepareRelocation(ulong generation, CandidateSet candidates,
        ImmutableArray<BatchDisposition> dispositions, VolumeRelocationRequest request,
        out VolumeRelocationStatus status, out PlanetWalkState? destination,
        Func<VolumeCapsule, VolumeCapsule, VolumeSweepResult>? sweepStatic = null) =>
        TryPrepareRelocation(generation, candidates, dispositions, request, out status, out destination, sweepStatic, null);

    public PreparedBarrierPublication? TryPrepareRelocation(ulong generation, CandidateSet candidates,
        ImmutableArray<BatchDisposition> dispositions, VolumeRelocationRequest request,
        out VolumeRelocationStatus status, out PlanetWalkState? destination,
        Func<VolumeCapsule, VolumeCapsule, VolumeSweepResult>? sweepStatic, VolumeRendererAuthority? renderer) =>
        PreparePublication(generation, candidates, dispositions, null, renderer, request, sweepStatic, out status, out destination);
}

internal static partial class VolumeWalker
{
    internal static VolumeRelocationStatus ValidateRelocation(VolumeGeneration generation, VolumeRelocationRequest request,
        Func<VolumeCapsule, VolumeCapsule, VolumeSweepResult>? sweepStatic, out PlanetWalkState destination)
    {
        destination = default;
        var source = new CollisionVector(request.SourcePlayer.Position);
        var foot = new CollisionVector(request.Destination);
        if (!foot.Finite || !double.IsFinite(foot.Length) ||
            foot.Length <= Height + VolumeRelocationRequest.MaximumSettleMeters ||
            (foot - source).Length > VolumeRelocationRequest.MaximumDistanceMeters)
            return VolumeRelocationStatus.InvalidDestination;

        var up = foot / foot.Length;
        var heading = new CollisionVector(request.SourcePlayer.Heading);
        heading -= up * CollisionVector.Dot(heading, up);
        if (!double.IsFinite(heading.Length) || heading.Length < 1e-9)
            return VolumeRelocationStatus.InvalidDestination;

        var start = new PlanetWalkState(foot.Planet, (heading / heading.Length).Planet, default);
        var limits = VolumeQueryLimits.Default;
        var remaining = limits.TriangleTests;
        var clearance = Query(start, default);
        if (clearance.Status != VolumeQueryStatus.Clear)
            return ClearanceFailure(clearance.Status);

        var down = up * -VolumeRelocationRequest.MaximumSettleMeters;
        var support = Query(start, down);
        if (support.Status is not (VolumeQueryStatus.Clear or VolumeQueryStatus.Contact))
            return ClearanceFailure(support.Status);
        if (support.Status == VolumeQueryStatus.Clear ||
            CollisionVector.Dot(new CollisionVector(support.Normal), up) < Walkable)
            return VolumeRelocationStatus.Unsupported;

        // Stop just above the first contact and recheck the reconstructed body at its published position.
        var settled = foot + down * Math.Max(0, support.Fraction - 0.001 / VolumeRelocationRequest.MaximumSettleMeters);
        if ((settled - source).Length > VolumeRelocationRequest.MaximumDistanceMeters)
            return VolumeRelocationStatus.InvalidDestination;
        var candidate = start with { Position = settled.Planet };
        clearance = Query(candidate, default);
        if (clearance.Status != VolumeQueryStatus.Clear)
            return ClearanceFailure(clearance.Status);

        destination = candidate;
        return VolumeRelocationStatus.Prepared;

        VolumeSweepResult Query(PlanetWalkState pose, CollisionVector delta)
        {
            if (remaining <= 0)
                return new(VolumeQueryStatus.BudgetExceeded, 0, default, generation.Info, 0);
            var body = Body(pose);
            var result = VolumeCollision.Sweep(generation, body, delta.Planet,
                new VolumeQueryLimits(remaining, limits.Iterations), requireFine: true);
            remaining -= result.TriangleTests;
            if (sweepStatic is null)
                return result;
            var end = new VolumeCapsule((new CollisionVector(body.A) + delta).Planet,
                (new CollisionVector(body.B) + delta).Planet, body.Radius);
            return CombineStatic(result, body, end, sweepStatic);
        }
    }

    private static VolumeRelocationStatus ClearanceFailure(VolumeQueryStatus status) =>
        status is VolumeQueryStatus.Contact or VolumeQueryStatus.InitialOverlap
            ? VolumeRelocationStatus.Obstructed : VolumeRelocationStatus.Unavailable;
}
