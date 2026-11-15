using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Groundwork.Rendering.Textures;

/// <summary>Cooks and validates the portable texture envelope using caller-owned spans and explicit limits.</summary>
public static class TextureCooker
{
    public const int ManifestVersion = 1;
    public const int ImporterVersion = 1;
    public const int RecipeVersion = 1;
    public const string ImporterName = "Groundwork.TextureCooker";

    private const int TgaHeaderLength = 18;
    private const int MaximumMipCount = 31; // A positive Int32 dimension can halve at most 30 times.
    private const int MipRecordLength = 5 * sizeof(int);
    private const string DigestPrefix = "sha256:";
    private static ReadOnlySpan<byte> Magic => "GWTX"u8;
    private static ReadOnlySpan<byte> ImporterNameBytes => "Groundwork.TextureCooker"u8;
    private static int FixedLength => Magic.Length + 15 * sizeof(int) + ImporterNameBytes.Length + SHA256.HashSizeInBytes;

    /// <summary>Cooks a top-left 32-bit uncompressed true-color TGA. Keep the input stable for the call.</summary>
    public static TextureCookError CookTga32(ReadOnlySpan<byte> source, TextureCookRecipe recipe, TextureLimits limits,
        out CookedTexture? texture)
    {
        ArgumentNullException.ThrowIfNull(limits);
        texture = null;
        if (source.Length > limits.MaxSourceBytes)
            return TextureCookError.SourceLimit;
        if (source.Length < TgaHeaderLength)
            return TextureCookError.InvalidSource;
        if (source[0] != 0 || source[1] != 0 || source[2] != 2 ||
            source[3..12].ContainsAnyExcept((byte)0) || source[16] != 32 || source[17] != 0x28)
            return TextureCookError.UnsupportedSource;

        const TextureFormat format = TextureFormat.Rgba8;
        const TextureSourceFormat sourceFormat = TextureSourceFormat.Tga32;
        int width = BinaryPrimitives.ReadUInt16LittleEndian(source[12..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(source[14..]);
        Span<TextureMip> layout = stackalloc TextureMip[MaximumMipCount];
        var error = ValidateRecipe(recipe);
        if (error != TextureCookError.None)
            return error;
        error = CalculateLayout(format, width, height, recipe.MipCount, limits, layout, out int payloadLength);
        if (error != TextureCookError.None)
            return error;
        if (source.Length - TgaHeaderLength != layout[0].ByteLength)
            return TextureCookError.InvalidSource;
        if (payloadLength > limits.MaxDecodedBytes)
            return TextureCookError.DecodedLimit;

        layout = layout[..recipe.MipCount];
        byte[] encoded = WriteManifest(source, sourceFormat, format, recipe, layout, payloadLength, out int payloadOffset);
        var payload = encoded.AsSpan(payloadOffset);
        CopyBgraToRgba(source[TgaHeaderLength..], payload[..layout[0].ByteLength]);
        for (int level = 1; level < layout.Length; level++)
            TextureMipFilter.Generate(payload, layout[level - 1], layout[level], recipe);

        texture = MakeTexture(encoded, payloadOffset, layout, sourceFormat, format, recipe, ComputeAssetId(encoded));
        return TextureCookError.None;
    }

    /// <summary>Cooks a complete supplied BC1 mip prefix in level order. Keep the input stable for the call.</summary>
    public static TextureCookError CookBc1(ReadOnlySpan<byte> source, int width, int height, TextureCookRecipe recipe,
        TextureLimits limits, out CookedTexture? texture)
    {
        ArgumentNullException.ThrowIfNull(limits);
        texture = null;
        if (source.Length > limits.MaxSourceBytes)
            return TextureCookError.SourceLimit;

        const TextureFormat format = TextureFormat.Bc1;
        const TextureSourceFormat sourceFormat = TextureSourceFormat.Bc1Blocks;
        Span<TextureMip> layout = stackalloc TextureMip[MaximumMipCount];
        var error = ValidateRecipe(recipe);
        if (error != TextureCookError.None)
            return error;
        error = CalculateLayout(format, width, height, recipe.MipCount, limits, layout, out int payloadLength);
        if (error != TextureCookError.None)
            return error;
        if (source.Length != payloadLength)
            return TextureCookError.InvalidSource;

        layout = layout[..recipe.MipCount];
        byte[] encoded = WriteManifest(source, sourceFormat, format, recipe, layout, payloadLength, out int payloadOffset);
        source.CopyTo(encoded.AsSpan(payloadOffset));
        texture = MakeTexture(encoded, payloadOffset, layout, sourceFormat, format, recipe, ComputeAssetId(encoded));
        return TextureCookError.None;
    }

    /// <summary>Validates a cooked envelope and its expected asset ID before taking an immutable copy.</summary>
    /// <remarks>Keep the supplied bytes and ID stable for the call. This operation performs no source decoding.</remarks>
    public static TextureCookError ReadCooked(ReadOnlySpan<byte> encoded, ReadOnlySpan<char> expectedAssetId,
        TextureLimits limits, out CookedTexture? texture)
    {
        ArgumentNullException.ThrowIfNull(limits);
        texture = null;
        if (encoded.Length > limits.MaxCookedBytes)
            return TextureCookError.CookedLimit;
        if (encoded.Length < FixedLength || !encoded[..Magic.Length].SequenceEqual(Magic))
            return TextureCookError.InvalidLayout;

        int offset = Magic.Length;
        if (ReadInt(encoded, ref offset) != ManifestVersion ||
            ReadInt(encoded, ref offset) != ImporterNameBytes.Length ||
            !encoded.Slice(offset, ImporterNameBytes.Length).SequenceEqual(ImporterNameBytes))
            return TextureCookError.UnsupportedCookedVersion;
        offset += ImporterNameBytes.Length;
        if (ReadInt(encoded, ref offset) != ImporterVersion)
            return TextureCookError.UnsupportedCookedVersion;
        var sourceFormat = (TextureSourceFormat)ReadInt(encoded, ref offset);
        if (ReadInt(encoded, ref offset) != SHA256.HashSizeInBytes)
            return TextureCookError.InvalidLayout;
        offset += SHA256.HashSizeInBytes;
        if (ReadInt(encoded, ref offset) != RecipeVersion)
            return TextureCookError.UnsupportedCookedVersion;

        var format = (TextureFormat)ReadInt(encoded, ref offset);
        var colorSpace = (TextureColorSpace)ReadInt(encoded, ref offset);
        var semantic = (TextureSemantic)ReadInt(encoded, ref offset);
        int width = ReadInt(encoded, ref offset);
        int height = ReadInt(encoded, ref offset);
        int layers = ReadInt(encoded, ref offset);
        int mipCount = ReadInt(encoded, ref offset);
        int layoutLength = ReadInt(encoded, ref offset);
        if (layers != 1 || (sourceFormat, format) is not
            ((TextureSourceFormat.Tga32, TextureFormat.Rgba8) or (TextureSourceFormat.Bc1Blocks, TextureFormat.Bc1)))
            return TextureCookError.InvalidMetadata;

        var recipe = new TextureCookRecipe(colorSpace, semantic, mipCount);
        var error = ValidateRecipe(recipe);
        if (error != TextureCookError.None)
            return error;
        Span<TextureMip> layout = stackalloc TextureMip[MaximumMipCount];
        error = CalculateLayout(format, width, height, mipCount, limits, layout, out int payloadLength);
        if (error != TextureCookError.None)
            return error;
        if (layoutLength != mipCount * MipRecordLength ||
            encoded.Length != FixedLength + layoutLength + payloadLength)
            return TextureCookError.InvalidLayout;

        layout = layout[..mipCount];
        foreach (var mip in layout)
        {
            var declared = new TextureMip(ReadInt(encoded, ref offset), ReadInt(encoded, ref offset),
                ReadInt(encoded, ref offset), ReadInt(encoded, ref offset), ReadInt(encoded, ref offset));
            if (declared != mip)
                return TextureCookError.InvalidLayout;
        }
        if (ReadInt(encoded, ref offset) != payloadLength)
            return TextureCookError.InvalidLayout;

        if (!expectedAssetId.StartsWith(DigestPrefix, StringComparison.Ordinal) ||
            expectedAssetId.Length != DigestPrefix.Length + SHA256.HashSizeInBytes * 2)
            return TextureCookError.DigestMismatch;
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(encoded, digest);
        Span<char> hex = stackalloc char[SHA256.HashSizeInBytes * 2];
        Convert.TryToHexStringLower(digest, hex, out _);
        if (!expectedAssetId[DigestPrefix.Length..].SequenceEqual(hex))
            return TextureCookError.DigestMismatch;

        texture = MakeTexture(encoded.ToArray(), offset, layout, sourceFormat, format, recipe, new string(expectedAssetId));
        return TextureCookError.None;
    }

    private static TextureCookError ValidateRecipe(TextureCookRecipe recipe)
    {
        if (recipe.ColorSpace is not (TextureColorSpace.Linear or TextureColorSpace.Srgb) ||
            recipe.Semantic is not (TextureSemantic.Color or TextureSemantic.Data or TextureSemantic.Normal) ||
            (recipe.ColorSpace == TextureColorSpace.Srgb && recipe.Semantic != TextureSemantic.Color))
            return TextureCookError.InvalidMetadata;

        return TextureCookError.None;
    }

    private static TextureCookError CalculateLayout(TextureFormat format, int width, int height, int mipCount,
        TextureLimits limits, Span<TextureMip> layout, out int payloadLength)
    {
        payloadLength = 0;
        int maxDimension = format == TextureFormat.Rgba8 ? Math.Min(ushort.MaxValue, limits.MaxDimension) : limits.MaxDimension;
        if (width <= 0 || height <= 0 || width > maxDimension || height > maxDimension)
            return TextureCookError.DimensionLimit;
        int fullMipCount = 32 - int.LeadingZeroCount(Math.Max(width, height));
        if (mipCount <= 0 || mipCount > limits.MaxMipCount || mipCount > fullMipCount)
            return TextureCookError.MipLimit;

        int payloadOffset = FixedLength + mipCount * MipRecordLength;
        int budget = Math.Min(limits.MaxCookedBytes, Array.MaxLength) - payloadOffset;
        for (int level = 0; level < mipCount; level++)
        {
            long rowBytes = format == TextureFormat.Bc1 ? ((width + 3L) / 4) * 8 : width * 4L;
            long rowCount = format == TextureFormat.Bc1 ? (height + 3L) / 4 : height;
            // Bound the row before multiplying, so even the largest declared dimensions cannot overflow.
            if (rowBytes > budget)
                return TextureCookError.CookedLimit;
            long levelLength = rowBytes * rowCount;
            if (levelLength > budget)
                return TextureCookError.CookedLimit;

            int length = (int)levelLength;
            layout[level] = new TextureMip(width, height, payloadLength, (int)rowBytes, length);
            payloadLength += length;
            budget -= length;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return TextureCookError.None;
    }

    private static void CopyBgraToRgba(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (int index = 0; index < source.Length; index += 4)
        {
            destination[index] = source[index + 2];
            destination[index + 1] = source[index + 1];
            destination[index + 2] = source[index];
            destination[index + 3] = source[index + 3];
        }
    }

    private static byte[] WriteManifest(ReadOnlySpan<byte> source, TextureSourceFormat sourceFormat, TextureFormat format,
        TextureCookRecipe recipe, ReadOnlySpan<TextureMip> layout, int payloadLength, out int payloadOffset)
    {
        int layoutLength = layout.Length * MipRecordLength;
        byte[] encoded = new byte[FixedLength + layoutLength + payloadLength];
        Magic.CopyTo(encoded);
        int offset = Magic.Length;
        WriteInt(encoded, ref offset, ManifestVersion);
        WriteInt(encoded, ref offset, ImporterNameBytes.Length);
        ImporterNameBytes.CopyTo(encoded.AsSpan(offset));
        offset += ImporterNameBytes.Length;
        WriteInt(encoded, ref offset, ImporterVersion);
        WriteInt(encoded, ref offset, (int)sourceFormat);
        WriteInt(encoded, ref offset, SHA256.HashSizeInBytes);
        SHA256.HashData(source, encoded.AsSpan(offset, SHA256.HashSizeInBytes));
        offset += SHA256.HashSizeInBytes;
        WriteInt(encoded, ref offset, RecipeVersion);
        WriteInt(encoded, ref offset, (int)format);
        WriteInt(encoded, ref offset, (int)recipe.ColorSpace);
        WriteInt(encoded, ref offset, (int)recipe.Semantic);
        WriteInt(encoded, ref offset, layout[0].Width);
        WriteInt(encoded, ref offset, layout[0].Height);
        WriteInt(encoded, ref offset, 1);
        WriteInt(encoded, ref offset, layout.Length);
        WriteInt(encoded, ref offset, layoutLength);
        foreach (var mip in layout)
        {
            WriteInt(encoded, ref offset, mip.Width);
            WriteInt(encoded, ref offset, mip.Height);
            WriteInt(encoded, ref offset, mip.Offset);
            WriteInt(encoded, ref offset, mip.RowBytes);
            WriteInt(encoded, ref offset, mip.ByteLength);
        }
        WriteInt(encoded, ref offset, payloadLength);
        payloadOffset = offset;
        return encoded;
    }

    private static CookedTexture MakeTexture(byte[] encoded, int payloadOffset, ReadOnlySpan<TextureMip> layout,
        TextureSourceFormat sourceFormat, TextureFormat format, TextureCookRecipe recipe, string assetId)
    {
        int digestOffset = Magic.Length + 5 * sizeof(int) + ImporterNameBytes.Length;
        string sourceDigest = DigestPrefix + Convert.ToHexStringLower(encoded.AsSpan(digestOffset, SHA256.HashSizeInBytes));
        return new CookedTexture(encoded, payloadOffset, layout.ToArray(), sourceFormat, format, recipe.ColorSpace,
            recipe.Semantic, sourceDigest, assetId);
    }

    private static string ComputeAssetId(ReadOnlySpan<byte> encoded)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(encoded, digest);
        return DigestPrefix + Convert.ToHexStringLower(digest);
    }

    private static int ReadInt(ReadOnlySpan<byte> encoded, ref int offset)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(encoded[offset..]);
        offset += sizeof(int);
        return value;
    }

    private static void WriteInt(Span<byte> encoded, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(encoded[offset..], value);
        offset += sizeof(int);
    }
}
