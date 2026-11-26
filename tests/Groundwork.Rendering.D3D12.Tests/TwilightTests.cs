using System.Numerics;
using System.Reflection;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [DataRow(null, false)]
    [DataRow("0", false)]
    [DataRow("true", false)]
    [DataRow("1", true)]
    public void WarpTwilightRuntimeSwitchGatesLookupAndMatchesRenderingAbi(string? value, bool enabled)
    {
        const string preview = "GROUNDWORK_TWILIGHT_PREVIEW";
        const string lookup = "GROUNDWORK_SKY_LOOKUP_PROTOTYPE";
        const string reuse = "GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES";
        var previousPreview = Environment.GetEnvironmentVariable(preview);
        var previousLookup = Environment.GetEnvironmentVariable(lookup);
        var previousReuse = Environment.GetEnvironmentVariable(reuse);
        try
        {
            Environment.SetEnvironmentVariable(preview, value);
            Environment.SetEnvironmentVariable(lookup, "1");
            Environment.SetEnvironmentVariable(reuse, "1");
            using var gpu = new GpuDevice(warp: true);
            Assert.AreEqual(enabled, gpu.TwilightPreviewEnabledForTests);
            Assert.AreEqual(enabled, gpu.SkyLookupPrototypeEnabledForTests);
            Assert.AreEqual(enabled, gpu.SkyLookupResourceReuseEnabledForTests);
            const BindingFlags abiFlags = BindingFlags.Static | BindingFlags.NonPublic;
            var engineAbi = typeof(RenderMesh).GetField("TwilightPreviewAbiVersion", abiFlags)?.GetRawConstantValue();
            var providerAbi = typeof(GpuDevice).GetField("TwilightPreviewAbiVersion", abiFlags)?.GetRawConstantValue();
            Assert.AreEqual(1, engineAbi);
            Assert.AreEqual(engineAbi, providerAbi);
        }
        finally
        {
            Environment.SetEnvironmentVariable(reuse, previousReuse);
            Environment.SetEnvironmentVariable(lookup, previousLookup);
            Environment.SetEnvironmentVariable(preview, previousPreview);
        }
    }

    [TestMethod]
    public void WarpTwilightReadbackCoversWarmHorizonDarkNightAndContinuousSunset() => VerifyTwilight(true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void NvidiaTwilightReadbackCoversWarmHorizonDarkNightAndContinuousSunset() => VerifyTwilight(false);

    private static void VerifyTwilight(bool warp)
    {
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, twilightPreview: true);
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.IsTrue(gpu.DebugLayerEnabled);
            Assert.IsTrue(gpu.DredEnabled);
            var sky = SkyFixture() with { EyeMeters = new(0, 4097, 0) };
            var elevations = new[] { -90f, -12f, -6f, -3f, -1f, 0f, 1f, 3f, 6f, 12f, 45f };
            var horizons = new Vector3[elevations.Length];
            for (var index = 0; index < elevations.Length; index++)
            {
                var degrees = elevations[index];
                var angle = degrees * (MathF.PI / 180);
                var sun = new Vector3(0, MathF.Sin(angle), -MathF.Cos(angle));
                var frame = GpuDevice.RenderOffscreen(gpu, 321, 181,
                    new RenderFrame(1, Vector4.Zero) { Sky = sky with { SunDirection = sun } });
                var horizon = MeanTwilightColor(frame, 180, 82);
                var upper = MeanTwilightColor(frame, 180, 25);
                horizons[index] = horizon;
                Console.WriteLine($"Twilight {degrees:F0} deg: horizon {horizon}; upper {upper}.");
                Assert.AreEqual(1f, frame.DepthAt(184, 84), "The sky must not write scene depth.");
                if (degrees >= -12 && degrees <= 3)
                {
                    Assert.IsGreaterThan(horizon.Y, horizon.X, "Low-angle scattering must warm the unobstructed horizon.");
                    Assert.IsGreaterThan(horizon.Z, horizon.Y, "Low-angle scattering must warm the unobstructed horizon.");
                    Assert.IsGreaterThan(upper.X + 15, horizon.X,
                        "Twilight glow must follow the long horizon path, not tint the entire sky uniformly.");
                }
            }
            Assert.IsLessThan(2f, horizons[0].Length(), "The shadowed night horizon must remain dark.");
            Assert.IsGreaterThan(horizons[1].X + 30, horizons[2].X, "Twilight must fade toward night.");
            Assert.IsGreaterThan(horizons[2].X + 30, horizons[5].X, "Sunset must fade toward twilight.");
            Assert.IsGreaterThan(2 * horizons[^1].X / MathF.Max(horizons[^1].Z, 1),
                horizons[5].X / MathF.Max(horizons[5].Z, 1),
                "The low sun must redden the horizon relative to the high sun.");
            // The shader has no clock/history: a rising sun must recover the same
            // canonical image as a setting sun, independent of submission order.
            foreach (var index in new[] { 5, 2, 1, 0 })
            {
                var angle = elevations[index] * (MathF.PI / 180);
                var dawn = GpuDevice.RenderOffscreen(gpu, 321, 181, new RenderFrame(1, Vector4.Zero)
                {
                    Sky = sky with { SunDirection = new(0, MathF.Sin(angle), -MathF.Cos(angle)) }
                });
                Assert.IsLessThanOrEqualTo(1f, Vector3.Distance(horizons[index], MeanTwilightColor(dawn, 180, 82)),
                    "Dawn and dusk at the same canonical sun/view must agree.");
            }
            var previous = Vector3.Zero;
            var maximumDelta = 0f;
            for (var step = 0; step <= 84; step++)
            {
                var degrees = -18 + step * 0.25f;
                var angle = degrees * (MathF.PI / 180);
                var frame = GpuDevice.RenderOffscreen(gpu, 321, 181, new RenderFrame(1, Vector4.Zero)
                {
                    Sky = sky with { SunDirection = new(0, MathF.Sin(angle), -MathF.Cos(angle)) }
                });
                var color = MeanTwilightColor(frame, 180, 82);
                if (step != 0)
                {
                    var delta = Vector3.Abs(color - previous);
                    maximumDelta = MathF.Max(maximumDelta, MathF.Max(delta.X, MathF.Max(delta.Y, delta.Z)));
                }
                previous = color;
            }
            Console.WriteLine($"Maximum quarter-degree twilight change: {maximumDelta}.");
            Assert.IsLessThanOrEqualTo(12f, maximumDelta,
                "A quarter-degree solar step must not expose binary atmosphere-sample shadow bands.");
            Assert.IsNull(gpu.CaptureDred());
        }
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);
    }

    private static Vector3 MeanTwilightColor(OffscreenFrame frame, int x, int y)
    {
        var sum = Vector3.Zero;
        for (var row = y; row < y + 5; row++)
        for (var column = x; column < x + 9; column++)
        {
            var color = frame.ColorAt(column, row);
            sum += new Vector3(color[0], color[1], color[2]);
        }
        return sum / 45;
    }
}
