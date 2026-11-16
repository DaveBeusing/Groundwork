using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Groundwork.Rendering.Materials;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void Mat02PairedTerrainTimings(bool warp)
    {
        if (Environment.GetEnvironmentVariable("GROUNDWORK_MAT02_MEASURE") != "1")
            return;

        const int width = 1920;
        const int height = 1080;
        const int warmup = 24;
        const int sampleCount = 128;
        var normal = PackedTerrainNormal.Encode(Vector3.UnitZ);
        var mesh = RenderMesh.CreateWithTerrainNormals(
            [new(-8, -5, -4), new(8, -5, -4), new(8, 5, -4), new(-8, 5, -4)], [0, 1, 2, 0, 2, 3],
            [0xff848484, 0xff848484, 0xff848484, 0xff848484], [normal, normal, normal, normal]);
        var origin = PlanetLocalFrame.Create(default);
        var region = new RenderMeshRegion([new(mesh, origin.ToLocal(default))]);
        var camera = new VolumeCamera(new(1, 1, origin), default, -Vector3.UnitZ, Vector3.UnitY,
            1, 0.1f, 100, Vector4.One, Vector4.Zero)
        { Sunlight = new(Vector3.Normalize(new(0.2f, 0.4f, 1f)), new(0.7f), new(0.15f)) };
        var pbr = camera with { TerrainMaterial = new PbrMaterialDescriptor(new(0.24f, 0.4f, 0.6f), 0.3f, 0.45f, 0.8f, default, 1) };

        long start = 0;
        long allocated = 0;
        double cpu = 0;
        long allocation = 0;
        var hooks = new GpuRetirementTestHooks(
            RejectPreparedPlan: _ =>
            {
                allocated = GC.GetAllocatedBytesForCurrentThread();
                start = Stopwatch.GetTimestamp();
                return false;
            },
            RejectRecordedPlan: _ =>
            {
                cpu = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
                return false;
            });
        var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true, retirementHooks: hooks, region: region);
        ulong sequence = 0;
        var legacySamples = new TerrainTimingSample[sampleCount];
        var pbrSamples = new TerrainTimingSample[sampleCount];
        using (gpu)
        {
            AssertPlanetAdapter(gpu.Adapter, warp);
            gpu.EnableTerrainTimingForTests();
            for (var i = -warmup; i < sampleCount; i++)
            {
                TerrainTimingSample legacySample, pbrSample;
                if ((i & 1) == 0)
                {
                    legacySample = Measure(camera);
                    pbrSample = Measure(pbr);
                }
                else
                {
                    pbrSample = Measure(pbr);
                    legacySample = Measure(camera);
                }

                Assert.AreNotEqual(legacySample.CenterRed, pbrSample.CenterRed);
                if (i >= 0)
                {
                    legacySamples[i] = legacySample;
                    pbrSamples[i] = pbrSample;
                }
            }

            Assert.IsNull(gpu.CaptureDred());
        }

        AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);
        Console.WriteLine(FormattableString.Invariant($"MAT-02 {gpu.Adapter.Description} driver={gpu.Adapter.DriverVersion} {width}x{height} paired={sampleCount} warmup={warmup}"));
        WriteSamples("legacy", legacySamples);
        WriteSamples("GGX", pbrSamples);

        TerrainTimingSample Measure(VolumeCamera input)
        {
            Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(input, ++sequence, out var frame));
            var result = GpuDevice.RenderOffscreen(gpu, width, height, frame);
            Assert.IsLessThan(1f, result.DepthAt(width / 2, height / 2));
            return new(cpu, gpu.ReadTerrainTimingForTests(sequence), allocation, result.ColorAt(width / 2, height / 2)[0]);
        }
    }

    private static void WriteSamples(string mode, TerrainTimingSample[] timings)
    {
        var gpu = timings.Select(sample => sample.GpuMicroseconds).Order().ToArray();
        Assert.IsGreaterThan(0d, Percentile(gpu, 50));
        Console.WriteLine($"{mode}: CPU record us p50/p95/p99={Summary(timings.Select(sample => sample.CpuMicroseconds).Order().ToArray())}; " +
            $"GPU terrain us p50/p95/p99={Summary(gpu)}; CPU allocated bytes p50/p95/p99={Summary(timings.Select(sample => (double)sample.AllocatedBytes).Order().ToArray())}");

        static string Summary(double[] sorted) => string.Create(CultureInfo.InvariantCulture,
            $"{Percentile(sorted, 50):F3}/{Percentile(sorted, 95):F3}/{Percentile(sorted, 99):F3}");
        static double Percentile(double[] sorted, int p) => sorted[(int)Math.Ceiling(sorted.Length * p / 100d) - 1];
    }

    private readonly record struct TerrainTimingSample(double CpuMicroseconds, double GpuMicroseconds, long AllocatedBytes, byte CenterRed);
}
