using System.Numerics;
using Groundwork.Rendering;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class WarpReadbackTests
{
    private const int Width = 160;
    private const int Height = 96;

    [TestMethod]
    public void ClearWritesTheExpectedRgbaAndDepth()
    {
        var pixels = GpuDevice.RenderOffscreen(Width, Height, new RenderFrame(1, new(0.25f, 0.5f, 0.75f, 1)));
        AssertColor(pixels, 0, 0, 64, 128, 191, 255);
        AssertColor(pixels, Width - 1, Height - 1, 64, 128, 191, 255);
        Assert.AreEqual(1f, pixels.DepthAt(Width / 2, Height / 2));
    }

    [TestMethod]
    public void AsymmetricWorldViewAndProjectionPutTheCubeAtThePredictedPixel()
    {
        var scene = new RenderScene(
            Matrix4x4.CreateScale(0.70f, 0.35f, 0.55f) * Matrix4x4.CreateRotationZ(0.38f) *
                Matrix4x4.CreateTranslation(1.6f, 0.4f, -3.3f),
            new Vector4(1, 0, 0, 1),
            Matrix4x4.CreateLookAt(new(0.6f, 0.1f, 0.2f), new(0.1f, 0.2f, -2f), Vector3.UnitY),
            0.95f, 0.2f, 15);

        // Compute the projection independently from the GPU's combined matrix.
        var center = Vector3.Transform(Vector3.Transform(Vector3.Zero, scene.World), scene.View);
        var heightAtCenter = -center.Z * MathF.Tan(scene.VerticalFieldOfView / 2);
        var x = (int)((1 + center.X / (heightAtCenter * Width / Height)) * Width / 2);
        var y = (int)((1 - center.Y / heightAtCenter) * Height / 2);
        Assert.IsGreaterThan(Width / 2 + 15, x);

        var pixels = GpuDevice.RenderOffscreen(Width, Height, new RenderFrame(1, new(0, 0, 0, 1), scene));
        AssertColor(pixels, x, y, 255, 0, 0, 255);
        AssertColor(pixels, Width - 1 - x, y, 0, 0, 0, 255);
        AssertColor(pixels, Width / 2, Height / 2, 0, 0, 0, 255);
        Assert.IsLessThan(1f, pixels.DepthAt(x, y));
    }

    [TestMethod]
    public void DepthReadbackPreservesTheNearCubeWhenTheFarCubeDrawsLast()
    {
        var near = new RenderScene(Matrix4x4.CreateTranslation(0, 0, -2), new(1, 0, 0, 1),
            Matrix4x4.Identity, 1, 0.2f, 10);
        var far = near with { World = Matrix4x4.CreateTranslation(0, 0, -4), Tint = new(0, 1, 0, 1) };
        var pixels = GpuDevice.RenderOffscreen(Width, Height, new RenderFrame(1, new(0, 0, 0, 1), near), far);
        AssertColor(pixels, Width / 2, Height / 2, 255, 0, 0, 255);
        var expectedDepth = near.FarPlane / (near.FarPlane - near.NearPlane) * (1 - near.NearPlane / 1.5f);
        Assert.AreEqual(expectedDepth, pixels.DepthAt(Width / 2, Height / 2), 0.00001f);
        Assert.AreEqual(1f, pixels.DepthAt(0, 0));
    }

    private static void AssertColor(OffscreenFrame pixels, int x, int y, byte red, byte green, byte blue, byte alpha)
    {
        ReadOnlySpan<byte> expected = [red, green, blue, alpha];
        var actual = pixels.ColorAt(x, y);
        for (int index = 0; index < actual.Length; index++)
        {
            Assert.IsLessThanOrEqualTo(1, Math.Abs(actual[index] - expected[index]),
                $"Pixel ({x}, {y}) channel {index} was {actual[index]}, expected {expected[index]}.");
        }
    }
}
