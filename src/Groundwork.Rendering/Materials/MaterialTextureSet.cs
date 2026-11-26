namespace Groundwork.Rendering.Materials;

/// <summary>Optional cooked texture references for the six opaque PBR inputs.</summary>
public sealed record MaterialTextureSet
{
    public static MaterialTextureSet Empty { get; } = new();

    public MaterialTextureSet(
        MaterialTextureReference? baseColor = null,
        MaterialTextureReference? emissive = null,
        MaterialTextureReference? roughness = null,
        MaterialTextureReference? metallic = null,
        MaterialTextureReference? ambientOcclusion = null,
        MaterialTextureReference? normal = null)
    {
        BaseColor = RequireSemantic(baseColor, MaterialTextureSemantic.BaseColor, nameof(baseColor));
        Emissive = RequireSemantic(emissive, MaterialTextureSemantic.Emissive, nameof(emissive));
        Roughness = RequireSemantic(roughness, MaterialTextureSemantic.Roughness, nameof(roughness));
        Metallic = RequireSemantic(metallic, MaterialTextureSemantic.Metallic, nameof(metallic));
        AmbientOcclusion = RequireSemantic(ambientOcclusion, MaterialTextureSemantic.AmbientOcclusion, nameof(ambientOcclusion));
        Normal = RequireSemantic(normal, MaterialTextureSemantic.Normal, nameof(normal));
    }

    public MaterialTextureReference? BaseColor { get; }
    public MaterialTextureReference? Emissive { get; }
    public MaterialTextureReference? Roughness { get; }
    public MaterialTextureReference? Metallic { get; }
    public MaterialTextureReference? AmbientOcclusion { get; }
    public MaterialTextureReference? Normal { get; }

    /// <summary>Whether the linear normal texture stores scalar roughness in alpha.</summary>
    public bool RoughnessInNormalAlpha { get; private init; }

    /// <summary>Uses RGB for the normal and alpha for roughness in one linear RGBA texture.</summary>
    public static MaterialTextureSet FromPackedNormalRoughness(MaterialTextureReference normal,
        MaterialTextureReference? baseColor = null, MaterialTextureReference? emissive = null,
        MaterialTextureReference? metallic = null, MaterialTextureReference? ambientOcclusion = null)
    {
        ArgumentNullException.ThrowIfNull(normal);
        return new(baseColor, emissive, metallic: metallic, ambientOcclusion: ambientOcclusion, normal: normal)
        {
            RoughnessInNormalAlpha = true,
        };
    }

    private static MaterialTextureReference? RequireSemantic(
        MaterialTextureReference? texture, MaterialTextureSemantic semantic, string parameterName)
    {
        if (texture is not null && texture.Semantic != semantic)
            throw new ArgumentException($"The texture must have the {semantic} semantic.", parameterName);

        return texture;
    }
}
