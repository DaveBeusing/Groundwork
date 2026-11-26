using System.Diagnostics;
using System.Numerics;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public void WarpTerrainAppearanceCombinesMaterialMapsAndShadowCascades() =>
        VerifyTerrainAppearance(warp: true, requireDebug: false, preview: false);

    [TestMethod]
    public void WarpPreviewTerrainAppearanceCombinesMaterialMapsAndShadowCascades() =>
        VerifyTerrainAppearance(warp: true, requireDebug: false, preview: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public void TerrainAppearanceCombinesMaterialMapsAndShadowCascadesWithDebugLayerAndDred(bool warp, bool preview) =>
        VerifyTerrainAppearance(warp, requireDebug: true, preview: preview);

    private static void VerifyTerrainAppearance(bool warp, bool requireDebug, bool preview)
    {
        const int width = 160, height = 96;
        const int rockX = 65, oreX = 115, sampleY = height / 2;
        var textureFixture = new Int01Textures();
        var origin = PlanetLocalFrame.Create(default);
        var identity = new VolumeViewIdentity(1, 1, origin);
        var view = new D3D12TextureView(new Guid("00112233-4455-6677-8899-aabbccddeeff"), identity, 1, 1);
        var stream = new D3D12TextureHandoff(new(16, 16, 16_384, 16_384, 16_384, 2 * 1024 * 1024,
            4 * 1024 * 1024, 16));
        var diagnostics = new ResourceDiagnostics();
        var region = new RenderMeshRegion([
            new(MappedQuad(-8, 0, 5, -24, Int01Textures.Rock), Vector3.Zero),
            new(MappedQuad(0, 8, 5, -24, Int01Textures.Ore), Vector3.Zero),
            new(MappedQuad(-1, 1, 1, -21.5f, Int01Textures.OrbitProxy), Vector3.Zero),
        ]);
        Action<GpuDevice>? duringSignal = null;
        var hooks = new GpuRetirementTestHooks(SignalFailure: (device, _) =>
        {
            var pending = duringSignal;
            duringSignal = null;
            pending?.Invoke(device);
            return null;
        });
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, retirementHooks: hooks, region: region,
            resourceDiagnostics: diagnostics, twilightPreview: preview);
        var textures = new Dictionary<D3D12TextureTicket, GpuTerrainMap>();
        ulong sequence = 0;
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DredEnabled);
            if (requireDebug)
                Assert.IsTrue(gpu.DebugLayerEnabled);

            var disabled = Read(24, shadow: false);
            Assert.IsLessThan(1f, disabled.DepthAt(rockX, sampleY));
            CollectionAssert.AreEqual(disabled.ColorAt(rockX, sampleY).ToArray(), disabled.ColorAt(oreX, sampleY).ToArray());
            var targets = Targets(textureFixture.Palette);
            Assert.HasCount(8, targets);
            Assert.IsTrue(stream.PublishTerrainAppearance(view, textureFixture.Palette, targets));
            var numeric = Read(24, shadow: false, SelectCut());
            foreach (var target in targets)
                Assert.AreEqual(default, Upload(target, textureFixture[target.Reference]));
            var original = SelectCut();
            var unshadowed = Read(24, shadow: false, original);
            Assert.AreEqual(disabled.DepthAt(rockX, sampleY), unshadowed.DepthAt(rockX, sampleY));
            Assert.IsFalse(disabled.ColorAt(rockX, sampleY).SequenceEqual(unshadowed.ColorAt(rockX, sampleY)));
            Assert.IsFalse(numeric.ColorAt(rockX, sampleY).SequenceEqual(unshadowed.ColorAt(rockX, sampleY)));
            Assert.IsGreaterThan(unshadowed.ColorAt(rockX, sampleY)[2], unshadowed.ColorAt(rockX, sampleY)[0]);
            Assert.IsGreaterThan(unshadowed.ColorAt(oreX, sampleY)[0], unshadowed.ColorAt(oreX, sampleY)[2]);

            foreach (var depth in new[] { 21.9, 24.0, 26.1 })
            {
                var lit = Read(depth, shadow: false, original);
                var shadowed = Read(depth, shadow: true, original);
                var attenuation = 0;
                for (var x = 57; x <= 73; x++)
                {
                    Assert.AreEqual(lit.DepthAt(x, sampleY), shadowed.DepthAt(x, sampleY));
                    attenuation = Math.Max(attenuation, lit.ColorAt(x, sampleY)[0] - shadowed.ColorAt(x, sampleY)[0]);
                }
                Assert.IsGreaterThan(8, attenuation, $"Rock receiver shadow at depth {depth} m.");
                Assert.IsTrue(diagnostics.TryRead(out var snapshot));
                Assert.IsTrue(snapshot.TryGetPass(GpuPassPlan.Terrain, out var pass));
                // Color, depth, mesh, material CBV, two shadow maps and eight exact material SRVs.
                Assert.AreEqual(14, pass.ResourceUses);
            }
            Assert.IsGreaterThanOrEqualTo(2UL, gpu.RecordedShadowPasses);

            var nextTexture = MakeStaticColor(0, 255);
            var nextAlbedo = new MaterialTextureReference(MaterialTextureSemantic.BaseColor, nextTexture.AssetId);
            var rock = textureFixture.Palette.Resolve(Int01Textures.Rock);
            var nextRock = new PbrMaterialDescriptor(rock.BaseColor, rock.Metallic, rock.Roughness, rock.AmbientOcclusion,
                rock.Emissive, rock.NormalStrength, new(baseColor: nextAlbedo, roughness: rock.Textures.Roughness,
                    metallic: rock.Textures.Metallic, normal: rock.Textures.Normal));
            var nextPalette = new MaterialPalette(2,
                [new(Int01Textures.Rock, nextRock), new(Int01Textures.Ore, textureFixture.Palette.Resolve(Int01Textures.Ore)),
                 new(Int01Textures.OrbitProxy, textureFixture.Palette.Resolve(Int01Textures.OrbitProxy))]);
            var nextTarget = new D3D12TextureTarget(new(Int01Textures.Rock, MaterialTextureSemantic.BaseColor), 2, nextAlbedo);
            var rockAlbedo = Array.FindIndex(targets, target => target.Slot == nextTarget.Slot);
            Assert.IsGreaterThanOrEqualTo(0, rockAlbedo);
            targets[rockAlbedo] = nextTarget;
            Assert.IsTrue(stream.PublishTerrainAppearance(view, nextPalette, targets));
            var predecessor = Upload(nextTarget, nextTexture);
            Assert.IsTrue(textures.TryGetValue(predecessor, out var oldAlbedo));
            var replacement = SelectCut();
            var releasedBeforeFence = false;
            var updatedDuringSignal = false;
            duringSignal = device =>
            {
                releasedBeforeFence = device.TryReleaseTexture(oldAlbedo.Identity);
                device.SelectTerrainMaterials(replacement, stream);
                updatedDuringSignal = true;
            };
            var submittedWithOldCut = Read(24, shadow: false, original);
            Assert.IsTrue(updatedDuringSignal);
            Assert.IsFalse(releasedBeforeFence);
            CollectionAssert.AreEqual(unshadowed.ColorAt(rockX, sampleY).ToArray(), submittedWithOldCut.ColorAt(rockX, sampleY).ToArray());
            Assert.IsTrue(gpu.TryReleaseTexture(oldAlbedo.Identity));
            Assert.IsTrue(stream.Release(predecessor));
            Assert.IsTrue(textures.Remove(predecessor));

            var withReplacement = Read(24, shadow: false, replacement);
            Assert.AreEqual(unshadowed.DepthAt(rockX, sampleY), withReplacement.DepthAt(rockX, sampleY));
            Assert.IsGreaterThan(withReplacement.ColorAt(rockX, sampleY)[0] + 8, withReplacement.ColorAt(rockX, sampleY)[1]);
            CollectionAssert.AreEqual(unshadowed.ColorAt(oreX, sampleY).ToArray(), withReplacement.ColorAt(oreX, sampleY).ToArray());

            var swept = SweepMaterialMaps(nextPalette, replacement, targets);
            var firstOrder = ReadMixedTriangle(0, swept);
            var rotatedOrder = ReadMixedTriangle(1, swept);
            const int centerX = width / 2;
            Assert.IsLessThan(1f, firstOrder.DepthAt(centerX, sampleY));
            Assert.AreEqual(firstOrder.DepthAt(centerX, sampleY), rotatedOrder.DepthAt(centerX, sampleY));
            Assert.IsGreaterThan(firstOrder.ColorAt(centerX, sampleY)[0], firstOrder.ColorAt(centerX, sampleY)[2]);
            CollectionAssert.AreEqual(firstOrder.ColorAt(centerX, sampleY).ToArray(), rotatedOrder.ColorAt(centerX, sampleY).ToArray());
            var expandedShadow = ReadMixedTriangle(0, swept, shadow: true);
            Assert.IsTrue(diagnostics.TryRead(out var shadowSnapshot));
            Assert.IsTrue(shadowSnapshot.TryGetPass(GpuPassPlan.NearShadow, out var nearShadow));
            Assert.AreEqual(2, nearShadow.ResourceUses); // Shadow depth and the index-free caster.
            var indexedShadow = ReadMixedTriangle(0, swept, retainIndices: true, shadow: true);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                Assert.AreEqual(expandedShadow.DepthAt(x, y), indexedShadow.DepthAt(x, y));
                Assert.IsTrue(expandedShadow.ColorAt(x, y).SequenceEqual(indexedShadow.ColorAt(x, y)));
            }

            if (preview)
            {
                var packedNormal = MakeSweepMap(MaterialTextureSemantic.Normal, alpha: 0);
                var packedMap = new MaterialTextureReference(MaterialTextureSemantic.Normal, packedNormal.AssetId);
                var sweptRock = swept.Palette.Resolve(Int01Textures.Rock);
                // Match the constant metallic sweep map with its numeric value so the packed render exercises two-map sampling.
                var packedRock = new PbrMaterialDescriptor(sweptRock.BaseColor, 160f / 255f, sweptRock.Roughness,
                    sweptRock.AmbientOcclusion, sweptRock.Emissive, sweptRock.NormalStrength,
                    MaterialTextureSet.FromPackedNormalRoughness(packedMap, sweptRock.Textures.BaseColor));
                var packedRevision = swept.Palette.VisualRevision + 1;
                var packedPalette = new MaterialPalette(packedRevision,
                    [new(Int01Textures.Rock, packedRock), new(Int01Textures.Ore, swept.Palette.Resolve(Int01Textures.Ore)),
                     new(Int01Textures.OrbitProxy, swept.Palette.Resolve(Int01Textures.OrbitProxy))]);
                var packedTarget = new D3D12TextureTarget(new(Int01Textures.Rock, MaterialTextureSemantic.Normal),
                    packedRevision, packedMap);
                var packedTargets = targets.Where(target => target.Slot != packedTarget.Slot
                    && target.Slot != new D3D12TextureSlot(Int01Textures.Rock, MaterialTextureSemantic.Roughness)
                    && target.Slot != new D3D12TextureSlot(Int01Textures.Rock, MaterialTextureSemantic.Metallic))
                    .Append(packedTarget).ToArray();
                Assert.IsTrue(stream.PublishTerrainAppearance(view, packedPalette, packedTargets));
                Upload(packedTarget, packedNormal);
                var packedFrame = ReadMixedTriangle(0, SelectCut());
                for (var y = 40; y <= 55; y++)
                for (var x = 57; x <= 103; x++)
                {
                    Assert.AreEqual(firstOrder.DepthAt(x, y), packedFrame.DepthAt(x, y));
                    CollectionAssert.AreEqual(firstOrder.ColorAt(x, y).ToArray(), packedFrame.ColorAt(x, y).ToArray());
                }
                Assert.IsTrue(diagnostics.TryRead(out var packedSnapshot));
                Assert.IsTrue(packedSnapshot.TryGetPass(GpuPassPlan.Terrain, out var packedPass));
                Assert.AreEqual(10, packedPass.ResourceUses);
            }

            gpu.SelectTerrainMaterials(null, null);
            foreach (var pair in textures)
            {
                Assert.IsTrue(gpu.TryReleaseTexture(pair.Value.Identity));
                Assert.IsTrue(stream.Release(pair.Key));
            }
            Assert.IsNull(gpu.CaptureDred());
        }

        Assert.AreEqual(default, stream.ReadUsage());
        if (requireDebug)
            AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);

        OffscreenFrame Read(double receiverDepth, bool shadow, GpuTerrainMaterialSet? materials = null, Vector3? sun = null)
        {
            var camera = new VolumeCamera(identity, new(0, 0, receiverDepth - 24), -Vector3.UnitZ, Vector3.UnitY,
                0.65f, 0.1f, 100, Vector4.One, Vector4.Zero)
            {
                Sunlight = new(sun ?? Vector3.Normalize(new(1, 0, 1)), new(2.25f), new(0.32f)),
                TerrainShadows = shadow,
            };
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, ++sequence, out var frame));
            gpu.SelectTerrainMaterials(materials, stream);
            return GpuDevice.RenderOffscreen(gpu, width, height, frame);
        }

        GpuTerrainMaterialSet SweepMaterialMaps(MaterialPalette palette, GpuTerrainMaterialSet bindings, D3D12TextureTarget[] targets)
        {
            var previous = Read(24, shadow: false, bindings, Vector3.UnitZ);
            foreach (var semantic in new[] { MaterialTextureSemantic.Roughness, MaterialTextureSemantic.Metallic, MaterialTextureSemantic.Normal })
            {
                var texture = MakeSweepMap(semantic);
                var current = palette.Resolve(Int01Textures.Rock);
                var old = current.Textures;
                var map = new MaterialTextureReference(semantic, texture.AssetId);
                var maps = new MaterialTextureSet(old.BaseColor, old.Emissive,
                    semantic == MaterialTextureSemantic.Roughness ? map : old.Roughness,
                    semantic == MaterialTextureSemantic.Metallic ? map : old.Metallic,
                    old.AmbientOcclusion, semantic == MaterialTextureSemantic.Normal ? map : old.Normal);
                var rock = new PbrMaterialDescriptor(current.BaseColor, current.Metallic, current.Roughness,
                    current.AmbientOcclusion, current.Emissive, current.NormalStrength, maps);
                palette = new MaterialPalette(palette.VisualRevision + 1,
                    [new(Int01Textures.Rock, rock), new(Int01Textures.Ore, palette.Resolve(Int01Textures.Ore)),
                     new(Int01Textures.OrbitProxy, palette.Resolve(Int01Textures.OrbitProxy))]);
                var target = new D3D12TextureTarget(new(Int01Textures.Rock, semantic), palette.VisualRevision, map);
                var index = Array.FindIndex(targets, candidate => candidate.Slot == target.Slot);
                Assert.IsGreaterThanOrEqualTo(0, index);
                targets[index] = target;
                Assert.IsTrue(stream.PublishTerrainAppearance(view, palette, targets));
                var predecessor = Upload(target, texture);
                Assert.IsTrue(textures.Remove(predecessor, out var retired));
                Assert.IsTrue(gpu.TryReleaseTexture(retired.Identity));
                Assert.IsTrue(stream.Release(predecessor));
                bindings = SelectCut();

                var next = Read(24, shadow: false, bindings, Vector3.UnitZ);
                var difference = 0;
                for (var y = 40; y <= 55; y++)
                for (var x = 57; x <= 72; x++)
                {
                    Assert.AreEqual(previous.DepthAt(x, y), next.DepthAt(x, y));
                    var before = previous.ColorAt(x, y);
                    var after = next.ColorAt(x, y);
                    for (var channel = 0; channel < 3; channel++)
                        difference = Math.Max(difference, Math.Abs(after[channel] - before[channel]));
                }
                Assert.IsGreaterThan(1, difference, $"Changing only {semantic} should change shaded rock pixels.");
                previous = next;
            }
            return bindings;
        }

        static CookedTexture MakeSweepMap(MaterialTextureSemantic semantic, byte alpha = 255)
        {
            const int headerBytes = 18;
            var pixels = new byte[headerBytes + 8 * 8 * 4];
            pixels[2] = 2;
            pixels[12] = pixels[14] = 8;
            pixels[16] = 32;
            pixels[17] = 0x28;
            var normal = semantic == MaterialTextureSemantic.Normal;
            var sample = semantic == MaterialTextureSemantic.Metallic ? (byte)160 : (byte)0;
            for (var i = headerBytes; i < pixels.Length; i += 4)
            {
                pixels[i] = pixels[i + 2] = normal ? (byte)218 : sample;
                pixels[i + 1] = normal ? (byte)128 : sample;
                pixels[i + 3] = alpha;
            }
            Assert.AreEqual(TextureCookError.None, TextureCooker.CookTga32(pixels,
                new(TextureColorSpace.Linear, normal ? TextureSemantic.Normal : TextureSemantic.Data, 1),
                Int01Textures.Limits, out var texture));
            Assert.IsNotNull(texture);
            return texture;
        }

        OffscreenFrame ReadMixedTriangle(int shift, GpuTerrainMaterialSet materials, bool retainIndices = false, bool shadow = false)
        {
            Vector3[] points = [new(-6, -4, -24), new(6, -4, -24), new(0, 5, -24)];
            ushort[] ids = [Int01Textures.Rock, Int01Textures.Ore, Int01Textures.Ore];
            var corners = new Vector3[retainIndices ? 4 : 3];
            var vertices = new TerrainAppearanceVertex[corners.Length];
            for (var i = 0; i < corners.Length; i++)
            {
                var source = (i + shift) % points.Length;
                corners[i] = points[source];
                vertices[i] = new(points[source] * PlanetTriplanarMapping.TexelsPerMeter,
                    ids[source] | TerrainAppearanceVertex.MappingBit);
            }

            Span<uint> indices = stackalloc uint[3];
            D3D12Renderer.OrderTerrainMaterialTriangle(indices, 0, vertices[0].Material, vertices[1].Material, vertices[2].Material);
            var normals = new uint[corners.Length];
            Array.Fill(normals, PackedTerrainNormal.Encode(Vector3.UnitZ));
            var mesh = RenderMesh.CreateWithTerrainNormals(corners, indices, [], normals, vertices);
            var previous = gpu.ActiveMeshGeometry;
            gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(new RenderMeshRegion([new(mesh, Vector3.Zero)]));
            var expectedBytes = corners.Length * (5 * sizeof(uint) + TerrainAppearanceVertex.SizeInBytes)
                + (retainIndices ? indices.Length * sizeof(uint) : 0);
            Assert.AreEqual(expectedBytes, gpu.ActiveMeshGeometry.PackedBytes);
            previous?.Dispose();
            return Read(24, shadow, materials);
        }

        GpuTerrainMaterialSet SelectCut()
        {
            Span<D3D12TerrainTextureBinding> captured = new D3D12TerrainTextureBinding[8];
            Assert.IsTrue(stream.TryCaptureTerrainAppearance(view, captured, out var palette, out var count));
            Assert.IsNotNull(palette);
            var maps = new List<GpuTerrainMap>(count);
            foreach (var binding in captured[..count])
                if (binding.Resident is { } resident)
                    maps.Add(textures[resident]);
            return new(view, palette, captured[..count], maps.ToArray());
        }

        D3D12TextureTicket Upload(D3D12TextureTarget target, CookedTexture texture)
        {
            Assert.IsTrue(gpu.TryGetTextureRequirements(texture, out var physical));
            var reservation = new D3D12TextureReservation(512, 512, texture.CanonicalBytes.Length,
                physical.StagingAllocationBytes, physical.TextureAllocationBytes);
            Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(view, target, reservation, out var ticket));
            Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, texture));
            Assert.IsTrue(stream.TryTake(view, out var transfer));
            Assert.AreEqual(ticket, transfer.Ticket);
            Assert.IsTrue(gpu.TrySubmitTexture(transfer.Texture, identity.RendererEpoch, physical, out var native));
            Assert.IsTrue(stream.MarkCopySubmitted(ticket));
            var started = Stopwatch.GetTimestamp();
            while (!gpu.TryRetireTextureCopy(out var retired) || retired != native)
            {
                Assert.IsLessThan(TimeSpan.FromSeconds(10), Stopwatch.GetElapsedTime(started));
                Thread.Sleep(1);
            }
            Assert.IsTrue(stream.TryInstall(ticket, view, out var previous));
            Assert.IsTrue(gpu.TryGetTerrainMap(target, native, identity.RendererEpoch, out var map));
            textures.Add(ticket, map);
            return previous;
        }
    }

    private static D3D12TextureTarget[] Targets(MaterialPalette palette)
    {
        var targets = new List<D3D12TextureTarget>();
        foreach (var entry in palette.Entries)
        {
            var maps = entry.Descriptor.Textures;
            Add(maps.BaseColor, entry.MaterialId);
            Add(maps.Normal, entry.MaterialId);
            Add(maps.Roughness, entry.MaterialId);
            Add(maps.Metallic, entry.MaterialId);
        }
        return targets.ToArray();

        void Add(MaterialTextureReference? map, ushort id)
        {
            if (map is not null)
                targets.Add(new(new(id, map.Semantic), 1, map));
        }
    }

    private static RenderMesh MappedQuad(float left, float right, float extentY, float z, ushort material)
    {
        var positions = new Vector3[] { new(left, -extentY, z), new(right, -extentY, z), new(right, extentY, z), new(left, extentY, z) };
        var appearance = new TerrainAppearanceVertex[positions.Length];
        var tag = material | TerrainAppearanceVertex.MappingBit;
        for (var i = 0; i < positions.Length; i++)
            appearance[i] = new(positions[i] * PlanetTriplanarMapping.TexelsPerMeter, tag);
        var normal = PackedTerrainNormal.Encode(Vector3.UnitZ);
        const uint gray = 0xff808080;
        return RenderMesh.CreateWithTerrainNormals(positions, [0, 1, 2, 0, 2, 3], [gray, gray, gray, gray],
            [normal, normal, normal, normal], appearance);
    }
}
