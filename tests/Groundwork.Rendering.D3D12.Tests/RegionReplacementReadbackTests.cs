using System.Numerics;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("D3D12RegionReplacementWarp")]
    public void WarpRegionReadbackUsesVertexColorsAndPreservesNearestDepth() =>
        VerifyRegionVertexColors(useWarp: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12RegionReplacementHardware")]
    public void HardwareRegionReadbackUsesVertexColorsAndPreservesNearestDepth() =>
        VerifyRegionVertexColors(useWarp: false);

    private static void VerifyRegionVertexColors(bool useWarp)
    {
        const uint red = 0xFF0000FF;
        const uint green = 0xFF00FF00;
        var front = RenderMesh.Create(
            [new(-12, -6, -32), new(0, -6, -32), new(0, 6, -32), new(-12, 6, -32),
             new(0, -6, -32), new(12, -6, -32), new(12, 6, -32), new(0, 6, -32)],
            [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7],
            [red, red, red, red, green, green, green, green]);
        var far = RegionQuad(144, 96, new Vector3(-72, -48, -96));
        var region = new RenderMeshRegion([new RenderMeshInstance(front, Vector3.Zero), far]);
        var frame = new RenderFrame(1, new Vector4(0, 0, 0, 1), new RenderScene(
            Matrix4x4.Identity, new Vector4(128f / 255, 1, 64f / 255, 1), Matrix4x4.Identity, MathF.PI / 2, NearPlane, FarPlane));

        var gpu = new GpuDevice(warp: useWarp, enableDebugLayer: true, enableDred: true, region: region);
        using (gpu)
        {
            AssertAdapter(gpu.Adapter, useWarp);
            Assert.IsTrue(gpu.DredEnabled);
            Assert.AreEqual(1, gpu.LiveMeshBuffers);
            var pixels = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);

            // The uncolored far panel draws last and overlaps both near quads.
            AssertVertexColorPixel(pixels, 76, [128, 0, 0, 255], ProjectTerrainDepth(32));
            AssertVertexColorPixel(pixels, 84, [0, 255, 0, 255], ProjectTerrainDepth(32));
            AssertVertexColorPixel(pixels, 50, [128, 255, 64, 255], ProjectTerrainDepth(96));
            Assert.IsNull(gpu.CaptureDred());
        }

        Assert.AreEqual(0, gpu.LiveMeshBuffers);
        Assert.AreEqual(0L, gpu.LiveMeshBytes);
        AssertCleanDiagnostics(new D3D12DiagnosticsSnapshot(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled,
            gpu.FinalDebugSummary, null), diagnosticsRequested: true);
    }

    private static void AssertVertexColorPixel(OffscreenFrame pixels, int x, ReadOnlySpan<byte> expected, float depth)
    {
        const int y = 48;
        var color = pixels.ColorAt(x, y);
        for (var channel = 0; channel < expected.Length; channel++)
            Assert.AreEqual(expected[channel], color[channel], $"Channel {channel} at ({x}, {y}).");
        Assert.AreEqual(depth, pixels.DepthAt(x, y), 0.00001f, $"Depth at ({x}, {y}).");
    }

    [TestMethod]
    [TestCategory("D3D12RegionReplacementWarp")]
    public void WarpRegionReplacementReadbackKeepsTheSeamAndClearsLeavingGeometry() =>
        VerifyRegionReplacementReadback(useWarp: true, requireDebugLayer: false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12RegionReplacementWarp")]
    public void WarpRegionReplacementReadbackWithDebugLayerKeepsTheSeam() =>
        VerifyRegionReplacementReadback(useWarp: true, requireDebugLayer: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12RegionReplacementHardware")]
    public void HardwareRegionReplacementReadbackKeepsTheSeamAndClearsLeavingGeometry() =>
        VerifyRegionReplacementReadback(useWarp: false, requireDebugLayer: true);

    private static void VerifyRegionReplacementReadback(bool useWarp, bool requireDebugLayer)
    {
        var frame = CreateRegionFrame();
        var gpu = new GpuDevice(warp: useWarp, enableDebugLayer: true, enableDred: true,
            region: CreateReadbackRegion(replacement: false));
        using (gpu)
        {
            AssertAdapter(gpu.Adapter, useWarp);
            if (requireDebugLayer)
                Assert.IsTrue(gpu.DebugLayerEnabled, "Install Windows Graphics Tools and run the diagnostic category separately.");
            Assert.IsTrue(gpu.DredEnabled);
            Assert.AreEqual(1, gpu.LiveMeshBuffers);

            var frames = RendererFrameAdmission.CreateWithMeshRegionReplacement();
            var regions = frames.MeshRegionReplacement!;
            frames.MarkInitialized();
            frames.MarkRunning();
            Assert.IsTrue(frames.TrySetSurface(RenderSurfaceState.Drawable, TerrainWidth, TerrainHeight));
            Assert.AreEqual(RenderSubmissionResult.Accepted, frames.TrySubmit(frame));
            Assert.IsTrue(frames.TryTakeLatest(out var current));

            var before = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, current);
            AssertRetainedRegionPixels(before);
            AssertTerrainPixel(before, 135, 26, ProjectTerrainDepth(64));
            AssertTerrainPixel(before, 25, 26, 1);

            InstallReadbackRegion(gpu, regions, CreateReadbackRegion(replacement: true));

            // Reuse the same frame. The retained seam stays solid and the old-only sample clears.
            var after = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, current);
            AssertRetainedRegionPixels(after);
            AssertTerrainPixel(after, 135, 26, 1);
            AssertTerrainPixel(after, 25, 26, ProjectTerrainDepth(64));
            Assert.AreEqual(frame.Sequence, frames.Read().LastAcceptedSequence);
            Assert.IsNull(gpu.CaptureDred());
        }

        Assert.AreEqual(0, gpu.LiveMeshBuffers);
        AssertCleanDiagnostics(new D3D12DiagnosticsSnapshot(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled,
            gpu.FinalDebugSummary, null), diagnosticsRequested: true);
    }

    private static void AssertRetainedRegionPixels(OffscreenFrame pixels)
    {
        // The near quads meet at x = 80. The later far panel overlaps both sides.
        for (var x = 76; x <= 84; x++)
            AssertTerrainPixel(pixels, x, 48, ProjectTerrainDepth(32));

        AssertTerrainPixel(pixels, 50, 48, ProjectTerrainDepth(64));
        AssertTerrainPixel(pixels, 110, 48, ProjectTerrainDepth(64));
        AssertTerrainPixel(pixels, 80, 65, ProjectTerrainDepth(96));
    }

    private static RenderMeshRegion CreateReadbackRegion(bool replacement)
    {
        var left = new RenderMeshInstance(RenderMesh.Create(
            [new(52, 10, 32), new(64, 10, 32), new(64, 22, 32), new(52, 22, 32),
                new(0, 0, 0), new(64, 0, 0), new(64, 32, 0), new(0, 32, 0)],
            [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]), new Vector3(-64, -16, -64));
        var right = new RenderMeshInstance(RenderMesh.Create(
            [new(0, 10, 32), new(12, 10, 32), new(12, 22, 32), new(0, 22, 32),
                new(0, 0, 0), new(64, 0, 0), new(64, 32, 0), new(0, 32, 0)],
            [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]), new Vector3(0, -16, -64));
        var farPanel = RegionQuad(144, 96, new Vector3(-72, -48, -96));
        var edge = RegionQuad(30, 20, new Vector3(replacement ? -90 : 60, 20, -64));
        var empty = RenderMesh.Create([], []);
        RenderMeshInstance EmptyAt(float x) => new(empty, new Vector3(x, 0, 0));

        // Exercise all nine slots and packing across empty chunks. The overlapping far panel draws last.
        return replacement
            ? new([edge, EmptyAt(-80), left, EmptyAt(-32), EmptyAt(-16), right, EmptyAt(16), EmptyAt(32), farPanel])
            : new([left, EmptyAt(-48), EmptyAt(-32), right, EmptyAt(16), EmptyAt(32), edge, EmptyAt(80), farPanel]);
    }

    private static RenderFrame CreateRegionFrame() => new(1, new Vector4(0, 0, 0, 1), new RenderScene(
        Matrix4x4.Identity, new Vector4(1, 0, 0, 1), Matrix4x4.Identity, MathF.PI / 2, NearPlane, FarPlane));

    private static RenderMeshInstance RegionQuad(float width, float height, Vector3 origin) => new(
        RenderMesh.Create([Vector3.Zero, new(width, 0, 0), new(width, height, 0), new(0, height, 0)],
            [0, 1, 2, 0, 2, 3]), origin);

    private static void InstallReadbackRegion(GpuDevice gpu, RendererFrameAdmission.MeshRegionAdmission regions,
        RenderMeshRegion replacement)
    {
        Assert.IsTrue(gpu.IsIdle);
        Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, regions.TryDeclareMeshGeneration(2));
        Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, regions.TrySubmitMeshRegion(2, replacement));
        Assert.IsTrue(regions.TryTake(out var candidate));
        GpuDevice.MeshGeometry? prepared = gpu.PrepareMeshGeometry(candidate!.Region);
        try
        {
            Assert.AreEqual(2, gpu.LiveMeshBuffers);
            Assert.IsTrue(regions.TryInstall<GpuDevice.MeshGeometry?>(candidate, ref gpu.ActiveMeshGeometry, ref prepared,
                static () => true));
            Assert.AreEqual(new MeshReplacementSnapshot(2, 2, 0), regions.ReadMeshReplacement());
        }
        finally
        {
            prepared?.Dispose();
        }

        Assert.AreEqual(1, gpu.LiveMeshBuffers);
    }
}
