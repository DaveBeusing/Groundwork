using System.Numerics;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("D3D12MeshReplacementWarp")]
    public void WarpDrawsMixedIndexWidthsAtTheSixteenBitBoundary()
    {
        var compact = new Vector3[ushort.MaxValue + 1];
        compact[^3] = new(-48, -20, -64);
        compact[^2] = new(-8, -20, -64);
        compact[^1] = new(-28, 20, -64);
        var wide = new Vector3[compact.Length + 1];
        wide[^3] = new(8, -20, -64);
        wide[^2] = new(48, -20, -64);
        wide[^1] = new(28, 20, -64);
        var meshes = new RenderMeshPair(
            new(RenderMesh.Create(compact, [ushort.MaxValue - 2, ushort.MaxValue - 1, ushort.MaxValue]), Vector3.Zero),
            new(RenderMesh.Create(wide, [ushort.MaxValue - 1, ushort.MaxValue, ushort.MaxValue + 1]), Vector3.Zero));
        var scene = new RenderScene(Matrix4x4.Identity, new(1, 0, 0, 1), Matrix4x4.Identity,
            MathF.PI / 2, NearPlane, FarPlane);
        var frame = new RenderFrame(1, new(0, 0, 0, 1), scene);

        WithTerrainDevice(true, meshes, gpu =>
        {
            Assert.IsNotNull(gpu.ActiveMeshGeometry);
            const int vertexBytes = 16;
            const int alignedCompactIndexBytes = 8;
            const int wideIndexBytes = 12;
            var packedBytes = (compact.Length + wide.Length) * vertexBytes + alignedCompactIndexBytes + wideIndexBytes;
            Assert.AreEqual(packedBytes, gpu.ActiveMeshGeometry.PackedBytes);

            var pixels = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
            var depth = ProjectTerrainDepth(64);
            AssertTerrainPixel(pixels, 60, 55, depth);
            AssertTerrainPixel(pixels, 100, 55, depth);
            AssertTerrainPixel(pixels, TerrainWidth / 2, TerrainHeight / 2, 1);
        });
    }

    [TestMethod]
    [TestCategory("D3D12MeshReplacementWarp")]
    public void WarpMeshReplacementPackingFailureReleasesPrivateBufferAndPreservesActivePair()
    {
        var initial = CreateTerrainPair();
        var overflowingMesh = RenderMesh.Create([Vector3.Zero, new Vector3(float.MaxValue, 0, 0), Vector3.UnitY], [0, 1, 2]);
        // The first mesh and the first vertex of the second mesh pack successfully.
        var candidate = new RenderMeshPair(initial.First,
            new RenderMeshInstance(overflowingMesh, new Vector3(float.MaxValue, 0, 0)));

        var gpu = new GpuDevice(warp: true, enableDebugLayer: true, enableDred: true, meshes: initial);
        using (gpu)
        {
            AssertAdapter(gpu.Adapter, useWarp: true);
            var active = gpu.ActiveMeshGeometry;
            Assert.IsNotNull(active);
            Assert.AreEqual(1, gpu.LiveMeshBuffers);

            Assert.ThrowsExactly<ArgumentException>(() =>
            {
                using var unexpected = gpu.PrepareMeshGeometry(candidate);
            });

            Assert.AreSame(active, gpu.ActiveMeshGeometry);
            Assert.AreEqual(1, gpu.LiveMeshBuffers);
            Assert.IsNull(gpu.CaptureDred());
        }

        Assert.AreEqual(0, gpu.LiveMeshBuffers);
        AssertCleanDiagnostics(new D3D12DiagnosticsSnapshot(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled,
            gpu.FinalDebugSummary, null), diagnosticsRequested: true);
    }
}
