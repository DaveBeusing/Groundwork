using System.Diagnostics;
using System.Numerics;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public void WarpMat02MatchesAnalyticReference() => VerifyMat02Brdf(warp: true, requireDebug: false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void Mat02MatchesAnalyticReferenceWithDebugLayerAndDred(bool warp) => VerifyMat02Brdf(warp, requireDebug: true);

    private static void VerifyMat02Brdf(bool warp, bool requireDebug)
    {
        const int size = 65;
        const int center = size / 2;
        const uint vertexColor = 0xc8d0a060;
        const float panelZ = -3;
        var baseColor = new Vector3(0.58f, 0.31f, 0.12f);
        var panelCenter = new Vector3(0, 0, panelZ);
        var normal = PackedTerrainNormal.Encode(Vector3.UnitZ);
        var panel = RenderMesh.CreateWithTerrainNormals(
            [new(-1, -1, panelZ), new(1, -1, panelZ), new(1, 1, panelZ), new(-1, 1, panelZ)],
            [0, 1, 2, 0, 2, 3], [vertexColor, vertexColor, vertexColor, vertexColor], [normal, normal, normal, normal]);
        var scene = new RenderScene(Matrix4x4.Identity, new Vector4(0.5f, 0.5f, 0.5f, 0.4f),
            Matrix4x4.Identity, 0.8f, 0.01f, 40);
        var sunlight = new RenderSunlight(new(0.8f, 0, 0.6f), new(0.7f), new(0.12f));
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true,
            region: new RenderMeshRegion([new(panel, Vector3.Zero)]));
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DredEnabled);
            if (requireDebug)
                Assert.IsTrue(gpu.DebugLayerEnabled);

            Assert.IsTrue(new RenderFrame(1, Vector4.Zero,
                scene with { TerrainMaterial = Material(0.5f) }).IsValid);
            Assert.IsFalse(new RenderFrame(1, Vector4.Zero,
                scene with { World = Matrix4x4.CreateTranslation(1, 0, 0), TerrainMaterial = Material(0.5f) }).IsValid);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Material(float.NaN));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Material(-0.001f));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Material(1.001f));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Material(0.5f, metal: float.PositiveInfinity));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Material(0.5f, ao: float.NaN));

            var legacy = Read(null, sunlight, Vector3.Zero);
            var legacyGain = 0.5f * (0.12f + 0.7f * 0.6f);
            AssertColor(legacy, new Vector3(96, 160, 208) * (legacyGain / 255), 80, "legacy vertex color and tint");
            var dielectric = Check(Material(0.7f), sunlight, Vector3.Zero, "dielectric");
            var conductor = Check(Material(0.7f, metal: 1), sunlight, Vector3.Zero, "conductor");
            Assert.IsGreaterThan(conductor[0] + 10, dielectric[0]);
            Assert.IsGreaterThan(conductor[2], conductor[0]);

            var normalSun = new RenderSunlight(Vector3.UnitZ, new(0.0004f), Vector3.Zero);
            var zero = Check(Material(0, metal: 1), normalSun, Vector3.Zero, "zero roughness");
            var floor = Check(Material(0.08f, metal: 1), normalSun, Vector3.Zero, "roughness floor");
            CollectionAssert.AreEqual(floor, zero);
            foreach (var roughness in new[] { 0.17f, 0.35f, 0.55f, 0.75f, 1f })
                Check(Material(roughness, metal: 1), sunlight, Vector3.Zero, $"roughness {roughness}");

            var glancingSun = new RenderSunlight(Vector3.Normalize(new(-3, 0, 0.25f)), new(0.22f), Vector3.Zero);
            var grazing = Check(Material(0.6f), glancingSun, new(3, 0, -2.75f), "grazing light and eye");
            Assert.IsGreaterThan(12, grazing[0], "The near-grazing Fresnel lobe must survive SDR quantization.");
            Check(Material(0.6f), sunlight, new(0, 0, -3.5f), "view through back hemisphere");
            Check(Material(0.6f), sunlight with { Direction = -Vector3.UnitZ }, Vector3.Zero, "light behind normal");

            var directOnly = sunlight with { Ambient = Vector3.Zero };
            var directWithAo = Check(Material(0.7f, ao: 0), directOnly, Vector3.Zero, "direct with zero AO");
            CollectionAssert.AreEqual(directWithAo, Check(Material(0.7f), directOnly, Vector3.Zero, "direct with full AO"));
            var ambientOnly = sunlight with { Direct = Vector3.Zero };
            Check(Material(0.7f, ao: 0.25f), ambientOnly, Vector3.Zero, "attenuated ambient diffuse");
            Check(Material(0.7f, metal: 1), ambientOnly, Vector3.Zero, "conductor with diffuse ambient only");
            var emissive = new PbrMaterialDescriptor(baseColor, 0.3f, 0.65f, 0.8f, new(0.08f, 0.16f, 0.22f), 1,
                new(baseColor: new MaterialTextureReference(MaterialTextureSemantic.BaseColor, "sha256:" + new string('a', 64))));
            Check(emissive, sunlight, Vector3.Zero, "emissive and numeric texture fallback");
            Check(emissive, default, Vector3.Zero, "emissive without sunlight");
            Check(emissive, sunlight, Vector3.Zero, "shadow-compatible root layout", shadow: true);
            CollectionAssert.AreEqual(legacy, Read(null, sunlight, Vector3.Zero), "Disabling PBR restores all legacy channels.");

            VerifyExistingTextureRoute();
            Assert.IsNull(gpu.CaptureDred());
        }

        if (requireDebug)
            AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);

        PbrMaterialDescriptor Material(float roughness, float metal = 0, float ao = 1) =>
            new(baseColor, metal, roughness, ao, Vector3.Zero, 1);

        byte[] Read(PbrMaterialDescriptor? material, RenderSunlight light, Vector3 eye, bool shadow = false)
        {
            var frame = new RenderFrame(1, Vector4.Zero, scene with
            {
                View = Matrix4x4.CreateLookAt(eye, panelCenter, Vector3.UnitY),
                Sunlight = light == default ? null : light,
                TerrainMaterial = material,
                ShadowOrigin = shadow ? new(0, 0, 0) : null,
            });
            Assert.IsTrue(frame.IsValid);
            var pixels = GpuDevice.RenderOffscreen(gpu, size, size, frame);
            Assert.IsLessThan(1f, pixels.DepthAt(center, center));
            return pixels.ColorAt(center, center).ToArray();
        }

        byte[] Check(PbrMaterialDescriptor material, RenderSunlight light, Vector3 eye, string name, bool shadow = false)
        {
            var pixel = Read(material, light, eye, shadow);
            AssertColor(pixel, EvaluateReference(material, light, Vector3.Normalize(eye - panelCenter)), 255, name);
            return pixel;
        }

        void VerifyExistingTextureRoute()
        {
            var limits = new TextureLimits(4_000_000, 4_000_000, 4_000_000, 16_384, 16);
            Assert.AreEqual(TextureCookError.None, TextureCooker.CookBc1([0xe0, 0x07, 0, 0, 0, 0, 0, 0], 4, 4,
                new(TextureColorSpace.Linear, TextureSemantic.Color, 1), limits, out var cooked));
            Assert.IsNotNull(cooked);
            Assert.IsTrue(gpu.TryGetTextureRequirements(cooked, out var requirements));
            Assert.IsTrue(gpu.TrySubmitTexture(cooked, 1, requirements, out var uploaded));
            var started = Stopwatch.GetTimestamp();
            while (!gpu.TryRetireTextureCopy(out var retired) || retired != uploaded)
            {
                Assert.IsLessThan(TimeSpan.FromSeconds(10), Stopwatch.GetElapsedTime(started));
                Thread.Sleep(1);
            }
            Assert.IsTrue(gpu.SetFrameTexture(uploaded, 1));
            var textured = Read(null, sunlight, Vector3.Zero);
            Assert.IsTrue(textured[0] == 0 && textured[1] > 0 && textured[2] == 0);
            CollectionAssert.AreEqual(textured, Read(Material(0.5f), sunlight, Vector3.Zero));
            Assert.IsTrue(gpu.TryReleaseTexture(uploaded));
        }
    }

    // Compare linear-light reference RGB after conversion to the fixture's R8G8B8A8_UNORM target.
    // Allow one 8-bit code value per channel for rounding/interpolation on either adapter.
    private static void AssertColor(ReadOnlySpan<byte> pixel, Vector3 linear, int alpha, string name)
    {
        Assert.AreEqual(255 * Math.Clamp(linear.X, 0, 1), pixel[0], 1, $"{name} red");
        Assert.AreEqual(255 * Math.Clamp(linear.Y, 0, 1), pixel[1], 1, $"{name} green");
        Assert.AreEqual(255 * Math.Clamp(linear.Z, 0, 1), pixel[2], 1, $"{name} blue");
        Assert.AreEqual(alpha, pixel[3], $"{name} alpha");
    }

    private static Vector3 EvaluateReference(PbrMaterialDescriptor material, RenderSunlight light, Vector3 view)
    {
        var ambient = material.BaseColor * light.Ambient * ((1 - material.Metallic) * material.AmbientOcclusion);
        var radiance = ambient + material.Emissive;
        double nl = light.Direction.Z;
        double nv = view.Z;
        if (nl <= 0 || nv <= 0)
            return radiance;

        var half = Vector3.Normalize(view + light.Direction);
        var alpha = Math.Pow(Math.Max(material.Roughness, 0.08), 2);
        var theta = Math.Acos(Math.Clamp(half.Z, 0, 1));
        var ggx = Math.Pow(alpha / (Math.Pow(alpha * Math.Cos(theta), 2) + Math.Pow(Math.Sin(theta), 2)), 2) / Math.PI;
        var schlick = Math.Pow(1 - Math.Clamp(Vector3.Dot(view, half), 0, 1), 5);
        var geometry = Smith(nl) * Smith(nv);
        return radiance + new Vector3(Channel(material.BaseColor.X, light.Direct.X),
            Channel(material.BaseColor.Y, light.Direct.Y), Channel(material.BaseColor.Z, light.Direct.Z));

        double Smith(double cosine) => 2 / (1 + Math.Sqrt(1 + alpha * alpha * (1 / (cosine * cosine) - 1)));

        float Channel(double albedo, double irradiance)
        {
            var f0 = (1 - material.Metallic) * 0.04 + material.Metallic * albedo;
            var fresnel = f0 + (1 - f0) * schlick;
            var diffuse = (1 - fresnel) * (1 - material.Metallic) * albedo / Math.PI;
            var specular = ggx * geometry * fresnel / (4 * nl * nv);
            return (float)((diffuse + specular) * nl * irradiance);
        }
    }
}
