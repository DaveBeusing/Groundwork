using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Passes;
using Groundwork.Voxels;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private const int Int01TimingWidth = 1920;
    private const int Int01TimingHeight = 1080;
    private const int Int01RouteSteps = 32;
    private const int Int01WarmupFrames = 32;

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task Int01PairedNativeMaterialTimings()
    {
        var output = Environment.GetEnvironmentVariable("GROUNDWORK_INT01_OUTPUT");
        if (string.IsNullOrEmpty(output))
        {
            Assert.Inconclusive("Run benchmarks/Measure-Int01Materials.ps1 to collect a paired observation.");
            return;
        }
        Assert.IsTrue(int.TryParse(Environment.GetEnvironmentVariable("GROUNDWORK_INT01_PAIR"), out var pair) && pair > 0);
        Assert.IsTrue(int.TryParse(Environment.GetEnvironmentVariable("GROUNDWORK_INT01_FRAMES"), out var frames) &&
            frames is >= 1 and <= 10_000);
        var warp = string.Equals(Environment.GetEnvironmentVariable("GROUNDWORK_INT01_ADAPTER"), "Warp", StringComparison.OrdinalIgnoreCase);
        var diagnosticsEnabled = Environment.GetEnvironmentVariable("GROUNDWORK_INT01_DEBUG") == "1";
        var assets = new Int01Textures();
        using var near = new VolumeRun(editable: true, openFloor: true,
            material: static point => point.X < 0 ? Int01Textures.Rock : Int01Textures.Ore);
        Assert.IsTrue(near.Terrain.ConfirmMask(near.Terrain.View));
        await near.Publish(1, cut: false);
        using var volume = near.Terrain.TryRead();
        Assert.IsNotNull(volume);
        var nearInfo = volume.Info;
        var nearRegion = D3D12Renderer.PackVolumeTerrain(volume.Meshes, nearInfo.View.Frame, VolumeRun.NativeBytes,
            long.MaxValue, null, out var rejected, includeTerrainAppearance: true);
        Assert.IsNotNull(nearRegion);
        Assert.IsFalse(rejected);
        var nearView = new D3D12TextureView(near.Descriptor.WorldId, nearInfo.View, nearInfo.TerrainRevision, nearInfo.Generation);

        // Match the windowed orbit fixture's uncarved sphere, proxy material and initial camera.
        var descriptor = new PlanetDescriptor(new("e2aba1c0-38cb-4cba-8411-2e6bb7c0bf37"),
            new("a73a1cde-df3f-4fc3-a509-be715dc16b20"), 42, 1, 1, 1, new string('a', 64), 64);
        var terrain = new PlanetTerrainCoordinator();
        var camera = AtRadius(96);
        var frame = PlanetLocalFrame.Create(camera);
        var plan = SelectFlight(96, 7, null, PlanetTerrainView.Far);
        using var orbit = StagePlanetPlan(terrain, plan, frame, camera);
        var orbitRegion = D3D12Renderer.PackPlanetTerrainWithAppearance(orbit, descriptor, [], Int01Textures.OrbitProxy);
        var proxy = new PbrMaterialDescriptor(Vector3.One, 0.25f, 0.75f, 1, default, 1,
            assets.Palette.Resolve(Int01Textures.Rock).Textures);
        var orbitPalette = new MaterialPalette(1, [new(Int01Textures.OrbitProxy, proxy)]);
        var orbitView = new D3D12TextureView(descriptor.WorldId, new(17, orbit.Generation, frame),
            plan.EditRevision, orbit.Generation);
        var phases = new Int01Phase[]
        {
            new("near", nearView, nearRegion, assets.Palette, NearFrame, [Int01TimingWidth * 3 / 8, Int01TimingWidth * 5 / 8]),
            new("orbit", orbitView, orbitRegion, orbitPalette, OrbitFrame, [Int01TimingWidth / 2]),
        };

        var results = new Int01PhaseResult[phases.Length];
        for (var phaseIndex = 0; phaseIndex < phases.Length; phaseIndex++)
        {
            var phase = phases[phaseIndex];
            var targets = Targets(phase.Palette);
            Assert.HasCount(phase.Name == "near" ? 8 : 4, targets);
            var routes = new Int01RouteResult[2];
            for (var routeIndex = 0; routeIndex < routes.Length; routeIndex++)
                routes[routeIndex] = MeasureInt01Route(phase, assets, targets, (pair + routeIndex) % 2 == 0,
                    warp, diagnosticsEnabled, frames, routeIndex + 1);
            Assert.AreEqual(routes[0].Adapter, routes[1].Adapter);
            CollectionAssert.AreEqual(routes[0].ProbeDepth, routes[1].ProbeDepth);
            Assert.IsFalse(routes[0].ProbeRgba.SequenceEqual(routes[1].ProbeRgba));
            results[phaseIndex] = new(phase.Name, HashInt01Scene(phase, targets),
                phase.Region.Instances.Length, phase.Region.Instances.ToArray().Sum(instance => instance.Mesh.Indices.Length / 3),
                targets.Select(target => new Int01Asset(target.Slot.Material, target.Slot.Semantic.ToString(), target.Reference.AssetId)).ToArray(),
                routes);
        }
        terrain.Stop();
        Assert.AreEqual(results[0].Routes[0].Adapter, results[1].Routes[0].Adapter);

        using var report = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(report, new
        {
            schema = 1,
            fixture = "int01-near-orbit-materials-v1",
            pair,
            adapter = results[0].Routes[0].Adapter,
            runtime = RuntimeInformation.FrameworkDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            debugLayerAndDred = diagnosticsEnabled,
            width = Int01TimingWidth,
            height = Int01TimingHeight,
            stopwatchFrequency = Stopwatch.Frequency,
            measuredFramesPerVariantPerPhase = frames,
            warmupFramesPerVariantPerPhase = Int01WarmupFrames,
            shaderSha256 = HashInt01Shaders(),
            phases = results,
        }, JsonSerializerOptions.Web);

        RenderFrame NearFrame(int step, ulong sequence)
        {
            var centered = step - Int01RouteSteps / 2;
            var input = VolumeCameraAt(nearInfo.View, centered * 0.035, 7 + Math.Abs(centered) * 0.015) with
            {
                Sunlight = new(Vector3.UnitZ, new(0.45f), new(0.85f)),
                TerrainShadows = true,
            };
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(input, sequence, out var current));
            return current;
        }

        RenderFrame OrbitFrame(int step, ulong sequence)
        {
            var centered = step - Int01RouteSteps / 2;
            var forward = Vector3.Normalize(FlightForward + Vector3.Cross(FlightForward, Vector3.UnitY) * (centered * 0.0015f));
            return D3D12Renderer.CreatePlanetFrame(orbit, sequence, forward, Vector3.UnitY, MathF.PI / 3,
                NearPlane, FarPlane, Vector4.One, FlightClear, new RenderSunlight(-FlightForward, new(0.15f), new(0.8f)));
        }
    }

    private static Int01RouteResult MeasureInt01Route(Int01Phase phase, Int01Textures assets,
        D3D12TextureTarget[] targets, bool appearanceEnabled, bool warp, bool diagnosticsEnabled, int frames, int order)
    {
        const int allocationBytes = 65_536;
        var stream = new D3D12TextureHandoff(new(targets.Length, targets.Length, 16_384, 0, 16_384,
            targets.Length * allocationBytes, (targets.Length + 1) * allocationBytes, targets.Length));
        Assert.IsTrue(stream.PublishTerrainAppearance(phase.View, phase.Palette, targets));
        var maps = new Dictionary<D3D12TextureTicket, GpuTerrainMap>(targets.Length);
        var diagnostics = new ResourceDiagnostics();
        long recordingStart = 0, recordTicks = 0;
        long stagingHighWater = 0;
        ulong retainedHighWater = 0;
        var hooks = new GpuRetirementTestHooks(
            RejectPreparedPlan: _ =>
            {
                recordingStart = Stopwatch.GetTimestamp();
                return false;
            },
            RejectRecordedPlan: _ =>
            {
                recordTicks = Stopwatch.GetTimestamp() - recordingStart;
                return false;
            },
            SignalFailure: (_, _) =>
            {
                Assert.IsTrue(diagnostics.TryRead(out var held));
                retainedHighWater = Math.Max(retainedHighWater, held.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes);
                return null;
            });
        var gpu = new GpuDevice(warp, enableDebugLayer: diagnosticsEnabled, enableDred: diagnosticsEnabled,
            retirementHooks: hooks, region: phase.Region, resourceDiagnostics: diagnostics);
        var measurements = new Int01Frame[frames];
        var depths = new float[phase.ProbeXs.Length];
        var rgba = new uint[phase.ProbeXs.Length];
        Int01Backing backing;
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.AreEqual(diagnosticsEnabled, gpu.DebugLayerEnabled);
            gpu.EnableTerrainTimingForTests();
            if (appearanceEnabled)
            {
                foreach (var target in targets)
                    Upload(target);

                Span<D3D12TerrainTextureBinding> bindings = new D3D12TerrainTextureBinding[targets.Length];
                Assert.IsTrue(stream.TryCaptureTerrainAppearance(phase.View, bindings, out var palette, out var count));
                Assert.IsNotNull(palette);
                Assert.AreEqual(targets.Length, count);
                var nativeMaps = new List<GpuTerrainMap>(maps.Count);
                foreach (var binding in bindings[..count])
                    if (binding.Resident is { } resident)
                        nativeMaps.Add(maps[resident]);
                Assert.HasCount(targets.Length, nativeMaps);
                var materials = new GpuTerrainMaterialSet(phase.View, palette, bindings[..count], nativeMaps.ToArray());
                gpu.SelectTerrainMaterials(materials, stream);
            }
            else
                gpu.SelectTerrainMaterials(null, null);

            var targetBuffer = CreateInt01Target(gpu);
            try
            {
                ulong sequence = 0;
                for (var index = 0; index < Int01WarmupFrames; index++)
                    Draw(index % Int01RouteSteps, ++sequence);
                var residentBacking = gpu.TextureStatistics.NativeBytes;
                var uploadBytes = gpu.TextureStatistics.UploadedBytes;
                Assert.AreEqual(appearanceEnabled ? (ulong)(targets.Length * 8 * 8 * 4) : 0UL, uploadBytes);
                Assert.AreEqual(maps.Count, stream.ReadUsage().Descriptors);
                Assert.AreEqual(0L, gpu.TextureStatistics.StagingBytes);
                for (var index = 0; index < frames; index++)
                {
                    var start = Stopwatch.GetTimestamp();
                    Draw(index % Int01RouteSteps, ++sequence);
                    var turnTicks = Stopwatch.GetTimestamp() - start;
                    Assert.IsGreaterThan(0L, recordTicks);
                    measurements[index] = new(recordTicks, gpu.ReadTerrainTimingForTests(sequence), turnTicks);
                }

                Assert.AreEqual(uploadBytes, gpu.TextureStatistics.UploadedBytes);
                Assert.AreEqual(residentBacking, gpu.TextureStatistics.NativeBytes);
                Assert.AreEqual(0L, gpu.TextureStatistics.StagingBytes);
                Assert.IsTrue(diagnostics.TryRead(out var snapshot));
                Assert.IsFalse(snapshot.LedgerOverflowed);
                Assert.IsTrue(snapshot.TryGetPass(GpuPassPlan.Terrain, out var pass));
                Assert.AreEqual(maps.Count + (appearanceEnabled ? 1 : 0) + (phase.Name == "near" ? 5 : 3), pass.ResourceUses);
                var textureBacking = maps.Values.Aggregate(0UL, (sum, map) =>
                {
                    Assert.IsTrue(gpu.TryGetTextureRequirements(assets[map.Target.Reference], out var physical));
                    return sum + (ulong)physical.TextureAllocationBytes;
                });
                var constantBacking = checked((ulong)residentBacking - textureBacking);
                Assert.AreEqual(appearanceEnabled, constantBacking > 0);
                backing = new(maps.Count, GpuTerrainMaterialSet.Capacity, residentBacking, constantBacking,
                    gpu.TextureStatistics.NativePeakBytes, stagingHighWater, retainedHighWater, 0, uploadBytes, pass.ResourceUses,
                    gpu.LiveMeshBytes, snapshot.GetBalance(D3D12ResourceOwner.Shadow).LiveBytes);

                // Only this untimed probe maps the reused 1080p readback buffer into managed pixels.
                Draw(Int01RouteSteps / 2, ++sequence);
                var image = gpu.ReadRetiredPresent();
                for (var index = 0; index < phase.ProbeXs.Length; index++)
                {
                    var x = phase.ProbeXs[index];
                    depths[index] = image.DepthAt(x, Int01TimingHeight / 2);
                    Assert.IsLessThan(1f, depths[index]);
                    rgba[index] = BinaryPrimitives.ReadUInt32LittleEndian(image.ColorAt(x, Int01TimingHeight / 2));
                }

                void Draw(int step, ulong sequence)
                {
                    var current = phase.Frame(step, sequence);
                    Assert.AreEqual(GpuFrameSubmitResult.Submitted, gpu.TryRenderWithPresentReadback(targetBuffer, current, owner: null!));
                    ObserveInt01Retirement(gpu);
                }
            }
            finally
            {
                if (gpu.IsIdle || gpu.IsRemoved)
                    targetBuffer.Dispose();
            }

            gpu.SelectTerrainMaterials(null, null);
            foreach (var entry in maps)
            {
                Assert.IsTrue(gpu.TryReleaseTexture(entry.Value.Identity));
                Assert.IsTrue(stream.Release(entry.Key));
            }
            Assert.IsNull(gpu.CaptureDred());

            void Upload(D3D12TextureTarget target)
            {
                var texture = assets[target.Reference];
                Assert.IsTrue(gpu.TryGetTextureRequirements(texture, out var physical));
                var reservation = new D3D12TextureReservation(512, 0, texture.CanonicalBytes.Length,
                    physical.StagingAllocationBytes, physical.TextureAllocationBytes);
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryRequest(phase.View, target, reservation, out var ticket));
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, texture));
                Assert.IsTrue(stream.TryTake(phase.View, out var transfer));
                Assert.AreEqual(ticket, transfer.Ticket);
                Assert.IsTrue(gpu.TrySubmitTexture(transfer.Texture, phase.View.View.RendererEpoch, physical, out var native));
                Assert.IsTrue(stream.MarkCopySubmitted(ticket));
                stagingHighWater = Math.Max(stagingHighWater, gpu.TextureStatistics.StagingBytes);
                ObserveInt01Retirement(gpu);
                Assert.IsTrue(gpu.TryRetireTextureCopy(out var retired));
                Assert.AreEqual(native, retired);
                Assert.IsTrue(stream.TryInstall(ticket, phase.View, out var predecessor));
                // The measurement warms a stable set. Replacement custody is a separate native fixture.
                Assert.AreEqual(default, predecessor);
                Assert.IsTrue(gpu.TryGetTerrainMap(target, native, phase.View.View.RendererEpoch, out var map));
                maps.Add(ticket, map);
            }
        }

        Assert.AreEqual(default, stream.ReadUsage());
        Assert.IsTrue(diagnostics.TryRead(out var settled));
        var textures = settled.GetBalance(D3D12ResourceOwner.Texture);
        var staging = settled.GetBalance(D3D12ResourceOwner.TextureStaging);
        Assert.AreEqual(0, textures.Live + textures.Retained + staging.Live + staging.Retained);
        backing = backing with { ReleasedTextureAndMaterialBytes = textures.ReleasedBytes, ReleasedStagingBytes = staging.ReleasedBytes };
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), diagnosticsEnabled);
        return new(appearanceEnabled ? "textured-pbr" : "appearance-off", order, gpu.Adapter, backing, depths, rgba, measurements);
    }

    private static unsafe GpuTarget CreateInt01Target(GpuDevice gpu)
    {
        var color = CreateResource(gpu.Device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
            Texture(Int01TimingWidth, Int01TimingHeight, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET), D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON);
        try { return gpu.Attach(color, Int01TimingWidth, Int01TimingHeight); }
        catch
        {
            Release(color);
            throw;
        }
    }

    private static void ObserveInt01Retirement(GpuDevice gpu)
    {
        var start = Stopwatch.GetTimestamp();
        var spin = new SpinWait();
        while (!gpu.IsIdle)
        {
            if (gpu.IsRemoved)
                throw new GpuNativeException("Wait for INT-01 timing fence", gpu.RemovedReason);
            Assert.IsLessThan(TimeSpan.FromSeconds(10), Stopwatch.GetElapsedTime(start));
            spin.SpinOnce();
        }
    }

    private static string HashInt01Scene(Int01Phase phase, ReadOnlySpan<D3D12TextureTarget> targets)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(System.Text.Encoding.UTF8.GetBytes(phase.Name));
        foreach (var instance in phase.Region.Instances)
        {
            AddValue(instance.Translation);
            Add(MemoryMarshal.AsBytes(instance.Mesh.Positions));
            Add(MemoryMarshal.AsBytes(instance.Mesh.Indices));
            Add(MemoryMarshal.AsBytes(instance.Mesh.Colors));
            Add(MemoryMarshal.AsBytes(instance.Mesh.PackedNormals));
            Add(MemoryMarshal.AsBytes(instance.Mesh.TerrainAppearance));
        }
        foreach (var row in phase.Palette.Entries)
        {
            AddValue(row.MaterialId);
            AddValue(row.Descriptor.BaseColor);
            AddValue(row.Descriptor.Metallic);
            AddValue(row.Descriptor.Roughness);
            AddValue(row.Descriptor.AmbientOcclusion);
            AddValue(row.Descriptor.Emissive);
            AddValue(row.Descriptor.NormalStrength);
        }
        foreach (var target in targets)
        {
            AddValue(target.Slot.Material);
            AddValue((int)target.Slot.Semantic);
            Add(System.Text.Encoding.ASCII.GetBytes(target.Reference.AssetId));
        }
        for (var index = 0; index < Int01RouteSteps; index++)
        {
            var frame = phase.Frame(index, (ulong)index + 1);
            Assert.IsTrue(frame.Scene.HasValue);
            var scene = frame.Scene.Value;
            AddValue(scene.World);
            AddValue(scene.View);
            AddValue(scene.Tint);
            AddValue(scene.VerticalFieldOfView);
            AddValue(scene.NearPlane);
            AddValue(scene.FarPlane);
            AddValue(frame.ClearColor);
            AddValue(scene.Sunlight!.Value.Direction);
            AddValue(scene.Sunlight.Value.Direct);
            AddValue(scene.Sunlight.Value.Ambient);
            AddValue(scene.ShadowOrigin.HasValue ? 1 : 0);
            if (scene.ShadowOrigin is { } shadowOrigin)
                AddValue(shadowOrigin);
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

    private static Dictionary<string, string> HashInt01Shaders()
    {
        string[] names = ["Cube.vs.dxil", "Cube.ps.dxil", "TerrainNormals.vs.dxil", "ShadowTerrain.ps.dxil",
            "TerrainAppearance.vs.dxil", "TerrainAppearance.ps.dxil", "TerrainAppearanceShadow.ps.dxil", "ShadowDepth.vs.dxil"];
        var result = new Dictionary<string, string>(names.Length);
        foreach (var name in names)
        {
            using var shader = typeof(GpuDevice).Assembly.GetManifestResourceStream(name);
            Assert.IsNotNull(shader);
            result.Add(name, Convert.ToHexStringLower(SHA256.HashData(shader)));
        }
        return result;
    }

    private sealed record Int01Phase(string Name, D3D12TextureView View, RenderMeshRegion Region,
        MaterialPalette Palette, Func<int, ulong, RenderFrame> Frame, int[] ProbeXs);
    private sealed record Int01PhaseResult(string Phase, string SceneSha256, int Meshes, int Triangles, Int01Asset[] Assets,
        Int01RouteResult[] Routes);
    private readonly record struct Int01Asset(ushort Material, string Semantic, string AssetId);
    private sealed record Int01RouteResult(string Route, int Order, D3D12AdapterInfo Adapter, Int01Backing Backing,
        float[] ProbeDepth, uint[] ProbeRgba, Int01Frame[] Measurements);
    private readonly record struct Int01Frame(long CpuRecordTicks, double GpuTerrainMicroseconds, long TurnTicks);
    private readonly record struct Int01Backing(int MapDescriptors, int MapDescriptorCapacity, long ResidentTextureAndMaterialBytes,
        ulong MaterialConstantBackingBytes, long CombinedBackingHighWaterBytes, long StagingHighWaterBytes,
        ulong FenceRetainedHighWaterBytes, ulong RetiringPredecessorPeakBytes, ulong SetupUploadPayloadBytes,
        int TerrainPassResourceUses, long GeometryBytes, ulong ShadowBytes, ulong ReleasedTextureAndMaterialBytes = 0,
        ulong ReleasedStagingBytes = 0);
}
