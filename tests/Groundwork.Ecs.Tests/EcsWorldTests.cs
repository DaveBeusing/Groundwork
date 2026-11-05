using Groundwork.Core;

namespace Groundwork.Ecs.Tests;

[TestClass]
public sealed class EcsWorldTests
{
    [TestMethod]
    public async Task StructuralCreationDefersAndRecycledSlotsRejectOldGenerations()
    {
        using var fixture = new EcsFixture(slots: 1);
        var original = await SeedAsync(fixture);
        Assert.AreEqual(new Position(12), fixture.World.Read<Position>(original).Value);
        Assert.AreEqual(ComponentReadStatus.Missing, fixture.World.Read<Health>(original).Status);
        Assert.AreEqual(ComponentReadStatus.InvalidId, fixture.World.Read<Position>(default).Status);
        using var foreign = new EcsFixture();
        Assert.AreEqual(ComponentReadStatus.ForeignWorld, foreign.World.Read<Position>(original).Status);

        using var destroy = fixture.Admit(JobInputKind.Borrowed, [fixture.WorldScope()], [fixture.WorldScope()]);
        var permit = fixture.Dispatch(destroy);
        TargetVersion? version;
        using (var input = fixture.Capture(permit))
            version = input.ReadEntity(original).Version;
        fixture.Complete(permit, EcsFixture.Existing(1, JobCommandKind.DestroyEntity, original, default, version));
        Assert.AreEqual(BatchDispositionKind.Defer, (await fixture.EndAsync(2)).Dispositions.Single().Kind);
        Assert.AreEqual(ComponentReadStatus.Present, fixture.World.Read<Position>(original).Status);

        await fixture.BeginAsync(3);
        Assert.AreEqual(ComponentReadStatus.Stale, fixture.World.Read<Position>(original).Status);
        using var create = fixture.Admit(JobInputKind.None, write: [fixture.WorldScope()]);
        fixture.Complete(fixture.Dispatch(create), EcsFixture.Create(), EcsFixture.AddNew(2, EcsFixture.Positions, new Position(33)));
        await fixture.EndAsync(3);
        await fixture.BeginAsync(4);
        var reused = fixture.World.Query<Position>().Single();
        Assert.AreEqual(original.Slot, reused.Entity.Slot);
        Assert.AreEqual(original.Generation + 1, reused.Entity.Generation);
        Assert.AreEqual(new Position(33), reused.Value);
        Assert.AreEqual(ComponentReadStatus.Stale, fixture.World.Read<Position>(original).Status);
    }

