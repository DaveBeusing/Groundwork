using System.Numerics;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
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
