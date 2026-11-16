using System.Buffers.Binary;
using System.Numerics;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;

namespace Groundwork.Rendering.D3D12.Tests;

// Original synthetic texels covered by the repository's Apache-2.0 license.
// The two source palettes use the same canonical IDs as the MAT-03 control fixture.
internal sealed class Int01Textures
{
    internal const ushort Rock = 7;
    internal const ushort Ore = 19;
    internal const ushort OrbitProxy = 51;
    private const int Size = 8;
    private readonly Dictionary<MaterialTextureReference, CookedTexture> _textures = [];

    internal static TextureLimits Limits { get; } = new(274, 256, 396, Size, 1);

    internal Int01Textures()
    {
        var normal = Cook(MaterialTextureSemantic.Normal, TextureColorSpace.Linear, TextureSemantic.Normal,
            [0xff8070fe, 0xff7080fe, 0xff8090fe, 0xff9080fe]);
        var rockColor = Cook(MaterialTextureSemantic.BaseColor, TextureColorSpace.Srgb, TextureSemantic.Color,
            [0xff995e37, 0xff854829, 0xffb87a4d, 0xffa06a42]);
        var oreColor = Cook(MaterialTextureSemantic.BaseColor, TextureColorSpace.Srgb, TextureSemantic.Color,
            [0xff408bb3, 0xff2c658e, 0xff56a7cb, 0xff4e82a8]);
        Palette = new MaterialPalette(1,
        [
            new(Rock, new(Vector3.One, 1, 1, 1, default, 1, new(baseColor: rockColor, normal: normal,
                roughness: Scalar(MaterialTextureSemantic.Roughness, 160, 184, 208, 152),
                metallic: Scalar(MaterialTextureSemantic.Metallic, 12, 25, 35, 28)))),
            new(Ore, new(Vector3.One, 1, 1, 1, default, 1, new(baseColor: oreColor, normal: normal,
                roughness: Scalar(MaterialTextureSemantic.Roughness, 64, 84, 112, 52),
                metallic: Scalar(MaterialTextureSemantic.Metallic, 223, 198, 241, 215)))),
            new(OrbitProxy, new(new(0.26f, 0.21f, 0.17f), 0, 0.9f, 1, default, 0)),
        ]);
    }

    internal MaterialPalette Palette { get; }

    internal CookedTexture this[MaterialTextureReference binding] => _textures[binding];

    private MaterialTextureReference Scalar(MaterialTextureSemantic slot, byte a, byte b, byte c, byte d) =>
        Cook(slot, TextureColorSpace.Linear, TextureSemantic.Data,
            [ScalarPixel(a), ScalarPixel(b), ScalarPixel(c), ScalarPixel(d)]);

    private static uint ScalarPixel(byte value) => 0xff000000u | value * 0x010101u;

    private MaterialTextureReference Cook(MaterialTextureSemantic slot, TextureColorSpace colorSpace, TextureSemantic semantic,
        ReadOnlySpan<uint> quadrants)
    {
        const int headerBytes = 18;
        Span<byte> source = stackalloc byte[headerBytes + Size * Size * sizeof(uint)];
        source.Clear();
        source[2] = 2;
        source[12] = Size;
        source[14] = Size;
        source[16] = 32;
        source[17] = 0x28;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var quadrant = (x / (Size / 2)) + 2 * (y / (Size / 2));
            BinaryPrimitives.WriteUInt32LittleEndian(source[(headerBytes + (y * Size + x) * sizeof(uint))..],
                quadrants[quadrant]);
        }

        var error = TextureCooker.CookTga32(source, new(colorSpace, semantic, 1), Limits, out var texture);
        if (error != TextureCookError.None || texture is null)
            throw new InvalidOperationException($"INT-01 fixture could not cook {slot} ({error}).");

        var binding = new MaterialTextureReference(slot, texture.AssetId);
        _textures.Add(binding, texture);
        return binding;
    }
}