    [TestMethod]
    public async Task BorrowedCaptureBlocksDrainUntilCopyOwnerReleases()
    {
        using var fixture = new EcsFixture();
        var entity = await SeedAsync(fixture);
        using var handle = fixture.Admit(JobInputKind.Borrowed, [fixture.WorldScope(EcsFixture.Positions)]);
        var permit = fixture.Dispatch(handle);
        using var input = fixture.Capture(permit);
        Assert.AreEqual(new Position(12), input.Read<Position>(entity).Value);
        Assert.AreEqual(ComponentReadStatus.UndeclaredAccess, input.Read<Health>(entity).Status);
        Assert.IsNotNull(fixture.Phase);
        fixture.Ports.Phases.Close(fixture.Phase);
        var request = new BarrierRequest(fixture.Ports.Committed.Read().Stamp, 2, BarrierKind.CommitResults);
        var drain = fixture.Ports.Borrow.CloseAndDrainAsync(request, fixture.World.Safety.CreateBorrowedDrainDeadline()).AsTask();
        Assert.IsFalse(drain.IsCompleted);
        input.Dispose();
        Assert.AreEqual(BorrowDrainStatus.Drained, await drain.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.AreEqual(ComponentReadStatus.ExpiredLease, input.Read<Position>(entity).Status);
        fixture.Complete(permit);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WholeBarrierConflictsRejectBothSourcesRegardlessOfCompletionOrder(bool reverse)
    {
        using var fixture = new EcsFixture();
        var first = await SeedAsync(fixture, second: true);
        var second = fixture.World.Query<Position>()[1].Entity;
        var firstScope = new ResourceKey(fixture.World.Id, EcsFixture.Positions, new ResourceScope(ScopeKind.Entity, null, first));
        var secondScope = firstScope with { Scope = new ResourceScope(ScopeKind.Entity, null, second) };
        using var left = fixture.Admit(JobInputKind.OwnedSnapshot, [firstScope], [firstScope], [new ComponentTarget(first, EcsFixture.Positions)]);
        using var right = fixture.Admit(JobInputKind.OwnedSnapshot, [firstScope], [firstScope], [new ComponentTarget(first, EcsFixture.Positions)]);
        using var disjoint = fixture.Admit(JobInputKind.OwnedSnapshot, [secondScope], [secondScope], [new ComponentTarget(second, EcsFixture.Positions)]);
        var jobs = new[] { left, right, disjoint };
        int[] order = reverse ? [2, 1, 0] : [0, 1, 2];
        foreach (int index in order)
        {
            var target = index == 2 ? second : first;
            // Vary readiness at the authority boundary. Overlapping workers do not run concurrently.
            var permit = fixture.Dispatch(jobs[index]);
            TargetVersion? expected;
            using (var input = fixture.Capture(permit))
                expected = input.Read<Position>(target).Version;
            var command = EcsFixture.Existing(1, JobCommandKind.ReplaceComponent, target, EcsFixture.Positions, expected,
                EcsFixture.Registry.Encode(new Position(index == 2 ? 99 : 19 + index)));
            fixture.Complete(permit, command);
        }
        var plan = await fixture.EndAsync(2);
        Assert.HasCount(2, plan.Issues);
        Assert.AreEqual(BatchRejection.WriteConflict, plan.Issues[0].Reason);
        Assert.AreEqual(left.Id, plan.Issues[0].Job);
        Assert.AreEqual(right.Id, plan.Issues[1].Job);
        Assert.AreEqual(plan.Issues[1].Batch, plan.Issues[0].ConflictingBatch);
        Assert.AreEqual(plan.Issues[0].Batch, plan.Issues[1].ConflictingBatch);
        Assert.AreEqual(new Position(12), fixture.World.Read<Position>(first).Value);
        Assert.AreEqual(new Position(99), fixture.World.Read<Position>(second).Value);
    }

    [TestMethod]
    public async Task SnapshotSpansBarriersAndStaleResultCannotOverwriteNewerComponent()
    {
        using var fixture = new EcsFixture();
        var entity = await SeedAsync(fixture);
        var all = fixture.WorldScope();
        using var mixed = fixture.Admit(JobInputKind.Borrowed, [all], [all]);
        var first = fixture.Dispatch(mixed);
        TargetVersion? positionVersion;
        TargetVersion? missingHealthVersion;
        using (var input = fixture.Capture(first))
        {
            positionVersion = input.Read<Position>(entity).Version;
            var missing = input.Read<Health>(entity);
            Assert.AreEqual(ComponentReadStatus.Missing, missing.Status);
            Assert.IsNotNull(missing.Version);
            missingHealthVersion = missing.Version;
        }
        fixture.Complete(first,
            EcsFixture.Existing(1, JobCommandKind.ReplaceComponent, entity, EcsFixture.Positions, positionVersion,
                EcsFixture.Registry.Encode(new Position(50))),
            EcsFixture.Existing(2, JobCommandKind.AddComponent, entity, EcsFixture.Healths, missingHealthVersion,
                EcsFixture.Registry.Encode(new Health(80))));

        var target = new ComponentTarget(entity, EcsFixture.Positions);
        using var delayed = fixture.Admit(JobInputKind.OwnedSnapshot, [all], [all], [target]);
        var second = fixture.Dispatch(delayed);
        using var snapshot = fixture.Capture(second);
        Assert.AreEqual(BatchDispositionKind.Defer, (await fixture.EndAsync(2)).Dispositions.Single().Kind);
        Assert.AreEqual(new Position(12), fixture.World.Read<Position>(entity).Value);
        await fixture.BeginAsync(3);
        Assert.AreEqual(new Position(50), fixture.World.Read<Position>(entity).Value);
        Assert.AreEqual(new Health(80), fixture.World.Read<Health>(entity).Value);
        var captured = snapshot.Read<Position>(entity);
        Assert.AreEqual(new Position(12), captured.Value);
        Assert.AreEqual(ComponentReadStatus.UndeclaredAccess, snapshot.Read<Health>(entity).Status);
        snapshot.Dispose();
        fixture.Complete(second, EcsFixture.Existing(1, JobCommandKind.ReplaceComponent, entity, EcsFixture.Positions, captured.Version,
            EcsFixture.Registry.Encode(new Position(100))));
        var stale = await fixture.EndAsync(3);
        Assert.AreEqual(BatchRejection.StaleTarget, stale.Issues.Single().Reason);
        Assert.AreEqual(new Position(50), fixture.World.Read<Position>(entity).Value);
    }

    [TestMethod]
    public async Task MalformedLaterCommandDoesNotSpendSlotOrPublishTheValidPrefix()
    {
        using var fixture = new EcsFixture(slots: 1);
        await fixture.BeginAsync(1);
        using var invalid = fixture.Admit(JobInputKind.None, write: [fixture.WorldScope()]);
        fixture.Complete(fixture.Dispatch(invalid), EcsFixture.Create(),
            EcsFixture.AddNew(2, EcsFixture.Positions, new Position(9)) with { Payload = [0] });
        var rejected = await fixture.EndAsync(1);
        Assert.AreEqual(BatchRejection.InvalidBatch, rejected.Issues.Single().Reason);

        await fixture.BeginAsync(2);
        Assert.IsEmpty(fixture.World.Query<Position>());
        using var valid = fixture.Admit(JobInputKind.None, write: [fixture.WorldScope()]);
        fixture.Complete(fixture.Dispatch(valid), EcsFixture.Create(), EcsFixture.AddNew(2, EcsFixture.Positions, new Position(10)));
        await fixture.EndAsync(2);
        await fixture.BeginAsync(3);
        Assert.AreEqual(new EntityId(fixture.World.Id, 0, 1), fixture.World.Query<Position>().Single().Entity);
    }

    [TestMethod]
    public async Task PartitionGrantDoesNotAcquireEntitiesAddedAfterAdmission()
    {
        var partition = new PartitionId(1);
        using var fixture = new EcsFixture(partitions: [partition]);
        var original = await SeedAsync(fixture, partition: fixture.World.FindPartition(partition));
        var admitted = fixture.World.FindPartition(partition);
        Assert.IsNotNull(admitted);
        var key = new ResourceKey(fixture.World.Id, EcsFixture.Positions, new ResourceScope(ScopeKind.Partition, admitted, null));
        using var waiting = fixture.Admit(JobInputKind.Borrowed, [key]);
        using var create = fixture.Admit(JobInputKind.None, write: [fixture.WorldScope()]);
        fixture.Complete(fixture.Dispatch(create), EcsFixture.Create(partition: admitted),
            EcsFixture.AddNew(2, EcsFixture.Positions, new Position(99)));
        await fixture.EndAsync(2);
        await fixture.BeginAsync(3);
        Assert.AreNotEqual(admitted, fixture.World.FindPartition(partition));
        var newcomer = fixture.World.Query<Position>()[1].Entity;
        var permit = fixture.Dispatch(waiting);
        using var input = fixture.Capture(permit);
        Assert.AreEqual(ComponentReadStatus.Present, input.Read<Position>(original).Status);
        Assert.AreEqual(ComponentReadStatus.UndeclaredAccess, input.Read<Position>(newcomer).Status);
        input.Dispose();
        fixture.Complete(permit);
    }

    [TestMethod]
    public async Task PartitionCreationAndAnExistingMemberWriteRejectBothBatches()
    {
        var partition = new PartitionId(1);
        using var fixture = new EcsFixture(partitions: [partition]);
        var entity = await SeedAsync(fixture, partition: fixture.World.FindPartition(partition));
        var destination = fixture.World.FindPartition(partition);
        var member = new ResourceKey(fixture.World.Id, EcsFixture.Positions, new ResourceScope(ScopeKind.Entity, null, entity));
        using var create = fixture.Admit(JobInputKind.None, write: [fixture.WorldScope()]);
        using var replace = fixture.Admit(JobInputKind.Borrowed, [member], [member]);
        fixture.Complete(fixture.Dispatch(create), EcsFixture.Create(partition: destination),
            EcsFixture.AddNew(2, EcsFixture.Positions, new Position(30)));
        var permit = fixture.Dispatch(replace);
        TargetVersion? expected;
        using (var input = fixture.Capture(permit))
            expected = input.Read<Position>(entity).Version;
        fixture.Complete(permit, EcsFixture.Existing(1, JobCommandKind.ReplaceComponent, entity, EcsFixture.Positions, expected,
            EcsFixture.Registry.Encode(new Position(40))));

        var plan = await fixture.EndAsync(2);
        Assert.HasCount(2, plan.Issues);
        Assert.IsTrue(plan.Issues.All(issue => issue.Reason == BatchRejection.WriteConflict));
        Assert.AreEqual(create.Id, plan.Issues[0].Job);
        Assert.AreEqual(replace.Id, plan.Issues[1].Job);
        Assert.AreEqual(plan.Issues[1].Batch, plan.Issues[0].ConflictingBatch);
        Assert.AreEqual(plan.Issues[0].Batch, plan.Issues[1].ConflictingBatch);
        Assert.AreEqual(new Position(12), fixture.World.Read<Position>(entity).Value);
        Assert.IsEmpty((await fixture.BeginAsync(3)).Dispositions);
        Assert.HasCount(1, fixture.World.Query<Position>());
    }

    [TestMethod]
    public async Task ConcurrentStageRetainsOnePlanAndStorageStaysChargedThroughCustody()
    {
        var clock = new TestClock();
        using var fixture = new EcsFixture(clock: clock);
        await fixture.BeginAsync(1);
        using (var seed = fixture.Admit(JobInputKind.None, write: [fixture.WorldScope()]))
        {
            fixture.Complete(fixture.Dispatch(seed), EcsFixture.Create(), EcsFixture.AddNew(2, EcsFixture.Positions, new Position(12)));
            await fixture.EndAsync(1);
        }
        var request = new BarrierRequest(fixture.Ports.Committed.Read().Stamp, 2, BarrierKind.BeginTick);
        Assert.AreEqual(BorrowDrainStatus.Drained,
            await fixture.Ports.Borrow.CloseAndDrainAsync(request, fixture.World.Safety.CreateBorrowedDrainDeadline()));
        var sealedSet = fixture.Ports.Candidates.SealCandidates(request);
        Assert.AreEqual(CandidateSealStatus.Sealed, sealedSet.Status);
        Assert.IsNotNull(sealedSet.Candidates);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = AttemptStageAsync(fixture.World, sealedSet.Candidates, ready.Task);
        var second = AttemptStageAsync(fixture.World, sealedSet.Candidates, ready.Task);
        ready.SetResult();
        var attempts = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        using var retained = attempts.OfType<EcsBatchPlan>().Single();
        Assert.AreEqual(BatchDispositionKind.Commit, retained.Dispositions.Single().Kind);
        Assert.AreEqual(BarrierPreparationStatus.Prepared, retained.Prepare(fixture.World.Safety.State.Read().PendingFaultGeneration));
        Assert.AreEqual(EcsFixture.RetainedBytes, fixture.StorageBudget.ReservedBytes);
        Assert.ThrowsExactly<InvalidOperationException>(() => new EcsFixture(storageBudget: fixture.StorageBudget));

        var deadline = fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        Assert.AreEqual(ShutdownSettlement.Pending, fixture.Runtime.SettleStop());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.IsTrue(deadline.IsExpired(clock));
        Assert.AreEqual(ShutdownSettlement.Custody, fixture.Runtime.SettleStop());
        Assert.IsFalse(fixture.World.TryReleaseStorage());
        Assert.AreEqual(EcsFixture.RetainedBytes, fixture.StorageBudget.ReservedBytes);
        Assert.AreEqual(BarrierStatus.Stopped, retained.FinalizePublication().Status);
        Assert.AreEqual(ShutdownSettlement.Released, fixture.Runtime.SettleStop());
        Assert.IsTrue(fixture.World.TryReleaseStorage());
        Assert.AreEqual(0L, fixture.StorageBudget.ReservedBytes);
        using var replacement = new EcsFixture(storageBudget: fixture.StorageBudget);
        Assert.AreEqual(EcsFixture.RetainedBytes, replacement.StorageBudget.ReservedBytes);
    }

    [TestMethod]
    public async Task PublishedObserverLeaseKeepsStorageChargedUntilAcknowledged()
    {
        var clock = new TestClock();
        using var fixture = new EcsFixture(clock: clock);
        var entity = await SeedAsync(fixture);
        await fixture.EndAsync(2);
        var result = fixture.Ports.ObserverSnapshots.TryAcquire();
        Assert.AreEqual(ObserverReadStatus.Acquired, result.Status);
        using var lease = result.Lease;
        Assert.IsNotNull(lease);
        Assert.AreEqual(new Position(12), ((EcsObserverState)lease.Current.State).Read<Position>(entity).Value);

        var deadline = fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.IsTrue(deadline.IsExpired(clock));
        Assert.AreEqual(ShutdownSettlement.Custody, fixture.Runtime.SettleStop());
        Assert.AreEqual(EcsFixture.RetainedBytes, fixture.StorageBudget.ReservedBytes);
        Assert.AreEqual(ObserverPublication.Blocked,
            fixture.World.PublishObserver(lease.Current.Stamp, fixture.World.Safety.State.Read().PendingFaultGeneration));
        lease.Dispose();
        Assert.AreEqual(ShutdownSettlement.Released, fixture.Runtime.SettleStop());
        Assert.IsTrue(fixture.World.TryReleaseStorage());
        Assert.AreEqual(0L, fixture.StorageBudget.ReservedBytes);
    }

    private static Task<EcsBatchPlan?> AttemptStageAsync(EcsWorld world, CandidateSet candidates, Task start) => Task.Run(async () =>
    {
        await start;
        try
        {
            return world.Stage(candidates);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    });

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        internal void Advance(TimeSpan interval) => Interlocked.Add(ref _timestamp, interval.Ticks);
    }

    private static async Task<EntityId> SeedAsync(EcsFixture fixture, bool second = false, PartitionScope? partition = null)
    {
        await fixture.BeginAsync(1);
        using var job = fixture.Admit(JobInputKind.None, write: [fixture.WorldScope()]);
        var permit = fixture.Dispatch(job);
        if (second)
            fixture.Complete(permit, EcsFixture.Create(partition: partition), EcsFixture.AddNew(2, EcsFixture.Positions, new Position(12)),
                EcsFixture.Create(3, 2, partition), EcsFixture.AddNew(4, EcsFixture.Positions, new Position(14), 2));
        else
            fixture.Complete(permit, EcsFixture.Create(partition: partition), EcsFixture.AddNew(2, EcsFixture.Positions, new Position(12)));
        Assert.AreEqual(BatchDispositionKind.Defer, (await fixture.EndAsync(1)).Dispositions.Single().Kind);
        Assert.IsEmpty(fixture.World.Query<Position>());
        Assert.AreEqual(BatchDispositionKind.Commit, (await fixture.BeginAsync(2)).Dispositions.Single().Kind);
        return fixture.World.Query<Position>()[0].Entity;
    }
}
