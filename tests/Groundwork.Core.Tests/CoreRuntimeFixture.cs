using System.Collections.Immutable;

namespace Groundwork.Core.Tests;

internal sealed class CoreRuntimeFixture : IDisposable
{
    public static ResourceQuota Quota { get; } = new(
        1,
        16,
        new JobQuota(8, 4, 16, 16, 4, 8, 16, 65536, 65536, 65536),
        new DiagnosticQuota(1, 16384, 12, 32768, 4, 4, 64, 1024));

    private static ResourceQuota WorldQuota { get; } = Quota with
    {
        OutstandingOperations = 8,
        Diagnostics = Quota.Diagnostics with { EmergencyRecords = 9, EmergencyBytes = 28672 },
    };

    public CoreRuntimeFixture(TestClock? clock = null, ResourceQuota? runtimeQuota = null, RuntimeTimingLimits? timing = null)
    {
        Clock = clock ?? new TestClock();
        var quota = runtimeQuota ?? Quota;
        Budget = new ProcessResourceBudget(new ProcessResourceLimits(1, quota));
        var reservation = Budget.TryReserveRuntime(quota);
        Assert.IsNotNull(reservation);
        var interval = TimeSpan.FromSeconds(2);
        Runtime = RuntimeSafetyAuthority.Create(Clock, timing ?? new RuntimeTimingLimits(interval, interval, interval), reservation);
    }

    public TestClock Clock { get; }
    public ProcessResourceBudget Budget { get; }
    public RuntimeSafetyAuthority Runtime { get; }

    public WorldSafetyPorts OpenWorld(IBarrierSupervisorPolicy? policy = null, JobQuota? jobs = null)
    {
        Assert.IsTrue(Runtime.TryAllocateWorldId(out var world));
        var quota = jobs is { } configuredJobs ? WorldQuota with { Jobs = configuredJobs } : WorldQuota;
        return Runtime.OpenWorld(world, quota, policy ?? new ContinuePolicy(), new Root(world), new ScopePolicy());
    }

    public static async Task<CandidateSet> SealAsync(WorldSafetyPorts world, ulong tick, BarrierKind kind)
    {
        var publication = world.Publication;
        var request = new BarrierRequest(publication.Committed.Read().Stamp, tick, kind);
        var drained = await publication.Borrow.CloseAndDrainAsync(request, world.CreateBorrowedDrainDeadline());
        Assert.AreEqual(BorrowDrainStatus.Drained, drained);

        var result = publication.Candidates.SealCandidates(request);
        Assert.AreEqual(CandidateSealStatus.Sealed, result.Status);
        Assert.IsNotNull(result.Candidates);
        return result.Candidates;
    }

    public static PreparedBarrierPublication Prepare(
        WorldSafetyPorts world,
        CandidateSet candidates,
        ImmutableArray<BatchDisposition> dispositions,
        IWorldStateRoot? root = null)
    {
        var publication = world.Publication;
        var result = publication.Preparation.TryPrepare(new BarrierPublicationDraft(
            candidates,
            root ?? publication.Committed.Read().Root,
            world.State.Read().PendingFaultGeneration,
            dispositions));
        Assert.AreEqual(BarrierPreparationStatus.Prepared, result.Status);
        Assert.IsNotNull(result.Publication);
        return result.Publication;
    }

    public static async Task<BarrierStamp> AdvanceEmptyBarrierAsync(WorldSafetyPorts world, ulong tick, BarrierKind kind)
    {
        var candidates = await SealAsync(world, tick, kind);
        Assert.IsEmpty(candidates.Batches);
        var result = world.Publication.Finalization.TryFinalize(Prepare(world, candidates, []));
        Assert.AreEqual(BarrierStatus.Unchanged, result.Status);
        Assert.IsTrue(result.Stamp.HasValue);
        return result.Stamp.GetValueOrDefault();
    }

    public static void PublishObserver(WorldSafetyPorts world, BarrierStamp stamp, int value)
    {
        var draft = new ObserverPublicationDraft(stamp, world.State.Read().PendingFaultGeneration, new ObserverState(stamp.World, value));
        Assert.AreEqual(ObserverPublication.Published, world.Publication.Observer.TryPublish(draft));
    }

    public void Dispose()
    {
        var deadline = Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        Clock.Advance(TimeSpan.FromSeconds(2));
        _ = deadline.IsExpired(Clock);
        Runtime.SettleStop();
    }

    internal class TestClock : TimeProvider
    {
        private long _timestamp = TimeSpan.TicksPerSecond;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan time) => Interlocked.Add(ref _timestamp, time.Ticks);
    }

    internal sealed record Root(WorldId World, int Value = 0) : IWorldStateRoot;

    internal sealed record ObserverState(WorldId World, int Value) : IWorldObserverState;

    private sealed record Scope(ImmutableArray<ResourceKey> ReadScopes, ImmutableArray<ResourceKey> CommandScopes) : IAdmittedJobScope;

    private sealed class ScopePolicy : IJobScopePolicy
    {
        public JobScopeAdmission ValidateAdmission(JobSubmission submission, in CommittedWorldView world) =>
            new(JobScopeStatus.Authorized, new Scope(submission.ReadScopes, submission.CommandScopes));

        public bool CanDispatch(IAdmittedJobScope scope, in CommittedWorldView world) => true;

        public JobScopeOverlap EvaluateOverlap(IAdmittedJobScope first, IAdmittedJobScope second, in CommittedWorldView world) =>
            JobScopeOverlap.Independent;
    }

    private sealed class ContinuePolicy : IBarrierSupervisorPolicy
    {
        public ValueTask<SupervisorAction> DecideAsync(
            BarrierReviewRequest request, IFaultEvidenceReader evidence, CancellationToken cancellationToken) =>
            ValueTask.FromResult(SupervisorAction.ContinueAtSafeBarrier);
    }
}
