using Groundwork.Core;
using Groundwork.Core.Tests;

namespace Groundwork.Voxels.Tests;

public sealed partial class VolumeStreamingTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MovementRebaseReusesPublishedGeometryWithinTheSameRendererEpoch(bool replaceRenderer)
    {
        using var run = new Run();
        var sampler = new RebaseProbeSource();
        using var initial = run.Stage(VolumeWorkClass.Movement, 0, 1, sampler);
        Assert.IsTrue(initial.Execute());
        await run.Publish(initial);
        using var before = run.Coordinator.TryRead();
        Assert.IsNotNull(before);
        var original = run.Coordinator.View;
        Assert.IsTrue(original.Frame.TryRebase(new(48, 0, 0), out var frame));
        var rebased = run.Coordinator.ChangeView(frame, original.RendererEpoch + (replaceRenderer ? 1UL : 0UL));
        Assert.IsTrue(run.Coordinator.ConfirmMask(rebased));

        sampler.DensitySamples = 0;
        using var next = run.Stage(VolumeWorkClass.Movement, 1, 1, sampler);
        Assert.IsTrue(next.ExecuteReusingPublishedAtRebase());
        Assert.AreEqual(replaceRenderer, sampler.DensitySamples > 0);
        Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);
        Assert.AreEqual(initial.Generation, ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root!).Terrain.Generation);

        await run.Publish(next);
        using var current = run.Coordinator.TryRead();
        Assert.IsNotNull(current);
        Assert.AreEqual(rebased, current.Info.View);
        Assert.AreEqual(before.Info.TerrainRevision, current.Info.TerrainRevision);
        foreach (var pair in before.Meshes.ToArray().Zip(current.Meshes.ToArray()))
            CollectionAssert.AreEqual(pair.First.Triangles.ToArray(), pair.Second.Triangles.ToArray());

        Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);
        await run.Open(next.Generation);
        Assert.IsFalse(run.Coordinator.Statistics.MovementHeld);
    }

    private sealed class RebaseProbeSource : IPlanetBrickSource
    {
        private readonly Source _source = new();
        internal int DensitySamples { get; set; }
        public PlanetDescriptor Descriptor => _source.Descriptor;

        public long SampleDensity(LatticeCoordinate coordinate)
        {
            DensitySamples++;
            return _source.SampleDensity(coordinate);
        }

        public ushort SampleMaterial(LatticeCoordinate coordinate) => _source.SampleMaterial(coordinate);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AheadWorkSurvivesViewsAndInitialCommitOnlyWithTheSameSampler(bool sameSampler)
    {
        using var run = new Run();
        var donorSampler = new Source();
        var publishingSampler = sameSampler ? donorSampler : new Source(threshold: 307);
        using var donor = StageAhead(run, donorSampler);
        Assert.IsTrue(donor.ExecuteDetailedProfiled(out var donorProfile));
        var prior = run.Coordinator.View;
        run.Coordinator.ChangeView(prior.Frame, prior.RendererEpoch);
        Assert.IsTrue(run.Coordinator.ConfirmMask(run.Coordinator.View));

        using var initial = run.Stage(VolumeWorkClass.Movement, 0, 1, publishingSampler);
        Assert.IsTrue(initial.ExecuteDetailedProfiled(out var initialProfile));
        await run.Publish(initial);
        Assert.AreEqual(!sameSampler, donor.IsCancellationRequested);

        using var target = run.Stage(VolumeWorkClass.Movement, 1, 1, publishingSampler);
        Assert.IsTrue(target.ExecuteDetailedProfiled(donor, out var profile));
        Assert.AreEqual(sameSampler ? donor.Generation : 0UL, profile.ReuseSourceGeneration);
        Assert.IsTrue(profile.Bricks.ToArray().All(brick => brick.Reused == sameSampler));
        Assert.IsTrue(profile.Meshes.ToArray().All(mesh => mesh.Reused == sameSampler));
        VolumeReuseAssertions.SameGeometry(sameSampler ? donorProfile : initialProfile, profile);
        donor.Dispose();
        run.Coordinator.Stop();
        Assert.AreEqual(default, run.Coordinator.Statistics.Used);
    }

    [TestMethod]
    [DataRow("pending")]
    [DataRow("disposed")]
    [DataRow("invalidated epoch")]
    public void AheadWorkFallsBackWithoutWaitingForUnavailableDonor(string unavailable)
    {
        using var run = new Run();
        var sampler = new Source();
        using var donor = StageAhead(run, sampler);
        if (unavailable != "pending")
            Assert.IsTrue(donor.Execute());
        if (unavailable == "disposed")
            donor.Dispose();
        var prior = run.Coordinator.View;
        if (unavailable == "invalidated epoch")
        {
            run.Coordinator.InvalidateRenderer(prior.RendererEpoch);
            Assert.IsTrue(donor.IsCancellationRequested);
            run.Coordinator.ChangeView(prior.Frame, prior.RendererEpoch + 1);
        }
        else
            run.Coordinator.ChangeView(prior.Frame, prior.RendererEpoch);

        using var target = run.Stage(VolumeWorkClass.Movement, 0, 1, sampler);
        using var cold = run.Stage(VolumeWorkClass.Recovery, 0, 1, sampler);
        Assert.IsTrue(target.ExecuteDetailedProfiled(donor, out var profile));
        Assert.IsTrue(cold.ExecuteDetailedProfiled(out var expected));
        Assert.AreEqual(0UL, profile.ReuseSourceGeneration);
        Assert.IsFalse(profile.Bricks.ToArray().Any(brick => brick.Reused));
        Assert.IsFalse(profile.Meshes.ToArray().Any(mesh => mesh.Reused));
        VolumeReuseAssertions.SameGeometry(expected, profile);
        donor.Dispose();
        run.Coordinator.Stop();
        Assert.AreEqual(default, run.Coordinator.Statistics.Used);
    }

    [TestMethod]
    public async Task PrivateBuildReusesPayloadButOnlyTargetCanPublish()
    {
        using var run = new Run();
        var sampler = new Source();
        using var donor = StagePrivate(run, sampler);
        Assert.IsTrue(donor.ExecuteDetailedProfiled(out var privateProfile));
        using var target = run.Stage(VolumeWorkClass.Movement, 0, 1, sampler);
        Assert.IsTrue(target.ExecuteDetailedProfiled(donor, out var profile));
        Assert.AreEqual(donor.Generation, profile.ReuseSourceGeneration);
        Assert.IsTrue(profile.Bricks.ToArray().All(brick => brick.Reused));
        Assert.IsTrue(profile.Meshes.ToArray().All(mesh => mesh.Reused));
        VolumeReuseAssertions.SameGeometry(privateProfile, profile);
        Assert.AreEqual(2, run.Coordinator.Statistics.Generations);
        Assert.AreEqual(run.Unit.Retained + run.Unit.Retained, run.Coordinator.Statistics.Used);

        var begin = await run.BeginTick();
        var candidates = await CoreRuntimeFixture.SealAsync(run.World, begin.Tick, BarrierKind.CommitResults);
        Assert.IsNull(run.Coordinator.TryPreparePublication(donor.Generation, candidates, []));
        Assert.IsNull(run.Coordinator.TryRead());
        donor.Dispose();
        Assert.AreEqual(1, run.Coordinator.Statistics.Generations);
        Assert.AreEqual(run.Unit.Retained, run.Coordinator.Statistics.Used);

        var preparation = run.Coordinator.TryPreparePublication(target.Generation, candidates, []);
        Assert.IsNotNull(preparation);
        Assert.AreEqual(BarrierStatus.Committed, run.World.Publication.Finalization.TryFinalize(preparation).Status);
        using (var published = run.Coordinator.TryRead())
        {
            Assert.IsNotNull(published);
            Assert.AreEqual(target.Generation, published.Info.Generation);
            Assert.IsTrue(published.Meshes.ToArray().All(mesh => mesh.PlanIdentity == published.Info.PlanIdentity &&
                mesh.TerrainRevision == published.Info.TerrainRevision));
            Assert.IsTrue(published.TryReadDensity(new(Descriptor.WorldId, Descriptor.PlanetId, default), out var density));
            Assert.IsLessThan(0L, density);
        }
        run.Coordinator.Stop();
        Assert.AreEqual(default, run.Coordinator.Statistics.Used);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PublishingWorkerFallsBackForDifferentSamplerOrDisposedSource(bool disposeDonor)
    {
        using var run = new Run();
        var sampler = new Source();
        using var donor = StagePrivate(run, sampler);
        Assert.IsTrue(donor.ExecuteDetailedProfiled(out var privateProfile));
        if (disposeDonor)
            donor.Dispose();
        using var target = run.Stage(VolumeWorkClass.Movement, 0, 1, disposeDonor ? sampler : new Source(threshold: 307));
        Assert.IsTrue(target.ExecuteDetailedProfiled(donor, out var profile));
        Assert.IsFalse(profile.Bricks.ToArray().Any(brick => brick.Reused));
        Assert.IsFalse(profile.Meshes.ToArray().Any(mesh => mesh.Reused));
        if (disposeDonor)
        {
            VolumeReuseAssertions.SameGeometry(privateProfile, profile);
        }
        else
        {
            Assert.IsTrue(privateProfile.Bricks.ToArray().Zip(profile.Bricks.ToArray())
                .Any(pair => pair.First.DensitySha256 != pair.Second.DensitySha256));
            Assert.IsTrue(privateProfile.Meshes.ToArray().Zip(profile.Meshes.ToArray())
                .Any(pair => pair.First.GeometrySha256 != pair.Second.GeometrySha256));
        }
        run.Coordinator.Stop();
        Assert.AreEqual(default, run.Coordinator.Statistics.Used);
    }

    [TestMethod]
    public void ShiftedWindowReusesOverlappingBricksButRebuildsMeshesWithNewClamps()
    {
        using var run = new Run();
        var sampler = new Source();
        using var donor = StagePrivate(run, sampler);
        Assert.IsTrue(donor.Execute());
        var shifted = VolumeStreamingSelection.Select(Descriptor, new(64, 0, 0), new(1, 0, 0));
        using var target = StageShifted(VolumeWorkClass.Movement);
        using var cold = StageShifted(VolumeWorkClass.Speculative);
        Assert.IsTrue(target.ExecuteDetailedProfiled(donor, out var profile));
        Assert.IsTrue(cold.ExecuteDetailedProfiled(out var expected));
        Assert.AreEqual(donor.Generation, profile.ReuseSourceGeneration);
        var shared = run.Selection.Requests.ToArray().Select(request => request.Brick)
            .Intersect(shifted.Requests.ToArray().Select(request => request.Brick)).ToArray();
        Assert.HasCount(32, shared);
        CollectionAssert.AreEquivalent(shared, profile.Bricks.ToArray().Where(brick => brick.Reused)
            .Select(brick => brick.Key).ToArray());
        Assert.IsFalse(profile.Meshes.ToArray().Any(mesh => mesh.Reused));
        VolumeReuseAssertions.SameGeometry(expected, profile);

        VolumeStreamingWork StageShifted(VolumeWorkClass kind)
        {
            Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.TryStage(kind, shifted, 0, 1,
                2_000_000, run.Unit, sampler, out var work));
            Assert.IsNotNull(work);
            return work;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrivateBuildReusesPublishedBricksAcrossRendererEpoch(bool shifted)
    {
        using var run = new Run();
        var sampler = new Source();
        using var initial = run.Stage(VolumeWorkClass.Movement, 0, 1, sampler);
        Assert.IsTrue(initial.Execute());
        await run.Publish(initial);
        var view = run.Coordinator.View;
        run.Coordinator.ChangeView(view.Frame, view.RendererEpoch + 1);
        var selection = shifted ? VolumeStreamingSelection.Select(Descriptor, new(64, 0, 0), new(1, 0, 0)) : run.Selection;

        using var target = StagePrivateRecipient(run, VolumeWorkClass.Recovery, selection, sampler, 1);
        using var cold = StagePrivateRecipient(run, VolumeWorkClass.Speculative, selection, sampler, 1);
        Assert.IsTrue(target.ExecuteDetailedProfiledReusingPublishedBricks(out var profile));
        Assert.IsTrue(cold.ExecuteDetailedProfiled(out var expected));
        Assert.AreEqual(initial.Generation, profile.ReuseSourceGeneration);
        var shared = run.Selection.Requests.ToArray().Select(request => request.Brick)
            .Intersect(selection.Requests.ToArray().Select(request => request.Brick)).ToArray();
        Assert.HasCount(shifted ? 32 : 64, shared);
        CollectionAssert.AreEquivalent(shared, profile.Bricks.ToArray().Where(brick => brick.Reused)
            .Select(brick => brick.Key).ToArray());
        Assert.IsFalse(profile.Meshes.ToArray().Any(mesh => mesh.Reused));
        VolumeReuseAssertions.SameGeometry(expected, profile);
        Assert.AreEqual(0, run.Coordinator.Statistics.Readers);
        Assert.AreEqual(run.Unit.Retained + run.Unit.Retained + run.Unit.Retained, run.Coordinator.Statistics.Used);
        run.Coordinator.Stop();
        Assert.AreEqual(default, run.Coordinator.Statistics.Used);
    }

    [TestMethod]
    [DataRow("exact")]
    [DataRow("shifted window")]
    [DataRow("changed strides")]
    public async Task PrivateBuildReusesPublishedMeshesOnlyForAnExactRequestWindow(string change)
    {
        using var run = new Run();
        var sampler = new Source();
        using var initial = run.Stage(VolumeWorkClass.Movement, 0, 1, sampler);
        Assert.IsTrue(initial.Execute());
        await run.Publish(initial);
        var selection = change switch
        {
            "shifted window" => VolumeStreamingSelection.Select(Descriptor, new(64, 0, 0), new(1, 0, 0)),
            "changed strides" => VolumeStreamingSelection.Select(Descriptor, default, new(1, 0, 0), supportCells: 32),
            _ => VolumeStreamingSelection.Select(Descriptor, default, new(1, 0, 0))
        };
        var exact = change == "exact";
        Assert.AreEqual(exact, selection.Requests.SequenceEqual(run.Selection.Requests));
        Assert.AreEqual(change != "shifted window", selection.Minimum == run.Selection.Minimum);
        var view = run.Coordinator.View;
        Assert.IsTrue(view.Frame.TryRebase(new(48, 0, 0), out var nextFrame));
        run.Coordinator.ChangeView(nextFrame, view.RendererEpoch + 1);

        using var target = StagePrivateRecipient(run, VolumeWorkClass.Recovery, selection, sampler, 1);
        using var cold = StagePrivateRecipient(run, VolumeWorkClass.Speculative, selection, sampler, 1);
        Assert.IsTrue(target.ExecuteDetailedProfiledReusingPublishedTerrain(out var profile));
        Assert.IsTrue(cold.ExecuteDetailedProfiled(out var expected));
        Assert.AreEqual(initial.Generation, profile.ReuseSourceGeneration);
        Assert.AreEqual(exact, profile.ReusedMeshPlan);
        Assert.AreEqual(change == "shifted window" ? 32 : 64, profile.Bricks.ToArray().Count(brick => brick.Reused));
        Assert.IsTrue(profile.Meshes.ToArray().All(mesh => mesh.Reused == exact));
        VolumeReuseAssertions.SameGeometry(expected, profile);
        Assert.AreEqual(0, run.Coordinator.Statistics.Readers);

        // Releasing the private plan must leave the committed plan's brick leases live.
        target.Dispose();
        using (var retained = run.Coordinator.TryRead())
        {
            Assert.IsNotNull(retained);
            Assert.AreEqual(initial.Generation, retained.Info.Generation);
            var vertex = new LatticeCoordinate(0, 0, 0);
            Assert.IsTrue(retained.TryReadDensity(new(Descriptor.WorldId, Descriptor.PlanetId, vertex), out var density));
            Assert.AreEqual(sampler.SampleDensity(vertex), density);
        }
        run.Coordinator.Stop();
        Assert.AreEqual(default, run.Coordinator.Statistics.Used);
    }

    [TestMethod]
    [DataRow("no published terrain")]
    [DataRow("different sampler")]
    [DataRow("reader limit")]
    public async Task PrivateBuildFallsBackWhenPublishedBricksAreUnavailable(string unavailable)
    {
        using var run = new Run();
        var sampler = new Source();
        var hasPublished = unavailable != "no published terrain";
        if (hasPublished)
        {
            using var initial = run.Stage(VolumeWorkClass.Movement, 0, 1, sampler);
            Assert.IsTrue(initial.Execute());
            await run.Publish(initial);
            var view = run.Coordinator.View;
            run.Coordinator.ChangeView(view.Frame, view.RendererEpoch + 1);
        }
        var readers = new List<VolumeStreamingLease>();
        try
        {
            if (unavailable == "reader limit")
            {
                for (var index = 0; index < run.Limits.Readers; index++)
                {
                    var lease = run.Coordinator.TryRead();
                    Assert.IsNotNull(lease);
                    readers.Add(lease);
                }
                Assert.IsNull(run.Coordinator.TryRead());
            }
            var replacement = unavailable == "different sampler" ? new Source(threshold: 307) : sampler;
            var revision = hasPublished ? 1UL : 0UL;
            using var target = StagePrivateRecipient(run, VolumeWorkClass.Recovery, run.Selection, replacement, revision);
            using var cold = StagePrivateRecipient(run, VolumeWorkClass.Speculative, run.Selection, replacement, revision);
            Assert.IsTrue(target.ExecuteDetailedProfiledReusingPublishedTerrain(out var profile));
            Assert.IsTrue(cold.ExecuteDetailedProfiled(out var expected));
            Assert.AreEqual(0UL, profile.ReuseSourceGeneration);
            Assert.IsFalse(profile.ReusedMeshPlan);
            Assert.IsFalse(profile.Bricks.ToArray().Any(brick => brick.Reused));
            Assert.IsFalse(profile.Meshes.ToArray().Any(mesh => mesh.Reused));
            VolumeReuseAssertions.SameGeometry(expected, profile);
            Assert.AreEqual(readers.Count, run.Coordinator.Statistics.Readers);
        }
        finally
        {
            foreach (var reader in readers)
                reader.Dispose();
        }
        run.Coordinator.Stop();
        Assert.AreEqual(default, run.Coordinator.Statistics.Used);
    }

    [TestMethod]
    public async Task PrivateBuildCannotReusePublishedMeshesBeyondItsPayloadLimit()
    {
        using var run = new Run();
        var sampler = new Source();
        using var initial = run.Stage(VolumeWorkClass.Movement, 0, 1, sampler);
        Assert.IsTrue(initial.Execute());
        await run.Publish(initial);
        var view = run.Coordinator.View;
        run.Coordinator.ChangeView(view.Frame, view.RendererEpoch + 1);
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.TryStagePrivate(VolumeWorkClass.Recovery,
            run.Selection, 1, 1, 0, run.Unit, sampler, out var work));
        Assert.IsNotNull(work);
        using (work)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                work.ExecuteDetailedProfiledReusingPublishedTerrain(out _));
        }
        Assert.AreEqual(0, run.Coordinator.Statistics.Readers);
        Assert.AreEqual(run.Unit.Retained, run.Coordinator.Statistics.Used);
        run.Coordinator.Stop();
        Assert.AreEqual(default, run.Coordinator.Statistics.Used);
    }

    private static VolumeStreamingWork StagePrivateRecipient(Run run, VolumeWorkClass kind,
        VolumeStreamingSelection selection, IPlanetBrickSource sampler, ulong sourceRevision)
    {
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.TryStagePrivate(kind, selection, sourceRevision, 1,
            2_000_000, run.Unit, sampler, out var work));
        Assert.IsNotNull(work);
        return work;
    }

    private static VolumeStreamingWork StagePrivate(Run run, IPlanetBrickSource sampler)
    {
        var result = run.Coordinator.TryStagePrivate(VolumeWorkClass.Recovery, run.Selection, 0, 1,
            2_000_000, run.Unit, sampler, out var work);
        Assert.AreEqual(VolumeAdmission.Accepted, result);
        Assert.IsNotNull(work);
        return work;
    }

    private static VolumeStreamingWork StageAhead(Run run, IPlanetBrickSource sampler)
    {
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.TryStagePrivateAhead(VolumeWorkClass.Speculative,
            run.Selection, 0, 1, 2_000_000, run.Unit, sampler, out var work));
        Assert.IsNotNull(work);
        return work;
    }
}

