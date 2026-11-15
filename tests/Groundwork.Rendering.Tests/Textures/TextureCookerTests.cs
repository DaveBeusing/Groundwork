using System.Buffers.Binary;
using System.Security.Cryptography;
using Groundwork.Rendering.Textures;

namespace Groundwork.Rendering.Tests.Textures;

[TestClass]
public sealed class TextureCookerTests
{
    private const string SrgbAssetId = "sha256:35ce506f2f83e8826edee95f316d56f9e1ba18d53b3987619b3e31765651239a";
    private const string SrgbSourceDigest = "sha256:b68cd5b4c573625d4d3b857ce24574944029eaf171fc6932554c25cdf81a0b10";
    private const string Bc1AssetId = "sha256:c1fbbc453e9bf74296dd18baba051855f8d8bdcc4cb6662ee11dfbc958c04a92";
    private static readonly TextureLimits Limits = new(1024, 1024, 1024, 32, 6);
    private static readonly TextureCookRecipe SrgbColor = new(TextureColorSpace.Srgb, TextureSemantic.Color, 2);
    private static ReadOnlySpan<byte> FixtureRgba => [0, 0, 255, 0, 255, 0, 0, 255, 0, 255, 255, 1, 255, 255, 0, 254];

    [TestMethod]
    public void SrgbGoldenFreezesManifestSourceAndFilteredBytes()
    {
        byte[] source = Tga(2, 2, FixtureRgba);
        var first = CookTga(source, SrgbColor);
        var second = CookTga(Tga(2, 2, FixtureRgba), SrgbColor);
        Array.Clear(source);

        Assert.AreEqual(SrgbSourceDigest, first.SourceDigest);
        Assert.AreEqual(SrgbAssetId, first.AssetId);
        Assert.AreEqual(first.AssetId, second.AssetId);
        Assert.AreEqual(TextureSourceFormat.Tga32, first.SourceFormat);
        Assert.AreEqual(TextureFormat.Rgba8, first.Format);
        Assert.AreEqual(TextureColorSpace.Srgb, first.ColorSpace);
        Assert.AreEqual(TextureSemantic.Color, first.Semantic);
        Assert.AreEqual(2, first.Width);
        Assert.AreEqual(2, first.Height);
        Assert.IsTrue(first.Mips.SequenceEqual([new(2, 2, 0, 8, 16), new(1, 1, 16, 4, 4)]));
        Assert.IsTrue(first.Payload.SequenceEqual<byte>([.. FixtureRgba, 188, 188, 188, 128]));
        Assert.IsTrue(first.CanonicalBytes.SequenceEqual(second.CanonicalBytes));
        Assert.AreEqual(SrgbAssetId, Hash(first.CanonicalBytes));
    }

    [TestMethod]
    public void LinearColorChangesTheFilterAndIdentityAndKeepsSourceDigest()
    {
        var source = Tga(2, 2, FixtureRgba);
        var linear = CookTga(source, SrgbColor with { ColorSpace = TextureColorSpace.Linear });
        Assert.IsTrue(linear.Payload.SequenceEqual<byte>([.. FixtureRgba, 128, 128, 128, 128]));
        Assert.AreEqual(SrgbSourceDigest, linear.SourceDigest);
        Assert.AreNotEqual(SrgbAssetId, linear.AssetId);

        var fallbackMipOnly = CookTga(source, SrgbColor with { MipCount = 1 });
        Assert.IsTrue(fallbackMipOnly.Payload.SequenceEqual(FixtureRgba));
        Assert.AreNotEqual(SrgbAssetId, fallbackMipOnly.AssetId);

        var data = CookTga(source, SrgbColor with { ColorSpace = TextureColorSpace.Linear, Semantic = TextureSemantic.Data });
        Assert.IsTrue(linear.Payload.SequenceEqual(data.Payload));
        Assert.AreNotEqual(linear.AssetId, data.AssetId);
    }

