using System.Collections.Immutable;
using System.Runtime.ExceptionServices;

namespace Groundwork.Core;

public enum FaultClass : byte
{
    Unspecified, DiagnosticOnly, Cancelled, DependencyFailure, StaleResult,
    WriteConflict, InvalidAccess, RecoverableJobFault, UnknownCritical,
    UnsafePartialCommit, BorrowedDrainExpired, IncompleteCleanup
}

public enum FaultSeverity : byte { Unspecified, Information, Warning, Error, Critical }
public enum FaultSubsystem : byte { Unspecified, Host, Platform, Simulation, Ecs, Jobs, Diagnostics }
public enum FaultOrigin : byte { Unspecified, Primary, Cleanup, Sink }
public enum FaultReportStatus : byte { EvidenceLost, Recorded, Coalesced }

public readonly record struct FaultContext(
    WorldId? World, ulong? Tick, TickPhase? Phase, JobId? Job,
    SourceKey? Source, Guid? Correlation);

public readonly ref struct FaultObservation(
    FaultClass classification, FaultSeverity severity, FaultSubsystem subsystem,
    FaultOrigin origin, uint code, long timestamp, FaultContext context,
    ReadOnlySpan<ResourceKey> targets, ReadOnlySpan<char> redactedText)
{
    public FaultClass Classification { get; } = classification;
    public FaultSeverity Severity { get; } = severity;
    public FaultSubsystem Subsystem { get; } = subsystem;
    public FaultOrigin Origin { get; } = origin;
    public uint Code { get; } = code;
    public long Timestamp { get; } = timestamp;
    public FaultContext Context { get; } = context;
    public ReadOnlySpan<ResourceKey> Targets { get; } = targets;
    public ReadOnlySpan<char> RedactedText { get; } = redactedText;
}

public interface IFaultReporter
{
    FaultReportStatus TryReport(scoped in FaultObservation observation);
}

/// <summary>A bounded structured diagnostic record. Operational exceptions stay on their original path.</summary>
public readonly record struct DiagnosticEvent(
    FaultClass Classification, FaultSeverity Severity, FaultSubsystem Subsystem, FaultOrigin Origin,
    uint Code, long Timestamp, FaultContext Context)
{
    internal DiagnosticEvent(scoped in FaultObservation observation)
        : this(observation.Classification, observation.Severity, observation.Subsystem, observation.Origin,
            observation.Code, observation.Timestamp, observation.Context) { }
}

public readonly record struct DiagnosticEmergencySnapshot(
    DiagnosticEvent? Primary, DiagnosticEvent? Cleanup, DiagnosticEvent? Sink);

/// <summary>Host-only drain and independent emergency observation for one runtime or world.</summary>
public interface IDiagnosticReader
{
    bool TryAcquire(out DiagnosticEvent observation, out DiagnosticReadLease lease);
    DiagnosticEmergencySnapshot ReadEmergency();
    void RecordSinkFailure(uint code, long timestamp);
}

/// <summary>Holds a normal diagnostic slot until the host or sink releases the event.</summary>
public readonly struct DiagnosticReadLease : IDisposable
{
    private readonly FaultLedger? _owner;
    private readonly ulong _ticket;

    internal DiagnosticReadLease(FaultLedger owner, ulong ticket) => (_owner, _ticket) = (owner, ticket);

    public void Dispose() => _owner?.ReleaseDiagnostic(_ticket);
}

public sealed record OperationalFailure(
    FaultClass Classification, FaultSubsystem Subsystem, FaultContext Context,
    ExceptionDispatchInfo Exception);

public sealed record RuntimeFailure(
    OperationalFailure? Primary, ImmutableArray<OperationalFailure> Cleanup)
{
    public ImmutableArray<OperationalFailure> Cleanup
    {
        get;
        init => field = value.IsDefault ? throw new ArgumentException("A cleanup array is required.", nameof(Cleanup)) : value;
    } = Cleanup.IsDefault ? throw new ArgumentException("A cleanup array is required.", nameof(Cleanup)) : Cleanup;
}

