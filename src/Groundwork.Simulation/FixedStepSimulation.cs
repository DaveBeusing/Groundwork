using Groundwork.Core;

namespace Groundwork.Simulation;

public sealed class FixedStepSimulation : ISimulationDriver
{
    private readonly TimeProvider _clock;
    private readonly IInputSource _input;
    private readonly ITickPipeline _pipeline;
    private readonly IRuntimeSafetyState _safety;
    private readonly IFaultReporter _faults;
    private readonly RuntimeWakeSignal _wake;
    private readonly FixedStep _baseStep;
    private readonly FixedStep _gameplayStep;
    private readonly int _gameplayInterval;
    private readonly int _stepLimit;
    private readonly Int128 _scale;
    private readonly Int128 _stepDebt;
    private readonly Int128 _maxDebt;

    private Int128 _debt;
    private long _lastTimestamp;
    private ulong _completedTick;
    private ulong _overloadEpisodes;
    private ulong? _activeTick;
    private TickPhase? _activePhase;
    private bool _hasTimestamp;
    private bool _paused;
    private bool _stopped;
    private bool _overloadActive;
    private bool _metricsSaturated;
    private int _pauseRequested;
    private int _advancing;

    public FixedStepSimulation(
        EngineModuleContext context,
        IInputSource input,
        ITickPipeline pipeline,
        IRuntimeSafetyState safety,
        RuntimeWakeSignal wake,
        SimulationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Clock);
        ArgumentNullException.ThrowIfNull(context.FaultReporter);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentNullException.ThrowIfNull(wake);
        options ??= SimulationOptions.Default;
        var frequency = context.Clock.TimestampFrequency;
        if (frequency <= 0)
            throw new ArgumentException("The simulation clock frequency must be positive.", nameof(context));
        if (options.BaseRateHz <= 0 || options.GameplayRateHz <= 0 || options.MaxStepsPerIteration <= 0 ||
            options.MaxBacklog <= TimeSpan.Zero || options.BaseRateHz % options.GameplayRateHz != 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Specify positive rates and limits, with gameplay dividing the base rate.");

        _scale = (Int128)options.BaseRateHz * TimeSpan.TicksPerSecond;
        _stepDebt = (Int128)frequency * TimeSpan.TicksPerSecond;
        try
        {
            _maxDebt = checked((Int128)options.MaxBacklog.Ticks * frequency * options.BaseRateHz);
            _ = checked(_maxDebt + ((Int128)long.MaxValue - long.MinValue) * _scale);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The clock and backlog exceed the supported time range.");
        }
        if (_maxDebt < _stepDebt)
            throw new ArgumentOutOfRangeException(nameof(options), "The backlog must hold at least one base step.");

        _clock = context.Clock;
        _faults = context.FaultReporter;
        _input = input;
        _pipeline = pipeline;
        _safety = safety;
        _wake = wake;
        _baseStep = new FixedStep(1, options.BaseRateHz);
        _gameplayStep = new FixedStep(1, options.GameplayRateHz);
        _gameplayInterval = options.BaseRateHz / options.GameplayRateHz;
        _stepLimit = options.MaxStepsPerIteration;
    }

    // The coordinator reads this after AdvanceAsync completes. A failed callback may leave a later writer revision.
    public BarrierStamp? LastConfirmedBarrier { get; private set; }

    public void RequestPause() => SetUserPause(1);

    public void RequestResume() => SetUserPause(0);

