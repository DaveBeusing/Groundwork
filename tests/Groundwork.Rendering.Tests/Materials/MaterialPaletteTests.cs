using System.Numerics;
using Groundwork.Rendering.Materials;

namespace Groundwork.Rendering.Tests;

[TestClass]
public sealed class MaterialPaletteTests
{
    private static readonly PbrMaterialDescriptor Rock = new(new Vector3(0.4f), 0f, 0.8f, 1f, Vector3.Zero, 1f);
    private static readonly PbrMaterialDescriptor Ore = new(new Vector3(0.3f, 0.5f, 0.6f), 1f, 0.4f, 0.8f, Vector3.Zero, 1f);

    [TestMethod]
    public void SnapshotOwnsItsEntriesAndResolvesOriginalCanonicalIds()
    {
        MaterialPaletteEntry[] authored = [new(ushort.MaxValue, Rock), new(18, Rock), new(1, Ore)];
        var palette = new MaterialPalette(10, authored);
        authored[0] = new MaterialPaletteEntry(ushort.MaxValue, Ore);
        authored[1] = new MaterialPaletteEntry(7, Ore);

        Assert.IsTrue(palette.Entries.SequenceEqual([new(1, Ore), new(18, Rock), new(ushort.MaxValue, Rock)]));
        Assert.AreSame(Ore, palette.Resolve(1));
        Assert.AreSame(Rock, palette.Resolve(18));
        Assert.AreSame(Rock, palette.Resolve(ushort.MaxValue));
        Assert.AreSame(MaterialPalette.MissingMaterial, palette.Resolve(7));
    }

    [TestMethod]
    public void UnknownNonzeroIdsGetTheVisibleUntexturedFallbackAndZeroIsInvalid()
    {
        var palette = new MaterialPalette(1, [new(4, Rock), new(8, Ore)]);
        var emptyPalette = new MaterialPalette(1, []);
        var fallback = palette.Resolve(3);
        Assert.AreSame(fallback, palette.Resolve(6));
        Assert.AreSame(fallback, palette.Resolve(9));
        Assert.AreSame(fallback, emptyPalette.Resolve(ushort.MaxValue));
        Assert.AreEqual(new Vector3(1f, 0f, 1f), fallback.BaseColor);
        Assert.IsGreaterThan(0f, fallback.Emissive.X);
        Assert.AreEqual(MaterialTextureSet.Empty, fallback.Textures);
        Assert.AreNotEqual(Rock, fallback);
        Assert.AreNotEqual(Ore, fallback);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => palette.Resolve(0));
    }

    [TestMethod]
    public void RejectsZeroRevisionReservedIdMissingDescriptorAndDuplicateIds()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MaterialPalette(0, []));
        Assert.ThrowsExactly<ArgumentException>(() => new MaterialPalette(1, [new(0, Rock)]));
        Assert.ThrowsExactly<ArgumentException>(() => new MaterialPalette(1, [new(1, null!)]));
        Assert.ThrowsExactly<ArgumentException>(() => new MaterialPalette(1, [new(1, Ore), new(1, Rock)]));
        Assert.ThrowsExactly<ArgumentException>(() => new MaterialPalette(1, [new(1, Rock), new(1, Rock)]));
    }

    [TestMethod]
    public void AcceptsEveryNonzeroIdAndRejectsLargerTables()
    {
        var entries = new MaterialPaletteEntry[ushort.MaxValue];
        for (var index = 0; index < entries.Length; index++)
            entries[index] = new((ushort)(entries.Length - index), Rock);

        var palette = new MaterialPalette(ulong.MaxValue, entries);
        Assert.AreEqual(ushort.MaxValue, palette.Entries.Length);
        Assert.AreEqual((ushort)1, palette.Entries[0].MaterialId);
        Assert.AreEqual(ushort.MaxValue, palette.Entries[^1].MaterialId);
        Assert.AreSame(Rock, palette.Resolve(32768));
        Assert.ThrowsExactly<ArgumentException>(() => new MaterialPalette(1, new MaterialPaletteEntry[ushort.MaxValue + 1]));
    }

    [TestMethod]
    public void PaletteEqualityAndRevisionConflictsDependOnValuesAndIdsAcrossInputOrders()
    {
        var initial = new MaterialPalette(20, [new(50, Ore), new(5, Rock)]);
        var replay = new MaterialPalette(20, [new(5, Rock with { }), new(50, Ore with { })]);
        var sameVisualsNewRevision = new MaterialPalette(21, replay.Entries);
        var replacement = new MaterialPalette(21, [new(50, Rock), new(5, Ore)]);

        Assert.AreEqual(initial, replay);
        Assert.IsTrue(initial == replay);
        Assert.AreEqual(initial.GetHashCode(), replay.GetHashCode());
        Assert.AreSame(initial, initial.AcceptReplacement(replay));
        Assert.AreNotEqual(initial, sameVisualsNewRevision);
        Assert.IsTrue(initial != sameVisualsNewRevision);
        Assert.AreSame(sameVisualsNewRevision, initial.AcceptReplacement(sameVisualsNewRevision));
        Assert.AreSame(replacement, initial.AcceptReplacement(replacement));
        Assert.ThrowsExactly<ArgumentException>(() => replacement.AcceptReplacement(initial));
        Assert.ThrowsExactly<ArgumentException>(() => replacement.AcceptReplacement(sameVisualsNewRevision));
        Assert.ThrowsExactly<ArgumentException>(() => initial.AcceptReplacement(new MaterialPalette(20, [new(5, Rock)])));
        Assert.ThrowsExactly<ArgumentException>(() => initial.AcceptReplacement(new MaterialPalette(20, [new(6, Rock), new(50, Ore)])));
        Assert.AreSame(Ore, initial.Resolve(50));
        Assert.AreSame(Rock, replacement.Resolve(50));
        Assert.AreEqual(20UL, initial.VisualRevision);
        Assert.AreEqual(21UL, replacement.VisualRevision);
    }
}
