using Groundwork.Voxels;

namespace Groundwork.Persistence;

public readonly record struct PlanetSaveRecipe(uint GeneratorId, uint GeneratorVersion, uint Seed);

/// <summary>A grounded craft pose in planet-centered coordinates.</summary>
public readonly record struct PlanetLandedPose(PlanetVector Position, PlanetFlightOrientation Orientation)
{
    internal bool IsValid
    {
        get
        {
            var (x, y, z) = Position;
            var radiusSquared = x * x + y * y + z * z;
            const double approachRadius = PlanetFlight.ReferenceRadius + 12;
            return radiusSquared > PlanetSurfaceWalker.BodyRadius * PlanetSurfaceWalker.BodyRadius &&
                radiusSquared < approachRadius * approachRadius && Orientation.IsUnit;
        }
    }
}

/// <summary>A signed millimeter change relative to the recipe at a canonical terrain sample.</summary>
public readonly record struct PlanetHeightEdit(PlanetSampleKey Sample, short DeltaMillimeters)
{
    internal bool IsValid => DeltaMillimeters is >= -500 and <= 500 and not 0 &&
        PlanetCoordinates.IsCanonicalSample(Sample);

    internal static int Compare(PlanetSampleKey left, PlanetSampleKey right)
    {
        var x = left.X.CompareTo(right.X);
        if (x != 0)
            return x;
        var y = left.Y.CompareTo(right.Y);
        return y != 0 ? y : left.Z.CompareTo(right.Z);
    }
}

/// <summary>An immutable planet recipe, landed pose and canonical edit overlay.</summary>
public sealed class PlanetCheckpoint
{
    public const int MaximumEdits = 4096;
    private readonly PlanetHeightEdit[] _edits;

    public PlanetCheckpoint(SaveFrameIdentity identity, PlanetSaveRecipe recipe, PlanetLandedPose pose,
        ReadOnlySpan<PlanetHeightEdit> edits)
    {
        SaveCodec.ValidateIdentity(identity);
        if (recipe.GeneratorId == 0 || recipe.GeneratorVersion == 0 || !pose.IsValid || edits.Length > MaximumEdits)
            throw new InvalidDataException("Invalid planet recipe, landed pose or edit count.");
        _edits = edits.ToArray();
        for (var i = 0; i < _edits.Length; i++)
        {
            if (!_edits[i].IsValid || (i > 0 && PlanetHeightEdit.Compare(_edits[i - 1].Sample, _edits[i].Sample) >= 0))
                throw new InvalidDataException("Planet edits must be valid and strictly ordered by sample.");
        }

        (Identity, Recipe, Pose) = (identity, recipe, pose);
    }

    public SaveFrameIdentity Identity { get; }
    public PlanetSaveRecipe Recipe { get; }
    public PlanetLandedPose Pose { get; }
    public ReadOnlySpan<PlanetHeightEdit> Edits => _edits;

    /// <summary>Gets the delta to apply to the base recipe when rebuilding any LOD.</summary>
    public short GetHeightDelta(PlanetSampleKey sample)
    {
        var low = 0;
        var high = _edits.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var comparison = PlanetHeightEdit.Compare(_edits[middle].Sample, sample);
            if (comparison == 0)
                return _edits[middle].DeltaMillimeters;
            if (comparison < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return 0;
    }
}

/// <summary>The decode outcome, with verified identity and schema available for unsupported frames.</summary>
public readonly record struct PlanetCheckpointReadResult(
    SaveReadStatus Status, SaveFrameIdentity? Identity, ushort? SchemaVersion, PlanetCheckpoint? Checkpoint = null)
{
    /// <summary>The structurally verified recipe, including when its generator is unavailable.</summary>
    public PlanetSaveRecipe? Recipe { get; init; }
}
