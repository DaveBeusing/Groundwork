using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public Task WarpGeologyPortalReadbackMatchesCollisionAfterRebase() => VerifyGeologyPortal(true, false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public Task GeologyPortalReadbackWithDebugLayerAndDred(bool warp) => VerifyGeologyPortal(warp, true);

    private static async Task VerifyGeologyPortal(bool warp, bool requireDebug)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = timeout.Token;
        var landform = new PlanetLandformRecipe(61, 0, 0, new(17, 0), new(11, 0), new(5, 0), 13, 7, 19);
        var recipe = new PlanetGeologyRecipe(landform, 12, 100, 19, 0, 7, 32768, 8, 112, 65536, 0, 21, 22, 23, 24);
        var descriptor = new PlanetDescriptor(new Guid("00112233-4455-6677-8899-aabbccddeeff"),
            new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"), 42, 2, 1, 2, recipe.ParameterSha256, 128);
        var start = new PlanetVector(59, 33, 26);
        var end = new PlanetVector(69, 33, 26);
        var geology = new PlanetGeologyField(descriptor, recipe);
        using var run = new VolumeRun(geology: geology, camera: start,
            forward: new(1, 0, 0), displacement: new(10, 0, 0));
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = requireDebug ? new WindowsHostModule(new WindowsHostOptions($"Groundwork geology {Guid.NewGuid():N}", 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership) : null;
        await using var renderer = window is null ? null : D3D12Renderer.CreateWithVolumeTerrain(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics, useWarp: warp, startupTimeout: TimeSpan.FromSeconds(15));
        try
        {
            var view = run.Terrain.View;
            if (window is not null && renderer is not null)
            {
                await window.InitializeAsync(context, token);
                await window.StartAsync(token);
                await renderer.InitializeAsync(context, token);
                await renderer.StartAsync(token);
                var initialDiagnostics = renderer.ReadDiagnostics();
                AssertPlanetAdapter(initialDiagnostics.Adapter, warp);
                Assert.IsTrue(initialDiagnostics.DredEnabled);
                Assert.IsTrue(initialDiagnostics.DebugLayerEnabled);
                Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 1, Vector4.Zero));
                await AwaitStatus(renderer, state => state.LastPresentedSequence == 1, token);
            }
            else
                Assert.IsTrue(run.Terrain.ConfirmMask(view));
            await run.Publish(1, cut: false, renderer);
            using var before = run.Terrain.TryRead();
            Assert.IsNotNull(before);
            if (renderer is not null)
            {
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                    renderer.TrySubmitVolumeTerrain(before.Info.Generation, 2, Camera(view)));
                await AwaitStatus(renderer, state => state.LastPresentedSequence == 2, token);
            }
            else
            {
                using var frame = run.Terrain.TryRegisterFrame(before.Info.Generation, view, 2);
                Assert.IsNotNull(frame);
                Assert.IsTrue(run.Terrain.AcknowledgeFrame(frame));
            }
            Assert.AreEqual(VolumeQueryStatus.Clear, before.Sweep(new(start, start with { Z = 27 }, 0.5), new(10, 0, 0)).Status);
            Assert.AreEqual(VolumeQueryStatus.Clear, before.Sweep(new(end, end with { Z = 27 }, 0.5), new(-10, 0, 0)).Status);
            var chartStart = new PlanetVector(34, 35, 30);
            var chartEnd = new PlanetVector(36, 35, 30);
            Assert.AreEqual(PlanetFace.PositiveY, PlanetCoordinates.Project(chartStart).Face);
            Assert.AreEqual(PlanetFace.PositiveX, PlanetCoordinates.Project(chartEnd).Face);
            Assert.AreEqual(VolumeQueryStatus.Clear,
                before.Sweep(new(chartStart, chartStart with { Z = 31 }, 0.5), new(2, 0, 0)).Status);
            AssertGeologyNeighborhoodNormals(before);
            Assert.AreEqual(VolumeQueryStatus.CoverageUnavailable,
                before.Sweep(new(start, start, 0.5), new(0, 0, 55)).Status);

            using var speculative = run.Stage(VolumeWorkClass.Speculative, 1, 1, cut: false, geology);
            Assert.AreEqual(1, run.Terrain.Statistics.Used.QueueSlots);
            var brush = new VolumeBrushIntent(0, 1, 1, new(63, 27, 20), new(65, 39, 32),
                VolumeBrushOperation.Add, 26);
            var next = await run.CutFloor(renderer, token, brush, new(64, 32, 32));
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            Assert.IsTrue(speculative.IsCancellationRequested);
            Assert.AreEqual(1, run.Terrain.Statistics.Used.QueueSlots);
            speculative.Dispose();
            Assert.AreEqual(0, run.Terrain.Statistics.Used.QueueSlots);
            var peak = run.Terrain.Statistics.Peak;
            Console.WriteLine($"Protected edit peak CPU {peak.CpuBytes}, native {peak.NativeBytes}, upload {peak.UploadBytes}, " +
                $"retirement {peak.RetirementBytes} bytes, {peak.QueueSlots} queue slots, {peak.LeaseSlots} lease slots.");
            using var after = run.Terrain.TryRead();
            Assert.IsNotNull(after);
            Assert.AreEqual(1UL, before.Info.TerrainRevision);
            Assert.AreEqual(2UL, after.Info.TerrainRevision);
            Assert.AreEqual(VolumeQueryStatus.Clear,
                after.Sweep(new(chartEnd, chartEnd with { Z = 31 }, 0.5), new(-2, 0, 0)).Status);
            AssertGeologyNeighborhoodNormals(after);
            Assert.AreEqual(VolumeQueryStatus.Contact, after.Sweep(new(start, start with { Z = 27 }, 0.5), new(10, 0, 0)).Status);
            var hits = new VolumeRayHit[8];
            Assert.AreEqual(VolumeQueryStatus.Contact, after.Raycast(start, end, hits).Status);
            Assert.AreEqual((ushort)26, hits[0].Material);
            if (renderer is not null)
            {
                Assert.AreEqual(RenderSubmissionResult.InvalidFrame,
                    renderer.TrySubmitVolumeCamera(before.Info.Generation, 4, Camera(view)));
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                    renderer.TrySubmitVolumeTerrain(after.Info.Generation, 4, Camera(next)));
                await AwaitStatus(renderer, state => state.LastPresentedSequence == 4, token);
                await run.OpenMovement();
                await renderer.StopAsync(token);
                AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
            }

            var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true);
            using (gpu)
            {
                AssertPlanetAdapter(gpu.Adapter, warp);
                if (requireDebug)
                    Assert.IsTrue(gpu.DebugLayerEnabled);
                var open = Readback(before, view, 1);
                var closed = Readback(after, next, 2);
                Assert.IsLessThan(1f, open.DepthAt(80, 48));
                Assert.IsLessThan(open.DepthAt(80, 48), closed.DepthAt(80, 48));
                Assert.AreEqual((byte)0x63, closed.ColorAt(80, 48)[0]);
                Console.WriteLine(FormattableString.Invariant($"Portal depth {open.DepthAt(80, 48):F6} to {closed.DepthAt(80, 48):F6}, edited material {hits[0].Material}."));
                Assert.IsNull(gpu.CaptureDred());

                OffscreenFrame Readback(VolumeStreamingLease lease, VolumeViewIdentity identity, ulong sequence)
                {
                    var packed = D3D12Renderer.PackVolumeTerrain(lease.Meshes, identity.Frame, VolumeRun.CaveNativeBytes);
                    Assert.IsNotNull(packed);
                    gpu.ActiveMeshGeometry?.Dispose();
                    gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(packed, VolumeRun.CaveNativeBytes);
                    Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(Camera(identity), sequence, out var frame));
                    return GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
                }
            }
            Assert.AreEqual(0L, gpu.LiveMeshBytes);
            AssertCleanDiagnostics(new D3D12DiagnosticsSnapshot(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled,
                gpu.FinalDebugSummary, null), diagnosticsRequested: true);
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }

        VolumeCamera Camera(VolumeViewIdentity identity) =>
            new(identity, start, Vector3.UnitX, Vector3.UnitZ, MathF.PI / 4, 0.1f, 100, Vector4.One, Vector4.Zero);
    }

    private static void AssertGeologyNeighborhoodNormals(VolumeStreamingLease lease)
    {
        var smooth = new Dictionary<(double X, double Y, double Z), (int Mesh, Vector3 Normal)>();
        var shared = 0;
        var creases = 0;
        var index = 0;
        foreach (var mesh in lease.Meshes)
        {
            foreach (var triangle in mesh.Triangles)
            {
                var face = Vector3.Normalize(Vector3.Cross(triangle.B.Position - triangle.A.Position,
                    triangle.C.Position - triangle.A.Position));
                Assert.IsGreaterThan(0f, Vector3.Dot(face, triangle.A.Normal + triangle.B.Normal + triangle.C.Normal));
                if (triangle.IsTransition)
                    continue;
                if (triangle.A.Normal == face && triangle.B.Normal == face && triangle.C.Normal == face)
                {
                    creases++;
                    continue;
                }
                Compare(triangle.A);
                Compare(triangle.B);
                Compare(triangle.C);
            }
            index++;

            void Compare(VolumeVertex vertex)
            {
                var key = (mesh.Origin.X + (double)vertex.Position.X, mesh.Origin.Y + (double)vertex.Position.Y,
                    mesh.Origin.Z + (double)vertex.Position.Z);
                if (smooth.TryAdd(key, (index, vertex.Normal)) || smooth[key].Mesh == index)
                    return;
                Assert.IsLessThan(0.00001f, Vector3.Distance(smooth[key].Normal, vertex.Normal));
                shared++;
            }
        }
        Assert.IsGreaterThan(0, shared, "The fixture must share smooth vertices between separate mesh outputs.");
        Console.WriteLine($"Matched {shared} smooth vertices across output neighborhoods and validated {creases} oriented face normals.");
    }
}
