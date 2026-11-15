namespace Groundwork.Rendering.Materials;

/// <summary>The material input a texture supplies.</summary>
public enum MaterialTextureSemantic
{
    BaseColor = 1,
    Emissive,
    Roughness,
    Metallic,
    AmbientOcclusion,
    /// <summary>Positive green points along the bitangent, computed as handedness * cross(normal, tangent).</summary>
    Normal,
}

/// <summary>How the shader interprets a cooked texture's channels.</summary>
public enum MaterialTextureColorSpace
{
    Linear = 1,
    Srgb,
}

/// <summary>Identifies a cooked texture and specifies the interpretation required by its material input.</summary>
public sealed record MaterialTextureReference
{
    public MaterialTextureReference(MaterialTextureSemantic semantic, string assetId)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        if (assetId.Length != 71 || !assetId.StartsWith("sha256:", StringComparison.Ordinal))
            throw new ArgumentException("A cooked texture ID must be sha256: followed by 64 lowercase hex digits.", nameof(assetId));

        foreach (var digit in assetId.AsSpan(7))
        {
            if (digit is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new ArgumentException("A cooked texture ID must be sha256: followed by 64 lowercase hex digits.", nameof(assetId));
        }

        ExpectedColorSpace = semantic switch
        {
            MaterialTextureSemantic.BaseColor or MaterialTextureSemantic.Emissive => MaterialTextureColorSpace.Srgb,
            MaterialTextureSemantic.Roughness or MaterialTextureSemantic.Metallic or MaterialTextureSemantic.AmbientOcclusion
                or MaterialTextureSemantic.Normal => MaterialTextureColorSpace.Linear,
            _ => throw new ArgumentOutOfRangeException(nameof(semantic)),
        };
        Semantic = semantic;
        AssetId = assetId;
    }

    public MaterialTextureSemantic Semantic { get; }
    public string AssetId { get; }
    public MaterialTextureColorSpace ExpectedColorSpace { get; }

    /// <summary>Checks cooked metadata before binding the texture to this material input.</summary>
    public bool MatchesBinding(string assetId, MaterialTextureColorSpace colorSpace) =>
        string.Equals(AssetId, assetId, StringComparison.Ordinal) && colorSpace == ExpectedColorSpace;
}
