namespace Groundwork.Rendering.Textures;

public enum TextureFormat
{
    Rgba8 = 1,
    Bc1 = 2
}

public enum TextureColorSpace
{
    Linear = 1,
    Srgb = 2
}

public enum TextureSemantic
{
    Color = 1,
    Data = 2,
    Normal = 3
}

public enum TextureSourceFormat
{
    Tga32 = 1,
    Bc1Blocks = 2
}

public enum TextureCookError
{
    None,
    SourceLimit,
    DecodedLimit,
    CookedLimit,
    DimensionLimit,
    MipLimit,
    InvalidSource,
    UnsupportedSource,
    InvalidMetadata,
    InvalidLayout,
    UnsupportedCookedVersion,
    DigestMismatch
}

/// <summary>Per-operation limits for the complete input, decoded texels, and canonical cooked bytes.</summary>
public sealed class TextureLimits
{
    public TextureLimits(int maxSourceBytes, int maxDecodedBytes, int maxCookedBytes, int maxDimension, int maxMipCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSourceBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCookedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMipCount);

        MaxSourceBytes = maxSourceBytes;
        MaxDecodedBytes = maxDecodedBytes;
        MaxCookedBytes = maxCookedBytes;
        MaxDimension = maxDimension;
        MaxMipCount = maxMipCount;
    }

    public int MaxSourceBytes { get; }
    public int MaxDecodedBytes { get; }
    public int MaxCookedBytes { get; }
    public int MaxDimension { get; }
    public int MaxMipCount { get; }
}

public readonly record struct TextureCookRecipe(TextureColorSpace ColorSpace, TextureSemantic Semantic, int MipCount);

/// <summary>A tightly packed mip. Offset is relative to the payload and row bytes count bytes per texel or block row.</summary>
public readonly record struct TextureMip(int Width, int Height, int Offset, int RowBytes, int ByteLength);

/// <summary>An immutable, validated 2D single-layer texture, including its canonical manifest and payload.</summary>
public sealed class CookedTexture
{
    private readonly byte[] _canonicalBytes;
    private readonly TextureMip[] _mips;
    private readonly int _payloadOffset;

    internal CookedTexture(byte[] canonicalBytes, int payloadOffset, TextureMip[] mips,
        TextureSourceFormat sourceFormat, TextureFormat format, TextureColorSpace colorSpace,
        TextureSemantic semantic, string sourceDigest, string assetId)
    {
        _canonicalBytes = canonicalBytes;
        _payloadOffset = payloadOffset;
        _mips = mips;
        SourceFormat = sourceFormat;
        Format = format;
        ColorSpace = colorSpace;
        Semantic = semantic;
        SourceDigest = sourceDigest;
        AssetId = assetId;
    }

    public string AssetId { get; }
    public string SourceDigest { get; }
    public TextureSourceFormat SourceFormat { get; }
    public TextureFormat Format { get; }
    public TextureColorSpace ColorSpace { get; }
    public TextureSemantic Semantic { get; }
    public int Width => _mips[0].Width;
    public int Height => _mips[0].Height;
    public ReadOnlySpan<TextureMip> Mips => _mips;
    public ReadOnlySpan<byte> Payload => _canonicalBytes.AsSpan(_payloadOffset);
    public ReadOnlySpan<byte> CanonicalBytes => _canonicalBytes;
}