    public ValueTask<SimulationIteration> AdvanceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _advancing, 1, 0) != 0)
            throw new InvalidOperationException("Only one simulation iteration can be active.");

        return AdvanceCoreAsync(cancellationToken);
    }

    private async ValueTask<SimulationIteration> AdvanceCoreAsync(CancellationToken cancellationToken)
    {
        var debtBefore = _debt;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopped)
                return Result(debtBefore, 0, 0, 0);
            var conditions = _safety.Read().Conditions;
            if (IsTerminal(conditions))
            {
                _stopped = true;
                return Result(debtBefore, 0, 0, 0);
            }

            var shouldPause = Volatile.Read(ref _pauseRequested) != 0 || (conditions & SafetyCondition.RecoveryPaused) != 0;
            if (_paused && shouldPause)
                return Result(debtBefore, 0, 0, 0);

            var elapsed = SampleElapsed();
            _paused = shouldPause;
            var available = _debt + elapsed;
            _debt = Int128.Min(available, _maxDebt);
            var dropped = available - _debt;
            UpdateOverload(dropped);

            int steps = 0;
            while (!_paused && steps < _stepLimit && _debt >= _stepDebt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                conditions = _safety.Read().Conditions;
                if (IsTerminal(conditions))
                {
                    _stopped = true;
                    break;
                }
                if ((conditions & SafetyCondition.RecoveryPaused) != 0)
                {
                    _paused = true;
                    break;
                }

                if (!await RunTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    _stopped = true;
                    break;
                }
                steps++;
            }
            if (!_stopped)
            {
                conditions = _safety.Read().Conditions;
                _stopped = IsTerminal(conditions);
                _paused |= (conditions & SafetyCondition.RecoveryPaused) != 0;
            }
            return Result(debtBefore, elapsed, dropped, steps);
        }
        catch (OperationCanceledException cancellation) when (cancellationToken.IsCancellationRequested)
        {
            if (_activeTick.HasValue)
                ReportTerminal(FaultClass.Cancelled, FaultSeverity.Warning, cancellation);
            throw;
        }
        catch (Exception failure)
        {
            ReportTerminal(FaultClass.UnknownCritical, FaultSeverity.Critical, failure);
            throw;
        }
        finally
        {
            _activeTick = null;
            _activePhase = null;
            Volatile.Write(ref _advancing, 0);
        }
    }

    private Int128 SampleElapsed()
    {
        var now = _clock.GetTimestamp();
        if (_hasTimestamp && now < _lastTimestamp)
            throw new InvalidOperationException("The monotonic simulation clock moved backwards.");

        var elapsed = _hasTimestamp && !_paused ? ((Int128)now - _lastTimestamp) * _scale : 0;
        if (_paused)
            _overloadActive = false;
        _lastTimestamp = now;
        _hasTimestamp = true;
        return elapsed;
    }

    private async ValueTask<bool> RunTickAsync(CancellationToken cancellationToken)
    {
        var tick = checked(_completedTick + 1);
        _activeTick = tick;
        _activePhase = TickPhase.AcquireInput;
        var input = _input.Capture();
        ArgumentNullException.ThrowIfNull(input);
        var context = new TickContext(tick, _baseStep, input);
        if (!CanContinueTick(cancellationToken))
            return false;

        _activePhase = TickPhase.BeginTick;
        var begin = await _pipeline.BeginTickAsync(context, cancellationToken).ConfigureAwait(false);
        if (!ConfirmBarrier(begin, tick, BarrierKind.BeginTick) || !CanContinueTick(cancellationToken))
            return false;

        if (tick % (ulong)_gameplayInterval == 0)
        {
            _activePhase = TickPhase.Gameplay;
            await _pipeline.RunGameplayAsync(context, _gameplayStep, cancellationToken).ConfigureAwait(false);
            if (!CanContinueTick(cancellationToken))
                return false;
        }

        _activePhase = TickPhase.Base;
        await _pipeline.RunBaseAsync(context, cancellationToken).ConfigureAwait(false);
        if (!CanContinueTick(cancellationToken))
            return false;

        _activePhase = TickPhase.CommitResults;
        var commit = await _pipeline.CommitResultsAsync(context, cancellationToken).ConfigureAwait(false);
        if (!ConfirmBarrier(commit, tick, BarrierKind.CommitResults) || !CanContinueTick(cancellationToken))
            return false;

        _activePhase = TickPhase.PublishState;
        var publication = _pipeline.TryPublishState(context);
        if (publication == ObserverPublication.Blocked && IsTerminal(_safety.Read().Conditions))
            return false;
        if (publication != ObserverPublication.Published)
            throw new InvalidOperationException($"Observer publication ended with {publication}.");

        // Publication settles the tick even if cancellation arrived inside the publication call.
        _debt -= _stepDebt;
        _completedTick = tick;
        _activeTick = null;
        _activePhase = null;
        return true;
    }

    private bool ConfirmBarrier(BarrierOutcome outcome, ulong tick, BarrierKind kind)
    {
        if (outcome.Status == BarrierStatus.Stopped)
            return false;
        if (outcome.Status is not (BarrierStatus.Committed or BarrierStatus.Unchanged) ||
            outcome.Stamp is not { } stamp || stamp.World.Value == 0 || stamp.Tick != tick || stamp.Kind != kind || stamp.Epoch == 0)
            throw new InvalidOperationException("The tick pipeline returned an invalid barrier outcome.");

        var prior = LastConfirmedBarrier;
        var priorEpoch = prior?.Epoch ?? 0;
        var priorRevision = prior?.Revision ?? 0;
        var expectedRevision = outcome.Status == BarrierStatus.Committed ? checked(priorRevision + 1) : priorRevision;
        if ((prior.HasValue && stamp.World != prior.Value.World) || stamp.Epoch != checked(priorEpoch + 1) || stamp.Revision != expectedRevision)
            throw new InvalidOperationException("The tick pipeline did not advance the same world's barrier.");

        LastConfirmedBarrier = stamp;
        return true;
    }

    private bool CanContinueTick(CancellationToken cancellationToken)
    {
        var conditions = _safety.Read().Conditions;
        if (IsTerminal(conditions))
            return false;
        cancellationToken.ThrowIfCancellationRequested();
        if ((conditions & SafetyCondition.RecoveryPaused) != 0)
            throw new InvalidOperationException("Recovery interrupted an active tick. It cannot be replayed.");
        return true;
    }

    private void UpdateOverload(Int128 dropped)
    {
        if (dropped > 0 && !_overloadActive)
        {
            if (_overloadEpisodes == ulong.MaxValue)
                _metricsSaturated = true;
            else
                _overloadEpisodes++;
        }
        _overloadActive = dropped > 0;
    }

    private SimulationIteration Result(Int128 debtBefore, Int128 elapsed, Int128 dropped, int steps)
    {
        var state = _stopped ? SimulationState.Stopped : _paused ? SimulationState.Paused : SimulationState.Running;
        var fraction = (double)(_debt % _stepDebt) / (double)_stepDebt;
        long? wake = null;
        if (state == SimulationState.Running)
        {
            var delay = _debt >= _stepDebt ? 0 : (_stepDebt - _debt - 1) / _scale + 1;
            wake = checked(_lastTimestamp + (long)delay);
        }
        return new SimulationIteration(_completedTick, steps, debtBefore, elapsed, _debt, dropped,
            _overloadEpisodes, _overloadActive, _metricsSaturated, Math.Min(fraction, Math.BitDecrement(1.0)), state, wake);
    }

    private void ReportTerminal(FaultClass classification, FaultSeverity severity, Exception original)
    {
        _stopped = true;
        try
        {
            var context = new FaultContext(LastConfirmedBarrier?.World, _activeTick, _activePhase, null, null, null);
            var fault = new FaultObservation(classification, severity, FaultSubsystem.Simulation, FaultOrigin.Primary,
                1, _lastTimestamp, context, [], []);
            _faults.TryReport(fault);
        }
        catch (Exception reportingFailure)
        {
            throw new AggregateException(original, reportingFailure);
        }
    }

    private void SetUserPause(int requested)
    {
        if (Interlocked.Exchange(ref _pauseRequested, requested) != requested)
            _wake.Signal();
    }

    private static bool IsTerminal(SafetyCondition conditions) =>
        (conditions & (SafetyCondition.TerminalStop | SafetyCondition.RequiredEvidenceLost)) != 0;
}
