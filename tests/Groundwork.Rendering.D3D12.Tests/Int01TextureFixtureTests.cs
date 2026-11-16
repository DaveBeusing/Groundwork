using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class Int01TextureFixtureTests
{
    [TestMethod]
    public void Int01CookedFixtureBindsAllFourInputsAndPreservesAssetIdentity()
    {
        var fixture = new Int01Textures();
        Assert.HasCount(3, fixture.Palette.Entries.ToArray());
        var rock = fixture.Palette.Resolve(Int01Textures.Rock).Textures;
        var ore = fixture.Palette.Resolve(Int01Textures.Ore).Textures;
        Assert.AreSame(MaterialTextureSet.Empty, fixture.Palette.Resolve(Int01Textures.OrbitProxy).Textures);
        Assert.AreSame(rock.Normal, ore.Normal);

        Verify(rock.Normal!, TextureSemantic.Normal, TextureColorSpace.Linear,
            "3a4ff1434ee5a4619a2b476edf87171aebded69b2f7f020393a0ae4a259727a7",
            "329359800b8c995a20908b25694e7c0adab131e7870a998b6684d74fe42c11e8");
        Verify(rock.BaseColor!, TextureSemantic.Color, TextureColorSpace.Srgb,
            "c06edf06c04155d44fa9c4c9c12db365ef52060557b732cee4e058e24ac0c4b8",
            "0cf9d7b194d80b8d8d8dc88b81306c514d718b0f40332c9640f43a7f93f20707");
        Verify(ore.BaseColor!, TextureSemantic.Color, TextureColorSpace.Srgb,
            "a9b3ba850d056e6dfbf79898891350d9833fcf83f6625f8951ead281bd31e525",
            "88c5fb77b3c1aab98bdc79269be06fbbee4e70c379cd9f7b3f0f29ccffc72d04");
        Verify(rock.Roughness!, TextureSemantic.Data, TextureColorSpace.Linear,
            "01e38aa2ef862a0a2918a16986eafac93bec59b10a2bb4914c6369515e210965",
            "f5e23f4bfe7ef5ddd36141694de3927cebe30bb2eec1f56604cfe7a81dbc1928");
        Verify(rock.Metallic!, TextureSemantic.Data, TextureColorSpace.Linear,
            "49581d4b4d252b7aa8790510c9fa13f2ba7e591810a7301967b979fbd63c3426",
            "d620cba135fd9c1c57a640241fe3786d035060345b817cb66a37a2d92c7e3073");
        Verify(ore.Roughness!, TextureSemantic.Data, TextureColorSpace.Linear,
            "9c80050e9464a4965093de671f72bdcbca84f0fe54f5f754ec356a8f6794cb96",
            "3f8575ef10d332ec1584ba0660996e42291f50d0f9006cb0db1749f8dd5828d5");
        Verify(ore.Metallic!, TextureSemantic.Data, TextureColorSpace.Linear,
            "ebb24b90ca9ce46bb7e1deffe188a921cc914799aa1738d4dbfa817349dfdcb8",
            "8c48db078636f358c68673f215c42ffa5babcb19ee34d8b2441283c166385e9e");

        void Verify(MaterialTextureReference reference, TextureSemantic semantic, TextureColorSpace colorSpace,
            string sourceHash, string assetHash)
        {
            var texture = fixture[reference];
            Assert.AreEqual($"sha256:{sourceHash}", texture.SourceDigest);
            Assert.AreEqual($"sha256:{assetHash}", reference.AssetId);
            Assert.AreEqual(reference.AssetId, texture.AssetId);
            Assert.AreEqual(semantic, texture.Semantic);
            Assert.AreEqual(colorSpace, texture.ColorSpace);
            Assert.AreEqual(new TextureMip(8, 8, 0, 32, 256), texture.Mips[0]);
            Assert.AreEqual(TextureCookError.None,
                TextureCooker.ReadCooked(texture.CanonicalBytes, reference.AssetId, Int01Textures.Limits, out var read));
            Assert.IsTrue(read!.Payload.SequenceEqual(texture.Payload));

            byte[] damaged = texture.CanonicalBytes.ToArray();
            damaged[^1] ^= 1;
            Assert.AreEqual(TextureCookError.DigestMismatch,
                TextureCooker.ReadCooked(damaged, reference.AssetId, Int01Textures.Limits, out var rejected));
            Assert.IsNull(rejected);
        }
    }
}
