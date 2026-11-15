namespace Groundwork.Voxels;

/// <summary>A material ID from an already available canonical surface control sample.</summary>
public readonly record struct PlanetSurfaceMaterialSample(PlanetLatticeSample Cell, ushort Material);

/// <summary>
/// Selects a material palette input for the orbit proxy using shared canonical samples.
/// A caller supplies samples from one coherent terrain snapshot or shared immutable
/// generator evaluation. The selector has no world, generator, or brick-admission dependency.
/// </summary>
public static class PlanetOrbitMaterialSelector
{
    public const int MaximumSamples = 8;

    /// <summary>
    /// Selects the nearest supplied material cell within one lattice step per axis, with
    /// ties resolved by ascending signed X, Y, then Z. Supply only shared geometric
    /// controls. If none are within range, the proxy material is selected. Pass an empty
    /// span on an unshared proxy surface.
    /// Conflicting IDs for one cell or samples from another planet are invalid.
    /// </summary>
    public static ushort Select(PlanetLatticeSample control, ReadOnlySpan<PlanetSurfaceMaterialSample> samples,
        ushort proxyMaterial)
    {
        ArgumentOutOfRangeException.ThrowIfZero(proxyMaterial);
        if (control.WorldId == Guid.Empty || control.PlanetId == Guid.Empty)
            throw new ArgumentException("The control requires a world and planet identity.", nameof(control));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(samples.Length, MaximumSamples);
        PlanetDensityField.ValidateCoordinate(control.Vertex);

        var bestDistance = int.MaxValue;
        var bestCell = default(LatticeCoordinate);
        var material = proxyMaterial;
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            var cell = sample.Cell.Vertex;
            if (sample.Material == 0 || sample.Cell.WorldId != control.WorldId || sample.Cell.PlanetId != control.PlanetId)
                throw new ArgumentException("Each sample needs a nonzero ID and the control's world and planet.", nameof(samples));
            PlanetDensityField.ValidateCoordinate(cell);
            for (var j = 0; j < i; j++)
                if (samples[j].Cell == sample.Cell && samples[j].Material != sample.Material)
                    throw new ArgumentException("A canonical cell contains conflicting material IDs.", nameof(samples));

            var x = cell.X - control.Vertex.X;
            var y = cell.Y - control.Vertex.Y;
            var z = cell.Z - control.Vertex.Z;
            if (Math.Abs(x) > 1 || Math.Abs(y) > 1 || Math.Abs(z) > 1)
                continue;

            var distance = (int)(x * x + y * y + z * z);
            if (distance < bestDistance || (distance == bestDistance && Before(cell, bestCell)))
            {
                bestDistance = distance;
                bestCell = cell;
                material = sample.Material;
            }
        }
        return material;
    }

    private static bool Before(LatticeCoordinate a, LatticeCoordinate b) =>
        a.X < b.X || (a.X == b.X && (a.Y < b.Y || (a.Y == b.Y && a.Z < b.Z)));
}
