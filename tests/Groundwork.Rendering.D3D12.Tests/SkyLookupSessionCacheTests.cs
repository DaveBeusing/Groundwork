using System.Numerics;
using System.Text.Json;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private static readonly bool[] LookupSessionReuseExpected = [false, true, true];
    private static readonly bool[] MixedSkySessionReuseExpected = [false, false, true, true];
    private static readonly string[] LookupPreparationStagesExpected = ["textures", "descriptors", "compute-pipeline", "ready"];
    private readonly record struct LookupPreparationEvent(int Owner, string Stage, long Start, long End, bool Reused);

    [TestMethod]
    public void WarpSkyLookupSessionReusesImmutableTripletAndKeepsOwnerResourcesFresh() => VerifyLookupSessionCache(true);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public void NvidiaSkyLookupSessionReusesImmutableTripletAndKeepsOwnerResourcesFresh() => VerifyLookupSessionCache(false);

    private static void VerifyLookupSessionCache(bool warp)
    {
        using var session = new D3D12DeviceSession(QualificationDiagnostics, useWarp: warp);
        var pipelineReuse = new List<bool>(3);
        var preparation = new List<LookupPreparationEvent>(12);
        var allocations = new List<long>(9);
        var texturePreparations = 0;
        var ownerThread = Environment.CurrentManagedThreadId;
        (nint Root, nint Graphics, nint Compute) firstHandles = default;
        byte[]? canonical = null;
        for (var owner = 0; owner < 3; owner++)
        {
            var currentOwner = owner;
            var hooks = new GpuRetirementTestHooks(
                AfterSkyPipelineReady: (start, end, reused) =>
                {
                    Assert.IsGreaterThan(0L, start);
                    Assert.IsGreaterThanOrEqualTo(start, end);
                    pipelineReuse.Add(reused);
                },
                AfterSkyLookupFirstAllocation: () => texturePreparations++,
                AfterSkyLookupPreparationStage: (stage, start, end, reused) =>
                {
                    Assert.AreEqual(ownerThread, Environment.CurrentManagedThreadId);
                    Assert.IsGreaterThan(0L, start);
                    Assert.IsGreaterThanOrEqualTo(start, end);
                    preparation.Add(new(currentOwner, stage, start, end, reused));
                },
                AfterSkyRecordMeasured: (_, _, _, bytes, _) => allocations.Add(bytes));
            using var gpu = new GpuDevice(warp, enableDebugLayer: true, enableDred: true,
                retirementHooks: hooks, session: session, skyLookupPrototype: true);
            AssertPlanetAdapter(gpu.Adapter, warp);
            Assert.AreEqual(0UL, gpu.SkyLookupAllocatedBytesForTests);
            Assert.AreEqual(0UL, gpu.SkyLookupDispatchesForTests);
            var frame = new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() };
            var first = SkyLookupPixels(GpuDevice.RenderOffscreen(gpu, 64, 64, frame), 64, 64);
            canonical ??= first;
            CollectionAssert.AreEqual(canonical, first);
            var handles = gpu.SkyLookupPipelineHandlesForTests;
            Assert.AreNotEqual((nint)0, handles.Root);
            Assert.AreNotEqual((nint)0, handles.Graphics);
            Assert.AreNotEqual((nint)0, handles.Compute);
            if (owner == 0) firstHandles = handles;
            else Assert.AreEqual(firstHandles, handles, "A cache hit must reuse all three actual native objects.");
            Assert.AreEqual(1024UL * 1024, gpu.SkyLookupAllocatedBytesForTests);
            CollectionAssert.AreEqual(LookupPreparationStagesExpected,
                preparation.Where(row => row.Owner == owner).Select(row => row.Stage).ToArray());
            var count = preparation.Count;
            var changed = frame with { Sequence = 2, Sky = frame.Sky!.Value with { SunRadiance = 0 } };
            var night = SkyLookupPixels(GpuDevice.RenderOffscreen(gpu, 64, 64, changed), 64, 64);
            Assert.IsFalse(first.SequenceEqual(night), "Cached PSOs must not retain stale LUT contents.");
            if (owner == 2)
                session.Dispose(); // The live owner retains independent COM references.
            var restored = SkyLookupPixels(GpuDevice.RenderOffscreen(gpu, 64, 64, frame with { Sequence = 3 }), 64, 64);
            CollectionAssert.AreEqual(first, restored);
            Assert.HasCount(count, preparation, "Warm frames must not emit first-use preparation stages.");
            Assert.AreEqual(3UL, gpu.SkyLookupDispatchesForTests, "Every frame must still regenerate owner-local LUTs.");
            Assert.AreEqual(0L, allocations[owner * 3 + 1]);
            Assert.AreEqual(0L, allocations[owner * 3 + 2]);
            gpu.Dispose();
            Assert.AreEqual(0UL, gpu.SkyLookupAllocatedBytesForTests);
            if (owner < 2) AssertSkyLookupSharedOwnerDiagnostics(gpu);
            else AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);
        }
        CollectionAssert.AreEqual(LookupSessionReuseExpected, pipelineReuse);
        CollectionAssert.AreEqual(LookupSessionReuseExpected,
            preparation.Where(row => row.Stage == "compute-pipeline").Select(row => row.Reused).ToArray());
        Assert.AreEqual(3, texturePreparations, "Textures must be allocated once per owner, including cache hits.");
        Assert.IsTrue(preparation.Where(row => row.Stage is "textures" or "descriptors").All(row => !row.Reused));
        var requested = Environment.GetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_CACHE_CAPTURE_DIR");
        var directory = Path.GetFullPath(requested ?? Path.Combine("artifacts", "sky-lookup-session-cache"));
        Directory.CreateDirectory(directory);
        var evidence = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(evidence, JsonSerializer.Serialize(new
        {
            DiagnosticOnly = true, PerformanceQualified = false, UseWarp = warp, PipelineReuse = pipelineReuse,
            Preparation = preparation, RecordAllocatedBytes = allocations, TexturePreparations = texturePreparations,
            Scope = "Three sequential owners, immutable native triplet reused, mutable resources regenerated, session disposed while final owner remains live",
        }, SkyLookupEvidenceOptions));
        Console.WriteLine("Lookup session cache evidence: " + evidence);
    }

    [TestMethod]
    public void WarpSkySessionKeepsDirectAndLookupPipelineAbisSeparate()
    {
        using var session = new D3D12DeviceSession(QualificationDiagnostics, useWarp: true);
        var reuse = new List<bool>(4);
        (nint Root, nint Graphics, nint Compute) direct = default, lookup = default;
        for (var owner = 0; owner < 4; owner++)
        {
            var prototype = (owner & 1) != 0;
            using var gpu = new GpuDevice(true, enableDebugLayer: true, enableDred: true, session: session,
                skyLookupPrototype: prototype, retirementHooks: new(AfterSkyPipelineReady: (_, _, hit) => reuse.Add(hit)),
                twilightPreview: true);
            _ = GpuDevice.RenderOffscreen(gpu, 64, 64, new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() });
            var handles = gpu.SkyLookupPipelineHandlesForTests;
            if (owner == 0) direct = handles;
            if (owner == 1) lookup = handles;
            if (owner == 2) Assert.AreEqual(direct, handles);
            if (owner == 3) Assert.AreEqual(lookup, handles);
            if (prototype)
            {
                Assert.AreNotEqual(direct.Root, handles.Root);
                Assert.AreNotEqual(direct.Graphics, handles.Graphics);
                Assert.AreNotEqual((nint)0, handles.Compute);
            }
            else
            {
                Assert.AreEqual((nint)0, handles.Compute);
                Assert.AreEqual(0UL, gpu.SkyLookupAllocatedBytesForTests);
                Assert.AreEqual(0UL, gpu.SkyLookupDispatchesForTests);
            }
            if (owner == 3) session.Dispose();
            gpu.Dispose();
            if (owner < 3)
            {
                AssertSkyLookupSharedOwnerDiagnostics(gpu);
                var retained = owner == 0 ? 2 : 5;
                Assert.AreEqual(retained, gpu.FinalDebugSummary!.RetainedSessionPreviewObjects);
                Assert.AreEqual(retained, gpu.FinalDebugSummary.LiveChildObjects);
            }
            else AssertCleanDiagnostics(new(gpu.Adapter, gpu.DebugLayerEnabled, gpu.DredEnabled, gpu.FinalDebugSummary, null), true);
        }
        CollectionAssert.AreEqual(MixedSkySessionReuseExpected, reuse);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WarpSkyLookupAllocationFailureCannotPublishPartialOrInvalidateCompleteSessionCache(bool primed)
    {
        using var session = new D3D12DeviceSession(QualificationDiagnostics, useWarp: true);
        var frame = new RenderFrame(1, Vector4.Zero) { Sky = SkyFixture() };
        if (primed)
        {
            using var prime = new GpuDevice(true, enableDebugLayer: true, enableDred: true,
                session: session, skyLookupPrototype: true);
            _ = GpuDevice.RenderOffscreen(prime, 64, 64, frame);
        }
        var failReuse = false;
        using (var failed = new GpuDevice(true, enableDebugLayer: true, enableDred: true,
            session: session, skyLookupPrototype: true,
            retirementHooks: new(AfterSkyPipelineReady: (_, _, reused) => failReuse = reused,
                AfterSkyLookupFirstAllocation: () => throw new InvalidOperationException("Injected cached-owner allocation failure."))))
        {
            var error = Assert.Throws<InvalidOperationException>(() => GpuDevice.RenderOffscreen(failed, 64, 64, frame));
            StringAssert.Contains(error.Message, "Injected cached-owner allocation failure");
            Assert.AreEqual(primed, failReuse);
            Assert.AreEqual(0UL, failed.SkyLookupAllocatedBytesForTests);
            failed.Dispose();
            AssertSkyLookupSharedOwnerDiagnostics(failed);
        }
        var recoveredReuse = false;
        var recoveredComputeReuse = false;
        using var recovered = new GpuDevice(true, enableDebugLayer: true, enableDred: true,
            session: session, skyLookupPrototype: true,
            retirementHooks: new(AfterSkyPipelineReady: (_, _, reused) => recoveredReuse = reused,
                AfterSkyLookupPreparationStage: (stage, _, _, reused) =>
                {
                    if (stage == "compute-pipeline") recoveredComputeReuse = reused;
                }));
        var a = SkyLookupPixels(GpuDevice.RenderOffscreen(recovered, 64, 64, frame), 64, 64);
        Assert.AreEqual(primed, recoveredReuse);
        Assert.AreEqual(primed, recoveredComputeReuse);
        session.Dispose();
        var b = SkyLookupPixels(GpuDevice.RenderOffscreen(recovered, 64, 64, frame with { Sequence = 2 }), 64, 64);
        CollectionAssert.AreEqual(a, b);
        recovered.Dispose();
        Assert.AreEqual(0UL, recovered.SkyLookupAllocatedBytesForTests);
        AssertCleanDiagnostics(new(recovered.Adapter, recovered.DebugLayerEnabled, recovered.DredEnabled, recovered.FinalDebugSummary, null), true);
    }
}
