namespace Groundwork.Core;

internal sealed class JobExecutionPort(WorldPublicationAuthority authority) : IJobExecutionAuthority
{
    public JobAdmissionResult TryAdmit(JobId job, JobSubmission submission, JobDispatchPhase phase)
        => authority.TryAdmit(job, submission, phase);

    public JobDispatchResult TryAuthorizeDispatch(JobId job, JobDispatchPhase phase)
        => authority.TryAuthorizeDispatch(job, phase);

    public JobCancellationStatus RequestCancellation(JobId job) => authority.RequestCancellation(job);

    public ExecutionSettlementStatus TrySettle(JobExecutionCompletion completion) => authority.TrySettle(completion);

    public ExecutionSettlementStatus TrySettleFault(JobDispatchPermit permit, scoped in FaultObservation fault)
        => authority.TrySettleFault(permit, in fault);
}

internal sealed class JobPhasePort(WorldPublicationAuthority authority) : IJobPhaseControl
{
    public JobPhaseOpenResult TryOpen(BarrierStamp stamp, TickPhase phase) => authority.TryOpen(stamp, phase);

    public void Close(JobDispatchPhase phase) => authority.Close(phase);
}

internal sealed class JobCapturePort(WorldPublicationAuthority authority) : IJobCaptureAuthority
{
    public JobCapturePermitResult TryAcquire(JobDispatchPermit permit) => authority.TryAcquire(permit);
}

internal sealed class TargetVersionPort(WorldPublicationAuthority authority) : ITargetVersionIssuer
{
    public TargetVersion Issue(in TargetVersionStamp stamp) => authority.Issue(in stamp);

    public bool TryRead(TargetVersion version, JobDispatchPermit permit, ComponentTarget target, out TargetVersionStamp stamp)
        => authority.TryRead(version, permit, target, out stamp);
}

internal sealed class CandidatePort(WorldPublicationAuthority authority) : IJobPublicationSource
{
    public CandidateSealResult SealCandidates(in BarrierRequest request) => authority.SealCandidates(in request);
}

internal sealed class BarrierPreparationPort(WorldPublicationAuthority authority) : IBarrierPreparationSource
{
    public BarrierPreparationResult TryPrepare(BarrierPublicationDraft draft) => authority.TryPrepare(draft);
}

internal sealed class BarrierFinalizationPort(WorldPublicationAuthority authority) : IBarrierPublicationAuthority
{
    public BarrierOutcome TryFinalize(PreparedBarrierPublication publication) => authority.TryFinalize(publication);

    public BarrierAbortStatus TryAbort(CandidateSet candidates, BarrierAbortReason reason)
        => authority.TryAbort(candidates, reason);
}

internal sealed class ObserverPublicationPort(WorldPublicationAuthority authority) : IObserverPublicationAuthority
{
    public ObserverPublication TryPublish(in ObserverPublicationDraft draft) => authority.TryPublish(in draft);
}

internal sealed class ObserverSnapshotPort(WorldPublicationAuthority authority) : IObserverSnapshotSource
{
    public ObserverReadResult TryAcquire() => authority.AcquireObserver();
}

internal sealed class CommittedWorldPort(WorldPublicationAuthority authority) : ICommittedWorldSource
{
    public CommittedWorldView Read() => authority.ReadCommitted();
}

internal sealed class BorrowBarrierPort(WorldPublicationAuthority authority) : IBorrowBarrierControl
{
    public ValueTask<BorrowDrainStatus> CloseAndDrainAsync(BarrierRequest request, RuntimeDeadline deadline)
        => authority.CloseAndDrainAsync(request, deadline);
}
