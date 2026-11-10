namespace Groundwork.Voxels;

/// <summary>
/// Immutable identity and lattice settings for one planet. The origin is the planet centre,
/// and the lattice uses the planet-centred Cartesian axes regardless of chart or render origin.
/// </summary>
public sealed record PlanetDescriptor
{
    public const double MaximumRadiusMeters = 6_400_000;
    public const int BrickEdge = 32;

    public PlanetDescriptor(Guid worldId, Guid planetId, ulong seed, uint generatorVersion,
        uint terrainSchemaVersion, uint materialCatalogVersion, string parameterSha256,
        double radiusMeters, double cellSizeMeters = 1)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(worldId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(planetId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfZero(generatorVersion);
        ArgumentOutOfRangeException.ThrowIfZero(terrainSchemaVersion);
        ArgumentOutOfRangeException.ThrowIfZero(materialCatalogVersion);
        ArgumentNullException.ThrowIfNull(parameterSha256);
        if (parameterSha256.Length != 64 || !parameterSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected a SHA-256 digest encoded as 64 hexadecimal characters.", nameof(parameterSha256));
        if (!double.IsFinite(radiusMeters) || radiusMeters <= 0 || radiusMeters > MaximumRadiusMeters)
            throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        // At the reference surface, retain double precision for half-cell rounding.
        if (!double.IsFinite(cellSizeMeters) || cellSizeMeters <= 0 || cellSizeMeters > radiusMeters ||
            radiusMeters / cellSizeMeters >= 0x10_0000_0000_0000L)
            throw new ArgumentOutOfRangeException(nameof(cellSizeMeters));

        WorldId = worldId;
        PlanetId = planetId;
        Seed = seed;
        GeneratorVersion = generatorVersion;
        TerrainSchemaVersion = terrainSchemaVersion;
        MaterialCatalogVersion = materialCatalogVersion;
        ParameterSha256 = parameterSha256.ToLowerInvariant();
        RadiusMeters = radiusMeters;
        CellSizeMeters = cellSizeMeters;
    }

    public Guid WorldId { get; }
    public Guid PlanetId { get; }
    public ulong Seed { get; }
    public uint GeneratorVersion { get; }
    public uint TerrainSchemaVersion { get; }
    public uint MaterialCatalogVersion { get; }
    public string ParameterSha256 { get; }
    public double RadiusMeters { get; }
    public double CellSizeMeters { get; }

    /// <summary>Quantizes authoritative double coordinates to the nearest lattice vertex, ties to even.</summary>
    public PlanetLatticeSample SampleAt(PlanetVector position) =>
        new(WorldId, PlanetId, new LatticeCoordinate(Quantize(position.X), Quantize(position.Y), Quantize(position.Z)));

    /// <summary>Projects a chart vertex to the reference sphere through the shared integer cube point.</summary>
    public PlanetVector ChartPosition(PlanetSampleKey chartSample)
    {
        var direction = PlanetCoordinates.Direction(chartSample);
        return new PlanetVector(direction.X * RadiusMeters, direction.Y * RadiusMeters, direction.Z * RadiusMeters);
    }

    public PlanetLatticeSample SampleAt(PlanetPatchKey patch, int u, int v) =>
        SampleAt(ChartPosition(PlanetCoordinates.Sample(patch, u, v)));

    public PlanetLatticeSample SampleAt(PlanetPatchKey patch, PlanetEdge edge, int index) =>
        SampleAt(ChartPosition(PlanetCoordinates.SampleEdge(patch, edge, index)));

    private long Quantize(double axis)
    {
        const double Int64UpperExclusive = 9_223_372_036_854_775_808d;
        var rounded = Math.Round(axis / CellSizeMeters, MidpointRounding.ToEven);
        if (!double.IsFinite(rounded) || rounded < -Int64UpperExclusive || rounded >= Int64UpperExclusive)
            throw new ArgumentOutOfRangeException(nameof(axis), "Coordinate does not fit the signed 64-bit lattice.");
        return checked((long)rounded);
    }
}
