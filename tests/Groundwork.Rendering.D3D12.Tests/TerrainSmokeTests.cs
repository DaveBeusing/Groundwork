using System.Numerics;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private const int TerrainWidth = 160;
    private const int TerrainHeight = 96;
    private const float NearPlane = 2;
    private const float FarPlane = 128;
    private const float FrontDistance = 32;
    private const float BackDistance = 64;

    [TestMethod]
    [TestCategory("D3D12TerrainWarp")]
    public void WarpTerrainReadbackDrawsBothMeshesAndPreservesNearestDepth() => VerifyTerrainReadback(useWarp: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12TerrainHardware")]
    public void HardwareTerrainReadbackDrawsBothMeshesAndPreservesNearestDepth() => VerifyTerrainReadback(useWarp: false);

    [TestMethod]
    [TestCategory("D3D12TerrainWarp")]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public void WarpTerrainReadbackAcceptsEmptyMeshes(bool drawFirst, bool drawSecond)
    {
        var frame = CreateTerrainFrame();
        WithTerrainDevice(useWarp: true, CreateTerrainPair(drawFirst, drawSecond), gpu =>
        {
            var pixels = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            var backDepth = ProjectTerrainDepth(BackDistance);
            var rightDepth = drawSecond ? backDepth : 1;
            AssertTerrainPixel(pixels, 72, 48, drawFirst ? backDepth : 1);
            AssertTerrainPixel(pixels, 118, 48, drawFirst ? ProjectTerrainDepth(FrontDistance) : rightDepth);
            AssertTerrainPixel(pixels, 135, 48, rightDepth);
        });
    }

    [TestMethod]
    [TestCategory("D3D12TerrainWarp")]
    public void WarpTerrainReadbackClearsWithoutAScene()
    {
        var frame = CreateTerrainFrame() with { Scene = null };
        WithTerrainDevice(useWarp: true, CreateTerrainPair(), gpu =>
        {
            var pixels = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            AssertTerrainPixel(pixels, 72, 48, 1);
            AssertTerrainPixel(pixels, 118, 48, 1);
            AssertTerrainPixel(pixels, 135, 48, 1);
        });
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12TerrainWarp")]
    public Task WarpTerrainPresentsThroughResizeMinimizeRestoreAndNativeClose() =>
        VerifySwapChainLifecycle(useWarp: true, minimizeDuringStartup: false, CreateTerrainPair());

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12TerrainHardware")]
    public Task HardwareTerrainPresentsThroughResizeMinimizeRestoreAndNativeClose() =>
        VerifySwapChainLifecycle(useWarp: false, minimizeDuringStartup: false, CreateTerrainPair());

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12TerrainWarp")]
    public Task WarpTerrainBlockedResizeRetainsOwnershipUntilTheCallReturns() =>
        VerifyBlockedResize(useWarp: true, rejectDiagnostics: false, CreateTerrainPair());

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12TerrainHardware")]
    public Task HardwareTerrainBlockedResizeRetainsOwnershipUntilTheCallReturns() =>
        VerifyBlockedResize(useWarp: false, rejectDiagnostics: false, CreateTerrainPair());

    private static void VerifyTerrainReadback(bool useWarp)
    {
        var frame = CreateTerrainFrame();
        WithTerrainDevice(useWarp, CreateTerrainPair(), gpu =>
        {
            var pixels = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            var farDepth = ProjectTerrainDepth(BackDistance);
            AssertTerrainPixel(pixels, 72, 48, farDepth);
            AssertTerrainPixel(pixels, 135, 48, farDepth);
            // The projected seam is near x = 102. Sample both sides for clear-colored cracks.
            for (var x = 98; x <= 106; x++)
                AssertTerrainPixel(pixels, x, 48, farDepth);

            // The first mesh overlaps the second here. The later, farther draw must lose the depth test.
            AssertTerrainPixel(pixels, 118, 48, ProjectTerrainDepth(FrontDistance));
            AssertTerrainPixel(pixels, 0, 0, 1);
        });
    }

    private static void WithTerrainDevice(bool useWarp, RenderMeshPair meshes, Action<GpuDevice> readback)
    {
        var gpu = new GpuDevice(warp: useWarp, enableDebugLayer: true, enableDred: true, meshes: meshes);
        using (gpu)
        {
            AssertAdapter(gpu.Adapter, useWarp);
            readback(gpu);
            Assert.IsNull(gpu.CaptureDred());
        }

        AssertCleanDiagnostics(new D3D12DiagnosticsSnapshot(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled,
            gpu.FinalDebugSummary, null), diagnosticsRequested: true);
    }

    private static RenderMeshPair CreateTerrainPair(bool drawFirst = true, bool drawSecond = true)
    {
        var first = drawFirst
            ? RenderMesh.Create(
                [new(0, 0, 0), new(16, 0, 0), new(16, 16, 0), new(0, 16, 0),
                    new(14, 6, 16), new(16, 6, 16), new(16, 10, 16), new(14, 10, 16)],
                [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7])
            : RenderMesh.Create([], []);
        var second = drawSecond
            ? RenderMesh.Create([new(16, 16, 16), new(0, 0, 0), new(16, 0, 0), new(16, 16, 0), new(0, 16, 0)],
                [1, 2, 3, 1, 3, 4])
            : RenderMesh.Create([], []);
        return new RenderMeshPair(new RenderMeshInstance(first, new Vector3(-16, 0, 0)),
            new RenderMeshInstance(second, Vector3.Zero));
    }

    // Scaling also verifies that the mesh translation takes effect before the scene world transform.
    private static RenderFrame CreateTerrainFrame() => new(1, new Vector4(0, 0, 0, 1), new RenderScene(
        Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(16, -16, -64), new Vector4(1, 0, 0, 1),
        Matrix4x4.Identity, 1, NearPlane, FarPlane));

    private static float ProjectTerrainDepth(float distance) =>
        FarPlane / (FarPlane - NearPlane) * (1 - NearPlane / distance);

    private static void AssertTerrainPixel(OffscreenFrame pixels, int x, int y, float depth)
    {
        var color = pixels.ColorAt(x, y);
        Assert.AreEqual(depth == 1 ? (byte)0 : (byte)255, color[0], $"Red at ({x}, {y}).");
        Assert.AreEqual((byte)0, color[1], $"Green at ({x}, {y}).");
        Assert.AreEqual((byte)0, color[2], $"Blue at ({x}, {y}).");
        Assert.AreEqual((byte)255, color[3], $"Alpha at ({x}, {y}).");
        Assert.AreEqual(depth, pixels.DepthAt(x, y), 0.00001f, $"Depth at ({x}, {y}).");
    }
}
