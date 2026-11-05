namespace Groundwork.Core;

public sealed class WorldSafetyPorts
{
    private readonly WorldControl _control;

    internal WorldSafetyPorts(WorldControl control)
    {
        _control = control;
        Reporter = new FaultReporterPort(control.Ledger);
        State = new SafetyStatePort(control.Ledger);
        Evidence = new FaultEvidencePort(control.Ledger);
        Writer = new WriterPort(control);
        Coordinator = new ReviewPort(control);
        Supervisor = new SupervisorPort(control);
    }

    public IFaultReporter Reporter { get; }
    public IRuntimeSafetyState State { get; }
    public IFaultEvidenceReader Evidence { get; }
    public IWorldCoherenceIssuer Writer { get; }
    public IBarrierReviewControl Coordinator { get; }
    public ISafeBarrierSupervisor Supervisor { get; }
    public WorldPublicationPorts Publication => _control.Publication.Ports;
    public IRuntimeOwnership Ownership => _control.Ownership;

    /// <summary>Creates a deadline in the world's clock domain for an ordinary borrowed-read drain.</summary>
    public RuntimeDeadline CreateBorrowedDrainDeadline() => _control.CreateBorrowedDrainDeadline();

    private sealed class WriterPort(WorldControl owner) : IWorldCoherenceIssuer
    {
        public WorldCoherenceProof? TryIssue(in BarrierStamp stamp, ulong faultGeneration)
            => owner.TryIssue(stamp, faultGeneration);
    }

    private sealed class ReviewPort(WorldControl owner) : IBarrierReviewControl
    {
        public BarrierReviewRequest? TryBegin(WorldCoherenceProof proof) => owner.TryBegin(proof);
        public DecisionApplication TryApply(SupervisorDecision decision) => owner.TryApply(decision);
        public DecisionApplication CompleteWithoutDecision(BarrierReviewRequest request) => owner.CompleteWithoutDecision(request);
        public DecisionApplication CompleteUnverified(ulong faultGeneration) => owner.CompleteUnverified(faultGeneration);
    }

    private sealed class SupervisorPort(WorldControl owner) : ISafeBarrierSupervisor
    {
        public ValueTask<SupervisorDecision> DecideAsync(BarrierReviewRequest request, CancellationToken cancellationToken)
            => owner.DecideAsync(request, cancellationToken);
    }
}

internal sealed class FaultReporterPort(FaultLedger ledger) : IFaultReporter
{
    public FaultReportStatus TryReport(scoped in FaultObservation observation) => ledger.TryReport(observation);
}

internal sealed class SafetyStatePort(FaultLedger ledger) : IRuntimeSafetyState
{
    public RuntimeSafetySnapshot Read() => ledger.Read();
}

internal sealed class FaultEvidencePort(FaultLedger ledger) : IFaultEvidenceReader
{
    public FaultEvidenceReadStatus CopyPending(
        ulong generation, Span<RequiredFaultEvidence> destination, out int count)
        => ledger.CopyPending(generation, destination, out count);
}

internal sealed class WorldControl
{
    private readonly RuntimeSafetyAuthority _runtime;
    private readonly IBarrierSupervisorPolicy _policy;
    private readonly IFaultEvidenceReader _evidence;
    private BarrierReviewRequest? _request;
    private ulong _lastRequest;
    private bool _policyStarted;

    internal WorldControl(
        RuntimeSafetyAuthority runtime, WorldId world, ResourceQuota quota,
        IBarrierSupervisorPolicy policy, IWorldStateRoot initialRoot, IJobScopePolicy scopePolicy)
    {
        _runtime = runtime;
        _policy = policy;
        World = world;
        Quota = quota;
        Ledger = new FaultLedger(runtime, world, quota.Diagnostics, StopWorld);
        _evidence = new FaultEvidencePort(Ledger);
        Ownership = new OwnershipScope(runtime, this, quota.OutstandingOperations);
        Publication = new WorldPublicationAuthority(this, world, initialRoot, scopePolicy, quota.Jobs, runtime.Clock.Provider, Ownership);
    }

    internal object Gate => _runtime.Gate;
    internal WorldId World { get; }
    internal ResourceQuota Quota { get; }
    internal FaultLedger Ledger { get; }
    internal WorldPublicationAuthority Publication { get; }
    internal OwnershipScope Ownership { get; }
    internal bool IsTerminal => Ledger.IsTerminal || Ledger.HasContendedEvidenceLoss || _runtime.IsPublicationFenced;
    internal int OwnerCount => Ownership.Count;
    internal RuntimeDeadline? Deadline { get; private set; }
    internal bool Settling { get; set; }
    internal bool InCustody { get; set; }
    internal bool IsStopping { get; private set; }

    internal bool TryAuthorizePublication(ulong expectedGeneration)
        => !Ledger.IsTerminal && !Ledger.EvidenceLost && !Ledger.IsPaused && !Ledger.HasPending &&
            expectedGeneration == Ledger.Generation && TryPassContendedFences();

    // Each fence is monotonic. If both are clear, the first check is the
    // linearization point, because the second was necessarily clear then.
    private bool TryPassContendedFences()
        => Ledger.TryPassContendedFence() && _runtime.TryAuthorizePublication();

    internal void RecordJobFault(scoped in FaultObservation observation)
    {
        if (observation.Classification == FaultClass.DiagnosticOnly)
        {
            var missingEvidence = new FaultObservation(
                FaultClass.Unspecified, observation.Severity, observation.Subsystem, observation.Origin,
                observation.Code, observation.Timestamp, observation.Context, observation.Targets, observation.RedactedText);
            Ledger.Record(missingEvidence);
            return;
        }

        Ledger.Record(observation);
    }

