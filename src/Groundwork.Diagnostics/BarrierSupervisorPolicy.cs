using Groundwork.Core;

namespace Groundwork.Diagnostics;

/// <summary>Classifies the evidence covered by a Core-issued barrier review.</summary>
/// <remarks>Create one policy per world and size its scratch storage to that world's required evidence capacity.</remarks>
public sealed class BarrierSupervisorPolicy : IBarrierSupervisorPolicy
{
    private readonly RequiredFaultEvidence[] _scratch;
    private int _reviewing;

    public BarrierSupervisorPolicy(int evidenceCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(evidenceCapacity);
        _scratch = new RequiredFaultEvidence[evidenceCapacity];
    }

    public ValueTask<SupervisorAction> DecideAsync(
        BarrierReviewRequest request, IFaultEvidenceReader evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _reviewing, 1, 0) != 0)
            return ValueTask.FromResult(SupervisorAction.StopWorld);

        try
        {
            return ValueTask.FromResult(Classify(request.Proof, evidence, cancellationToken));
        }
        finally
        {
            Volatile.Write(ref _reviewing, 0);
        }
    }

    private SupervisorAction Classify(
        WorldCoherenceProof proof, IFaultEvidenceReader evidence, CancellationToken cancellationToken)
    {
        if (evidence.CopyPending(proof.FaultGeneration, _scratch, out var count) != FaultEvidenceReadStatus.Copied ||
            count <= 0 || count > _scratch.Length || _scratch[count - 1].Generation != proof.FaultGeneration)
        {
            return SupervisorAction.StopWorld;
        }

        // Core authenticates the proof and checks its generation again when applying the decision.
        var action = SupervisorAction.ContinueAtSafeBarrier;
        ulong previousGeneration = 0;
        foreach (ref readonly var record in _scratch.AsSpan(0, count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Context.World != proof.Stamp.World || record.Generation == 0 ||
                record.Generation < previousGeneration || record.Generation > proof.FaultGeneration)
            {
                return SupervisorAction.StopWorld;
            }

            previousGeneration = record.Generation;
            switch (record.Classification)
            {
                case FaultClass.Cancelled:
                case FaultClass.DependencyFailure:
                case FaultClass.StaleResult:
                case FaultClass.WriteConflict:
                    break;
                case FaultClass.InvalidAccess:
                case FaultClass.RecoverableJobFault:
                    action = SupervisorAction.PauseForRecovery;
                    break;
                default:
                    return SupervisorAction.StopWorld;
            }
        }

        return action;
    }
}
