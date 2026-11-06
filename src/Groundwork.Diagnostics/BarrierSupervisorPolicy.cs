using Groundwork.Core;

namespace Groundwork.Diagnostics;

/// <summary>Classifies the evidence covered by a Core-issued barrier review.</summary>
/// <remarks>Configure the evidence limit to cover the world's required evidence capacity.</remarks>
public sealed class BarrierSupervisorPolicy : IBarrierSupervisorPolicy
{
    private readonly int _evidenceCapacity;

    public BarrierSupervisorPolicy(int evidenceCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(evidenceCapacity);
        _evidenceCapacity = evidenceCapacity;
    }

    public ValueTask<SupervisorAction> DecideAsync(
        BarrierReviewRequest request, IFaultEvidenceReader evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Classify(request.Proof, evidence, cancellationToken));
    }

    private SupervisorAction Classify(
        WorldCoherenceProof proof, IFaultEvidenceReader evidence, CancellationToken cancellationToken)
    {
        // Core authenticates the proof and checks its generation again when applying the decision.
        var action = SupervisorAction.ContinueAtSafeBarrier;
        ulong previousGeneration = 0;
        var count = 1;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evidence.ReadPending(proof.FaultGeneration, index, out var record, out var observedCount) !=
                    FaultEvidenceReadStatus.Copied || observedCount <= index || observedCount > _evidenceCapacity ||
                (index != 0 && observedCount != count))
            {
                return SupervisorAction.StopWorld;
            }

            count = observedCount;
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

        return previousGeneration == proof.FaultGeneration ? action : SupervisorAction.StopWorld;
    }
}