    [TestMethod]
    public void LinearDataWeightsOddMipEdgesAndRetainsOriginalTexels()
    {
        byte[] sourcePixels = new byte[5 * 3 * 4];
        sourcePixels[(2 * 5 + 2) * 4] = 255; // Middle column contributes to both destination pixels.
        sourcePixels[(2 * 5 + 4) * 4] = 255; // Last column and last row both contribute.
        var texture = CookTga(Tga(5, 3, sourcePixels), new(TextureColorSpace.Linear, TextureSemantic.Data, 3));

        Assert.IsTrue(texture.Mips.SequenceEqual([new(5, 3, 0, 20, 60), new(2, 1, 60, 8, 8), new(1, 1, 68, 4, 4)]));
        Assert.IsTrue(texture.Payload[..60].SequenceEqual(sourcePixels));
        Assert.IsTrue(texture.Payload[60..].SequenceEqual<byte>([17, 0, 0, 0, 51, 0, 0, 0, 34, 0, 0, 0]));
    }

    [TestMethod]
    [DataRow(255, 128, 128, 0, 127, 127, 128, 128, 255)]
    [DataRow(255, 128, 128, 128, 255, 127, 218, 218, 128)]
    [DataRow(0, 127, 127, 0, 128, 128, 0, 128, 128)]
    public void LinearNormalFiltersRenormalizeAndUseFlatFallbackForCancellation(
        int firstX, int firstY, int firstZ, int secondX, int secondY, int secondZ, int x, int y, int z)
    {
        byte[] rgba = [(byte)firstX, (byte)firstY, (byte)firstZ, 20, (byte)secondX, (byte)secondY, (byte)secondZ, 21];
        var texture = CookTga(Tga(2, 1, rgba), new(TextureColorSpace.Linear, TextureSemantic.Normal, 2));

        Assert.IsTrue(texture.Payload[..8].SequenceEqual(rgba));
        Assert.IsTrue(texture.Payload[8..].SequenceEqual<byte>([(byte)x, (byte)y, (byte)z, 21]));
    }

    [TestMethod]
    public void Bc1GoldenUsesWholeBlocksAndKeepsSuppliedMips()
    {
        byte[] blocks = Enumerable.Range(0, 48).Select(static value => (byte)value).ToArray();
        Assert.AreEqual(TextureCookError.None, TextureCooker.CookBc1(blocks, 5, 7, SrgbColor with { MipCount = 3 },
            new TextureLimits(48, 1, 228, 7, 3), out var srgb));
        Assert.IsNotNull(srgb);
        Assert.AreEqual(Bc1AssetId, srgb.AssetId);
        Assert.AreEqual(TextureSourceFormat.Bc1Blocks, srgb.SourceFormat);
        Assert.AreEqual(TextureFormat.Bc1, srgb.Format);
        Assert.IsTrue(srgb.Mips.SequenceEqual([new(5, 7, 0, 16, 32), new(2, 3, 32, 8, 8), new(1, 1, 40, 8, 8)]));
        Assert.IsTrue(srgb.Payload.SequenceEqual(blocks));

        var linearRecipe = new TextureCookRecipe(TextureColorSpace.Linear, TextureSemantic.Color, 3);
        Assert.AreEqual(TextureCookError.None, TextureCooker.CookBc1(blocks, 5, 7, linearRecipe, Limits, out var linear));
        Assert.IsNotNull(linear);
        Assert.IsTrue(srgb.Payload.SequenceEqual(linear.Payload));
        Assert.AreNotEqual(srgb.AssetId, linear.AssetId);
        Assert.AreEqual(srgb.SourceDigest, linear.SourceDigest);
        Assert.AreEqual(TextureCookError.None,
            TextureCooker.ReadCooked(srgb.CanonicalBytes, Bc1AssetId, Limits, out var read));
        Assert.IsNotNull(read);
        Assert.IsTrue(read.CanonicalBytes.SequenceEqual(srgb.CanonicalBytes));
        Array.Clear(blocks);
        Assert.AreEqual(47, srgb.Payload[^1]);
    }

