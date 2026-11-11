using Groundwork.Core;
using Groundwork.Core.Tests;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class VolumeEditTests
{
    [TestMethod]
    public async Task SealedHeadCommitsCavityAtomicallyAndReloadPreservesOverlay()
    {
        using var run = new EditRun();
        await run.BuildInitial();
        using var before = run.Coordinator.TryRead();
        Assert.IsNotNull(before);
        var begin = await run.OpenTick();
        var cut = Brush(begin.Tick, 1, 1, VolumeBrushOperation.Remove);
        var successor = cut with { Id = 2, Minimum = new(8, 0, 0), Maximum = new(10, 2, 2) };
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [successor, cut]));
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [cut, successor]));
        Assert.AreEqual(VolumeAdmission.Invalid, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [cut, cut]));
        run.Hold();
        Assert.AreEqual(VolumeAdmission.Backpressure, run.Stage(successor, out _));
        Assert.AreEqual(VolumeAdmission.Accepted, run.Stage(cut, out var work));
        Assert.IsNotNull(work);
        using (work)
        {
            Assert.IsTrue(await Task.Run(() => work.Execute()));
            Assert.AreEqual(1UL, ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Terrain.TerrainRevision);
            Assert.AreEqual(VolumeEditStatus.Building, run.Status(cut));
            await run.Commit(work, edit: true);
        }

        Assert.AreEqual(VolumeEditStatus.Committed, run.Status(cut));
        Assert.AreEqual(VolumeEditStatus.Stale, run.Status(successor));
        Assert.IsTrue(run.Coordinator.Statistics.MovementHeld);
        using var after = run.Coordinator.TryRead();
        Assert.IsNotNull(after);
        var hits = new VolumeRayHit[8];
        Assert.AreEqual(0, before.Raycast(new(0, 0, -5), new(0, 0, 5), hits).Count);
        Assert.AreEqual(2, after.Raycast(new(0, 0, -5), new(0, 0, 5), hits).Count);
        Assert.AreEqual(cut, ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).CommittedEdit);
        var bytes = Overlay(after);
        before.Dispose();

        await run.OpenTick();
        await run.CommitUnchanged();
        run.Hold();
        await run.StreamAt(new PlanetVector(192, 0, 0), 2);
        run.Hold();
        await run.StreamAt(default, 2);
        using var reloaded = run.Coordinator.TryRead();
        Assert.IsNotNull(reloaded);
        CollectionAssert.AreEqual(bytes, Overlay(reloaded));
        Assert.AreEqual(2, reloaded.Raycast(new(0, 0, -5), new(0, 0, 5), hits).Count);
        after.Dispose();
        reloaded.Dispose();

        begin = await run.OpenTick();
        var fill = Brush(begin.Tick, 1, 2, VolumeBrushOperation.Add);
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [fill]));
        run.Hold();
        Assert.AreEqual(VolumeAdmission.Accepted, run.Stage(fill, out work));
        Assert.IsNotNull(work);
        using (work)
        {
            Assert.IsTrue(work.Execute());
            await run.Commit(work, edit: true);
        }
        using var filled = run.Coordinator.TryRead();
        Assert.IsNotNull(filled);
        Assert.AreEqual(0, filled.Raycast(new(0, 0, -5), new(0, 0, 5), hits).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RejectedEditKeepsOldGenerationAndReturnsReservation(bool cancel)
    {
        using var run = new EditRun(cancel ? 256 : 1);
        await run.BuildInitial();
        var begin = await run.OpenTick();
        var intent = Brush(begin.Tick, 1, 1, VolumeBrushOperation.Remove);
        var far = intent with { Minimum = new(1000, 0, 0), Maximum = new(1002, 2, 2) };
        Assert.AreEqual(VolumeAdmission.Invalid, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [far]));
        Assert.AreEqual(VolumeAdmission.Stale, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [intent with { ExpectedRevision = 2 }]));
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [intent]));
        run.Hold();
        Assert.AreEqual(VolumeAdmission.Accepted, run.Stage(intent, out var work));
        Assert.IsNotNull(work);
        using (work)
        {
            if (cancel)
            {
                Assert.IsTrue(work.Execute());
                Assert.IsTrue(run.Coordinator.RejectEdit(intent.Tick, intent.Id));
            }
            else
                Assert.IsFalse(work.Execute());
        }
        Assert.AreEqual(VolumeEditStatus.Rejected, run.Status(intent));
        Assert.AreEqual(1, run.Coordinator.Statistics.Generations);
        using var after = run.Coordinator.TryRead();
        Assert.IsNotNull(after);
        Assert.AreEqual(1UL, after.Info.TerrainRevision);
        Assert.HasCount(8, Overlay(after));
    }

    [TestMethod]
    public void DependencyBoundsCoverEightBricksAndRejectOverflowingInputs()
    {
        var brush = Brush(1, 1, 1, VolumeBrushOperation.Add);
        Assert.IsTrue(VolumeStreamingCoordinator.TryGetEditDependencies(brush, out var footprint));
        Assert.AreEqual(new LatticeBrick(-1, -1, -1), footprint.Minimum);
        Assert.AreEqual(new LatticeBrick(0, 0, 0), footprint.Maximum);
        foreach (var coordinate in new[] { long.MinValue, long.MaxValue, -PlanetDensityField.MaximumCoordinate, PlanetDensityField.MaximumCoordinate })
            Assert.IsFalse(VolumeStreamingCoordinator.TryGetEditDependencies(
                brush with { Minimum = new(coordinate, 0, 0), Maximum = new(coordinate, 0, 0) }, out _));
        Assert.IsFalse(VolumeStreamingCoordinator.TryGetEditDependencies(brush with { Maximum = new(64, 1, 1) }, out _));
    }

    [TestMethod]
    public async Task ReorderedInputsAndConcurrentAttemptsProduceIdenticalBytesAndMeshes()
    {
        var first = await Replay(reverse: false);
        var second = await Replay(reverse: true);
        CollectionAssert.AreEqual(first.Bytes, second.Bytes);
        CollectionAssert.AreEqual(first.Triangles, second.Triangles);
        Assert.AreEqual(first.Plan, second.Plan);

        static async Task<(byte[] Bytes, VolumeTriangle[] Triangles, string Plan)> Replay(bool reverse)
        {
            using var run = new EditRun();
            await run.BuildInitial();
            var begin = await run.OpenTick();
            var first = Brush(begin.Tick, 1, 1, VolumeBrushOperation.Remove);
            var second = first with { Id = 2, Operation = VolumeBrushOperation.Add, Material = 11 };
            Assert.AreEqual(VolumeAdmission.Accepted,
                run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, reverse ? [second, first] : [first, second]));
            run.Hold();
            var later = Task.Run(() => run.Stage(second, out _));
            Assert.AreEqual(VolumeAdmission.Accepted, run.Stage(first, out var work));
            Assert.IsNotNull(work);
            using (work)
            {
                Assert.IsTrue(work.Execute());
                Assert.AreEqual(VolumeAdmission.Backpressure, await later);
                await run.Commit(work, edit: true);
            }
            using var committed = run.Coordinator.TryRead();
            Assert.IsNotNull(committed);
            return (Overlay(committed), committed.Meshes.ToArray().SelectMany(mesh => mesh.Triangles.ToArray()).ToArray(),
                committed.Info.PlanIdentity);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedTickCannotBeResealedWithAPartialSet(bool mixedTickFirst)
    {
        using var run = new EditRun();
        await run.BuildInitial();
        var begin = await run.OpenTick();
        var intent = Brush(begin.Tick, 1, 1, VolumeBrushOperation.Remove);
        var oversized = Enumerable.Range(1, VolumeStreamingCoordinator.MaximumEditsPerTick + 1)
            .Select(id => intent with { Id = (ulong)id }).ToArray();
        oversized[^1] = oversized[^1] with { Tick = begin.Tick + 1 };
        if (mixedTickFirst)
            Array.Reverse(oversized);
        Assert.AreEqual(VolumeAdmission.Invalid, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, oversized));
        Assert.AreEqual(VolumeAdmission.Stale, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [intent]));
        Assert.IsFalse(run.Coordinator.TryReadEdit(intent.Tick, intent.Id, out _, out _));
    }

    [TestMethod]
    public async Task StopBeforeCoreFinalizationPreservesTheOldOverlayAndRejectsTheReceipt()
    {
        using var run = new EditRun();
        await run.BuildInitial();
        var begin = await run.OpenTick();
        var intent = Brush(begin.Tick, 1, 1, VolumeBrushOperation.Remove);
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [intent]));
        run.Hold();
        Assert.AreEqual(VolumeAdmission.Accepted, run.Stage(intent, out var work));
        Assert.IsNotNull(work);
        using (work)
        {
            Assert.IsTrue(work.Execute());
            using var retained = run.Coordinator.TryRead();
            Assert.IsNotNull(retained);
            var old = run.World.Publication.Committed.Read().Root;
            await run.CommitUnchanged();
            var next = await run.Begin();
            var candidates = await CoreRuntimeFixture.SealAsync(run.World, next.Tick, BarrierKind.CommitResults);
            var prepared = run.Coordinator.TryPreparePublication(work.Generation, candidates, []);
            Assert.IsNotNull(prepared);
            run.Coordinator.Stop();
            Assert.AreEqual(BarrierStatus.Unchanged, run.World.Publication.Finalization.TryFinalize(prepared).Status);
            Assert.AreSame(old, run.World.Publication.Committed.Read().Root);
            Assert.AreEqual(VolumeEditStatus.Rejected, run.Status(intent));
            Assert.HasCount(8, Overlay(retained));
            var found = false;
            while (run.Coordinator.TryReadEvent(out var record))
                found |= record.Edit == intent && record.EditStatus == VolumeEditStatus.Rejected;
            Assert.IsTrue(found);
        }
    }

    [TestMethod]
    public async Task PrivateRendererLeaseCannotExportAnUncommittedOverlay()
    {
        using var run = new EditRun();
        await run.BuildInitial();
        var begin = await run.OpenTick();
        var intent = Brush(begin.Tick, 1, 1, VolumeBrushOperation.Remove);
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [intent]));
        var view = run.Coordinator.ChangeView(run.Coordinator.View.Frame, 2);
        var renderer = run.Coordinator.TryBindRenderer(2);
        Assert.IsNotNull(renderer);
        Assert.IsTrue(run.Coordinator.ConfirmMask(view, renderer, 2));
        Assert.AreEqual(VolumeAdmission.Accepted, run.Stage(intent, out var work));
        Assert.IsNotNull(work);
        using (work)
        {
            Assert.IsTrue(work.Execute());
            using var staging = run.Coordinator.TryAcquirePreparedGeometry(renderer, work.Generation, out _, out _);
            Assert.IsNotNull(staging);
            var bytes = new byte[16_392];
            Assert.IsFalse(staging.TryWriteEditOverlay(bytes, out var written));
            Assert.AreEqual(0, written);
            Assert.IsNull(staging.EditLineage);
            Assert.IsTrue(run.Coordinator.RejectEdit(intent.Tick, intent.Id));
            Assert.IsFalse(staging.TryWriteEditOverlay(bytes, out _));
            staging.Dispose();
            Assert.AreEqual(VolumeEditStatus.Rejected, run.Status(intent));
            using var committed = run.Coordinator.TryRead();
            Assert.IsNotNull(committed);
            Assert.AreEqual(run.Source.Descriptor, committed.EditLineage);
            Assert.HasCount(8, Overlay(committed));
        }
    }

    [TestMethod]
    public async Task LateReadinessWaitsForTheNextCutAndInvalidPreparationHasNoTerminalReceipt()
    {
        using var run = new EditRun();
        await run.BuildInitial();
        var begin = await run.OpenTick();
        var cut = await CoreRuntimeFixture.SealAsync(run.World, begin.Tick, BarrierKind.CommitResults);
        var intent = Brush(begin.Tick, 1, 1, VolumeBrushOperation.Remove);
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [intent]));
        run.Hold();
        Assert.AreEqual(VolumeAdmission.Accepted, run.Stage(intent, out var work));
        Assert.IsNotNull(work);
        using (work)
        {
            Assert.IsTrue(work.Execute());
            Assert.IsNull(run.Coordinator.TryPreparePublication(work.Generation, cut, []));
            run.FinishUnchanged(cut);
            var next = await run.Begin();
            cut = await CoreRuntimeFixture.SealAsync(run.World, next.Tick, BarrierKind.CommitResults);
            Assert.IsNull(run.Coordinator.TryPreparePublication(work.Generation, cut, default));
            Assert.AreEqual(VolumeEditStatus.Building, run.Status(intent));
            while (run.Coordinator.TryReadEvent(out var entry))
                Assert.IsNull(entry.EditStatus);
            var prepared = run.Coordinator.TryPreparePublication(work.Generation, cut, []);
            Assert.IsNotNull(prepared);
            var result = run.World.Publication.Finalization.TryFinalize(prepared);
            Assert.AreEqual(BarrierStatus.Committed, result.Status);
            Assert.AreEqual(VolumeEditStatus.Committed, run.Status(intent));
            Assert.IsTrue(run.Coordinator.TryReadEvent(out var receipt));
            Assert.AreEqual(VolumeEditStatus.Committed, receipt.EditStatus);
            Assert.AreEqual(result.Stamp, receipt.Stamp);
            Assert.IsFalse(run.Coordinator.TryReadEvent(out _));
        }
    }

    [TestMethod]
    public async Task RecoveryCommitsWhileCanceledOverlayWorkerStillOwnsItsReservation()
    {
        using var run = new EditRun();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        await run.BuildInitial();
        var begin = await run.OpenTick();
        var intent = Brush(begin.Tick, 1, 1, VolumeBrushOperation.Remove);
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [intent]));
        run.Hold();
        Assert.AreEqual(VolumeAdmission.Accepted, run.Stage(intent, out var edit));
        Assert.IsNotNull(edit);
        using (edit)
        {
            run.Source.BeforeDensity = () =>
            {
                entered.Set();
                Assert.IsTrue(resume.Wait(TimeSpan.FromSeconds(15)));
            };
            var worker = Task.Run(() => edit.Execute());
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
                Assert.AreEqual(VolumeAdmission.Accepted, run.StageRecovery(out var recovery));
                Assert.IsNotNull(recovery);
                using (recovery)
                {
                    Assert.IsTrue(run.Coordinator.RejectEdit(intent.Tick, intent.Id));
                    Assert.AreEqual(VolumeEditStatus.Rejected, run.Status(intent));
                    Assert.IsTrue(recovery.Execute());
                    await run.Commit(recovery);
                    Assert.IsFalse(worker.IsCompleted);
                    Assert.AreEqual(2, run.Coordinator.Statistics.Generations);
                }
            }
            finally
            {
                resume.Set();
            }
            Assert.IsFalse(await worker);
        }
        Assert.AreEqual(1, run.Coordinator.Statistics.Generations);
        using var retained = run.Coordinator.TryRead();
        Assert.IsNotNull(retained);
        Assert.AreEqual(1UL, retained.Info.TerrainRevision);
        Assert.HasCount(8, Overlay(retained));
    }

    [TestMethod]
    public async Task RemovingSupportPreservesPoseUntilTheEditedViewOpens()
    {
        using var run = new EditRun();
        await run.BuildInitial();
        var begin = await run.OpenTick();
        var player = new PlanetWalkState(new(0, 0, 8), new(1, 0, 0), default);
        var initial = run.Coordinator.TryPlanMovement(player, 0, 0, out _);
        Assert.IsNotNull(initial);
        Assert.IsTrue(initial.Result.Grounded);
        var cut = await CoreRuntimeFixture.SealAsync(run.World, begin.Tick, BarrierKind.CommitResults);
        var movement = run.Coordinator.TryPrepareMovement(initial, cut, []);
        Assert.IsNotNull(movement);
        var poseCommit = run.World.Publication.Finalization.TryFinalize(movement);
        Assert.AreEqual(BarrierStatus.Committed, poseCommit.Status);
        CoreRuntimeFixture.PublishObserver(run.World, poseCommit.Stamp!.Value, (int)begin.Tick);

        begin = await run.OpenTick();
        var brush = new VolumeBrushIntent(begin.Tick, 1, 1, new(-3, -3, 6), new(3, 3, 9), VolumeBrushOperation.Remove);
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [brush]));
        run.Hold();
        Assert.AreEqual(VolumeAdmission.Accepted, run.Stage(brush, out var work));
        Assert.IsNotNull(work);
        using (work)
        {
            Assert.IsTrue(work.Execute());
            await run.Commit(work, edit: true);
        }
        var retained = ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Player;
        Assert.AreEqual(initial.Result.State, retained);
        Assert.IsNull(run.Coordinator.TryPlanMovement(retained!.Value, 0, 0, out _));
        await run.OpenTick();
        var falling = run.Coordinator.TryPlanMovement(retained.Value, 0, 0, out _);
        Assert.IsNotNull(falling);
        Assert.IsFalse(falling.Result.Grounded);
        Assert.IsLessThan(retained.Value.Position.Z, falling.Result.State.Position.Z);
    }

    [TestMethod]
    public async Task FullJournalStopsBeforeAnUnrecordedStaleReceipt()
    {
        using var run = new EditRun(journalRecords: 13);
        await run.BuildInitial();
        var begin = await run.OpenTick();
        var intent = Brush(begin.Tick, 1, 1, VolumeBrushOperation.Remove);
        Assert.AreEqual(VolumeAdmission.Accepted, run.Coordinator.SealEditTick(begin.Tick, run.Source.Descriptor, [intent]));
        run.Hold();
        Assert.AreEqual(VolumeAdmission.Accepted, run.StageRecovery(out var recovery));
        Assert.IsNotNull(recovery);
        using (recovery)
        {
            Assert.IsTrue(recovery.Execute());
            await run.Commit(recovery);
        }
        Assert.AreEqual(VolumeAdmission.Closed, run.Stage(intent, out _));
        Assert.IsTrue(run.Coordinator.Statistics.Stopped);
        Assert.AreEqual(VolumeEditStatus.Rejected, run.Status(intent));
        Assert.AreEqual(VolumeEventKind.Stopped, run.Coordinator.TerminalEvidence!.Value.Kind);
    }

    private static VolumeBrushIntent Brush(ulong tick, ulong id, ulong revision, VolumeBrushOperation operation) =>
        new(tick, id, revision, new(-1, -1, -1), new(1, 1, 1), operation,
            operation == VolumeBrushOperation.Add ? (ushort)7 : (ushort)0);

    private static byte[] Overlay(VolumeStreamingLease lease)
    {
        var buffer = new byte[16_392];
        Assert.IsTrue(lease.TryWriteEditOverlay(buffer, out var written));
        return buffer[..written];
    }

    private sealed class EditRun : IDisposable
    {
        private const long MeshBytes = 8_000_000;
        private readonly CoreRuntimeFixture _fixture = new();
        private readonly VolumeResources _resources;
        private ulong _tick;
        private ulong _frame;
        private ulong _generation;
        private bool _open;
        internal SourceField Source { get; } = new();
        internal WorldSafetyPorts World { get; }
        internal VolumeStreamingCoordinator Coordinator { get; }
        internal VolumeStreamingSelection Selection { get; }

        internal EditRun(int limit = 256, int journalRecords = 256)
        {
            World = _fixture.OpenWorld();
            Selection = Select(default);
            _resources = new(VolumeStreamingCoordinator.RequiredCpuBytes(Selection, MeshBytes) + limit * 64L + 4096,
                0, 0, 0, 1, 1, 1, 64, 1);
            var baseline = _resources.Retained + _resources.Retained + _resources.Retained;
            baseline = baseline with { LeaseSlots = baseline.LeaseSlots + 8 };
            Coordinator = new(World, Source, new VolumeStreamingLimits(baseline + _resources + _resources + _resources + _resources,
                baseline, _resources, _resources, _resources, _resources, 12, 8, journalRecords), limit);
        }

        private VolumeStreamingSelection Select(PlanetVector center) =>
            VolumeStreamingSelection.Select(Source.Descriptor, center, new PlanetVector(-1, 0, 0));

        internal Task BuildInitial()
        {
            Assert.IsTrue(Coordinator.ConfirmMask(Coordinator.View));
            return StreamAt(default, 0);
        }

        internal async Task StreamAt(PlanetVector center, ulong revision)
        {
            Assert.AreEqual(VolumeAdmission.Accepted, Coordinator.TryStage(VolumeWorkClass.Movement, Select(center),
                revision, Math.Max(1, revision), MeshBytes, _resources, Source, out var work));
            Assert.IsNotNull(work);
            using (work)
            {
                Assert.IsTrue(work.Execute());
                await Commit(work);
            }
        }

        internal VolumeAdmission StageRecovery(out VolumeStreamingWork? work) =>
            Coordinator.TryStage(VolumeWorkClass.Recovery, Selection, 1, 1, MeshBytes, _resources, Source, out work);

        internal void FinishUnchanged(CandidateSet cut)
        {
            var unchanged = CoreRuntimeFixture.Prepare(World, cut, []);
            var outcome = World.Publication.Finalization.TryFinalize(unchanged);
            Assert.AreEqual(BarrierStatus.Unchanged, outcome.Status);
            CoreRuntimeFixture.PublishObserver(World, outcome.Stamp!.Value, (int)_tick);
            _open = false;
        }

        internal VolumeAdmission Stage(VolumeBrushIntent intent, out VolumeStreamingWork? work) =>
            Coordinator.TryStageEdit(intent.Tick, intent.Id, Selection, MeshBytes, _resources, out work);

        internal void Hold()
        {
            Coordinator.ChangeView(Coordinator.View.Frame, Coordinator.View.RendererEpoch);
            Assert.IsTrue(Coordinator.ConfirmMask(Coordinator.View));
        }

        internal async Task<BarrierStamp> OpenTick()
        {
            using var registration = Coordinator.TryRegisterFrame(_generation, Coordinator.View, ++_frame);
            Assert.IsNotNull(registration);
            Assert.IsTrue(Coordinator.AcknowledgeFrame(registration));
            var begin = await Begin();
            Assert.IsTrue(Coordinator.BeginTick(begin));
            return begin;
        }

        internal async Task<BarrierStamp> Begin()
        {
            var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, ++_tick, BarrierKind.BeginTick);
            _open = true;
            return stamp;
        }

        internal async Task Commit(VolumeStreamingWork work, bool edit = false)
        {
            if (edit && _open)
                await CommitUnchanged();
            if (!_open)
                await Begin();
            var candidates = await CoreRuntimeFixture.SealAsync(World, _tick, BarrierKind.CommitResults);
            var prepared = Coordinator.TryPreparePublication(work.Generation, candidates, []);
            Assert.IsNotNull(prepared);
            var outcome = World.Publication.Finalization.TryFinalize(prepared);
            Assert.AreEqual(BarrierStatus.Committed, outcome.Status);
            CoreRuntimeFixture.PublishObserver(World, outcome.Stamp!.Value, (int)_tick);
            _generation = work.Generation;
            _open = false;
        }

        internal async Task CommitUnchanged()
        {
            var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(World, _tick, BarrierKind.CommitResults);
            CoreRuntimeFixture.PublishObserver(World, stamp, (int)_tick);
            _open = false;
        }

        internal VolumeEditStatus Status(VolumeBrushIntent intent)
        {
            Assert.IsTrue(Coordinator.TryReadEdit(intent.Tick, intent.Id, out var status, out var stamp));
            Assert.AreEqual(status == VolumeEditStatus.Committed, stamp.HasValue);
            return status;
        }

        public void Dispose()
        {
            Coordinator.Stop();
            _fixture.Dispose();
        }
    }

    private sealed class SourceField : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor { get; } = new(new Guid("00112233-4455-6677-8899-aabbccddeeff"),
            new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"), 42, 1, 1, 1, new string('a', 64), 128);
        internal Action? BeforeDensity;
        public long SampleDensity(LatticeCoordinate p)
        {
            Interlocked.Exchange(ref BeforeDensity, null)?.Invoke();
            return (p.Z - 8) * PlanetDensityField.UnitsPerCell;
        }
        public ushort SampleMaterial(LatticeCoordinate p) => 3;
    }
}
