using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public void WarpSkyDrawCullCapturesSunRadiusAndFieldOfViewExtremes() => CaptureSkyDrawCull(true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void NvidiaSkyDrawCullCapturesSunRadiusAndFieldOfViewExtremes() => CaptureSkyDrawCull(false);

    private static void CaptureSkyDrawCull(bool warp)
    {
        using var direct = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, skyLookupPrototype: false);
        using var lookup = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, skyLookupPrototype: true);
        AssertPlanetAdapter(direct.Adapter, warp);
        AssertPlanetAdapter(lookup.Adapter, warp);
        var width = warp ? 320 : 1920;
        var height = warp ? 180 : 1080;
        var requested = Environment.GetEnvironmentVariable("GROUNDWORK_SKY_DRAW_CULL_CAPTURE_DIR");
        var output = Path.Combine(Path.GetFullPath(requested ?? Path.Combine("artifacts", "sky-exact-cull-captures")),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        ulong sequence = 0;
        for (var radiusIndex = 0; radiusIndex < 3; radiusIndex++)
        for (var fovIndex = 0; fovIndex < 3; fovIndex++)
        {
            var radius = radiusIndex switch { 0 => 0.001f, 1 => 0.009f, _ => 0.25f };
            var fov = fovIndex switch { 0 => 0.01f, 1 => 1f, _ => 3.13f };
            Capture($"sun-radius-{radiusIndex}-fov-{fovIndex}", SkyFixture() with
            {
                EyeMeters = new(0, 0, -8192), SunDirection = -Vector3.UnitZ,
                SunAngularRadius = radius, VerticalFieldOfView = fov,
            });
        }
        Capture("sun-behind-camera", SkyFixture() with { EyeMeters = new(0, 0, -8192), SunDirection = Vector3.UnitZ });
        direct.Dispose();
        lookup.Dispose();
        AssertSkyLookupSharedOwnerDiagnostics(direct);
        AssertCleanDiagnostics(new(lookup.Adapter, lookup.DebugLayerEnabled, lookup.DredEnabled, lookup.FinalDebugSummary, null), true);
        Console.WriteLine("Exact draw-cull input captures: " + output);

        void Capture(string name, RenderSky parameters)
        {
            var frame = new RenderFrame(++sequence, Vector4.Zero) { Sky = parameters };
            var a = SkyLookupPixels(GpuDevice.RenderOffscreen(direct, width, height, frame), width, height);
            var b = SkyLookupPixels(GpuDevice.RenderOffscreen(lookup, width, height, frame), width, height);
            var directory = Path.Combine(output, name);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "direct.rgba8"), a);
            File.WriteAllBytes(Path.Combine(directory, "lookup.rgba8"), b);
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
            {
                DiagnosticOnly = true, Width = width, Height = height, Parameters = parameters, direct.Adapter,
                DirectSha256 = Convert.ToHexString(SHA256.HashData(a)), LookupSha256 = Convert.ToHexString(SHA256.HashData(b)),
                AssemblySha256 = SkyDepthFileHash(typeof(GpuDevice).Assembly.Location),
            }, SkyLookupEvidenceOptions));
        }
    }
}
