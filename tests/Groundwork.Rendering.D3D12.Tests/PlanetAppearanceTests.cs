using System.Numerics;
using System.Runtime.InteropServices;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Rendering.Materials;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OrbitAppearanceUsesTheAcceptedLeaseAndWaitsForProtectedFirstPresent(bool warp)
    {
        const int width = 1920;
        const int height = 1080;
        const ulong epoch = 17;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var runtime = CreateRuntime(timing);
        var context = new EngineModuleContext(TimeProvider.System, timing, runtime.Reporter, runtime.StopControl);
        var title = $"Groundwork orbit appearance {Guid.NewGuid():N}";
        await using var window = new WindowsHostModule(new WindowsHostOptions(title, width, height, 32), runtime.UiWake, runtime.RuntimeOwnership);

        var descriptor = new PlanetDescriptor(new("e2aba1c0-38cb-4cba-8411-2e6bb7c0bf37"),
            new("a73a1cde-df3f-4fc3-a509-be715dc16b20"), 42, 1, 1, 1, new string('a', 64), 64);
        var terrain = new PlanetTerrainCoordinator();
        var camera = AtRadius(96);
        var origin = PlanetLocalFrame.Create(camera);
        var plan = SelectFlight(96, 7, null, PlanetTerrainView.Far);
        using var orbit = StagePlanetPlan(terrain, plan, origin, camera);
        using var otherLease = StagePlanetPlan(new PlanetTerrainCoordinator(), plan, origin, camera);

        var fixture = new Int01Textures();
        var maps = fixture.Palette.Resolve(Int01Textures.Rock).Textures;
        var proxy = new PbrMaterialDescriptor(Vector3.One, 0.25f, 0.75f, 1, default, 1, maps);
        var palette = new MaterialPalette(1, [new(Int01Textures.OrbitProxy, proxy)]);
        D3D12TextureTarget[] targets =
        [
            Target(maps.BaseColor!), Target(maps.Normal!), Target(maps.Roughness!), Target(maps.Metallic!),
        ];
        const int allocationAlignment = 65_536;
        // Reserve one aligned native block for the material constants as well as the four textures.
        var stream = new D3D12TextureHandoff(new(targets.Length, targets.Length, 4096, 0, 4096,
            targets.Length * allocationAlignment, (targets.Length + 1) * allocationAlignment, targets.Length));
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<OffscreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var textured = new TaskCompletionSource<OffscreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var install = new ManualResetEventSlim();
        var copies = 0;
        var held = 0;
        D3D12Renderer? observedRenderer = null;
        D3D12TextureDiagnostics firstTextureDiagnostics = default;
        var generation = orbit.Generation;
        const int x = width / 2;
        const int y = height / 2;

        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, QualificationDiagnostics, warp,
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), null, _ =>
            {
                if (Interlocked.Exchange(ref held, 1) != 0)
                    return;
                prepared.TrySetResult();
                if (!install.Wait(TimeSpan.FromSeconds(8), token))
                    throw new TimeoutException("Orbit fixture did not release the protected installation.");
            })
        {
            BeforeTextureUploadForTests = (_, _) => Interlocked.Increment(ref copies),
            PlanetAppearanceReadbackForTests = (identity, sequence, observedGeneration, pixels) =>
            {
                if (sequence != 1 || observedGeneration != generation)
                    return;
                if (!first.Task.IsCompleted)
                {
                    Assert.AreEqual(0UL, identity);
                    firstTextureDiagnostics = observedRenderer!.ReadTextureDiagnostics();
                    first.TrySetResult(pixels);
                    return;
                }
                if (Volatile.Read(ref copies) == targets.Length &&
                    !pixels.ColorAt(x, y).SequenceEqual(first.Task.Result.ColorAt(x, y)))
                {
                    Assert.IsGreaterThan(0UL, identity);
                    textured.TrySetResult(pixels);
                }
            },
        };
        observedRenderer = renderer;
        renderer.AttachPlanetTextureHandoff(stream, terrain, descriptor, epoch);
        Assert.IsFalse(renderer.TryGetExpectedPlanetTextureView(orbit, out _));
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            SizeInt01Client(window, title, width, height);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            await AwaitStatus(renderer, state => state.AppliedWidth == width && state.AppliedHeight == height, token);
            AssertPlanetAdapter(renderer.ReadDiagnostics().Adapter, warp);
            var sunlight = new RenderSunlight(-FlightForward, new(0.15f), new(0.8f));
            Assert.AreEqual(orbit.Stamp, otherLease.Stamp);
            Assert.AreEqual(MeshReplacementSubmissionResult.InvalidRegion,
                renderer.TrySubmitPlanetTerrainWithAppearance(otherLease, 1, FlightForward, Vector3.UnitY, MathF.PI / 3,
                    NearPlane, FarPlane, Vector4.One, FlightClear, descriptor, [], Int01Textures.OrbitProxy, sunlight: sunlight));
            Assert.AreEqual(0UL, renderer.Read().LastAcceptedSequence);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitPlanetTerrainWithAppearance(orbit, 1, FlightForward, Vector3.UnitY, MathF.PI / 3,
                    NearPlane, FarPlane, Vector4.One, FlightClear, descriptor, [], Int01Textures.OrbitProxy, sunlight: sunlight));
            await prepared.Task.WaitAsync(token);
            Assert.IsTrue(renderer.TryGetExpectedPlanetTextureView(orbit, out var cut));
            Assert.IsFalse(renderer.TryGetExpectedPlanetTextureView(otherLease, out _));
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame, renderer.TrySubmitPlanetCamera(otherLease, 2,
                FlightForward, Vector3.UnitY, MathF.PI / 3, NearPlane, FarPlane, Vector4.One, FlightClear));
            Assert.AreEqual(new D3D12TextureView(descriptor.WorldId, new(epoch, generation, origin), plan.EditRevision, generation), cut);
            Assert.IsFalse(stream.PublishTerrainAppearance(cut with { World = Guid.NewGuid() }, palette, targets));
            Assert.IsFalse(stream.PublishTerrainAppearance(cut with { View = cut.View with { RendererEpoch = epoch + 1 } }, palette, targets));
            Assert.IsTrue(stream.PublishTerrainAppearance(cut, palette, targets));
            foreach (var target in targets)
            {
                var reservation = new D3D12TextureReservation(512, 0, 1024, allocationAlignment, allocationAlignment);
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(cut, target, reservation, out var ticket));
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, fixture[target.Reference]));
            }
            Assert.AreEqual(0, Volatile.Read(ref copies));
            Assert.IsFalse(first.Task.IsCompleted);
            Assert.IsFalse(renderer.WasPlanetTerrainPresented(orbit, 1));
            install.Set();

            var numeric = await first.Task.WaitAsync(token);
            Assert.IsGreaterThan(0L, firstTextureDiagnostics.ResidentAndPendingBytes);
            Assert.AreEqual(0UL, firstTextureDiagnostics.UploadedBytes);
            Assert.AreEqual(0L, firstTextureDiagnostics.StagingBytes);
            var mapped = await textured.Task.WaitAsync(token);
            Assert.IsLessThan(1f, numeric.DepthAt(x, y));
            Assert.AreEqual(numeric.DepthAt(x, y), mapped.DepthAt(x, y));
            var color = mapped.ColorAt(x, y).ToArray();
            Assert.IsGreaterThan(color[1], color[0]);
            Assert.IsGreaterThan(color[2], color[1]);
            Assert.AreEqual(targets.Length, Volatile.Read(ref copies));
            Assert.AreEqual((ulong)(targets.Length * 8 * 8 * 4), renderer.ReadTextureDiagnostics().UploadedBytes);
            Assert.AreEqual(1UL, renderer.Read().LastAcceptedSequence);
            Assert.AreEqual(1UL, renderer.Read().LastPresentedSequence);
            Assert.IsTrue(renderer.WasPlanetTerrainPresented(orbit, 1));
            Assert.IsFalse(renderer.WasPlanetTerrainPresented(orbit, 2));
            Assert.IsTrue(terrain.AcknowledgePresented(orbit));

            await renderer.StopAsync(token);
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            AssertCleanDiagnostics(renderer.ReadDiagnostics(), diagnosticsRequested: true);
        }
        finally
        {
            install.Set();
            terrain.Stop();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }

        static D3D12TextureTarget Target(MaterialTextureReference map) =>
            new(new(Int01Textures.OrbitProxy, map.Semantic), 1, map);
    }

    private static void SizeInt01Client(WindowsHostModule window, string title, int width, int height)
    {
        var handle = FindWindow(null, title);
        Assert.AreNotEqual(nint.Zero, handle);
        Assert.IsTrue(GetOrbitWindowBounds(handle, out var bounds));
        var client = window.Read();
        // WindowsHostOptions sizes the outer frame. Apply the requested client dimensions.
        Assert.IsTrue(SetWindowPos(handle, 0, 0, 0, bounds.Right - bounds.Left + width - client.ClientWidth,
            bounds.Bottom - bounds.Top + height - client.ClientHeight, 0x0016));
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOrbitWindowBounds(nint window, out OrbitWindowBounds bounds);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct OrbitWindowBounds
    {
        internal readonly int Left;
        internal readonly int Top;
        internal readonly int Right;
        internal readonly int Bottom;
    }
}
