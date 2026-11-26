using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private static readonly JsonSerializerOptions SkyDepthCaptureOptions = new() { WriteIndented = true };

    [TestMethod]
    public void WarpDepthCulledSkyMatchesOpaqueOverBackgroundAtEveryPixel() => VerifyDepthCulledSky(true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void NvidiaDepthCulledSkyMatchesOpaqueOverBackgroundAtEveryPixel() => VerifyDepthCulledSky(false);

    private static void VerifyDepthCulledSky(bool warp)
    {
        using var gpu = new GpuDevice(warp: warp, enableDebugLayer: true, enableDred: true, twilightPreview: true);
        AssertPlanetAdapter(gpu.Adapter, warp);
        var requestedCapture = Environment.GetEnvironmentVariable("GROUNDWORK_SKY_DEPTH_CAPTURE_DIR");
        var capture = string.IsNullOrWhiteSpace(requestedCapture) ? null :
            Path.Combine(Path.GetFullPath(requestedCapture), Guid.NewGuid().ToString("N"));
        var sky = SkyFixture() with { EyeMeters = new(0, 4097, 0) };
        RenderSky[] phases =
        [
            sky with { SunDirection = Vector3.Normalize(new(0, 0.2f, -1)) },
            sky with { SunDirection = -Vector3.UnitZ },
            sky with { SunDirection = Vector3.Normalize(new(0, -0.04f, -1)) },
            sky with { SunDirection = -Vector3.UnitY, SunRadiance = 0 },
            sky with { EyeMeters = new(0, 0, -8192), SunDirection = -Vector3.UnitZ },
        ];
        var near = new RenderScene(Matrix4x4.CreateScale(1.25f, 0.8f, 0.7f) * Matrix4x4.CreateTranslation(-0.35f, 0, -2.8f),
            new(0.2f, 0.4f, 0.6f, 1), Matrix4x4.Identity, 1, 0.1f, 15)
        {
            Fog = new(new(0.8f, 0.1f, 0.1f), 0, 8, 0.8f),
        };
        var followOn = near with
        {
            World = Matrix4x4.CreateScale(0.8f) * Matrix4x4.CreateTranslation(0.8f, 0.2f, -3.5f),
            Tint = new(0.1f, 0.7f, 0.3f, 1),
        };
        ulong sequence = 0;
        foreach (var phase in phases)
        {
            // Alternate padded extents to verify both DSVs are recreated against
            // the current depth generation, rather than retaining a stale view.
            var width = sequence % 2 == 0 ? 161 : 163;
            var height = width == 161 ? 97 : 99;
            var frame = new RenderFrame(++sequence, new Vector4(0.03f, 0.05f, 0.07f, 1), near) { Sky = phase };
            var opaque = GpuDevice.RenderOffscreen(gpu, width, height, frame with { Sky = null }, followOn);
            var background = GpuDevice.RenderOffscreen(gpu, width, height, frame with { Scene = null });
            var combined = GpuDevice.RenderOffscreen(gpu, width, height, frame, followOn);
            var covered = 0;
            var uncovered = 0;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var foreground = opaque.DepthAt(x, y) < 1;
                var expected = foreground ? opaque.ColorAt(x, y) : background.ColorAt(x, y);
                Assert.IsTrue(combined.ColorAt(x, y).SequenceEqual(expected), $"Sky composite mismatch at {x},{y}.");
                Assert.AreEqual(opaque.DepthAt(x, y), combined.DepthAt(x, y));
                if (foreground) covered++; else uncovered++;
            }
            Assert.IsGreaterThan(0, covered);
            Assert.IsGreaterThan(0, uncovered);
            if (capture is not null)
                WriteSkyDepthComparison(Path.Combine(capture, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    width, height, opaque, background, combined, phase, gpu.Adapter, covered, uncovered);
        }
        Assert.IsNull(gpu.CaptureDred());
        WriteSkyDepthWarnings(gpu);
        gpu.Dispose();
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);
        if (capture is not null)
            Console.WriteLine("Sky depth comparison captures: " + capture);
    }

    private static unsafe void WriteSkyDepthWarnings(GpuDevice gpu)
    {
        ID3D12InfoQueue* queue = null;
        if (gpu.Device->QueryInterface(__uuidof<ID3D12InfoQueue>(), (void**)&queue).Value < 0)
            return;
        try
        {
            var buffer = stackalloc byte[4096];
            var count = Math.Min(queue->GetNumStoredMessagesAllowedByRetrievalFilter(), 64UL);
            for (ulong index = 0; index < count; index++)
            {
                nuint size = 4096;
                var message = (D3D12_MESSAGE*)buffer;
                if (queue->GetMessage(index, message, &size).Value < 0 ||
                    message->Severity != D3D12_MESSAGE_SEVERITY.D3D12_MESSAGE_SEVERITY_WARNING)
                    continue;
                Console.WriteLine("Sky depth warning: " + JsonSerializer.Serialize(new
                {
                    Id = (int)message->ID,
                    Description = Marshal.PtrToStringUTF8((nint)message->pDescription,
                        checked((int)Math.Min(message->DescriptionByteLength, 4095U)))?.TrimEnd('\0'),
                }));
            }
        }
        finally { queue->Release(); }
    }

    private static void WriteSkyDepthComparison(string directory, int width, int height, OffscreenFrame opaque,
        OffscreenFrame sky, OffscreenFrame combined, RenderSky parameters, D3D12AdapterInfo adapter,
        int covered, int uncovered)
    {
        Directory.CreateDirectory(directory);
        var expected = Path.Combine(directory, "expected-composite.bmp");
        var actual = Path.Combine(directory, "actual.bmp");
        WriteSkyDepthBitmap(expected, width, height, opaque, sky);
        WriteSkyDepthBitmap(actual, width, height, combined);
        var expectedHash = SkyDepthFileHash(expected);
        var actualHash = SkyDepthFileHash(actual);
        Assert.AreEqual(expectedHash, actualHash);
        var assembly = typeof(GpuDevice).Assembly;
        using var vertex = assembly.GetManifestResourceStream("Preview.Sky.vs.dxil")!;
        using var pixel = assembly.GetManifestResourceStream("Preview.Sky.ps.dxil")!;
        using var manifest = File.Create(Path.Combine(directory, "manifest.json"));
        JsonSerializer.Serialize(manifest, new
        {
            Width = width, Height = height, CoveredPixels = covered, UncoveredPixels = uncovered,
            DifferentRgbaPixels = 0, DifferentDepthValues = 0,
            Encoding = "BMP 32-bit top-down BGRA from native linear RGBA8 UNORM, alpha retained; no gamma conversion or resizing",
            Scope = "Fixed cube/fog/FollowOn renderer composition, not a running WYU gameplay screenshot",
            Expected = new { File = "expected-composite.bmp", Sha256 = expectedHash },
            Actual = new { File = "actual.bmp", Sha256 = actualHash },
            VertexShaderSha256 = Convert.ToHexString(SHA256.HashData(vertex)),
            PixelShaderSha256 = Convert.ToHexString(SHA256.HashData(pixel)),
            AssemblySha256 = SkyDepthFileHash(assembly.Location), Adapter = adapter,
            Eye = new { parameters.EyeMeters.X, parameters.EyeMeters.Y, parameters.EyeMeters.Z },
            Sun = new { parameters.SunDirection.X, parameters.SunDirection.Y, parameters.SunDirection.Z },
            parameters.SunRadiance,
        }, SkyDepthCaptureOptions);
    }

    private static string SkyDepthFileHash(string path)
    {
        using var bytes = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void WriteSkyDepthBitmap(string path, int width, int height, OffscreenFrame foreground,
        OffscreenFrame? background = null)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("BM"u8);
        writer.Write(checked(54 + width * height * 4));
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(-height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(checked(width * height * 4));
        for (var i = 0; i < 4; i++) writer.Write(0);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = background is not null && foreground.DepthAt(x, y) == 1 ?
                background.ColorAt(x, y) : foreground.ColorAt(x, y);
            writer.Write(color[2]); writer.Write(color[1]); writer.Write(color[0]); writer.Write(color[3]);
        }
    }
}
