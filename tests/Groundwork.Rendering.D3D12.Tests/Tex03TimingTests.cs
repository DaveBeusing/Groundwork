using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private const int Tex03Width = 640;
    private const int Tex03Height = 360;
    private const int Tex03WarmupFrames = 8;
    private const int Tex03MeasuredFrames = 64;
    private static readonly D3D12TextureMipPolicy Tex03Policy = new(0, 2, 10, 30);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void Tex03PairedNativeMipTimings()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("GROUNDWORK_TEX03_PAIR"), out var pair) || pair < 1)
            Assert.Inconclusive("Run benchmarks/Measure-Tex03MipResidency.ps1 to collect a paired observation.");

        var warp = string.Equals(Environment.GetEnvironmentVariable("GROUNDWORK_TEX03_ADAPTER"), "Warp", StringComparison.OrdinalIgnoreCase);
        var texture = CreateTex03TimingTexture();
        var normal = PackedTerrainNormal.Encode(Vector3.UnitZ);
        var mesh = RenderMesh.CreateWithTerrainNormals(
            [new(-8, -5, -4), new(8, -5, -4), new(8, 5, -4), new(-8, 5, -4)], [0, 1, 2, 0, 2, 3],
            [0xffffffff, 0xffffffff, 0xffffffff, 0xffffffff], [normal, normal, normal, normal]);
        var origin = PlanetLocalFrame.Create(default);
        var region = new RenderMeshRegion([new(mesh, origin.ToLocal(default))]);
        var view = new D3D12TextureView(new Guid("99cda307-5f53-4753-9885-ad34a6928854"), new(1, 1, origin), 1, 1);
        var routes = new Tex03Route[2];
        for (var index = 0; index < routes.Length; index++)
            routes[index] = MeasureTex03Route((pair + index) % 2 == 0, warp, texture, region, view, index + 1);

        var fixedMip = routes.Single(route => route.Route == "fixed-mip0");
        var tieredMip = routes.Single(route => route.Route == "bounded-mip-tiers");
        Assert.AreEqual(fixedMip.Adapter, tieredMip.Adapter);
        Assert.AreEqual(1, fixedMip.Samples.Count(sample => sample.CopySubmitted));
        Assert.AreEqual(3, tieredMip.Samples.Count(sample => sample.CopySubmitted));
        Assert.IsLessThan(fixedMip.Samples[^1].ResidentBytes, tieredMip.Samples[^1].ResidentBytes);
        for (var index = 0; index < Tex03MeasuredFrames; index++)
        {
            Assert.AreEqual(fixedMip.Samples[index].Depth, tieredMip.Samples[index].Depth);
            Assert.AreEqual(fixedMip.Samples[index].CenterRgba, tieredMip.Samples[index].CenterRgba);
        }

        Console.WriteLine("TEX03 " + JsonSerializer.Serialize(new
        {
            schema = 1,
            fixture = "tex03-offscreen-terrain-camera-bands-v1",
            pair,
            adapter = fixedMip.Adapter,
            runtime = RuntimeInformation.FrameworkDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            width = Tex03Width,
            height = Tex03Height,
            warmupFrames = Tex03WarmupFrames,
            measuredFrames = Tex03MeasuredFrames,
            stopwatchFrequency = Stopwatch.Frequency,
            texture = new { texture.AssetId, texture.Width, texture.Height, mipCount = texture.Mips.Length },
            policy = Tex03Policy,
            routes
        }, JsonSerializerOptions.Web));
    }

    private static Tex03Route MeasureTex03Route(bool tiered, bool warp, CookedTexture texture, RenderMeshRegion region,
        D3D12TextureView view, int order)
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
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, retirementHooks: hooks, region: region);
        Tex03Route result;
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            gpu.EnableTerrainTimingForTests();
            Assert.IsTrue(gpu.TryGetTextureRequirements(texture, Tex03Policy.FineMip, out var fineRequirements));
            Assert.IsTrue(gpu.TryGetTextureRequirements(texture, Tex03Policy.CoarseMip, out var coarseRequirements));
            var fine = Credit(fineRequirements);
            var coarse = Credit(coarseRequirements);
            var target = new D3D12TextureTarget(new(1, MaterialTextureSemantic.BaseColor), 1,
                new(MaterialTextureSemantic.BaseColor, texture.AssetId));
            var limits = new D3D12TextureLimits(1, 2, 2 * texture.CanonicalBytes.Length, 0,
                2 * texture.CanonicalBytes.Length, Math.Max(fine.StagingBytes, coarse.StagingBytes),
                fine.NativeBytes + coarse.NativeBytes, 2);
            var stream = BeginStream();
            ulong sequence = 0;
            var binding = default(GpuTextureIdentity);
            var residentTicket = default(D3D12TextureTicket);
            long previousBytes = 0;
            var samples = new Tex03Frame[Tex03MeasuredFrames];

            BindTier(36);
            for (var index = 0; index < Tex03WarmupFrames; index++)
                Render(index, 36);
            ReleaseResident();
            stream = BeginStream();
            var beforeUploads = gpu.TextureStatistics.UploadedBytes;
            for (var index = 0; index < samples.Length; index++)
            {
                // Include both hysteresis bands and identical camera positions for the two routes.
                var distance = index switch
                {
                    < 16 => 36,
                    < 20 => 16,
                    < 32 => 6,
                    < 40 => 16,
                    _ => 36
                };
                var turnStart = Stopwatch.GetTimestamp();
                var transition = BindTier(distance);
                var image = Render(index, distance);
                var turnTicks = Stopwatch.GetTimestamp() - turnStart;
                var gpuMicroseconds = gpu.ReadTerrainTimingForTests(sequence);
                var center = image.ColorAt(Tex03Width / 2, Tex03Height / 2);
                var depth = image.DepthAt(Tex03Width / 2, Tex03Height / 2);
                Assert.IsLessThan(1f, depth);
                Assert.IsGreaterThan(0L, recordTicks);
                Assert.IsTrue(stream.TryReadResidency(view, target, out var residence));
                Assert.IsNotNull(residence.ResidentMip);
                var expectedMip = tiered && (index < 20 || index >= 40) ? Tex03Policy.CoarseMip : Tex03Policy.FineMip;
                Assert.AreEqual(expectedMip, residence.ResidentMip.Value);
                Assert.AreEqual(previousBytes, residence.ResidentBytes);
                Assert.AreEqual(previousBytes, gpu.TextureStatistics.NativeBytes);
                Assert.AreEqual(0L, gpu.TextureStatistics.StagingBytes);
                samples[index] = new(index, distance, residence.RequestedMip, residence.ResidentMip.Value, recordTicks,
                    gpuMicroseconds, turnTicks, transition.WaitTicks, transition.Submitted, previousBytes,
                    transition.ResidentAndCandidateBytes, transition.StagingBytes, transition.RetiringBytes,
                    transition.CombinedBackingBytes, gpu.TextureStatistics.UploadedBytes - beforeUploads,
                    BinaryPrimitives.ReadUInt32LittleEndian(center), depth);
            }

            var combinedBacking = samples.Max(sample => sample.CombinedBackingBytes);
            Assert.AreEqual(combinedBacking, gpu.TextureStatistics.NativePeakBytes);
            result = new(tiered ? "bounded-mip-tiers" : "fixed-mip0", order, gpu.Adapter,
                Percentiles(samples.Select(sample => TicksToMicroseconds(sample.CpuRecordTicks))),
                Percentiles(samples.Select(sample => sample.GpuTerrainMicroseconds)),
                Percentiles(samples.Select(sample => TicksToMicroseconds(sample.TurnTicks))),
                Percentiles(samples.Where(sample => sample.CopySubmitted).Select(sample => TicksToMicroseconds(sample.CopyWaitTicks))),
                samples.Max(sample => sample.ResidentBytes), samples.Max(sample => sample.ResidentAndCandidateBytes),
                samples.Max(sample => sample.StagingBytes), samples.Max(sample => sample.RetiringBytes),
                combinedBacking, samples[^1].UploadedBytes, samples);
            ReleaseResident();
            Assert.IsNull(gpu.CaptureDred());

            D3D12TextureReservation Credit(GpuTextureRequirements requirements) =>
                new(texture.CanonicalBytes.Length, 0, texture.CanonicalBytes.Length,
                    requirements.StagingAllocationBytes, requirements.TextureAllocationBytes);

            D3D12TextureHandoff BeginStream()
            {
                var handoff = new D3D12TextureHandoff(limits);
                Assert.IsTrue(handoff.Publish(view, 1, [target]));
                return handoff;
            }

            Tex03Transition BindTier(int distance)
            {
                var request = tiered
                    ? stream.TryRequestTier(view, target, Tex03Policy, distance, fine, coarse, out var ticket)
                    : stream.TryRequest(view, target, fine, out ticket);
                if (request == D3D12TextureResult.Coalesced)
                    return new(false, 0, gpu.TextureStatistics.NativeBytes, 0, 0, gpu.TextureStatistics.NativeBytes);

                Assert.AreEqual(D3D12TextureResult.Accepted, request);
                Assert.AreEqual(D3D12TextureResult.Accepted, stream.TryComplete(ticket, texture));
                Assert.IsTrue(stream.TryTake(view, out var transfer));
                Assert.AreEqual(ticket, transfer.Ticket);
                Assert.IsTrue(gpu.TryGetTextureTierRequirements(transfer.Texture, transfer.RequestedMip, transfer.Reservation,
                    out var requirements));
                Assert.IsTrue(gpu.TrySubmitTexture(transfer.Texture, view.View.RendererEpoch, requirements, out var candidate));
                Assert.IsTrue(stream.MarkCopySubmitted(ticket, requirements));
                var pending = gpu.TextureStatistics;
                var waitStart = Stopwatch.GetTimestamp();
                GpuTextureIdentity completed;
                while (!gpu.TryRetireTextureCopy(out completed))
                {
                    Assert.IsLessThan(TimeSpan.FromSeconds(10), Stopwatch.GetElapsedTime(waitStart));
                    Thread.Sleep(1);
                }
                var waitTicks = Stopwatch.GetTimestamp() - waitStart;
                Assert.AreEqual(candidate, completed);
                Assert.IsTrue(stream.TryInstall(ticket, view, out var predecessor));
                Assert.AreEqual(residentTicket, predecessor);
                var retiringBytes = previousBytes;
                Assert.AreEqual(retiringBytes + requirements.TextureAllocationBytes, pending.NativeBytes);
                Assert.AreEqual(requirements.StagingAllocationBytes, pending.StagingBytes);
                Assert.IsTrue(gpu.SetFrameTexture(candidate, view.View.RendererEpoch));
                if (predecessor != default)
                {
                    Assert.IsTrue(gpu.TryReleaseTexture(binding));
                    Assert.IsTrue(stream.Release(predecessor));
                }
                residentTicket = ticket;
                binding = candidate;
                previousBytes = requirements.TextureAllocationBytes;
                return new(true, waitTicks, pending.NativeBytes, pending.StagingBytes, retiringBytes,
                    pending.NativeBytes + pending.StagingBytes);
            }

            OffscreenFrame Render(int index, int distance)
            {
                var x = (index % 9 - 4) * 0.2f;
                var camera = new VolumeCamera(view.View, new(x, 0, distance - 4),
                    Vector3.Normalize(new(-x, 0, -distance)), Vector3.UnitY, 1, 0.1f, 100, Vector4.One, Vector4.Zero);
                Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, ++sequence, out var frame));
                return GpuDevice.RenderOffscreen(gpu, Tex03Width, Tex03Height, frame);
            }

            void ReleaseResident()
            {
                Assert.IsTrue(gpu.TryReleaseTexture(binding));
                Assert.IsTrue(stream.Release(residentTicket));
                Assert.AreEqual(default, stream.ReadUsage());
                binding = default;
                residentTicket = default;
                previousBytes = 0;
            }
        }
        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);
        return result;
    }

    private static CookedTexture CreateTex03TimingTexture()
    {
        const int size = 512;
        const int header = 18;
        const int budget = 2 * 1024 * 1024;
        var source = new byte[header + size * size * 4];
        source[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(12), size);
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(14), size);
        source[16] = 32;
        source[17] = 0x28;
        for (var index = header; index < source.Length; index += 4)
        {
            source[index] = 38;
            source[index + 1] = 137;
            source[index + 2] = 212;
            source[index + 3] = 255;
        }
        Assert.AreEqual(TextureCookError.None, TextureCooker.CookTga32(source, new(TextureColorSpace.Srgb, TextureSemantic.Color, 3),
            new(budget, budget, budget, size, 3), out var texture));
        Assert.IsNotNull(texture);
        return texture;
    }

    private static double TicksToMicroseconds(long ticks) => ticks * 1_000_000d / Stopwatch.Frequency;

    private static Tex03Percentiles Percentiles(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToArray();
        double At(double fraction) => sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
        return new(At(.5), At(.95), At(.99));
    }

    private readonly record struct Tex03Percentiles(double P50, double P95, double P99);
    private readonly record struct Tex03Transition(bool Submitted, long WaitTicks, long ResidentAndCandidateBytes,
        long StagingBytes, long RetiringBytes, long CombinedBackingBytes);
    private readonly record struct Tex03Frame(int Frame, int DistanceMeters, int RequestedMip, int SampleableMip,
        long CpuRecordTicks, double GpuTerrainMicroseconds, long TurnTicks, long CopyWaitTicks, bool CopySubmitted,
        long ResidentBytes, long ResidentAndCandidateBytes, long StagingBytes, long RetiringBytes, long CombinedBackingBytes,
        ulong UploadedBytes, uint CenterRgba, float Depth);
    private sealed record Tex03Route(string Route, int Order, D3D12AdapterInfo Adapter, Tex03Percentiles CpuRecordUs,
        Tex03Percentiles GpuTerrainUs, Tex03Percentiles TurnUs, Tex03Percentiles CopyWaitUs, long ResidentHighWaterBytes,
        long ResidentAndCandidateHighWaterBytes, long StagingHighWaterBytes, long RetiringHighWaterBytes,
        long CombinedBackingHighWaterBytes, ulong UploadedBytes, Tex03Frame[] Samples);
}
