namespace Groundwork.Core;

public enum TickPhase
{
    AcquireInput = 1,
    BeginTick,
    Gameplay,
    Base,
    CommitResults,
    PublishState
}

public readonly record struct FixedStep(int Numerator, int Denominator)
{
    public int Numerator
    {
        get;
        init => field = RequirePositive(value, nameof(Numerator));
    } = RequirePositive(Numerator, nameof(Numerator));

    public int Denominator
    {
        get;
        init => field = RequirePositive(value, nameof(Denominator));
    } = RequirePositive(Denominator, nameof(Denominator));

    private static int RequirePositive(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, parameterName);
        return value;
    }
}

public readonly record struct TickContext(ulong Tick, FixedStep BaseStep, InputSnapshot Input);

public enum BarrierStatus
{
    Committed = 1,
    Unchanged,
    Stopped
}

public readonly record struct BarrierOutcome(BarrierStatus Status, BarrierStamp? Stamp);

public enum ObserverPublication
{
    Published = 1,
    Blocked,
    PreparationFailed
}

public interface ITickPipeline
{
    ValueTask<BarrierOutcome> BeginTickAsync(TickContext tick, CancellationToken cancellationToken);

    ValueTask RunGameplayAsync(TickContext tick, FixedStep gameplayStep, CancellationToken cancellationToken);

    ValueTask RunBaseAsync(TickContext tick, CancellationToken cancellationToken);

    ValueTask<BarrierOutcome> CommitResultsAsync(TickContext tick, CancellationToken cancellationToken);

    ObserverPublication TryPublishState(TickContext tick);
}
