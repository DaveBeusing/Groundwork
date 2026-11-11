using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Groundwork.Voxels;

/// <summary>Content parameters for generator V2. Lengths are in finest lattice cells.</summary>
public sealed class PlanetGeologyRecipe
{
    public PlanetGeologyRecipe(PlanetLandformRecipe landform, int shallowBandCells, int deepBandCells,
        int strataNoiseCells, int strataReliefCells, int oreNoiseCells, int oreThreshold,
        int minimumCaveDepth, int maximumCaveDepth, int portalChance, int shaftChance,
        ushort shallowRock, ushort middleRock, ushort deepRock, ushort ore)
    {
        ArgumentNullException.ThrowIfNull(landform);
        ArgumentOutOfRangeException.ThrowIfLessThan(shallowBandCells, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(deepBandCells, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deepBandCells, shallowBandCells);
        ArgumentOutOfRangeException.ThrowIfLessThan(strataNoiseCells, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(strataNoiseCells, 4096);
        ArgumentOutOfRangeException.ThrowIfNegative(strataReliefCells);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(strataReliefCells, shallowBandCells - 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(oreNoiseCells, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(oreNoiseCells, 4096);
        ArgumentOutOfRangeException.ThrowIfNegative(oreThreshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(oreThreshold, 32768);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumCaveDepth, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCaveDepth, 20);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCaveDepth, 4096);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumCaveDepth, maximumCaveDepth - 16);
        ArgumentOutOfRangeException.ThrowIfNegative(portalChance);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(portalChance, 65536);
        ArgumentOutOfRangeException.ThrowIfNegative(shaftChance);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(shaftChance, 65536);
        ushort[] palette = [shallowRock, middleRock, deepRock, ore];
        if (palette.Contains((ushort)0) || palette.Distinct().Count() != palette.Length)
            throw new ArgumentException("The four geology material IDs must be distinct and nonzero.", nameof(ore));

        Landform = landform;
        ShallowBandCells = shallowBandCells;
        DeepBandCells = deepBandCells;
        StrataNoiseCells = strataNoiseCells;
        StrataReliefCells = strataReliefCells;
        OreNoiseCells = oreNoiseCells;
        OreThreshold = oreThreshold;
        MinimumCaveDepth = minimumCaveDepth;
        MaximumCaveDepth = maximumCaveDepth;
        PortalChance = portalChance;
        ShaftChance = shaftChance;
        ShallowRock = shallowRock;
        MiddleRock = middleRock;
        DeepRock = deepRock;
        Ore = ore;

        Span<byte> canonical = stackalloc byte[84];
        "PGL2"u8.CopyTo(canonical);
        Convert.FromHexString(landform.ParameterSha256).CopyTo(canonical[4..36]);
        ReadOnlySpan<int> parameters = [shallowBandCells, deepBandCells, strataNoiseCells, strataReliefCells,
            oreNoiseCells, oreThreshold, minimumCaveDepth, maximumCaveDepth, portalChance, shaftChance];
        for (var i = 0; i < parameters.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(36 + i * sizeof(int))..], parameters[i]);
        for (var i = 0; i < palette.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(canonical[(76 + i * sizeof(ushort))..], palette[i]);
        ParameterSha256 = Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    public PlanetLandformRecipe Landform { get; }
    public int ShallowBandCells { get; }
    public int DeepBandCells { get; }
    public int StrataNoiseCells { get; }
    public int StrataReliefCells { get; }
    public int OreNoiseCells { get; }
    /// <summary>Minimum signed noise value for ore. 32768 disables ore.</summary>
    public int OreThreshold { get; }
    public int MinimumCaveDepth { get; }
    public int MaximumCaveDepth { get; }
    /// <summary>Number of accepted values in the upper 16 bits of a portal hash.</summary>
    public int PortalChance { get; }
    /// <summary>Number of accepted values in the upper 16 bits of a shaft hash.</summary>
    public int ShaftChance { get; }
    public ushort ShallowRock { get; }
    public ushort MiddleRock { get; }
    public ushort DeepRock { get; }
    public ushort Ore { get; }
    public string ParameterSha256 { get; }
}