public sealed partial class VolumeEditTests
{
    [TestMethod]
    [DataRow(9, 1, 1, false)]
    [DataRow(9, 1, 1, true)]
    [DataRow(-1, 1, 2, true)]
    [DataRow(0, 2, 2, true)]
    public async Task PrivateReuseMatchesColdEditAndInvalidatesIncidentBricksAndMeshHalo(
        int brushX, int invalidatedBricks, int invalidatedMeshes, bool ahead)
    {
        // The x=0 mesh face also separates bricks. A brush one vertex earlier still affects its gradient stencil.
        var cached = await EditWithOptionalPrivateSource(brushX, true, ahead);
        var cold = await EditWithOptionalPrivateSource(brushX, false, ahead);
        VolumeReuseAssertions.SameGeometry(cold.Profile, cached.Profile);
        Assert.AreEqual(cold.Identity, cached.Identity);
        CollectionAssert.AreEqual(cold.Overlay, cached.Overlay);
        Assert.AreEqual(invalidatedBricks, cached.Profile.Bricks.ToArray().Count(brick => !brick.Reused));
        Assert.AreEqual(invalidatedMeshes, cached.Profile.Meshes.ToArray().Count(mesh => !mesh.Reused));
        Assert.IsTrue(cached.Profile.Bricks.ToArray().Any(brick => brick.Reused));
        Assert.IsTrue(cached.Profile.Meshes.ToArray().Any(mesh => mesh.Reused));
        if (brushX <= 0)
        {
            var haloNeighbor = cached.Profile.Meshes.ToArray().Single(mesh => mesh.Origin == new LatticeCoordinate(0, 0, 0));
            Assert.IsFalse(haloNeighbor.Reused);
        }
    }

