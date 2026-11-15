using System.Numerics;
using Groundwork.Rendering.Materials;

namespace Groundwork.Rendering.Tests;

[TestClass]
public sealed class PbrMaterialDescriptorTests
{
    private const string TextureId = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string DifferentTextureId = "sha256:1123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    [DataRow(0f, 0f, 0f)]
    [DataRow(0.25f, 1f, 0.75f)]
    [DataRow(1f, 0.25f, 1f)]
    public void RetainsLinearParametersAndFiniteHdrValues(float metallic, float roughness, float occlusion)
    {
        var baseColor = new Vector3(0f, 0.25f, 1f);
        var emissive = new Vector3(0f, 4f, float.MaxValue);
        var material = new PbrMaterialDescriptor(baseColor, metallic, roughness, occlusion, emissive, float.MaxValue);
        Assert.AreEqual(baseColor, material.BaseColor);
        Assert.AreEqual(metallic, material.Metallic);
        Assert.AreEqual(roughness, material.Roughness);
        Assert.AreEqual(occlusion, material.AmbientOcclusion);
        Assert.AreEqual(emissive, material.Emissive);
        Assert.AreEqual(float.MaxValue, material.NormalStrength);
        Assert.AreEqual(MaterialTextureSet.Empty, material.Textures);
        Assert.AreEqual(material, new PbrMaterialDescriptor(baseColor, metallic, roughness, occlusion, emissive,
            float.MaxValue, new MaterialTextureSet()));
    }

    [TestMethod]
    [DataRow(float.NaN)]
    [DataRow(float.PositiveInfinity)]
    [DataRow(float.NegativeInfinity)]
    [DataRow(-float.Epsilon)]
    public void RejectsNonFiniteAndNegativeParameters(float value)
    {
        AssertInvalid("baseColor", () => new PbrMaterialDescriptor(new Vector3(value, 0f, 1f), 0f, 0f, 0f, Vector3.Zero, 0f));
        AssertInvalid("baseColor", () => new PbrMaterialDescriptor(new Vector3(0f, value, 1f), 0f, 0f, 0f, Vector3.Zero, 0f));
        AssertInvalid("baseColor", () => new PbrMaterialDescriptor(new Vector3(0f, 1f, value), 0f, 0f, 0f, Vector3.Zero, 0f));
        AssertInvalid("metallic", () => new PbrMaterialDescriptor(Vector3.Zero, value, 0f, 0f, Vector3.Zero, 0f));
        AssertInvalid("roughness", () => new PbrMaterialDescriptor(Vector3.Zero, 0f, value, 0f, Vector3.Zero, 0f));
        AssertInvalid("ambientOcclusion", () => new PbrMaterialDescriptor(Vector3.Zero, 0f, 0f, value, Vector3.Zero, 0f));
        AssertInvalid("emissive", () => new PbrMaterialDescriptor(Vector3.Zero, 0f, 0f, 0f, new Vector3(value, 1f, 0f), 0f));
        AssertInvalid("emissive", () => new PbrMaterialDescriptor(Vector3.Zero, 0f, 0f, 0f, new Vector3(0f, value, 1f), 0f));
        AssertInvalid("emissive", () => new PbrMaterialDescriptor(Vector3.Zero, 0f, 0f, 0f, new Vector3(1f, 0f, value), 0f));
        AssertInvalid("normalStrength", () => new PbrMaterialDescriptor(Vector3.Zero, 0f, 0f, 0f, Vector3.Zero, value));
    }

    [TestMethod]
    public void RejectsParametersAboveTheInclusiveUnitRange()
    {
        var value = float.BitIncrement(1f);
        AssertInvalid("baseColor", () => new PbrMaterialDescriptor(new Vector3(value), 0f, 0f, 0f, Vector3.Zero, 0f));
        AssertInvalid("metallic", () => new PbrMaterialDescriptor(Vector3.One, value, 0f, 0f, Vector3.Zero, 0f));
        AssertInvalid("roughness", () => new PbrMaterialDescriptor(Vector3.One, 0f, value, 0f, Vector3.Zero, 0f));
        AssertInvalid("ambientOcclusion", () => new PbrMaterialDescriptor(Vector3.One, 0f, 0f, value, Vector3.Zero, 0f));
    }

