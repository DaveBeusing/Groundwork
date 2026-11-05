using Groundwork.Core;

namespace Groundwork.Simulation.Tests;

internal sealed class SimulationFixture : IDisposable
{
    private readonly RuntimeSafetyAuthority _runtime;
    private readonly EngineModuleContext _context;

    internal SimulationFixture(long frequency, SimulationOptions? options = null)
    {
        Clock = new SyntheticClock(frequency);
        var quota = new ResourceQuota(1, 16,
            new JobQuota(8, 4, 16, 16, 4, 8, 16, 65536, 65536, 65536),
            new DiagnosticQuota(1, 16384, 12, 32768, 4, 4, 64, 1024));
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, quota));
        using var reservation = budget.TryReserveRuntime(quota);
        Assert.IsNotNull(reservation);
        var duration = TimeSpan.FromMilliseconds(100);
        var timing = new RuntimeTimingLimits(duration, duration, duration);
        _runtime = RuntimeSafetyAuthority.Create(Clock, timing, reservation);
        _context = new EngineModuleContext(Clock, timing, Faults, _runtime.StopControl);
        Driver = CreateDriver(options);
    }

    internal SyntheticClock Clock { get; }
    internal CountingInput Input { get; } = new();
    internal RecordingPipeline Pipeline { get; } = new();
    internal MutableSafety Safety { get; } = new();
    internal RecordingFaults Faults { get; } = new();
    internal RuntimeWakeSignal Wake => _runtime.SimulationWake;
    internal FixedStepSimulation Driver { get; }

    internal FixedStepSimulation CreateDriver(SimulationOptions? options) => new(_context, Input, Pipeline, Safety, Wake, options);

    internal ValueTask<SimulationIteration> AdvanceAsync() => Driver.AdvanceAsync(CancellationToken.None);

    public void Dispose()
    {
        _runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        _runtime.SettleStop();
    }

    internal sealed class SyntheticClock(long frequency) : TimeProvider
    {
        public override long TimestampFrequency => frequency;
        internal long Timestamp { get; set; }
        public override long GetTimestamp() => Timestamp;
    }

    internal sealed class CountingInput : IInputSource
    {
        internal ulong Count { get; private set; }
        public InputSnapshot Capture() => new(++Count, [], [], null, false);
    }

    internal sealed class MutableSafety : IRuntimeSafetyState
    {
        internal SafetyCondition Conditions { get; set; }
        public RuntimeSafetySnapshot Read() => new(0, 0, Conditions, 0, 0, 0, 0);
    }

    internal sealed class RecordingFaults : IFaultReporter
    {
        internal int Count { get; private set; }
        internal FaultClass LastClass { get; private set; }
        internal FaultContext LastContext { get; private set; }

        public FaultReportStatus TryReport(scoped in FaultObservation observation)
        {
            Count++;
            LastClass = observation.Classification;
            LastContext = observation.Context;
            return FaultReportStatus.Recorded;
        }
    }

    internal readonly record struct Call(TickPhase Phase, ulong Tick, ulong Input, FixedStep Step);

    internal sealed class RecordingPipeline : ITickPipeline
    {
        private ulong _epoch;
        internal List<Call> Calls { get; } = [];
        internal Action<TickPhase>? OnPhase { get; set; }

        public ValueTask<BarrierOutcome> BeginTickAsync(TickContext tick, CancellationToken cancellationToken)
            => Barrier(tick, TickPhase.BeginTick, BarrierKind.BeginTick);

        public ValueTask RunGameplayAsync(TickContext tick, FixedStep gameplayStep, CancellationToken cancellationToken)
        {
            Add(tick, TickPhase.Gameplay, gameplayStep);
            return ValueTask.CompletedTask;
        }

        public ValueTask RunBaseAsync(TickContext tick, CancellationToken cancellationToken)
        {
            Add(tick, TickPhase.Base, tick.BaseStep);
            return ValueTask.CompletedTask;
        }

        public ValueTask<BarrierOutcome> CommitResultsAsync(TickContext tick, CancellationToken cancellationToken)
            => Barrier(tick, TickPhase.CommitResults, BarrierKind.CommitResults);

        public ObserverPublication TryPublishState(TickContext tick)
        {
            Add(tick, TickPhase.PublishState, tick.BaseStep);
            return ObserverPublication.Published;
        }

        private ValueTask<BarrierOutcome> Barrier(TickContext tick, TickPhase phase, BarrierKind kind)
        {
            Add(tick, phase, tick.BaseStep);
            return ValueTask.FromResult(new BarrierOutcome(BarrierStatus.Unchanged,
                new BarrierStamp(new WorldId(1), tick.Tick, kind, ++_epoch, 0)));
        }

        private void Add(TickContext tick, TickPhase phase, FixedStep step)
        {
            Calls.Add(new Call(phase, tick.Tick, tick.Input.LastSequence, step));
            OnPhase?.Invoke(phase);
        }
    }
}
