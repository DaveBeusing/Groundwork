using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private static readonly bool[] ExpectedSessionSkyReuse = [false, true, true];
    [TestMethod]
    public void WarpSkyPipelineIsReusedAcrossSettledSessionOwners()
    {
        using var session = new D3D12DeviceSession(useWarp: true);
        var reuse = new List<bool>();
        var hooks = new GpuRetirementTestHooks(AfterSkyPipelineReady: (start, end, reused) =>
        {
            Assert.IsGreaterThan(0L, start);
            Assert.IsGreaterThanOrEqualTo(start, end);
            reuse.Add(reused);
        });
        for (var owner = 0; owner < 3; owner++)
        {
            using var gpu = new GpuDevice(warp: true, session: session, retirementHooks: hooks);
            GpuDevice.RenderOffscreen(gpu, 64, 64, new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() });
            GpuDevice.RenderOffscreen(gpu, 64, 64, new RenderFrame(2, Vector4.Zero) { Sky = SkyFixture() });
            if (owner == 2)
            {
                // Session disposal may close admissions while the attached owner
                // still holds its independent pipeline/root/device references.
                session.Dispose();
                GpuDevice.RenderOffscreen(gpu, 64, 64, new RenderFrame(3, Vector4.Zero) { Sky = SkyFixture() });
            }
        }
        CollectionAssert.AreEqual(ExpectedSessionSkyReuse, reuse);
        using var independent = new GpuDevice(warp: true, retirementHooks: hooks);
        GpuDevice.RenderOffscreen(independent, 64, 64, new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() });
        Assert.IsFalse(reuse[^1]);
    }

    private static readonly JsonSerializerOptions SkyEvidenceJsonOptions = new() { WriteIndented = true };

    [TestMethod]
    public void WarpQualificationCallbacksPublishOnlySubmittedRetiredOwnerFrames()
    {
        var ownerThread = Environment.CurrentManagedThreadId;
        var initialized = 0;
        var exposeCompletion = false;
        var recorded = new List<(ulong Sequence, ulong Fence, int Draws)>();
        var retired = new List<(ulong Sequence, ulong Fence, bool HasSky)>();
        var hooks = new GpuRetirementTestHooks(
            CompletedValue: (_, actual) => exposeCompletion ? actual : 0,
            RejectRecordedPlan: sequence => sequence == 3,
            AfterDeviceInitialize: device =>
            {
                Assert.AreEqual(ownerThread, Environment.CurrentManagedThreadId);
                device.EnableSkyTimingForTests();
                initialized++;
            },
            AfterSkyRecordMeasured: (sequence, fence, cpu, allocation, draws) =>
            {
                Assert.AreEqual(ownerThread, Environment.CurrentManagedThreadId);
                Assert.IsGreaterThanOrEqualTo(0d, cpu);
                Assert.IsGreaterThanOrEqualTo(0L, allocation);
                Assert.IsGreaterThan(0UL, fence);
                recorded.Add((sequence, fence, draws));
            },
            AfterSkyTimingRetired: (sequence, fence, frame, sky, hasSky) =>
            {
                Assert.AreEqual(ownerThread, Environment.CurrentManagedThreadId);
                Assert.IsGreaterThanOrEqualTo(sky, frame);
                retired.Add((sequence, fence, hasSky));
            });
        var gpu = new GpuDevice(warp: true, enableDebugLayer: true, enableDred: true, retirementHooks: hooks);
        GpuTarget? target = null;
        try
        {
            Assert.AreEqual(1, initialized);
            target = gpu.CreateSkyTimingTargetForTests(64, 64);
            Assert.AreEqual(GpuFrameSubmitResult.Submitted,
                gpu.TryRenderSkyTimingForTests(target, new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() }));
            Assert.IsFalse(gpu.IsIdle);
            Assert.IsEmpty(retired, "A hidden retirement fence must not publish timestamp results.");
            exposeCompletion = true;
            WaitForRetirement();
            Assert.IsTrue(gpu.IsIdle);
            Assert.IsTrue(gpu.IsIdle);
            CollectionAssert.AreEqual(new[] { (1UL, true) }, retired.Select(row => (row.Sequence, row.HasSky)).ToArray());
            Assert.AreEqual(GpuFrameSubmitResult.Submitted,
                gpu.TryRenderSkyTimingForTests(target, new RenderFrame(2, Vector4.Zero)));
            WaitForRetirement();
            // Appearance redraws can legitimately reuse an acknowledged logical
            // sequence. Their native command lists must retain distinct identities.
            Assert.AreEqual(GpuFrameSubmitResult.Submitted,
                gpu.TryRenderSkyTimingForTests(target, new RenderFrame(2, Vector4.Zero) { Sky = SkyFixture() }));
            WaitForRetirement();
            Assert.AreEqual(GpuFrameSubmitResult.Rejected,
                gpu.TryRenderSkyTimingForTests(target, new RenderFrame(3, Vector4.Zero) { Sky = SkyFixture() }));
            Assert.IsTrue(gpu.IsIdle);
            CollectionAssert.AreEqual(new[] { (1UL, 1), (2UL, 0), (2UL, 1) },
                recorded.Select(row => (row.Sequence, row.Draws)).ToArray());
            CollectionAssert.AreEqual(new[] { (1UL, true), (2UL, false), (2UL, true) },
                retired.Select(row => (row.Sequence, row.HasSky)).ToArray());
            CollectionAssert.AreEqual(recorded.Select(row => (row.Sequence, row.Fence)).ToArray(),
                retired.Select(row => (row.Sequence, row.Fence)).ToArray());
            Assert.AreEqual(3, retired.Select(row => row.Fence).Distinct().Count());
        }
        finally
        {
            exposeCompletion = true;
            if (gpu.IsIdle || gpu.IsRemoved)
            {
                target?.Dispose();
                gpu.Dispose();
            }
        }

        void WaitForRetirement()
        {
            var start = Stopwatch.GetTimestamp();
            while (!gpu.IsIdle)
            {
                Assert.IsLessThan(TimeSpan.FromSeconds(10), Stopwatch.GetElapsedTime(start));
                Thread.Sleep(1);
            }
        }
    }

    [TestMethod]
    public void WarpSkyTimestampsRequireRetiredIdentityAndDistinguishDisabledPass()
    {
        using var gpu = new GpuDevice(warp: true, enableDebugLayer: true, enableDred: true);
        gpu.EnableSkyTimingForTests();
        Assert.IsGreaterThan(0UL, gpu.SkyTimestampFrequencyForTests);
        Assert.Throws<InvalidOperationException>(() => gpu.ReadSkyTimingForTests(1));
        GpuDevice.RenderOffscreen(gpu, 64, 64, new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() });
        var sky = gpu.ReadSkyTimingForTests(1);
        Assert.IsTrue(sky.HasSky);
        Assert.IsGreaterThan(0d, sky.SkyMicroseconds);
        Assert.IsGreaterThan(0UL, sky.Frequency);
        Assert.IsGreaterThanOrEqualTo(sky.FrameStart, sky.SkyStart);
        Assert.IsGreaterThanOrEqualTo(sky.SkyStart, sky.SkyEnd);
        Assert.IsGreaterThanOrEqualTo(sky.SkyEnd, sky.FrameEnd);
        Assert.AreEqual(sky.FrameMicroseconds, (sky.FrameEnd - sky.FrameStart) * 1_000_000d / sky.Frequency);
        Assert.AreEqual(sky.SkyMicroseconds, (sky.SkyEnd - sky.SkyStart) * 1_000_000d / sky.Frequency);
        Assert.IsGreaterThanOrEqualTo(sky.SkyMicroseconds, sky.FrameMicroseconds);
        Assert.Throws<InvalidOperationException>(() => gpu.ReadSkyTimingForTests(2));
        GpuDevice.RenderOffscreen(gpu, 64, 64, new RenderFrame(2, Vector4.Zero));
        var clear = gpu.ReadSkyTimingForTests(2);
        Assert.IsFalse(clear.HasSky);
        Assert.AreEqual(0d, clear.SkyMicroseconds);
        Assert.AreEqual(0UL, clear.SkyStart);
        Assert.AreEqual(0UL, clear.SkyEnd);
        Assert.Throws<InvalidOperationException>(() => gpu.ReadSkyTimingForTests(1));
    }

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void NvidiaSkyPassQualificationCollectsRetired1080pTimestamps()
    {
        RequireDirectSkyQualification(Environment.GetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_PROTOTYPE") == "1");
        if (Environment.GetEnvironmentVariable("GROUNDWORK_SKY_MEASURE") != "1")
            Assert.Inconclusive("Set GROUNDWORK_SKY_MEASURE=1; run exclusively, without other GPU work.");

        var strict = Environment.GetEnvironmentVariable("GROUNDWORK_SKY_STRICT") == "1";
        var pairs = strict ? 5 : 1;
        var count = strict ? 2000 : 128;
        const int warmup = 64;
        const int width = 1920;
        const int height = 1080;
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("GROUNDWORK_SKY_OUTPUT") ??
            Path.Combine("artifacts", "sky-qualification", DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(output);

        var rows = new List<SkyQualificationSample>(pairs * count * 6);
        var summaries = new List<SkyQualificationSummary>();
        long started = 0, allocated = 0, allocation = 0;
        double cpu = 0;
        var draws = 0;
        var hooks = new GpuRetirementTestHooks(
            BeforeSkyRecord: () =>
            {
                allocated = GC.GetAllocatedBytesForCurrentThread();
                started = Stopwatch.GetTimestamp();
            },
            AfterSkyRecord: () =>
            {
                cpu = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
            },
            SkyDrawRecorded: () => draws++);
        var ledger = new ResourceDiagnostics();
        var gpu = new GpuDevice(warp: false, retirementHooks: hooks, resourceDiagnostics: ledger, skyLookupPrototype: false);
        Assert.IsFalse(gpu.SkyLookupPrototypeEnabledForTests);
        GpuTarget? target = null;
        D3D12ResourceSnapshot before = default, after = default;
        ulong sequence = 0;
        ulong timestampFrequency = 0;
        var warmupRows = 0;
        var completed = false;
        try
        {
            AssertPlanetAdapter(gpu.Adapter, false);
            gpu.EnableSkyTimingForTests();
            timestampFrequency = gpu.SkyTimestampFrequencyForTests;
            target = gpu.CreateSkyTimingTargetForTests(width, height);
            var surface = SkyFixture() with
            {
                EyeMeters = new(0, 4097, 0), SunDirection = Vector3.Normalize(new(0, -0.02f, -1))
            };
            var phases = new[]
            {
                (Name: "surface", Sky: surface),
                (Name: "ascent", Sky: surface with { EyeMeters = new(0, 4330, 0) }),
                (Name: "orbit", Sky: surface with { EyeMeters = new(0, 0, 8192), SunDirection = Vector3.UnitX })
            };
            foreach (var phase in phases)
            for (var pair = 0; pair < pairs; pair++)
            {
                for (var i = -warmup; i < count; i++)
                {
                    // Alternate pair order to reduce systematic thermal/order bias.
                    for (var variant = 0; variant < 2; variant++)
                    {
                        var enabled = ((i + pair + variant) & 1) == 0;
                        cpu = 0;
                        allocation = 0;
                        draws = 0;
                        var frame = new RenderFrame(++sequence, Vector4.Zero) { Sky = enabled ? phase.Sky : null };
                        Assert.AreEqual(GpuFrameSubmitResult.Submitted, gpu.TryRenderSkyTimingForTests(target, frame));
                        var wait = Stopwatch.GetTimestamp();
                        while (!gpu.IsIdle)
                        {
                            Assert.IsFalse(gpu.IsRemoved, "Device removed while waiting for the timestamp fence.");
                            Assert.IsLessThan(TimeSpan.FromSeconds(10), Stopwatch.GetElapsedTime(wait), "GPU timestamp did not retire.");
                            Thread.Sleep(1);
                        }
                        var timing = gpu.ReadSkyTimingForTests(sequence);
                        Assert.AreEqual(enabled, timing.HasSky);
                        Assert.AreEqual(enabled ? 1 : 0, draws, "Count observed at the native sky DrawInstanced site.");
                        Assert.IsTrue(double.IsFinite(timing.FrameMicroseconds) && double.IsFinite(timing.SkyMicroseconds));
                        if (i >= 0)
                            rows.Add(new(phase.Name, pair, i, enabled, timing.FrameMicroseconds,
                                timing.SkyMicroseconds, cpu, allocation, draws));
                        else
                            warmupRows++;
                    }
                    if (i == -1 && before.FrameSequence == 0)
                        Assert.IsTrue(ledger.TryRead(out before));
                }
            }
            Assert.IsTrue(ledger.TryRead(out after));
            Assert.IsFalse(before.LedgerOverflowed || after.LedgerOverflowed || before.LedgerHasUnknownSizes || after.LedgerHasUnknownSizes);
            foreach (var owner in Enum.GetValues<D3D12ResourceOwner>())
                Assert.AreEqual(before.GetBalance(owner), after.GetBalance(owner),
                    $"Warmed synthetic sky frames must not create/release tracked {owner} backing allocations.");
            completed = true;
        }
        finally
        {
            // Never release in-flight native resources on a failed timeout.
            if (gpu.IsIdle || gpu.IsRemoved)
            {
                target?.Dispose();
                gpu.Dispose();
            }
            WriteEvidence();
        }

        foreach (var group in rows.Where(row => row.Enabled).GroupBy(row => (row.Phase, row.Pair)))
        {
            var sky = group.Select(row => row.SkyMicroseconds).Order().ToArray();
            var cpuSamples = group.Select(row => row.RecordMicroseconds).Order().ToArray();
            var summary = new SkyQualificationSummary(group.Key.Phase, group.Key.Pair, group.Count(),
                SkyPercentile(sky, 95), SkyPercentile(sky, 99), SkyPercentile(cpuSamples, 99),
                group.Max(row => row.AllocatedBytes));
            summaries.Add(summary);
            Console.WriteLine(JsonSerializer.Serialize(summary));
        }
        foreach (var group in rows.Where(row => row.Enabled).GroupBy(row => row.Phase))
        {
            summaries.Add(new(group.Key, -1, group.Count(),
                SkyPercentile(group.Select(row => row.SkyMicroseconds).Order().ToArray(), 95),
                SkyPercentile(group.Select(row => row.SkyMicroseconds).Order().ToArray(), 99),
                SkyPercentile(group.Select(row => row.RecordMicroseconds).Order().ToArray(), 99),
                group.Max(row => row.AllocatedBytes)));
        }
        WriteEvidence();
        // Local mode records observations, never turns a short workstation run
        // into reference qualification. Strict mode preserves the existing caps.
        if (strict)
        {
            foreach (var summary in summaries)
            {
                Assert.IsLessThanOrEqualTo(800d, summary.SkyP95Microseconds);
                Assert.IsLessThanOrEqualTo(1200d, summary.SkyP99Microseconds);
                Assert.IsLessThanOrEqualTo(100d, summary.RecordP99Microseconds);
                Assert.AreEqual(0L, summary.MaximumAllocatedBytes);
            }
        }

        void WriteEvidence()
        {
            var csv = Path.Combine(output, "samples.csv");
            using (var writer = new StreamWriter(csv))
            {
                writer.WriteLine("phase,pair,sample,skyEnabled,gpuFrameUs,gpuSkyUs,cpuSkyRecordUs,recordAllocatedBytes,nativeSkyDraws");
                foreach (var row in rows)
                    writer.WriteLine(FormattableString.Invariant($"{row.Phase},{row.Pair},{row.Sample},{row.Enabled},{row.FrameMicroseconds:R},{row.SkyMicroseconds:R},{row.RecordMicroseconds:R},{row.AllocatedBytes},{row.Draws}"));
            }
            var dll = typeof(D3D12Renderer).Assembly.Location;
            var evidence = new
            {
                TimestampUtc = DateTime.UtcNow, Adapter = gpu.Adapter, strict, pairs, count, warmup, width, height,
                timestampFrequency, CpuTimestampFrequency = Stopwatch.Frequency,
                ProcessorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                OperatingSystem = Environment.OSVersion.ToString(),
                HardwareQualification = "Not established by this test; adapter metadata does not prove reference-class qualification.",
                SourceHead = Environment.GetEnvironmentVariable("GROUNDWORK_SKY_SOURCE_HEAD") ?? "UNRECORDED",
                AssemblyVersion = typeof(D3D12Renderer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                AssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))),
                SamplesSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(csv))),
                completed, SampleRows = rows.Count, ExpectedSampleRows = pairs * count * 6,
                SummaryPairMinusOne = "Pooled samples for the named phase; other rows are per pair.",
                WarmupRowsCompleted = warmupRows, ExpectedWarmupRows = pairs * warmup * 6, summaries,
                ObservedSkyCapsSatisfied = summaries.Count > 0 && summaries.All(summary =>
                    summary.SkyP95Microseconds <= 800 && summary.SkyP99Microseconds <= 1200 &&
                    summary.RecordP99Microseconds <= 100 && summary.MaximumAllocatedBytes == 0),
                Scope = "Synthetic fixed-camera sky-only surface/ascent/orbit; direct graphics-queue timestamps read after retirement; frame interval includes clear+sky+transitions, excludes Present/copy/retirement wait.",
                CpuScope = "Target binding, RenderSky normalization/constants and sky command recording including instrumentation; excludes application preparation and admission.",
                MemoryScope = "Aligned ID3D12Resource ledger only; excludes driver PSO/root-signature memory, timing heap and full integrated handoff overlap. Not the complete 4 MiB gate.",
                MissingQualification = "Reference hardware identity/route; full B/E terrain and all-effects installed route; total-frame relative gates; handoff/Present; combined application CPU; complete GPU memory accounting.",
                UncollectedRows = pairs * count * 6 - rows.Count,
                ResourceLedgerBefore = Balances(before), ResourceLedgerAfter = Balances(after)
            };
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(evidence, SkyEvidenceJsonOptions));
            Console.WriteLine($"Sky qualification evidence: {output}; retained {rows.Count}/{pairs * count * 6} samples.");
        }
    }

    private static object[] Balances(D3D12ResourceSnapshot snapshot) => Enum.GetValues<D3D12ResourceOwner>()
        .Select(owner => (object)new { Owner = owner.ToString(), Balance = snapshot.GetBalance(owner) }).ToArray();

    private static double SkyPercentile(double[] sorted, int p) => sorted[(int)Math.Ceiling(sorted.Length * p / 100d) - 1];

    private readonly record struct SkyQualificationSample(string Phase, int Pair, int Sample, bool Enabled,
        double FrameMicroseconds, double SkyMicroseconds, double RecordMicroseconds, long AllocatedBytes, int Draws);

    private readonly record struct SkyQualificationSummary(string Phase, int Pair, int Count,
        double SkyP95Microseconds, double SkyP99Microseconds, double RecordP99Microseconds, long MaximumAllocatedBytes);
}