    [TestMethod]
    [DataRow(33, 20, 180, 2, 2, TextureCookError.SourceLimit)]
    [DataRow(34, 19, 180, 2, 2, TextureCookError.DecodedLimit)]
    [DataRow(34, 20, 179, 2, 2, TextureCookError.CookedLimit)]
    [DataRow(34, 20, 180, 1, 2, TextureCookError.DimensionLimit)]
    [DataRow(34, 20, 180, 2, 1, TextureCookError.MipLimit)]
    [DataRow(34, 20, 180, 2, 2, TextureCookError.None)]
    public void TgaCapsAcceptExactBoundsAndRejectOneBelow(int source, int decoded, int cooked, int dimension,
        int mips, TextureCookError expected)
    {
        var caps = new TextureLimits(source, decoded, cooked, dimension, mips);
        Assert.AreEqual(expected, TextureCooker.CookTga32(Tga(2, 2, FixtureRgba), SrgbColor, caps, out var texture));
        Assert.AreEqual(expected == TextureCookError.None, texture is not null);
    }

    [TestMethod]
    public void LimitsRequireFivePositiveCaps()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new TextureLimits(0, 1, 1, 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new TextureLimits(1, -1, 1, 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new TextureLimits(1, 1, 0, 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new TextureLimits(1, 1, 1, 0, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new TextureLimits(1, 1, 1, 1, 0));
    }

    [TestMethod]
    [DataRow(0, 1, TextureCookError.UnsupportedSource)] // Image ID.
    [DataRow(1, 1, TextureCookError.UnsupportedSource)] // Color map.
    [DataRow(2, 10, TextureCookError.UnsupportedSource)] // RLE.
    [DataRow(3, 1, TextureCookError.UnsupportedSource)] // Nonzero color map field.
    [DataRow(8, 1, TextureCookError.UnsupportedSource)] // Nonzero origin.
    [DataRow(16, 24, TextureCookError.UnsupportedSource)]
    [DataRow(17, 8, TextureCookError.UnsupportedSource)] // Bottom-up.
    [DataRow(17, 56, TextureCookError.UnsupportedSource)] // Right-to-left.
    [DataRow(12, 0, TextureCookError.DimensionLimit)]
    public void TgaRejectsUnsupportedOrInvalidHeader(int offset, int value, TextureCookError expected)
    {
        byte[] source = Tga(2, 2, FixtureRgba);
        source[offset] = (byte)value;
        Assert.AreEqual(expected, TextureCooker.CookTga32(source, SrgbColor, Limits, out var texture));
        Assert.IsNull(texture);
    }

    [TestMethod]
    public void SourceLengthsAndDeclaredMipCountsMustMatch()
    {
        byte[] tga = Tga(2, 2, FixtureRgba);
        foreach (byte[] invalid in new byte[][] { [], tga[..17], tga[..^1], [.. tga, 0] })
        {
            Assert.AreEqual(TextureCookError.InvalidSource, TextureCooker.CookTga32(invalid, SrgbColor, Limits, out var result));
            Assert.IsNull(result);
        }

        Assert.AreEqual(TextureCookError.InvalidSource, TextureCooker.CookBc1(new byte[7], 1, 1,
            SrgbColor with { MipCount = 1 }, Limits, out _));
        Assert.AreEqual(TextureCookError.InvalidSource, TextureCooker.CookBc1(new byte[9], 1, 1,
            SrgbColor with { MipCount = 1 }, Limits, out _));
        Assert.AreEqual(TextureCookError.MipLimit, TextureCooker.CookBc1(new byte[16], 1, 1, SrgbColor, Limits, out _));
        Assert.AreEqual(TextureCookError.MipLimit, TextureCooker.CookTga32(tga, SrgbColor with { MipCount = -1 }, Limits, out _));
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(3, 1)]
    [DataRow(1, 0)]
    [DataRow(1, 4)]
    [DataRow(2, 2)]
    [DataRow(2, 3)]
    public void RejectsUnsupportedColorMetadata(int colorSpace, int semantic)
    {
        var recipe = new TextureCookRecipe((TextureColorSpace)colorSpace, (TextureSemantic)semantic, 1);
        Assert.AreEqual(TextureCookError.InvalidMetadata, TextureCooker.CookTga32(Tga(2, 2, FixtureRgba), recipe, Limits, out _));
        Assert.AreEqual(TextureCookError.InvalidMetadata, TextureCooker.CookBc1(new byte[8], 4, 4, recipe, Limits, out _));
    }

    [TestMethod]
    public void ExtremeDimensionsAndMipCountsFailWithoutAllocatingTexels()
    {
        var max = new TextureLimits(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
        var one = SrgbColor with { MipCount = 1 };
        Assert.AreEqual(TextureCookError.CookedLimit, TextureCooker.CookBc1([], int.MaxValue, int.MaxValue, one, max, out _));
        Assert.AreEqual(TextureCookError.CookedLimit, TextureCooker.CookBc1([], 4, int.MaxValue, one, max, out _));
        Assert.AreEqual(TextureCookError.DimensionLimit, TextureCooker.CookBc1([], int.MinValue, 1, one, max, out _));
        Assert.AreEqual(TextureCookError.MipLimit, TextureCooker.CookBc1([], int.MaxValue, int.MaxValue,
            one with { MipCount = int.MaxValue }, max, out _));

        byte[] giantTgaHeader = Tga(ushort.MaxValue, ushort.MaxValue, []);
        Assert.AreEqual(TextureCookError.CookedLimit, TextureCooker.CookTga32(giantTgaHeader, one, max, out _));
    }

    [TestMethod]
    public void CookedReadOwnsItsBytesAndRechecksCookedDimensionAndMipCaps()
    {
        var original = CookTga(Tga(2, 2, FixtureRgba), SrgbColor);
        var bytes = original.CanonicalBytes.ToArray();
        Assert.AreEqual(TextureCookError.None, TextureCooker.ReadCooked(bytes, SrgbAssetId, new(1, 1, 180, 2, 2), out var texture));
        Assert.IsNotNull(texture);
        Array.Clear(bytes);

        Assert.IsTrue(texture.CanonicalBytes.SequenceEqual(original.CanonicalBytes));
        Assert.IsTrue(texture.Payload.SequenceEqual(original.Payload));
        Assert.IsTrue(texture.Mips.SequenceEqual(original.Mips));
        Assert.AreEqual(SrgbSourceDigest, texture.SourceDigest);
        Assert.AreEqual(TextureCookError.CookedLimit, TextureCooker.ReadCooked(original.CanonicalBytes, SrgbAssetId, new(1, 1, 179, 2, 2), out _));
        Assert.AreEqual(TextureCookError.DimensionLimit, TextureCooker.ReadCooked(original.CanonicalBytes, SrgbAssetId, new(1, 1, 180, 1, 2), out _));
        Assert.AreEqual(TextureCookError.MipLimit, TextureCooker.ReadCooked(original.CanonicalBytes, SrgbAssetId, new(1, 1, 180, 2, 1), out _));
    }

    [TestMethod]
    [DataRow(0, 0, TextureCookError.InvalidLayout)]
    [DataRow(4, 2, TextureCookError.UnsupportedCookedVersion)]
    [DataRow(8, int.MaxValue, TextureCookError.UnsupportedCookedVersion)]
    [DataRow(12, 0, TextureCookError.UnsupportedCookedVersion)]
    [DataRow(36, 2, TextureCookError.UnsupportedCookedVersion)]
    [DataRow(40, 2, TextureCookError.InvalidMetadata)]
    [DataRow(44, 31, TextureCookError.InvalidLayout)]
    [DataRow(80, 2, TextureCookError.UnsupportedCookedVersion)]
    [DataRow(84, 3, TextureCookError.InvalidMetadata)]
    [DataRow(88, 0, TextureCookError.InvalidMetadata)]
    [DataRow(92, 3, TextureCookError.InvalidMetadata)]
    [DataRow(96, -1, TextureCookError.DimensionLimit)]
    [DataRow(100, 3, TextureCookError.InvalidLayout)]
    [DataRow(104, 2, TextureCookError.InvalidMetadata)]
    [DataRow(108, int.MaxValue, TextureCookError.MipLimit)]
    [DataRow(112, int.MaxValue, TextureCookError.InvalidLayout)]
    [DataRow(116, 1, TextureCookError.InvalidLayout)]
    [DataRow(120, 1, TextureCookError.InvalidLayout)]
    [DataRow(124, int.MaxValue, TextureCookError.InvalidLayout)]
    [DataRow(128, 4, TextureCookError.InvalidLayout)]
    [DataRow(132, 15, TextureCookError.InvalidLayout)]
    [DataRow(144, 15, TextureCookError.InvalidLayout)] // Second mip overlaps the first.
    [DataRow(156, int.MaxValue, TextureCookError.InvalidLayout)]
    public void CookedReadRejectsMalformedMetadataAndLayoutEvenWithMatchingHash(int offset, int value,
        TextureCookError expected)
    {
        byte[] encoded = CookTga(Tga(2, 2, FixtureRgba), SrgbColor).CanonicalBytes.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(offset), value);
        Assert.AreEqual(expected, TextureCooker.ReadCooked(encoded, Hash(encoded), Limits, out var texture));
        Assert.IsNull(texture);
    }

    [TestMethod]
    public void CookedReadRejectsTruncationTrailingBytesAndDigestMismatch()
    {
        byte[] encoded = CookTga(Tga(2, 2, FixtureRgba), SrgbColor).CanonicalBytes.ToArray();
        for (int length = 0; length < encoded.Length; length++)
        {
            Assert.AreEqual(TextureCookError.InvalidLayout, TextureCooker.ReadCooked(encoded.AsSpan(0, length), SrgbAssetId, Limits, out _));
        }
        Assert.AreEqual(TextureCookError.InvalidLayout, TextureCooker.ReadCooked([.. encoded, 0], SrgbAssetId, Limits, out _));
        Assert.AreEqual(TextureCookError.DigestMismatch, TextureCooker.ReadCooked(encoded, "sha256:", Limits, out _));
        Assert.AreEqual(TextureCookError.DigestMismatch,
            TextureCooker.ReadCooked(encoded, string.Concat("sha256:", SrgbAssetId[7..].ToUpperInvariant()), Limits, out _));
        encoded[^1] ^= 1;
        Assert.AreEqual(TextureCookError.DigestMismatch, TextureCooker.ReadCooked(encoded, SrgbAssetId, Limits, out var texture));
        Assert.IsNull(texture);
    }

    private static CookedTexture CookTga(byte[] source, TextureCookRecipe recipe)
    {
        Assert.AreEqual(TextureCookError.None, TextureCooker.CookTga32(source, recipe, Limits, out var texture));
        Assert.IsNotNull(texture);
        return texture;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Tga(ushort width, ushort height, ReadOnlySpan<byte> rgba)
    {
        byte[] tga = new byte[18 + rgba.Length];
        tga[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(12), width);
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(14), height);
        tga[16] = 32;
        tga[17] = 0x28;
        for (int index = 0; index < rgba.Length; index += 4)
        {
            tga[18 + index] = rgba[index + 2];
            tga[19 + index] = rgba[index + 1];
            tga[20 + index] = rgba[index];
            tga[21 + index] = rgba[index + 3];
        }

        return tga;
    }
}
