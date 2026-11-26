using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private static readonly JsonSerializerOptions SkyLookupEvidenceOptions = new() { WriteIndented = true, IncludeFields = true };
    private static void RequireDirectSkyQualification(bool lookupRequested)
    {
        if (lookupRequested)
            throw new InvalidOperationException("Sky lookup prototype is diagnostic only and cannot enter sky qualification.");
    }

    [TestMethod]
    public void SkyLookupPrototypeCannotEnterQualification()
    {
        Assert.Throws<InvalidOperationException>(() => RequireDirectSkyQualification(true));
        RequireDirectSkyQualification(false);
    }

    [TestMethod]
    public void WarpSkyLookupPrototypePreservesFreshnessAndCapturesOracleDifferences() => VerifySkyLookup(true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void NvidiaSkyLookupPrototypePreservesFreshnessAndCapturesOracleDifferences() => VerifySkyLookup(false);

    private static void VerifySkyLookup(bool warp)
    {
        using var direct = new GpuDevice(warp, enableDebugLayer: true, enableDred: true,
            skyLookupPrototype: false, twilightPreview: true);
        using var lookup = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, skyLookupPrototype: true);
        AssertPlanetAdapter(direct.Adapter, warp);
        AssertPlanetAdapter(lookup.Adapter, warp);
        var width = warp ? 320 : 1920;
        var height = warp ? 180 : 1080;
        var captureBase = Environment.GetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_CAPTURE_DIR");
        var capture = Path.Combine(Path.GetFullPath(string.IsNullOrWhiteSpace(captureBase)
            ? Path.Combine("artifacts", "sky-lookup-prototype-captures") : captureBase), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(capture);
        var sky = SkyFixture() with { EyeMeters = new(0, 4097, 0) };
        List<(string Name, RenderSky Sky)> cases =
        [
            ("surface-day", sky with { SunDirection = Vector3.Normalize(new(0, 0.2f, -1)) }),
            ("surface-horizon", sky with { SunDirection = -Vector3.UnitZ }),
            ("surface-dusk", sky with { SunDirection = Vector3.Normalize(new(0, -0.04f, -1)) }),
            ("surface-night", sky with { SunDirection = -Vector3.UnitY, SunRadiance = 0 }),
            ("orbit-limb", sky with { EyeMeters = new(0, 0, 8192), VerticalFieldOfView = 1.2f, SunDirection = Vector3.UnitX }),
            ("orbit-sun", sky with { EyeMeters = new(0, 0, -8192), SunDirection = -Vector3.UnitZ }),
            ("sun-pole-basis", sky with { SunDirection = Vector3.UnitY }),
            ("changed-profile", sky with { EyeMeters = new(0, 4320, 0), AtmosphereRadiusMeters = 4608,
                AerosolHeightMeters = 48, AerosolExtinction = new(0.004f), StarSeed = 77 }),
            ("upper-atmosphere-limb", sky with { EyeMeters = new(0, 0, 4351),
                Forward = Vector3.Normalize(new(0.94f, 0, -0.34f)), VerticalFieldOfView = 0.15f, SunDirection = Vector3.UnitX }),
            ("far-64r-limb", sky with { EyeMeters = new(0, 0, 262144),
                Forward = Vector3.Normalize(new(0.015625f, 0, -1)), VerticalFieldOfView = 0.01f, SunDirection = Vector3.UnitX }),
            ("atmosphere-edge-inside", sky with { EyeMeters = new(0, 0, 4351.75f),
                Forward = Vector3.Normalize(new(0.94f, 0, -0.34f)), VerticalFieldOfView = 0.15f, SunDirection = Vector3.UnitX }),
            ("atmosphere-edge-outside", sky with { EyeMeters = new(0, 0, 4352.25f),
                Forward = Vector3.Normalize(new(0.94f, 0, -0.34f)), VerticalFieldOfView = 0.15f, SunDirection = Vector3.UnitX }),
        ];
        // Half-pixel pitch steps cross a moving horizon and fallback boundary.
        // Preserve the raw sequence for review; this creates no new tolerance.
        for (var motion = 0; motion < 5; motion++)
        {
            var pitch = (motion - 2) * 0.5f * (2 * MathF.Tan(sky.VerticalFieldOfView * 0.5f) / height);
            cases.Add(($"motion-{motion:00}", cases[2].Sky with
            {
                Forward = new(0, MathF.Sin(pitch), -MathF.Cos(pitch)),
                Up = new(0, MathF.Cos(pitch), MathF.Sin(pitch)),
            }));
        }
        // Repeat the same continuous half-pixel sweep across the outer limb
        // at ordinary orbit and the maximum validated eye radius.
        for (var flight = 0; flight < 2; flight++)
        {
            var phaseName = flight == 0 ? "orbit-limb" : "far-64r-limb";
            var prefix = flight == 0 ? "orbit-motion" : "far-motion";
            var basis = cases.Single(item => item.Name == phaseName).Sky;
            for (var motion = 0; motion < 5; motion++)
            {
                var pitch = (motion - 2) * 0.5f * (2 * MathF.Tan(basis.VerticalFieldOfView * 0.5f) / height);
                var sine = MathF.Sin(pitch);
                var cosine = MathF.Cos(pitch);
                cases.Add(($"{prefix}-{motion:00}", basis with
                {
                    Forward = Vector3.Normalize(basis.Forward * cosine + basis.Up * sine),
                    Up = Vector3.Normalize(basis.Up * cosine - basis.Forward * sine),
                }));
            }
        }
        ulong sequence = 0;
        byte[]? first = null;
        foreach (var item in cases)
        {
            var frame = new RenderFrame(++sequence, Vector4.Zero) { Sky = item.Sky };
            var expected = GpuDevice.RenderOffscreen(direct, width, height, frame);
            var actual = GpuDevice.RenderOffscreen(lookup, width, height, frame);
            Assert.AreEqual(sequence, lookup.SkyLookupDispatchesForTests, "Every recorded sky must rebuild its tables.");
            Assert.AreEqual(0UL, direct.SkyLookupDispatchesForTests);
            Assert.AreEqual(0UL, direct.SkyLookupAllocatedBytesForTests);
            Assert.AreEqual(256, lookup.SkyLookupWidthForTests);
            Assert.AreEqual(128, lookup.SkyLookupHeightForTests);
            Assert.IsLessThanOrEqualTo(1024UL * 1024, lookup.SkyLookupAllocatedBytesForTests);
            var colors = SkyLookupPixels(actual, width, height);
            first ??= colors;
            Assert.IsTrue(colors.Where((_, index) => index % 4 != 3).Any(value => value > 0), "Unexpected wholly black lookup output.");
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                Assert.AreEqual(1f, actual.DepthAt(x, y), "Sky must not write depth.");
                Assert.AreEqual((byte)255, actual.ColorAt(x, y)[3]);
            }
            lookup.SetSkyLookupFallbackMaskForTests(true);
            var fallbackMask = GpuDevice.RenderOffscreen(lookup, width, height, frame with { Sequence = ++sequence });
            lookup.SetSkyLookupFallbackMaskForTests(false);
            Assert.AreEqual(sequence, lookup.SkyLookupDispatchesForTests);
            WriteSkyLookupEvidence(Path.Combine(capture, item.Name), width, height, expected, actual, fallbackMask, item.Sky, lookup);
        }
        // Revisit the original input after altitude, atmosphere, light and seed
        // changes. No stale table or partial overwrite may survive the rebuild.
        var restored = GpuDevice.RenderOffscreen(lookup, width, height,
            new RenderFrame(++sequence, Vector4.Zero) { Sky = cases[0].Sky });
        CollectionAssert.AreEqual(first!, SkyLookupPixels(restored, width, height));
        Assert.AreEqual(sequence, lookup.SkyLookupDispatchesForTests);

        var opaqueScene = new RenderScene(Matrix4x4.CreateScale(1.25f) * Matrix4x4.CreateTranslation(0, 0, -3),
            Vector4.One, Matrix4x4.Identity, 1, 0.1f, 15);
        var opaqueFrame = new RenderFrame(++sequence, Vector4.Zero, opaqueScene) { Sky = cases[0].Sky };
        var opaque = GpuDevice.RenderOffscreen(direct, 161, 97, opaqueFrame);
        var composite = GpuDevice.RenderOffscreen(lookup, 161, 97, opaqueFrame);
        var covered = 0;
        for (var y = 0; y < 97; y++)
        for (var x = 0; x < 161; x++)
        {
            Assert.AreEqual(opaque.DepthAt(x, y), composite.DepthAt(x, y));
            if (opaque.DepthAt(x, y) >= 1) continue;
            Assert.IsTrue(opaque.ColorAt(x, y).SequenceEqual(composite.ColorAt(x, y)));
            covered++;
        }
        Assert.IsGreaterThan(0, covered);
        Assert.AreEqual(sequence, lookup.SkyLookupDispatchesForTests);
        Assert.IsNull(direct.CaptureDred());
        Assert.IsNull(lookup.CaptureDred());
        direct.Dispose();
        lookup.Dispose();
        Assert.AreEqual(0UL, lookup.SkyLookupAllocatedBytesForTests);
        AssertSkyLookupSharedOwnerDiagnostics(direct);
        AssertCleanDiagnostics(new(lookup.Adapter, lookup.DebugLayerEnabled, lookup.DredEnabled, lookup.FinalDebugSummary, null), true);
        Console.WriteLine("Diagnostic lookup oracle captures (quality approval remains open): " + capture);
    }

    [TestMethod]
    public void WarpSkyLookupPrototypeBoundsTwoOwnersAndDisabledPath()
    {
        using var a = new GpuDevice(true, enableDebugLayer: true, enableDred: true, skyLookupPrototype: true);
        using var b = new GpuDevice(true, enableDebugLayer: true, enableDred: true, skyLookupPrototype: true);
        var disabled = new RenderFrame(1, new Vector4(0.2f));
        _ = GpuDevice.RenderOffscreen(a, 64, 64, disabled);
        Assert.AreEqual(0UL, a.SkyLookupAllocatedBytesForTests);
        Assert.AreEqual(0UL, a.SkyLookupDispatchesForTests);
        var frame = disabled with { Sky = SkyFixture() };
        _ = GpuDevice.RenderOffscreen(a, 64, 64, frame);
        _ = GpuDevice.RenderOffscreen(b, 64, 64, frame);
        Assert.IsGreaterThan(0UL, a.SkyLookupAllocatedBytesForTests);
        Assert.IsLessThan(4UL * 1024 * 1024, a.SkyLookupAllocatedBytesForTests + b.SkyLookupAllocatedBytesForTests);
        a.Dispose();
        Assert.AreEqual(0UL, a.SkyLookupAllocatedBytesForTests);
        _ = GpuDevice.RenderOffscreen(b, 64, 64, frame with { Sequence = 2 });
        Assert.AreEqual(2UL, b.SkyLookupDispatchesForTests);
        b.Dispose();
        Assert.AreEqual(0UL, b.SkyLookupAllocatedBytesForTests);
        AssertSkyLookupSharedOwnerDiagnostics(a);
        AssertCleanDiagnostics(new(b.Adapter, b.DebugLayerEnabled, b.DredEnabled, b.FinalDebugSummary, null), true);
    }

    [TestMethod]
    public void WarpSkyLookupPrototypeReleasesPartialPreparationOnFailure()
    {
        using var gpu = new GpuDevice(true, enableDebugLayer: true, enableDred: true,
            retirementHooks: new(AfterSkyLookupFirstAllocation: () =>
                throw new InvalidOperationException("Injected lookup preparation failure.")), skyLookupPrototype: true);
        var frame = new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() };
        var error = Assert.Throws<InvalidOperationException>(() => GpuDevice.RenderOffscreen(gpu, 64, 64, frame));
        StringAssert.Contains(error.Message, "Injected lookup preparation failure");
        Assert.AreEqual(0UL, gpu.SkyLookupAllocatedBytesForTests);
        Assert.AreEqual(0UL, gpu.SkyLookupDispatchesForTests);
        gpu.Dispose();
        Assert.AreEqual(0UL, gpu.SkyLookupAllocatedBytesForTests);
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);
    }

    private static byte[] SkyLookupPixels(OffscreenFrame frame, int width, int height)
    {
        var colors = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            frame.ColorAt(x, y).CopyTo(colors.AsSpan((y * width + x) * 4, 4));
        return colors;
    }

    private static void AssertSkyLookupSharedOwnerDiagnostics(GpuDevice first)
    {
        // DXGI may return the same native device for concurrent owners. The
        // first report legitimately contains the other owner's live objects;
        // the strict final-owner report below must prove all owners released.
        Assert.IsTrue(first.DebugLayerEnabled);
        Assert.IsTrue(first.DredEnabled);
        var messages = first.FinalDebugSummary;
        Assert.IsNotNull(messages);
        Assert.AreEqual(0UL, messages.Errors);
        Assert.AreEqual(0UL, messages.Corruption);
        Assert.AreEqual(0UL, messages.Discarded);
        Assert.AreEqual(0, messages.LiveObjectReportResult);
    }

    private static void WriteSkyLookupEvidence(string directory, int width, int height,
        OffscreenFrame direct, OffscreenFrame lookup, OffscreenFrame fallbackMask, RenderSky sky, GpuDevice gpu)
    {
        Directory.CreateDirectory(directory);
        var expected = SkyLookupPixels(direct, width, height);
        var actual = SkyLookupPixels(lookup, width, height);
        var difference = new byte[actual.Length];
        var maskPixels = new byte[actual.Length];
        var max = 0;
        long differentPixels = 0, differentChannels = 0;
        long fallbackPixels = 0, fallbackDifferentPixels = 0;
        double squared = 0;
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var changed = false;
            for (var channel = 0; channel < 4; channel++)
            {
                var index = pixel * 4 + channel;
                var delta = Math.Abs(expected[index] - actual[index]);
                difference[index] = (byte)delta;
                max = Math.Max(max, delta);
                squared += delta * delta;
                if (delta == 0) continue;
                differentChannels++;
                changed = true;
            }
            if (changed) differentPixels++;
            difference[pixel * 4 + 3] = 255;
            var mask = fallbackMask.ColorAt(pixel % width, pixel / width);
            if (!actual.AsSpan(pixel * 4, 3).SequenceEqual(mask[..3]))
                Assert.Fail("Fallback-mask diagnostic changed RGB output.");
            if (mask[3] is not (0 or 255))
                Assert.Fail("Fallback-mask diagnostic did not export a Boolean branch decision.");
            if (mask[3] != 0)
            {
                fallbackPixels++;
                if (changed) fallbackDifferentPixels++;
            }
            maskPixels[pixel * 4] = maskPixels[pixel * 4 + 1] = maskPixels[pixel * 4 + 2] = mask[3];
            maskPixels[pixel * 4 + 3] = 255;
        }
        File.WriteAllBytes(Path.Combine(directory, "direct.rgba8"), expected);
        File.WriteAllBytes(Path.Combine(directory, "lookup.rgba8"), actual);
        File.WriteAllBytes(Path.Combine(directory, "fallback-mask.rgba8"), maskPixels);
        WriteSkyDepthBitmap(Path.Combine(directory, "direct.bmp"), width, height, direct);
        WriteSkyDepthBitmap(Path.Combine(directory, "lookup.bmp"), width, height, lookup);
        WriteSkyDepthBitmap(Path.Combine(directory, "difference.bmp"), width, height,
            new OffscreenFrame(width, difference, new float[width * height]));
        WriteSkyDepthBitmap(Path.Combine(directory, "fallback-mask.bmp"), width, height,
            new OffscreenFrame(width, maskPixels, new float[width * height]));
        var assembly = typeof(GpuDevice).Assembly;
        var shaderHashes = new Dictionary<string, string>();
        foreach (var name in new[] { "Preview.Sky.vs.dxil", "Preview.Sky.ps.dxil", "SkyLookup.ps.dxil", "SkyLookup.cs.dxil" })
        {
            using var shader = assembly.GetManifestResourceStream(name)!;
            shaderHashes.Add(name, Convert.ToHexString(SHA256.HashData(shader)));
        }
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            DiagnosticOnly = true, QualityApproved = false, PerformanceQualified = false,
            Scope = "Direct 8x4 oracle versus ground/atmosphere/vacuum boundary-aware view-ray/direct hybrid; not WYU gameplay acceptance",
            LookupMappingVersion = 5,
            Width = width, Height = height, Parameters = sky, gpu.Adapter,
            LookupWidth = gpu.SkyLookupWidthForTests, LookupHeight = gpu.SkyLookupHeightForTests,
            AllocationBytes = gpu.SkyLookupAllocatedBytesForTests, Dispatches = gpu.SkyLookupDispatchesForTests,
            MaxAbsoluteRgba8 = max, RmseRgba8 = Math.Sqrt(squared / actual.Length),
            DifferentPixels = differentPixels, DifferentChannels = differentChannels,
            FallbackPixels = fallbackPixels, FallbackFraction = (double)fallbackPixels / (width * height),
            FallbackOracleDifferentPixels = fallbackDifferentPixels,
            FallbackCountScope = "Actual GPU branch exported in alpha of a separate idle-guarded diagnostic frame; RGB identity asserted; no UAV atomics",
            DirectRgba8Sha256 = Convert.ToHexString(SHA256.HashData(expected)),
            LookupRgba8Sha256 = Convert.ToHexString(SHA256.HashData(actual)),
            ShaderHashes = shaderHashes, AssemblySha256 = SkyDepthFileHash(assembly.Location),
            MemoryScope = "Committed LUT texture allocation sizes only; descriptor/PSO/driver backing and pressure qualification remain open",
        }, SkyLookupEvidenceOptions));
    }
}
