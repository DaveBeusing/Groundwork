using System.Numerics;
using Groundwork.Core;
using Groundwork.Core.Tests;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public async Task WarpCubeSphereTransitionsMatchCollisionAcrossRebase()
    {
        var gpu = new GpuDevice(warp: true);
        try
        {
            AssertPlanetAdapter(gpu.Adapter, useWarp: true);

            // Directions with exactly two maximal components are edge midpoints.
            // Directions with three maximal components are corners.
            var edges = 0;
            var corners = 0;
            foreach (var x in new[] { -32, 0, 32 })
            foreach (var y in new[] { -32, 0, 32 })
            foreach (var z in new[] { -32, 0, 32 })
            {
                var components = (x != 0 ? 1 : 0) + (y != 0 ? 1 : 0) + (z != 0 ? 1 : 0);
                if (components < 2)
                    continue;
                if (components == 2)
                    edges++;
                else
                    corners++;
                await VerifySeam(gpu, new(x, y, z));
            }

            Assert.AreEqual(12, edges);
            Assert.AreEqual(8, corners);
            gpu.ActiveMeshGeometry?.Dispose();
            gpu.ActiveMeshGeometry = null;
            Assert.AreEqual(0L, gpu.LiveMeshBytes);
        }
        finally
        {
            if (gpu.IsIdle || gpu.IsRemoved)
                gpu.Dispose();
        }
    }

    private static async Task VerifySeam(GpuDevice gpu, PlanetVector seam)
    {
        var axis = seam.X != 0 ? 0 : 1;
        var normalAxis = (axis + 1) % 3;
        var center = Add(seam, Axis(axis, -Component(seam, axis)));
        var direction = Axis(axis, -Math.Sign(Component(seam, axis)));
        var normal = Axis(normalAxis, 1);
        var eye = Add(seam, Axis(normalAxis, 4));
        var farPoint = Add(seam, Axis(normalAxis, -4));
        var displacement = Axis(normalAxis, -8);
        var sphere = new VolumeCapsule(eye, eye, 0.25);

        using var run = new VolumeRun(camera: center, forward: direction);
        var source = new SeamPlane(run.Descriptor, normalAxis, (long)Component(seam, normalAxis));
        var origin = run.Terrain.View.Frame;
        var firstView = run.Terrain.ChangeView(origin, 1);
        await PublishSeam(run, source, firstView, 0);
        using var original = run.Terrain.TryRead();
        Assert.IsNotNull(original);
        Assert.AreEqual(firstView, original.Info.View);
        Assert.AreEqual(1UL, original.Info.TerrainRevision);
        var hit = CheckCollision(original, eye, farPoint, sphere, displacement, seam);
        var first = RenderSeam(gpu, original, firstView, eye, normal, hit.Distance, seam);
        using var firstFrame = run.Terrain.TryRegisterFrame(original.Info.Generation, firstView, 1);
        Assert.IsNotNull(firstFrame);
        await AdmitReadback(run, firstFrame, center);
        using var pendingOldFrame = run.Terrain.TryRegisterFrame(original.Info.Generation, firstView, 2);
        Assert.IsNotNull(pendingOldFrame);
        Assert.IsTrue(run.Terrain.IsCurrentFrame(pendingOldFrame));

        Assert.IsTrue(origin.TryRebase(Add(center, Axis(axis, 24)), out var rebased));
        var nextView = run.Terrain.ChangeView(rebased, 1);
        Assert.IsFalse(run.Terrain.HasAdmittedCoverage(center));
        Assert.IsFalse(run.Terrain.AcknowledgeFrame(pendingOldFrame));
        await PublishSeam(run, source, nextView, 1);
        using var replacement = run.Terrain.TryRead();
        Assert.IsNotNull(replacement);
        Assert.AreEqual(nextView, replacement.Info.View);
        Assert.IsFalse(run.Terrain.HasAdmittedCoverage(center));
        Assert.AreNotEqual(original.Info.Generation, replacement.Info.Generation);
        Assert.AreEqual(original.Info.TerrainRevision, replacement.Info.TerrainRevision);
        Assert.AreEqual(original.Info.PlanIdentity, replacement.Info.PlanIdentity);
        Assert.AreEqual(hit, CheckCollision(replacement, eye, farPoint, sphere, displacement, seam));
        Assert.AreEqual(hit, CheckCollision(original, eye, farPoint, sphere, displacement, seam));
        var second = RenderSeam(gpu, replacement, nextView, eye, normal, hit.Distance, seam);
        Assert.AreEqual(first, second, 1e-6f, $"Center pixel depth changed at {seam}.");
        using var secondFrame = run.Terrain.TryRegisterFrame(replacement.Info.Generation, nextView, 3);
        Assert.IsNotNull(secondFrame);
        await AdmitReadback(run, secondFrame, center);
    }

    // The offscreen test supplies the ACK only after color and depth have been read back.
    private static async Task AdmitReadback(VolumeRun run, VolumeFrameRegistration registration, PlanetVector center)
    {
        Assert.IsTrue(run.Terrain.AcknowledgeFrame(registration));
        Assert.IsFalse(run.Terrain.HasAdmittedCoverage(center));
        await run.OpenMovement();
        Assert.IsTrue(run.Terrain.HasAdmittedCoverage(center));
    }

    private static async Task PublishSeam(VolumeRun run, SeamPlane source,
        VolumeViewIdentity view, ulong previousRevision)
    {
        Assert.IsTrue(run.Terrain.ConfirmMask(view));
        using var work = run.Stage(VolumeWorkClass.Movement, previousRevision, 1, cut: false, source);
        Assert.IsTrue(work.Execute());
        var tick = await run.BeginHeldTick();
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, tick.Tick, BarrierKind.CommitResults);
        var prepared = run.Terrain.TryPreparePublication(work.Generation, candidates, []);
        Assert.IsNotNull(prepared);
        var committed = run.World.Publication.Finalization.TryFinalize(prepared);
        Assert.AreEqual(BarrierStatus.Committed, committed.Status);
        CoreRuntimeFixture.PublishObserver(run.World, committed.Stamp!.Value, (int)tick.Tick);
    }

    private static VolumeRayHit CheckCollision(VolumeStreamingLease terrain, PlanetVector eye, PlanetVector far,
        VolumeCapsule sphere, PlanetVector displacement, PlanetVector seam)
    {
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[4];
        var ray = terrain.Raycast(eye, far, hits);
        Assert.AreEqual(VolumeQueryStatus.Contact, ray.Status, $"Ray at {seam}.");
        Assert.AreEqual(1, ray.Count);
        var hit = hits[0];
        Assert.IsTrue(hit.IsTransition, $"Ray must hit transition triangles at {seam}.");
        Assert.AreEqual(4, hit.Distance, 2e-4);
        var sweep = terrain.Sweep(sphere, displacement);
        Assert.AreEqual(VolumeQueryStatus.Contact, sweep.Status, $"Sweep at {seam}.");
        Assert.IsTrue(sweep.IsTransition, $"Sweep must hit transition triangles at {seam}.");
        Assert.AreEqual(3.75 / 8, sweep.Fraction, 2e-4);
        return hit;
    }

    private static float RenderSeam(GpuDevice gpu, VolumeStreamingLease terrain, VolumeViewIdentity view,
        PlanetVector eye, PlanetVector normal, double distance, PlanetVector seam)
    {
        const float near = 0.1f;
        const float far = 100;
        const int size = 65; // Odd dimensions put one pixel center on the collision ray.
        var region = D3D12Renderer.PackVolumeTerrain(terrain.Meshes, view.Frame, VolumeRun.NativeBytes);
        Assert.IsNotNull(region);
        var previous = gpu.ActiveMeshGeometry;
        gpu.ActiveMeshGeometry = gpu.PrepareMeshGeometry(region, VolumeRun.NativeBytes);
        previous?.Dispose();
        var forward = new Vector3((float)-normal.X, (float)-normal.Y, (float)-normal.Z);
        var up = normal.Y == 0 ? Vector3.UnitY : Vector3.UnitZ;
        var camera = new VolumeCamera(view, eye, forward, up, MathF.PI / 4, near, far, Vector4.One, Vector4.Zero);
        Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, view.View, out var frame));
        var image = GpuDevice.RenderOffscreen(gpu, size, size, frame);
        var expectedDepth = (float)(far / (far - near) * (1 - near / distance));
        ReadOnlySpan<byte> expectedColor = [0x5a, 0x90, 0x50, 0xff];
        for (var y = 29; y <= 35; y++)
        for (var x = 29; x <= 35; x++)
        {
            var pixel = image.ColorAt(x, y);
            Assert.IsTrue(pixel.SequenceEqual(expectedColor), $"Color at {seam} pixel {x},{y}.");
            Assert.AreEqual(expectedDepth, image.DepthAt(x, y), 2e-5f, $"Depth at {seam} pixel {x},{y}.");
        }
        return image.DepthAt(size / 2, size / 2);
    }

    private static PlanetVector Axis(int axis, double value) => axis switch
    {
        0 => new(value, 0, 0),
        1 => new(0, value, 0),
        _ => new(0, 0, value),
    };

    private static PlanetVector Add(PlanetVector a, PlanetVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static double Component(PlanetVector value, int axis) => axis switch
    {
        0 => value.X,
        1 => value.Y,
        _ => value.Z,
    };

    private sealed class SeamPlane(PlanetDescriptor descriptor, int axis, long offset) : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => descriptor;
        public long SampleDensity(LatticeCoordinate p) => (axis == 0 ? p.X : axis == 1 ? p.Y : p.Z) - offset;
        public ushort SampleMaterial(LatticeCoordinate cell) => 1;
    }
}
