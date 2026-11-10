using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Groundwork.Voxels;

/// <summary>A noise scale in lattice cells and a nonnegative elevation amplitude in density units.</summary>
public readonly record struct TerrainReliefLayer(int ScaleCells, int Amplitude);

/// <summary>Game-supplied parameters for the version 1 landform field. Elevations use 1/256 cell units.</summary>
public sealed class PlanetLandformRecipe
{
    public PlanetLandformRecipe(int continentScaleCells, int lowElevation, int highElevation,
        TerrainReliefLayer mountains, TerrainReliefLayer valleys, TerrainReliefLayer detail,
        int materialScaleCells, ushort baseMaterial, ushort alternateMaterial)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(continentScaleCells);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lowElevation, highElevation);
        ValidateLayer(mountains, nameof(mountains));
        ValidateLayer(valleys, nameof(valleys));
        ValidateLayer(detail, nameof(detail));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(materialScaleCells);
        ArgumentOutOfRangeException.ThrowIfZero(baseMaterial);
        ArgumentOutOfRangeException.ThrowIfZero(alternateMaterial);

        ContinentScaleCells = continentScaleCells;
        LowElevation = lowElevation;
        HighElevation = highElevation;
        Mountains = mountains;
        Valleys = valleys;
        Detail = detail;
        MaterialScaleCells = materialScaleCells;
        BaseMaterial = baseMaterial;
        AlternateMaterial = alternateMaterial;

        // PLF1 followed by the fields in constructor order, signed Int32 and unsigned UInt16, little endian.
        Span<byte> canonical = stackalloc byte[48];
        "PLF1"u8.CopyTo(canonical);
        BinaryPrimitives.WriteInt32LittleEndian(canonical[4..], continentScaleCells);
        BinaryPrimitives.WriteInt32LittleEndian(canonical[8..], lowElevation);
        BinaryPrimitives.WriteInt32LittleEndian(canonical[12..], highElevation);
        WriteLayer(canonical[16..], mountains);
        WriteLayer(canonical[24..], valleys);
        WriteLayer(canonical[32..], detail);
        BinaryPrimitives.WriteInt32LittleEndian(canonical[40..], materialScaleCells);
        BinaryPrimitives.WriteUInt16LittleEndian(canonical[44..], baseMaterial);
        BinaryPrimitives.WriteUInt16LittleEndian(canonical[46..], alternateMaterial);
        ParameterSha256 = Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    public int ContinentScaleCells { get; }
    public int LowElevation { get; }
    public int HighElevation { get; }
    public TerrainReliefLayer Mountains { get; }
    public TerrainReliefLayer Valleys { get; }
    public TerrainReliefLayer Detail { get; }
    public int MaterialScaleCells { get; }
    public ushort BaseMaterial { get; }
    public ushort AlternateMaterial { get; }
    public string ParameterSha256 { get; }

    private static void ValidateLayer(TerrainReliefLayer layer, string parameter)
    {
        if (layer.ScaleCells <= 0 || layer.Amplitude < 0)
            throw new ArgumentOutOfRangeException(parameter, "Expected a positive scale and nonnegative amplitude.");
    }

    private static void WriteLayer(Span<byte> destination, TerrainReliefLayer layer)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, layer.ScaleCells);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], layer.Amplitude);
    }
}
