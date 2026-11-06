using System.Numerics;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("D3D12MeshReplacementWarp")]
    public void WarpMeshReplacementReadbackDrawsTheNewPairWithDepth() => VerifyMeshReplacementReadback(useWarp: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12MeshReplacementHardware")]
    public void HardwareMeshReplacementReadbackDrawsTheNewPairWithDepth() => VerifyMeshReplacementReadback(useWarp: false);

    private static void VerifyMeshReplacementReadback(bool useWarp)
    {
        var frame = new RenderFrame(1, new Vector4(0, 0, 0, 1), new RenderScene(
            Matrix4x4.Identity, new Vector4(1, 0, 0, 1), Matrix4x4.Identity, MathF.PI / 2, NearPlane, FarPlane));
        var initial = new RenderMeshPair(
            new(RenderMesh.Create([new(-48, -16, -64), new(0, -16, -64), new(0, 16, -64), new(-48, 16, -64)],
                [0, 1, 2, 0, 2, 3]), Vector3.Zero),
            new(RenderMesh.Create([new(0, -16, -64), new(48, -16, -64), new(48, 16, -64), new(0, 16, -64)],
                [0, 1, 2, 0, 2, 3]), Vector3.Zero));
        var replacement = new RenderMeshPair(
            new(RenderMesh.Create([new(-64, 0, -48), new(8, 0, -48), new(8, 32, -48), new(-64, 32, -48),
                    new(12, 5, -24), new(18, 5, -24), new(18, 9, -24), new(12, 9, -24)],
                [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]), Vector3.Zero),
            new(RenderMesh.Create([new(8, 0, -48), new(64, 0, -48), new(64, 32, -48), new(8, 32, -48)],
                [0, 1, 2, 0, 2, 3]), Vector3.Zero));

        WithTerrainDevice(useWarp, initial, gpu =>
        {
            var frames = RendererFrameAdmission.CreateWithMeshReplacement();
            var meshes = frames.MeshReplacement!;
            frames.MarkInitialized();
            frames.MarkRunning();
            Assert.IsTrue(frames.TrySetSurface(RenderSurfaceState.Drawable, TerrainWidth, TerrainHeight));
            Assert.AreEqual(RenderSubmissionResult.Accepted, frames.TrySubmit(frame));
            Assert.IsTrue(frames.TryTakeLatest(out var current));

            var before = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, current);
            AssertTerrainPixel(before, 60, 55, ProjectTerrainDepth(64));
            AssertTerrainPixel(before, 100, 55, ProjectTerrainDepth(64));
            AssertTerrainPixel(before, 32, 30, 1);
            AssertTerrainPixel(before, 130, 30, 1);

            InstallReadbackPair(gpu, meshes, 2, replacement, expectedAllocated: true);

            // Reuse the same frame. Each new instance covers a sample the old instance
            // cannot reach, and the two old-only samples must disappear on this first draw.
            var after = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, current);
            var far = ProjectTerrainDepth(48);
            AssertTerrainPixel(after, 32, 30, far);
            AssertTerrainPixel(after, 130, 30, far);
            AssertTerrainPixel(after, 60, 55, 1);
            AssertTerrainPixel(after, 100, 55, 1);
            for (var x = 84; x <= 92; x++)
                AssertTerrainPixel(after, x, 42, far);

            // The first instance is nearer at this sample. The second draws later.
            AssertTerrainPixel(after, 110, 34, ProjectTerrainDepth(24));

            var clear = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, current with { Scene = null });
            AssertTerrainPixel(clear, 32, 30, 1);
            AssertTerrainPixel(clear, 130, 30, 1);
            AssertTerrainPixel(clear, 110, 34, 1);

            InstallReadbackPair(gpu, meshes, 3, CreateTerrainPair(drawFirst: false, drawSecond: false), expectedAllocated: false);
            var empty = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, current);
            AssertTerrainPixel(empty, 32, 30, 1);
            AssertTerrainPixel(empty, 130, 30, 1);
            AssertTerrainPixel(empty, 110, 34, 1);
            Assert.AreEqual(frame.Sequence, frames.Read().LastAcceptedSequence);
        });
    }

    private static void InstallReadbackPair(GpuDevice gpu, RendererFrameAdmission.MeshPairAdmission meshes,
        ulong generation, RenderMeshPair pair, bool expectedAllocated)
    {
        Assert.IsTrue(gpu.IsIdle);
        Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, meshes.TryDeclareMeshGeneration(generation));
        Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, meshes.TrySubmitMeshPair(generation, pair));
        Assert.IsTrue(meshes.TryTake(out var candidate));
        GpuDevice.MeshGeometry? prepared = gpu.PrepareMeshGeometry(candidate!.Pair);
        try
        {
            Assert.AreEqual(expectedAllocated ? 2 : 1, gpu.LiveMeshBuffers);
            Assert.IsTrue(meshes.TryInstall<GpuDevice.MeshGeometry?>(candidate, ref gpu.ActiveMeshGeometry, ref prepared, static () => true));
            Assert.AreEqual(generation, meshes.ReadMeshReplacement().InstalledGeneration);
        }
        finally
        {
            prepared?.Dispose();
        }

        Assert.AreEqual(expectedAllocated ? 1 : 0, gpu.LiveMeshBuffers);
    }
}
