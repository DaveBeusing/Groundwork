using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Groundwork.Voxels;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private const int Wp15Width = 1920;
    private const int Wp15Height = 1080;
    private const int Wp15Warmup = 32;
    private const int Wp15RouteSteps = 32;
    private const float Wp15DepthTolerance = 0.000001f;
    private const int Wp15MinimumDarkening = 4;

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void Wp15PairedNativeShadowTimings()
    {
        var output = Environment.GetEnvironmentVariable("GROUNDWORK_WP15_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            Assert.Inconclusive("Run benchmarks/Measure-Wp15Shadows.ps1 to collect paired shadow timings.");
            return;
        }

        Assert.IsTrue(int.TryParse(Environment.GetEnvironmentVariable("GROUNDWORK_WP15_PAIR"), out var pair) && pair > 0);
        var configuredFrames = Environment.GetEnvironmentVariable("GROUNDWORK_WP15_FRAMES") ?? "2000";
        Assert.IsTrue(int.TryParse(configuredFrames, out var frames) && frames is >= 1 and <= 10_000);
        var adapterName = Environment.GetEnvironmentVariable("GROUNDWORK_WP15_ADAPTER");
        Assert.IsTrue(adapterName is "Hardware" or "Warp");
        var warp = adapterName == "Warp";
        var region = CreateWp15Region();
        string[] phaseNames = ["midday", "low-sun", "quick-turns"];
        var phases = new Wp15PhaseResult[phaseNames.Length];
        for (var phaseIndex = 0; phaseIndex < phases.Length; phaseIndex++)
        {
            var phase = phaseNames[phaseIndex];
            var onFirst = pair % 2 == 0;
            var first = MeasureWp15Route(phase, region, onFirst, warp, frames, 1);
            var second = MeasureWp15Route(phase, region, !onFirst, warp, frames, 2);
            var off = onFirst ? second : first;
            var on = onFirst ? first : second;
            Assert.AreEqual(off.Route.Adapter, on.Route.Adapter);
            Assert.AreEqual(off.Route.GeometryBytes, on.Route.GeometryBytes);
            Assert.AreEqual(0UL, off.Route.ShadowBackingBytes);
            Assert.IsGreaterThan(0UL, on.Route.ShadowBackingBytes);
            var probes = new Wp15Probe[off.ProbeImages.Length];
            for (var index = 0; index < probes.Length; index++)
                probes[index] = CompareWp15Probe(phase, Wp15ProbeStep(index), off.ProbeImages[index], on.ProbeImages[index]);
            phases[phaseIndex] = new(phase, HashWp15Scene(phase, region), probes, [first.Route, second.Route]);
        }

        var adapter = phases[0].Routes[0].Adapter;
        foreach (var phase in phases)
            Assert.AreEqual(adapter, phase.Routes[0].Adapter);
        using var report = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(report, new
        {
            schema = 1,
            fixture = "wp15-synthetic-two-cascade-1080p-v1",
            pair,
            adapter,
            runtime = RuntimeInformation.FrameworkDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            stopwatchFrequency = Stopwatch.Frequency,
            width = Wp15Width,
            height = Wp15Height,
            warmupFrames = Wp15Warmup,
            routeSteps = Wp15RouteSteps,
            measuredFramesPerVariantPerPhase = frames,
            debugLayerEnabled = false,
            dredEnabled = false,
            geometryTriangles = region.Instances.ToArray().Sum(instance => instance.Mesh.Indices.Length / 3),
            shaderSha256 = HashWp15Shaders(),
            phases,
        }, JsonSerializerOptions.Web);
    }

    private static Wp15Observation MeasureWp15Route(string phase, RenderMeshRegion region, bool shadows, bool warp, int frames,
        int order)
    {
        long recordStart = 0;
        long recordTicks = 0;
        var hooks = new GpuRetirementTestHooks(
            RejectPreparedPlan: _ =>
            {
                recordStart = Stopwatch.GetTimestamp();
                return false;
            },
            RejectRecordedPlan: _ =>
            {
                recordTicks = Stopwatch.GetTimestamp() - recordStart;
                return false;
            });
        var diagnostics = new ResourceDiagnostics();
        var gpu = new GpuDevice(warp, retirementHooks: hooks, region: region, resourceDiagnostics: diagnostics);
        var observations = new Wp15Measurement[frames];
        var probeImages = new OffscreenFrame[2];
        Wp15RouteResult route;
        using (gpu)
        {
            AssertAdapter(gpu.Adapter, warp);
            Assert.IsFalse(gpu.DebugLayerEnabled);
            Assert.IsFalse(gpu.DredEnabled);
            gpu.EnableShadowTimingForTests();
            var target = CreateWp15Target(gpu);
            try
            {
                ulong sequence = 0;
                for (var index = 0; index < Wp15Warmup; index++)
                    Draw(index % Wp15RouteSteps, ++sequence);
                var before = gpu.RecordedShadowPasses;
                for (var index = 0; index < frames; index++)
                {
                    var start = Stopwatch.GetTimestamp();
                    Draw(index % Wp15RouteSteps, ++sequence);
                    var turnTicks = Stopwatch.GetTimestamp() - start;
                    var gpuTiming = gpu.ReadShadowTimingForTests(sequence);
                    Assert.AreEqual(shadows ? 2 : 0, gpuTiming.ShadowPassCount, $"{phase} step {index % Wp15RouteSteps}");
                    Assert.IsGreaterThan(0L, recordTicks);
                    Assert.IsGreaterThan(0L, turnTicks);
                    Assert.IsTrue(IsDuration(gpuTiming.FrameMicroseconds));
                    Assert.IsTrue(IsDuration(gpuTiming.TerrainMicroseconds));
                    Assert.IsTrue(IsDuration(gpuTiming.NearShadowMicroseconds));
                    Assert.IsTrue(IsDuration(gpuTiming.FarShadowMicroseconds));
                    if (!shadows)
                    {
                        Assert.AreEqual(0d, gpuTiming.NearShadowMicroseconds);
                        Assert.AreEqual(0d, gpuTiming.FarShadowMicroseconds);
                    }
                    observations[index] = new(index % Wp15RouteSteps, recordTicks, turnTicks, gpuTiming.FrameMicroseconds,
                        gpuTiming.TerrainMicroseconds, gpuTiming.NearShadowMicroseconds, gpuTiming.FarShadowMicroseconds,
                        gpuTiming.ShadowPassCount);
                }

                Assert.AreEqual(shadows ? (ulong)frames * 2 : 0UL, gpu.RecordedShadowPasses - before);
                Assert.IsTrue(observations.Any(sample => sample.FrameMicroseconds > 0 && sample.TerrainMicroseconds > 0));
                if (shadows)
                {
                    Assert.IsTrue(observations.Any(sample => sample.NearShadowMicroseconds > 0));
                    Assert.IsTrue(observations.Any(sample => sample.FarShadowMicroseconds > 0));
                }
                Assert.IsTrue(diagnostics.TryRead(out var snapshot));
                Assert.IsFalse(snapshot.LedgerOverflowed);
                Assert.IsFalse(snapshot.LedgerHasUnknownSizes);
                var shadow = snapshot.GetBalance(D3D12ResourceOwner.Shadow);
                Assert.AreEqual(shadows ? 2 : 0, shadow.Live);
                Assert.AreEqual(0, shadow.Retained);
                Assert.IsGreaterThan(0L, gpu.LiveMeshBytes);
                route = new(shadows ? "shadows-on" : "shadows-off", order, gpu.Adapter, gpu.LiveMeshBytes,
                    shadow.LiveBytes, observations);

                for (var probe = 0; probe < probeImages.Length; probe++)
                {
                    Draw(Wp15ProbeStep(probe), ++sequence);
                    probeImages[probe] = gpu.ReadRetiredPresent();
                }

                void Draw(int step, ulong id)
                {
                    var frame = CreateWp15Frame(phase, step, id, shadows);
                    Assert.AreEqual(GpuFrameSubmitResult.Submitted, gpu.TryRenderWithPresentReadback(target, frame, owner: null!));
                    ObserveWp15Retirement(gpu);
                }
            }
            finally
            {
                if (gpu.IsIdle || gpu.IsRemoved)
                    target.Dispose();
            }
        }

        Assert.IsNull(gpu.FinalDebugSummary);
        Assert.IsTrue(diagnostics.TryRead(out var released));
        var settled = released.GetBalance(D3D12ResourceOwner.Shadow);
        Assert.AreEqual(0, settled.Live + settled.Retained);
        Assert.AreEqual(shadows ? 2UL : 0UL, settled.Released);
        return new(route, probeImages);

        static bool IsDuration(double value) => double.IsFinite(value) && value >= 0;
    }

    private static RenderFrame CreateWp15Frame(string phase, int step, ulong sequence, bool shadows)
    {
        var centered = step - (Wp15RouteSteps - 1) / 2f;
        var (yaw, sun) = phase switch
        {
            "midday" => (centered * 0.003f, new Vector3(0.52f + step * 0.004f, 0.10f, 1f)),
            "low-sun" => (-centered * 0.002f, new Vector3(1f, -0.13f + step * 0.004f, 0.30f)),
            "quick-turns" => ((step % 2 == 0 ? -0.27f : 0.27f) + centered * 0.0015f,
                new Vector3(0.62f, 0.12f + step * 0.005f, 0.72f)),
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };
        var forward = new Vector3(MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var origin = PlanetLocalFrame.Create(default);
        var camera = new VolumeCamera(new(1, 1, origin), new(centered * 0.025, 0, 0), forward, Vector3.UnitY, 0.65f,
            0.1f, 100, Vector4.One, new(0.12f, 0.16f, 0.22f, 1))
        {
            Sunlight = new(Vector3.Normalize(sun), new(0.85f), new(0.12f)),
            Fog = new(new(0.18f, 0.24f, 0.32f), 18, 68, 0.25f),
            TerrainShadows = shadows,
        };
        Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, sequence, out var frame));
        return frame;
    }

    private static RenderMeshRegion CreateWp15Region() =>
        new([
            new(CreateWp15Grid(18, 10, -24, 48, 24), Vector3.Zero),
            new(CreateWp15Grid(35, 19, -48, 48, 24), Vector3.Zero),
            new(CreateWp15Grid(1, 1, -21.5f, 1, 1), Vector3.Zero),
            new(CreateWp15Grid(2, 2, -42, 1, 1), new Vector3(-20, -3, 0)),
        ]);

    private static RenderMesh CreateWp15Grid(float halfWidth, float halfHeight, float z, int columns, int rows)
    {
        var positions = new Vector3[(columns + 1) * (rows + 1)];
        var normals = new uint[positions.Length];
        Array.Fill(normals, PackedTerrainNormal.Encode(Vector3.UnitZ));
        var indices = new uint[columns * rows * 6];
        var index = 0;
        for (var y = 0; y <= rows; y++)
        for (var x = 0; x <= columns; x++)
        {
            var bottomLeft = (uint)(y * (columns + 1) + x);
            positions[bottomLeft] = new((2f * x / columns - 1) * halfWidth, (2f * y / rows - 1) * halfHeight, z);
            if (x == columns || y == rows)
                continue;
            var topLeft = bottomLeft + (uint)columns + 1;
            indices[index++] = bottomLeft;
            indices[index++] = bottomLeft + 1;
            indices[index++] = topLeft + 1;
            indices[index++] = bottomLeft;
            indices[index++] = topLeft + 1;
            indices[index++] = topLeft;
        }
        return RenderMesh.CreateWithTerrainNormals(positions, indices, [], normals);
    }

    private static int Wp15ProbeStep(int index) => index == 0 ? 0 : 17;

    private static Wp15Probe CompareWp15Probe(string phase, int step, OffscreenFrame off, OffscreenFrame on)
    {
        var commonReceivers = 0;
        var darkerReceivers = 0;
        var maximumDepthDelta = 0f;
        var darkestDelta = 0;
        for (var y = 0; y < Wp15Height; y++)
        for (var x = 0; x < Wp15Width; x++)
        {
            var unshadowed = off.DepthAt(x, y);
            var shadowed = on.DepthAt(x, y);
            maximumDepthDelta = Math.Max(maximumDepthDelta, Math.Abs(unshadowed - shadowed));
            if (unshadowed >= 1 || shadowed >= 1)
                continue;
            commonReceivers++;
            var darkening = off.ColorAt(x, y)[0] - on.ColorAt(x, y)[0];
            if (darkening >= Wp15MinimumDarkening)
                darkerReceivers++;
            darkestDelta = Math.Max(darkestDelta, darkening);
        }
        Assert.IsLessThanOrEqualTo(Wp15DepthTolerance, maximumDepthDelta, $"{phase}, step {step} depth");
        Assert.IsGreaterThan(0, commonReceivers, $"{phase}, step {step} receiver coverage");
        Assert.IsGreaterThan(0, darkerReceivers, $"{phase}, step {step} shadowed receiver pixels");
        return new(step, commonReceivers, darkerReceivers, maximumDepthDelta, darkestDelta);
    }

    private static unsafe GpuTarget CreateWp15Target(GpuDevice gpu)
    {
        var color = CreateResource(gpu.Device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
            Texture(Wp15Width, Wp15Height, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET), D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON);
        try { return gpu.Attach(color, Wp15Width, Wp15Height); }
        catch
        {
            Release(color);
            throw;
        }
    }

    private static void ObserveWp15Retirement(GpuDevice gpu)
    {
        var start = Stopwatch.GetTimestamp();
        var spin = new SpinWait();
        while (!gpu.IsIdle)
        {
            if (gpu.IsRemoved)
                throw new GpuNativeException("Wait for WP15 timing fence", gpu.RemovedReason);
            if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(10))
                throw new TimeoutException("The offscreen WP15 timing frame did not retire.");
            spin.SpinOnce();
        }
    }

    private static string HashWp15Scene(string phase, RenderMeshRegion region)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(Encoding.UTF8.GetBytes(phase));
        AddValue(Wp15Width);
        AddValue(Wp15Height);
        foreach (var instance in region.Instances)
        {
            AddValue(instance.Translation);
            Add(MemoryMarshal.AsBytes(instance.Mesh.Positions));
            Add(MemoryMarshal.AsBytes(instance.Mesh.Indices));
            Add(MemoryMarshal.AsBytes(instance.Mesh.PackedNormals));
        }
        for (var step = 0; step < Wp15RouteSteps; step++)
        {
            var on = CreateWp15Frame(phase, step, 1, shadows: true);
            var off = CreateWp15Frame(phase, step, 1, shadows: false);
            Assert.IsTrue(on.Scene.HasValue);
            var scene = on.Scene.Value;
            Assert.AreEqual(off, on with { Scene = scene with { ShadowOrigin = null } });
            AddValue(on.ClearColor);
            AddValue(scene.World);
            AddValue(scene.View);
            AddValue(scene.Tint);
            AddValue(scene.VerticalFieldOfView);
            AddValue(scene.NearPlane);
            AddValue(scene.FarPlane);
            AddValue(scene.Sunlight!.Value);
            AddValue(scene.Fog!.Value);
            AddValue(scene.ShadowOrigin!.Value);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Add(ReadOnlySpan<byte> bytes)
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        void AddValue<T>(T value) where T : unmanaged =>
            Add(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)));
    }

    private static Dictionary<string, string> HashWp15Shaders()
    {
        string[] names = ["TerrainNormals.vs.dxil", "Cube.ps.dxil", "ShadowTerrain.ps.dxil", "ShadowDepth.vs.dxil"];
        var hashes = new Dictionary<string, string>(names.Length);
        foreach (var name in names)
        {
            using var shader = typeof(GpuDevice).Assembly.GetManifestResourceStream(name);
            Assert.IsNotNull(shader);
            hashes.Add(name, Convert.ToHexStringLower(SHA256.HashData(shader)));
        }
        return hashes;
    }

    private sealed record Wp15PhaseResult(string Phase, string SceneSha256, Wp15Probe[] Probes, Wp15RouteResult[] Routes);
    private sealed record Wp15Observation(Wp15RouteResult Route, OffscreenFrame[] ProbeImages);
    private sealed record Wp15RouteResult(string Route, int Order, D3D12AdapterInfo Adapter, long GeometryBytes,
        ulong ShadowBackingBytes, Wp15Measurement[] Measurements);
    private readonly record struct Wp15Measurement(int Step, long CpuRecordTicks, long TurnTicks, double FrameMicroseconds,
        double TerrainMicroseconds, double NearShadowMicroseconds, double FarShadowMicroseconds, int ShadowPassCount);
    private readonly record struct Wp15Probe(int Step, int CommonReceiverPixels, int DarkerReceiverPixels,
        float MaximumDepthDelta, int MaximumRedDarkening);
}
