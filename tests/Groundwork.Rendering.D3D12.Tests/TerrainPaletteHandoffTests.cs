using Groundwork.Rendering.Materials;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class TerrainPaletteHandoffTests
{
    [TestMethod]
    public void PaletteCapturePinsExactVisualRevisionAndKeepsUnchangedMapTickets()
    {
        var fixture = new Int01Textures();
        var view = new D3D12TextureView(new Guid("2e3a0dab-4b5d-463a-9ff2-14585d871be0"),
            new(1, 1, PlanetLocalFrame.Create(default)), 1, 1);
        var stream = new D3D12TextureHandoff(new(3, 3, 4096, 1024, 8192, 196_608, 196_608, 3));
        var rock = fixture.Palette.Resolve(Int01Textures.Rock);
        var ore = fixture.Palette.Resolve(Int01Textures.Ore);
        var rockColor = new D3D12TextureTarget(new(Int01Textures.Rock, MaterialTextureSemantic.BaseColor), 1, rock.Textures.BaseColor!);
        var oreColor = new D3D12TextureTarget(new(Int01Textures.Ore, MaterialTextureSemantic.BaseColor), 1, ore.Textures.BaseColor!);
        Assert.IsFalse(stream.PublishTerrainAppearance(view, fixture.Palette, [rockColor with { Reference = oreColor.Reference }]));
        Assert.IsTrue(stream.PublishTerrainAppearance(view, fixture.Palette, [rockColor, oreColor]));

        var credit = new D3D12TextureReservation(512, 0, 1024, 65_536, 65_536);
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(view, rockColor, credit, out var resident));
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(resident, fixture[rockColor.Reference]));
        Assert.IsTrue(stream.TryTake(view, out var transfer));
        Assert.AreEqual(resident, transfer.Ticket);
        Assert.IsTrue(stream.MarkCopySubmitted(resident));
        Assert.IsTrue(stream.TryInstall(resident, view, out _));
        Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(view, oreColor, credit, out var pending));

        var cut = new D3D12TerrainTextureBinding[2];
        Assert.IsTrue(stream.TryCaptureTerrainAppearance(view, cut, out var captured, out var length));
        Assert.AreSame(fixture.Palette, captured);
        Assert.AreEqual(2, length);
        Assert.AreEqual(new D3D12TerrainTextureBinding(rockColor, resident), cut[0]);
        Assert.AreEqual(new D3D12TerrainTextureBinding(oreColor, null), cut[1]);

        var nextPalette = new MaterialPalette(2, fixture.Palette.Entries);
        var nextOreColor = oreColor with { Sequence = 2 };
        Assert.IsTrue(stream.PublishTerrainAppearance(view, nextPalette, [nextOreColor, rockColor]));
        Assert.AreEqual(D3D12TextureResult.Stale, stream.TryComplete(pending, fixture[oreColor.Reference]));
        Assert.IsTrue(stream.TryCaptureTerrainAppearance(view, cut, out captured, out length));
        Assert.AreSame(nextPalette, captured);
        Assert.AreEqual(2, length);
        Assert.AreEqual(new D3D12TerrainTextureBinding(nextOreColor, null), cut[0]);
        Assert.AreEqual(new D3D12TerrainTextureBinding(rockColor, resident), cut[1]);
        Assert.IsFalse(stream.TryCaptureTerrainAppearance(default, cut, out _, out _));
    }
}
