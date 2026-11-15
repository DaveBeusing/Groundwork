using System.Numerics;

namespace Groundwork.Rendering.Materials;

/// <summary>Linear-light parameters and optional texture references for an opaque metallic-roughness material.</summary>
public sealed record PbrMaterialDescriptor
{
    public PbrMaterialDescriptor(Vector3 baseColor, float metallic, float roughness,
        float ambientOcclusion, Vector3 emissive, float normalStrength, MaterialTextureSet? textures = null)
    {
        if (!InUnitRange(baseColor.X) || !InUnitRange(baseColor.Y) || !InUnitRange(baseColor.Z))
            throw new ArgumentOutOfRangeException(nameof(baseColor), "Each linear base color channel must be finite and in [0, 1].");
        if (!InUnitRange(metallic))
            throw new ArgumentOutOfRangeException(nameof(metallic), "Metallic must be finite and in [0, 1].");
        if (!InUnitRange(roughness))
            throw new ArgumentOutOfRangeException(nameof(roughness), "Roughness must be finite and in [0, 1].");
        if (!InUnitRange(ambientOcclusion))
            throw new ArgumentOutOfRangeException(nameof(ambientOcclusion), "Ambient occlusion must be finite and in [0, 1].");
        if (!RenderMesh.IsFinite(emissive) || emissive.X < 0 || emissive.Y < 0 || emissive.Z < 0)
            throw new ArgumentOutOfRangeException(nameof(emissive), "Each linear HDR emissive channel must be finite and nonnegative.");
        if (!float.IsFinite(normalStrength) || normalStrength < 0)
            throw new ArgumentOutOfRangeException(nameof(normalStrength), "Normal strength must be finite and nonnegative.");

        BaseColor = baseColor;
        Metallic = metallic;
        Roughness = roughness;
        AmbientOcclusion = ambientOcclusion;
        Emissive = emissive;
        NormalStrength = normalStrength;
        Textures = textures ?? MaterialTextureSet.Empty;
    }

    public Vector3 BaseColor { get; }
    public float Metallic { get; }
    public float Roughness { get; }
    public float AmbientOcclusion { get; }
    public Vector3 Emissive { get; }
    public float NormalStrength { get; }
    public MaterialTextureSet Textures { get; }

    private static bool InUnitRange(float value) => value is >= 0 and <= 1;
}