    [TestMethod]
    [DataRow(MaterialTextureSemantic.BaseColor, MaterialTextureColorSpace.Srgb, MaterialTextureColorSpace.Linear)]
    [DataRow(MaterialTextureSemantic.Emissive, MaterialTextureColorSpace.Srgb, MaterialTextureColorSpace.Linear)]
    [DataRow(MaterialTextureSemantic.Roughness, MaterialTextureColorSpace.Linear, MaterialTextureColorSpace.Srgb)]
    [DataRow(MaterialTextureSemantic.Metallic, MaterialTextureColorSpace.Linear, MaterialTextureColorSpace.Srgb)]
    [DataRow(MaterialTextureSemantic.AmbientOcclusion, MaterialTextureColorSpace.Linear, MaterialTextureColorSpace.Srgb)]
    [DataRow(MaterialTextureSemantic.Normal, MaterialTextureColorSpace.Linear, MaterialTextureColorSpace.Srgb)]
    public void BindingRequiresBothExpectedColorSpaceAndExactCookedId(MaterialTextureSemantic semantic,
        MaterialTextureColorSpace expected, MaterialTextureColorSpace mismatch)
    {
        var reference = new MaterialTextureReference(semantic, TextureId);
        Assert.AreEqual(semantic, reference.Semantic);
        Assert.AreEqual(TextureId, reference.AssetId);
        Assert.AreEqual(expected, reference.ExpectedColorSpace);
        Assert.IsTrue(reference.MatchesBinding(TextureId, expected));
        Assert.IsFalse(reference.MatchesBinding(TextureId, mismatch));
        Assert.IsFalse(reference.MatchesBinding(TextureId, (MaterialTextureColorSpace)0));
        Assert.IsFalse(reference.MatchesBinding(DifferentTextureId, expected));
        Assert.IsFalse(reference.MatchesBinding(TextureId.ToUpperInvariant(), expected));
    }

    [TestMethod]
    public void ReferencesRequireTheCanonicalCookedIdSyntaxAndAKnownSemantic()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new MaterialTextureReference(MaterialTextureSemantic.Normal, null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MaterialTextureReference((MaterialTextureSemantic)0, TextureId));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MaterialTextureReference((MaterialTextureSemantic)255, TextureId));
        foreach (var invalid in new[] { TextureId[7..], TextureId[..^1], TextureId + "0", TextureId + "\n",
            "SHA256:" + TextureId[7..], TextureId[..^1] + "F", TextureId[..^1] + "g", TextureId[..^1] + "１" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => new MaterialTextureReference(MaterialTextureSemantic.Normal, invalid));
        }
    }

    [TestMethod]
    public void TextureSetExposesEachSlotAndRejectsReferencesForOtherSlots()
    {
        var color = new MaterialTextureReference(MaterialTextureSemantic.BaseColor, TextureId);
        var emissive = new MaterialTextureReference(MaterialTextureSemantic.Emissive, TextureId);
        var roughness = new MaterialTextureReference(MaterialTextureSemantic.Roughness, TextureId);
        var metallic = new MaterialTextureReference(MaterialTextureSemantic.Metallic, TextureId);
        var occlusion = new MaterialTextureReference(MaterialTextureSemantic.AmbientOcclusion, TextureId);
        var normal = new MaterialTextureReference(MaterialTextureSemantic.Normal, TextureId);
        var maps = new MaterialTextureSet(color, emissive, roughness, metallic, occlusion, normal);
        Assert.AreEqual(color, maps.BaseColor);
        Assert.AreEqual(emissive, maps.Emissive);
        Assert.AreEqual(roughness, maps.Roughness);
        Assert.AreEqual(metallic, maps.Metallic);
        Assert.AreEqual(occlusion, maps.AmbientOcclusion);
        Assert.AreEqual(normal, maps.Normal);
        Assert.IsNull(new MaterialTextureSet(normal: normal).BaseColor);
        Assert.ThrowsExactly<ArgumentException>(() => new MaterialTextureSet(baseColor: emissive));
        Assert.ThrowsExactly<ArgumentException>(() => new MaterialTextureSet(emissive: color));
        Assert.ThrowsExactly<ArgumentException>(() => new MaterialTextureSet(roughness: metallic));
        Assert.ThrowsExactly<ArgumentException>(() => new MaterialTextureSet(metallic: roughness));
        Assert.ThrowsExactly<ArgumentException>(() => new MaterialTextureSet(ambientOcclusion: normal));
        Assert.ThrowsExactly<ArgumentException>(() => new MaterialTextureSet(normal: occlusion));
    }

    [TestMethod]
    public void DescriptorEqualityIncludesParameterValuesAndReferencedAssetIdsAndSlots()
    {
        var first = Textured(0.5f, new MaterialTextureSet(normal: new(MaterialTextureSemantic.Normal, TextureId)));
        var equal = Textured(0.5f, new MaterialTextureSet(normal: new(MaterialTextureSemantic.Normal, TextureId)));
        var otherRoughness = Textured(0.75f, first.Textures);
        var otherTexture = Textured(0.5f, new MaterialTextureSet(normal: new(MaterialTextureSemantic.Normal, DifferentTextureId)));
        var otherSemantic = Textured(0.5f, new MaterialTextureSet(metallic: new(MaterialTextureSemantic.Metallic, TextureId)));
        Assert.AreEqual(first, equal);
        Assert.AreEqual(first.GetHashCode(), equal.GetHashCode());
        Assert.AreNotEqual(first, otherRoughness);
        Assert.AreNotEqual(first, otherTexture);
        Assert.AreNotEqual(first, otherSemantic);

        static PbrMaterialDescriptor Textured(float roughness, MaterialTextureSet textures) =>
            new(Vector3.One, 0f, roughness, 1f, Vector3.Zero, 1f, textures);
    }

    private static void AssertInvalid(string parameterName, Func<PbrMaterialDescriptor> create) =>
        Assert.AreEqual(parameterName, Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => { _ = create(); }).ParamName);
}
