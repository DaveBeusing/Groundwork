namespace Groundwork.Simulation;

public sealed record SimulationOptions(
    int BaseRateHz,
    int GameplayRateHz,
    int MaxStepsPerIteration,
    TimeSpan MaxBacklog)
{
    public static SimulationOptions Default { get; } = new(60, 20, 5, TimeSpan.FromMilliseconds(250));
}

public enum SimulationState
{
    Running = 1,
    Paused,
    Stopped
}

public readonly record struct SimulationIteration(
    ulong CompletedTick,
    int AppliedSteps,
    Int128 DebtBeforeScaledTime,
    Int128 ElapsedScaledTime,
    Int128 RemainingScaledTime,
    Int128 DroppedScaledTime,
    ulong OverloadEpisodeCount,
    bool OverloadActive,
    bool MetricsSaturated,
    double Interpolation,
    SimulationState State,
    long? NextWakeTimestamp);

public interface ISimulationDriver
{
    ValueTask<SimulationIteration> AdvanceAsync(CancellationToken cancellationToken);

    void RequestPause();

    void RequestResume();
}
