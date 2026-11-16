using System.Security.Cryptography;
using System.Text.Json;

namespace Groundwork.Rendering.D3D12.Tests;

internal static class Int01VisualCapture
{
    private static readonly JsonSerializerOptions ManifestOptions = new() { WriteIndented = true };

    internal static string? RequestedDirectory(bool warp, bool diagnostics)
    {
        var root = Environment.GetEnvironmentVariable("GROUNDWORK_INT01_CAPTURE_DIR");
        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root,
            warp ? diagnostics ? "warp-debug-dred" : "warp" : "nvidia-debug-dred");
    }

    internal static void Write<T>(string directory, int extent, T fixture,
        params (string Name, OffscreenFrame Pixels)[] frames) => Write(directory, extent, extent, fixture, frames);

    internal static void Write<T>(string directory, int width, int height, T fixture,
        params (string Name, OffscreenFrame Pixels)[] frames)
    {
        Directory.CreateDirectory(directory);
        var images = new CaptureImage[frames.Length];
        for (var index = 0; index < frames.Length; index++)
        {
            var (name, pixels) = frames[index];
            var file = $"{name}.bmp";
            var path = Path.Combine(directory, file);
            WriteBitmap(path, width, height, pixels);
            using var input = File.OpenRead(path);
            images[index] = new(file, HexHash(input));
        }

        using var manifest = File.Create(Path.Combine(directory, "manifest.json"));
        JsonSerializer.Serialize(manifest, new
        {
            Extent = new { Width = width, Height = height },
            Encoding = "BMP 24-bit top-down BGR from native linear RGBA8 UNORM, alpha omitted, no gamma conversion",
            Fixture = fixture,
            VertexShader = Fingerprint("TerrainAppearance.vs.dxil"),
            PixelShader = Fingerprint("TerrainAppearance.ps.dxil"),
            ShadowPixelShader = Fingerprint("TerrainAppearanceShadow.ps.dxil"),
            Images = images,
        }, ManifestOptions);
    }

    private static void WriteBitmap(string path, int width, int height, OffscreenFrame pixels)
    {
        const int headerBytes = 54;
        var stride = (width * 3 + 3) & ~3;
        var imageSize = checked(stride * height);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("BM"u8);
        writer.Write(checked(headerBytes + imageSize));
        writer.Write(0); // Reserved file-header fields.
        writer.Write(headerBytes);
        writer.Write(40); // BITMAPINFOHEADER size.
        writer.Write(width);
        writer.Write(-height); // A negative height stores the same top-down order as readback.
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(0); // BI_RGB (uncompressed).
        writer.Write(imageSize);
        for (var field = 0; field < 4; field++)
            writer.Write(0);

        var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = pixels.ColorAt(x, y);
                row[x * 3] = color[2];
                row[x * 3 + 1] = color[1];
                row[x * 3 + 2] = color[0];
            }
            writer.Write(row);
        }
    }

    private static CaptureImage Fingerprint(string resource)
    {
        using var bytes = typeof(GpuDevice).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded shader {resource}.");
        return new(resource, HexHash(bytes));
    }

    private static string HexHash(Stream bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private readonly record struct CaptureImage(string Name, string Sha256);
}