    private static async Task<(VolumeStreamingWorkDetailedProfile Profile, string Identity, byte[] Overlay)>
        EditWithOptionalPrivateSource(int brushX, bool reuse, bool ahead)
    {
        using var run = new EditRun();
        var prepareBeforeInitial = brushX == 9;
        if (!prepareBeforeInitial)
            await run.BuildInitial();
        VolumeStreamingWork? aheadSource = null;
        if (reuse && ahead)
            Assert.AreEqual(VolumeAdmission.Accepted, run.StageAheadRecovery(out aheadSource, prepareBeforeInitial ? 0UL : 1UL));
        using var ownedAhead = aheadSource;
        VolumeStreamingWorkDetailedProfile privateProfile = default;
        if (aheadSource is not null)
            Assert.IsTrue(aheadSource.ExecuteDetailedProfiled(out privateProfile));
        if (prepareBeforeInitial)
            await run.BuildInitial();
        var begin = await run.OpenTick();
        var brush = new VolumeBrushIntent(begin.Tick, 1, 1, new(brushX, 9, 8), new(brushX, 11, 9),
            VolumeBrushOperation.Add, 11);
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [brush]));
        run.Hold();
        VolumeStreamingWork? source = null;
        if (reuse && !ahead)
            Assert.AreEqual(VolumeAdmission.Accepted, run.StagePrivateRecovery(out source));
        using var ownedSource = source;
        if (source is not null)
        {
            Assert.IsTrue(source.ExecuteDetailedProfiled(out privateProfile));
        }
        var donor = aheadSource ?? source;
        if (reuse)
            Assert.IsNotNull(donor);
        Assert.AreEqual(VolumeAdmission.Accepted, run.Stage(brush, out var edit));
        Assert.IsNotNull(edit);
        using (edit)
        {
            VolumeStreamingWorkDetailedProfile profile;
            if (donor is null)
            {
                Assert.IsTrue(edit.ExecuteDetailedProfiled(out profile));
            }
            else
            {
                Assert.IsTrue(edit.ExecuteDetailedProfiled(donor, out profile));
                Assert.AreEqual(donor.Generation, profile.ReuseSourceGeneration);
                Assert.IsTrue(privateProfile.Bricks.ToArray().Zip(profile.Bricks.ToArray())
                    .Any(pair => pair.First.DensitySha256 != pair.Second.DensitySha256));
                Assert.IsTrue(privateProfile.Meshes.ToArray().Zip(profile.Meshes.ToArray())
                    .Any(pair => pair.First.GeometrySha256 != pair.Second.GeometrySha256));
                if (!ahead)
                    donor.Dispose();
            }
            await run.Commit(edit, edit: true);
            if (aheadSource is not null)
            {
                Assert.IsTrue(aheadSource.IsCancellationRequested);
                if (prepareBeforeInitial)
                {
                    Assert.AreEqual(VolumeAdmission.Accepted, run.StageRecovery(out var successor, revision: 2));
                    Assert.IsNotNull(successor);
                    using (successor)
                    {
                        Assert.IsTrue(successor.ExecuteDetailedProfiled(aheadSource, out var fallback));
                        Assert.AreEqual(0UL, fallback.ReuseSourceGeneration);
                        Assert.IsFalse(fallback.Bricks.ToArray().Any(brick => brick.Reused));
                        Assert.IsFalse(fallback.Meshes.ToArray().Any(mesh => mesh.Reused));
                        VolumeReuseAssertions.SameGeometry(profile, fallback);
                    }
                }
            }
            using var published = run.Coordinator.TryRead();
            Assert.IsNotNull(published);
            Assert.AreEqual(edit.Generation, published.Info.Generation);
            Assert.AreEqual(2UL, published.Info.TerrainRevision);
            Assert.IsTrue(published.Meshes.ToArray().All(mesh => mesh.PlanIdentity == published.Info.PlanIdentity &&
                mesh.TerrainRevision == published.Info.TerrainRevision));
            Assert.IsTrue(published.TryReadDensity(new(run.Source.Descriptor.WorldId, run.Source.Descriptor.PlanetId,
                brush.Minimum), out var density));
            Assert.IsLessThan(0L, density);
            return (profile, published.Info.PlanIdentity, Overlay(published));
        }
    }
}

internal static class VolumeReuseAssertions
{
    internal static void SameGeometry(VolumeStreamingWorkDetailedProfile expected, VolumeStreamingWorkDetailedProfile actual)
    {
        CollectionAssert.AreEqual(
            expected.Bricks.ToArray().Select(brick => brick with { Reused = false, ElapsedTicks = 0 }).ToArray(),
            actual.Bricks.ToArray().Select(brick => brick with { Reused = false, ElapsedTicks = 0 }).ToArray());
        CollectionAssert.AreEqual(
            expected.Meshes.ToArray().Select(mesh => mesh with { Reused = false, ElapsedTicks = 0 }).ToArray(),
            actual.Meshes.ToArray().Select(mesh => mesh with { Reused = false, ElapsedTicks = 0 }).ToArray());
    }
}