    internal RuntimeDeadline CreateBorrowedDrainDeadline()
    {
        lock (Gate)
        {
            var result = _runtime.Clock.Start(_runtime.BorrowedDrainDuration);
            if (result.ForcedExpired)
            {
                StopWorld();
            }

            return Earlier(result, Deadline);
        }
    }

    internal void StopWorld()
    {
        if (Ledger.IsTerminal)
        {
            return;
        }

        var deadline = _runtime.Clock.Start(_runtime.ShutdownDuration);
        StopForRuntime(deadline);
    }

    internal void StopForRuntime(RuntimeDeadline deadline)
    {
        if (Ledger.HasContendedEvidenceLoss)
        {
            deadline = Earlier(_runtime.Clock.At(Ledger.ContendedStopTimestamp, _runtime.ShutdownDuration), deadline);
        }

        Deadline = Earlier(deadline, Deadline);
        if (Ledger.IsTerminal)
        {
            return;
        }

        Ledger.MarkStopped();
        _request = null;
        _runtime.SimulationWake.Signal();
        IsStopping = true;
        try
        {
            Publication.OnStopped();
        }
        finally
        {
            IsStopping = false;
        }
    }

    internal void Release()
    {
        Ledger.ReleaseStorage();
        Publication.OnReleased();
    }

    internal WorldCoherenceProof? TryIssue(in BarrierStamp stamp, ulong generation)
    {
        lock (Gate)
        {
            return CanReview(stamp, generation) ? new WorldCoherenceProof(this, stamp, generation) : null;
        }
    }

    internal BarrierReviewRequest? TryBegin(WorldCoherenceProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        lock (Gate)
        {
            if (!ReferenceEquals(proof.Issuer, this) || !CanReview(proof.Stamp, proof.FaultGeneration) ||
                (_request is { } current && IsCurrent(current)))
            {
                return null;
            }

            var deadline = _runtime.Clock.Start(_runtime.SupervisorDuration);
            if (deadline.ForcedExpired || _lastRequest == ulong.MaxValue)
            {
                StopWorld();
                return null;
            }

            _policyStarted = false;
            _request = new BarrierReviewRequest(++_lastRequest, proof, deadline);
            return _request;
        }
    }

    internal async ValueTask<SupervisorDecision> DecideAsync(BarrierReviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IRuntimeWorkLease ownership;
        lock (Gate)
        {
            if (!IsCurrent(request) || _policyStarted || request.Deadline.IsExpired(_runtime.Clock.Provider))
            {
                throw new InvalidOperationException("The review request is no longer available.");
            }

            ownership = Ownership.TryTrack(_policy)
                ?? throw new InvalidOperationException("No operation capacity is available for the supervisor.");
            _policyStarted = true;
        }

        using (ownership)
        {
            var action = await _policy.DecideAsync(request, _evidence, cancellationToken).ConfigureAwait(false);
            return new SupervisorDecision(Enum.IsDefined(action) ? action : SupervisorAction.StopWorld, request);
        }
    }

    internal DecisionApplication TryApply(SupervisorDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (Gate)
        {
            if (!IsCurrent(decision.Request) || decision.Request.Deadline.IsExpired(_runtime.Clock.Provider) ||
                !TryPassContendedFences())
            {
                return DecisionApplication.Rejected;
            }

            _request = null;
            switch (decision.Action)
            {
                case SupervisorAction.ContinueAtSafeBarrier when !Ledger.HasCriticalPending:
                    Ledger.Continue();
                    _runtime.SimulationWake.Signal();
                    return DecisionApplication.Continued;
                case SupervisorAction.PauseForRecovery:
                    Ledger.Pause();
                    _runtime.SimulationWake.Signal();
                    return DecisionApplication.Paused;
                default:
                    StopWorld();
                    return DecisionApplication.Stopped;
            }
        }
    }

    internal DecisionApplication CompleteWithoutDecision(BarrierReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (Gate)
        {
            if (!IsCurrent(request) || !TryPassContendedFences())
            {
                return DecisionApplication.Rejected;
            }

            _request = null;
            Ledger.Pause();
            _runtime.SimulationWake.Signal();
            return DecisionApplication.Paused;
        }
    }

    internal DecisionApplication CompleteUnverified(ulong generation)
    {
        lock (Gate)
        {
            if (IsTerminal || !Ledger.HasPending || generation != Ledger.Generation ||
                (_request is { } current && IsCurrent(current)))
            {
                return DecisionApplication.Rejected;
            }

            StopWorld();
            return DecisionApplication.Stopped;
        }
    }

    private bool CanReview(in BarrierStamp stamp, ulong generation)
        => !IsTerminal && !Ledger.EvidenceLost && Ledger.HasPending && generation == Ledger.Generation &&
            stamp.World == World && stamp.Kind is BarrierKind.BeginTick or BarrierKind.CommitResults &&
            stamp == Publication.Stamp;

    private bool IsCurrent(BarrierReviewRequest request)
        => ReferenceEquals(_request, request) && CanReview(request.Proof.Stamp, request.Proof.FaultGeneration);

    private static RuntimeDeadline Earlier(RuntimeDeadline candidate, RuntimeDeadline? current)
        => current is { } existing && (existing.ForcedExpired ||
            (!candidate.ForcedExpired && existing.Timestamp <= candidate.Timestamp))
            ? existing : candidate;
}
