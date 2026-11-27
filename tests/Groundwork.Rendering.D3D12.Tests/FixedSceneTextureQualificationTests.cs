using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    public async Task FixedSessionTextureCopyQualificationRejectsBothEmptyMeshes()
    {
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        var runtime = CreateRuntime(timing);
        var options = QualificationDiagnostics with
        {
            EnableResourceSnapshots = true,
            TextureCopyQualification = new(),
        };
        using var session = new D3D12DeviceSession(options, useWarp: true);
        var stream = new D3D12TextureHandoff(new(1, 1, 4096, 4096, 4096, 65_536, 65_536, 1));
        var view = new D3D12TextureView(Guid.NewGuid(), new(2, 1, PlanetLocalFrame.Create(default)), 1, 1);
        var empty = new RenderMeshInstance(RenderMesh.Create([], []), Vector3.Zero);
        var pair = new RenderMeshPair(empty, empty);
        await using var window = new WindowsHostModule(new WindowsHostOptions("Groundwork empty qualification scene", 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);

        var error = Assert.ThrowsExactly<ArgumentException>(() => D3D12Renderer.CreateWithSessionMeshesForTextureCopyQualification(
            window, runtime.RuntimeOwnership, pair, session, stream, view));
        Assert.AreEqual("pair", error.ParamName);
        await using var ordinary = D3D12Renderer.CreateWithSessionMeshes(window, runtime.RuntimeOwnership, pair, session);
        Assert.AreEqual(RendererLifecycle.Created, ordinary.Read().Lifecycle);
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public Task WarpFixedSessionTextureCopyQualificationKeepsSceneUntextured() =>
        VerifyFixedSessionTextureCopyQualification(warp: true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public Task NvidiaFixedSessionTextureCopyQualificationKeepsSceneUntextured() =>
        VerifyFixedSessionTextureCopyQualification(warp: false);

    private static async Task VerifyFixedSessionTextureCopyQualification(bool warp)
    {
        const int bytes = 512 * 1024 * 4;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var qualification = new D3D12TextureCopyQualification();
        var options = QualificationDiagnostics with { EnableResourceSnapshots = true, TextureCopyQualification = qualification };
        using var session = new D3D12DeviceSession(options, useWarp: warp, reuseDirectQueue: true);
        using var ordinarySession = new D3D12DeviceSession(QualificationDiagnostics, useWarp: warp);
        var stream = new D3D12TextureHandoff(new(2, 2, 2 * (bytes + 4096), 2 * (bytes + 4096),
            2 * (bytes + 4096), 2 * (bytes + 65_536), 2 * (bytes + 65_536), 2));
        var view = new D3D12TextureView(Guid.NewGuid(), new(2, 1, PlanetLocalFrame.Create(default)), 1, 1);
        var mesh = RenderMesh.Create([new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0, 0.5f, 0.5f)], [0, 1, 2]);
        var pair = new RenderMeshPair(new(mesh, Vector3.Zero), new(RenderMesh.Create([], []), Vector3.Zero));
        var asset = CookPressureTexture(512, 1024, 1);
        var target = PressureTarget(1, asset);
        var other = PressureTarget(2, asset);
        var reservation = new D3D12TextureReservation(bytes + 256, bytes, bytes + 256, bytes, bytes);
        var scene = new RenderScene(Matrix4x4.Identity, Vector4.One, Matrix4x4.Identity, 1, 0.2f, 20);
        var title = $"Groundwork fixed-scene qualification {Guid.NewGuid():N}";
        await using var window = new WindowsHostModule(new WindowsHostOptions(title, 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        Assert.ThrowsExactly<ArgumentException>(() => D3D12Renderer.CreateWithSessionMeshesForTextureCopyQualification(
            window, runtime.RuntimeOwnership, pair, ordinarySession, stream, view));
        Assert.ThrowsExactly<ArgumentException>(() => D3D12Renderer.CreateWithSessionMeshesForTextureCopyQualification(
            window, runtime.RuntimeOwnership, pair, session, stream, default));
        await using var renderer = D3D12Renderer.CreateWithSessionMeshesForTextureCopyQualification(
            window, runtime.RuntimeOwnership, pair, session, stream, view, timing.Shutdown, timing.BorrowedDrain);
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            AssertPlanetAdapter(renderer.ReadDiagnostics().Adapter, warp);

            await Present(new(1, Vector4.Zero));
            Assert.IsFalse(stream.Publish(view with { CoverageGeneration = 2 }, 1, [target]));
            Assert.IsTrue(stream.Publish(view, 1, [target, other]));
            Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(view, target, reservation, out var ticket));
            using var hold = qualification.Arm(ticket);
            Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, asset));
            await Task.Delay(100, token);
            Assert.IsFalse(hold.Submitted.IsCompleted, "A clear-only Present must not authorize the diagnostic copy.");
            Assert.AreEqual(0UL, renderer.ReadTextureDiagnostics().UploadedBytes);

            await Present(new(2, Vector4.Zero, scene));
            var submitted = await hold.Submitted.WaitAsync(TimeSpan.FromSeconds(4), token);
            Assert.AreEqual(ticket, submitted.Ticket);
            Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var before));
            Assert.AreEqual(2UL, before.PresentedSequence);
            Assert.AreEqual(submitted.CopyFence, before.TextureCopyFence);
            Assert.IsLessThan(submitted.CopyFence, before.CompletedFence);
            Assert.IsGreaterThan(0UL, before.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes);
            Assert.IsTrue(before.TryGetPass(GpuPassPlan.Terrain, out var beforeTerrain));
            Assert.IsGreaterThanOrEqualTo(submitted.SubmittedAt, hold.Release());
            await AwaitStatus(renderer, _ => renderer.TryReadResourceDiagnostics(out var retired) &&
                retired.CompletedFence >= submitted.CopyFence &&
                retired.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes == 0 &&
                retired.GetBalance(D3D12ResourceOwner.Texture).LiveBytes == bytes, token);

            Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(view, other, reservation, out var unarmed));
            Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(unarmed, asset));
            await AwaitStatus(renderer, _ => stream.ReadUsage() is { Requests: 0, Descriptors: 1 }, token);
            Assert.AreEqual((ulong)bytes, renderer.ReadTextureDiagnostics().UploadedBytes);

            await Present(new(3, Vector4.Zero, scene));
            Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var after));
            Assert.AreEqual(3UL, after.FrameSequence);
            Assert.IsTrue(after.TryGetPass(GpuPassPlan.Terrain, out var afterTerrain));
            Assert.AreEqual(beforeTerrain, afterTerrain, "A resident qualification copy must never be a frame texture.");
            Assert.AreEqual(bytes, (long)after.GetBalance(D3D12ResourceOwner.Texture).LiveBytes);

            session.Dispose();
            await renderer.StopAsync(token);
            await window.StopAsync(token);
            Assert.AreEqual(default, stream.ReadUsage());
            Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var final));
            Assert.AreEqual(D3D12CustodyStatus.None, final.Custody);
            Assert.AreEqual(0UL, final.GetBalance(D3D12ResourceOwner.Texture).LiveBytes);
            Assert.AreEqual(0UL, final.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes);
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
        }
        finally { runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation); }

        async Task Present(RenderFrame frame)
        {
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmit(frame));
            Assert.AreEqual(frame.Sequence, (await renderer.WaitForPresentedAsync(frame.Sequence, token)).LastPresentedSequence);
        }
    }
}