public sealed class IncompleteCleanupException : Exception
{
    public IncompleteCleanupException() : this(FaultSubsystem.Unspecified) { }
    public IncompleteCleanupException(string message) : base(message) { }
    public IncompleteCleanupException(string message, Exception innerException) : base(message, innerException) { }

    public IncompleteCleanupException(FaultSubsystem subsystem)
        : base("Runtime resources remain in custody after the shutdown deadline.")
        => Subsystem = subsystem;

    public FaultSubsystem Subsystem { get; }
}

[Flags]
public enum SafetyCondition : byte
{
    None = 0, RecoveryPaused = 1, RequiredEvidenceLost = 2,
    TerminalStop = 4, IncompleteCleanup = 8, CounterSaturated = 16
}

public readonly record struct RuntimeSafetySnapshot(
    ulong PendingFaultGeneration, int PendingRequiredEvidenceRecords,
    SafetyCondition Conditions, ulong RepeatCount, ulong CoalescedCount,
    ulong DeliveryLossCount, ulong EvidenceLossCount);

public interface IRuntimeSafetyState
{
    RuntimeSafetySnapshot Read();
}

public readonly record struct RequiredFaultEvidence(
    ulong Generation, FaultClass Classification, FaultSubsystem Subsystem,
    FaultOrigin Origin, uint Code, FaultContext Context, ResourceKey? Target);

public enum FaultEvidenceReadStatus : byte
{
    EvidenceLost, Copied, InsufficientCapacity, StaleGeneration
}

public interface IFaultEvidenceReader
{
    FaultEvidenceReadStatus CopyPending(
        ulong generation, Span<RequiredFaultEvidence> destination, out int count);

    /// <summary>Reads one entry from the exact pending generation and returns the total entry count.</summary>
    /// <remarks>Readers that implement only bulk copy fail closed for indexed review.</remarks>
    FaultEvidenceReadStatus ReadPending(ulong generation, int index, out RequiredFaultEvidence record, out int count)
    {
        record = default;
        count = 0;
        return FaultEvidenceReadStatus.InsufficientCapacity;
    }
}

public enum SupervisorAction : byte { StopWorld, PauseForRecovery, ContinueAtSafeBarrier }
public enum DecisionApplication : byte { Rejected, Continued, Paused, Stopped }

public sealed class WorldCoherenceProof
{
    internal WorldCoherenceProof(WorldControl issuer, BarrierStamp stamp, ulong faultGeneration)
        => (Issuer, Stamp, FaultGeneration) = (issuer, stamp, faultGeneration);

    internal WorldControl Issuer { get; }
    public BarrierStamp Stamp { get; }
    public ulong FaultGeneration { get; }
}

public sealed class BarrierReviewRequest
{
    internal BarrierReviewRequest(ulong requestId, WorldCoherenceProof proof, RuntimeDeadline deadline)
        => (RequestId, Proof, Deadline) = (requestId, proof, deadline);

    public ulong RequestId { get; }
    public WorldCoherenceProof Proof { get; }
    public RuntimeDeadline Deadline { get; }
}

public sealed class SupervisorDecision
{
    internal SupervisorDecision(SupervisorAction action, BarrierReviewRequest request)
        => (Action, Request) = (action, request);

    public SupervisorAction Action { get; }
    public BarrierReviewRequest Request { get; }
}

public interface IWorldCoherenceIssuer
{
    WorldCoherenceProof? TryIssue(in BarrierStamp stamp, ulong faultGeneration);
}

public interface IBarrierReviewControl
{
    BarrierReviewRequest? TryBegin(WorldCoherenceProof proof);
    DecisionApplication TryApply(SupervisorDecision decision);
    DecisionApplication CompleteWithoutDecision(BarrierReviewRequest request);
    DecisionApplication CompleteUnverified(ulong faultGeneration);
}

public interface ISafeBarrierSupervisor
{
    ValueTask<SupervisorDecision> DecideAsync(BarrierReviewRequest request, CancellationToken cancellationToken);
}

public interface IBarrierSupervisorPolicy
{
    ValueTask<SupervisorAction> DecideAsync(
        BarrierReviewRequest request, IFaultEvidenceReader evidence, CancellationToken cancellationToken);
}
