# WP01 Core API freeze candidate

This is the declaration-level follow-up to the [owner-accepted runtime design](https://github.com/DaveBeusing/Groundwork/pull/15) at `75d0847760fc70a6387969e1d14b8899dceff0c1`. Review these declarations and their companion specifications together before implementing Core or starting the four runtime lanes. All shared declarations are in `Groundwork.Core` and use only the .NET 10 base libraries.

- [ECS and Jobs declarations](wp01-core-jobs-api.md)
- [Safety, supervision and finite policy declarations](wp01-core-safety-api.md)
- [Test wiring and validation](wp01-test-wiring.md)

## Lifecycle and host stop

```csharp
public interface IEngineModule : IAsyncDisposable
{
    ValueTask InitializeAsync(EngineModuleContext context, CancellationToken cancellationToken);
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

public sealed record EngineModuleContext(
    TimeProvider Clock,
    RuntimeTimingLimits Timing,
    IFaultReporter FaultReporter,
    IRuntimeStopControl StopControl);

public enum HostStopReason
{
    WindowClose = 1,
    ExternalCancellation,
    StartupFault,
    RuntimeFault
}
```

The host validates the context before handing it to a module. Inject typed module-specific ports at construction. A module receives only the capabilities necessary for its role. A reporter or stop requester cannot issue writer proof, alter scheduler state or read another module's mutable storage.

The lifecycle coordinator serializes transitions from created through initialized and running to stopping and disposed. Failed startup enters the same terminal cleanup path. An individual module cannot transition back to initialized or running once terminal intent is latched. Initialization and startup cancellation tokens signal cooperative interruption. The stop operation receives a coordinator token constrained by the common shutdown deadline. Cancellation of a caller's wait does not cancel the shared shutdown operation or change the deadline. Dispose remains idempotent, including after failed initialization. Report incomplete cleanup through the safety contract.

The first `RequestStop` records terminal intent, starts the shared deadline, closes publication and signals the separate retained UI and Simulation wakes from `RuntimeSafetyAuthority`. It runs no arbitrary callbacks and performs no join. The host dispatches cooperative cancellation outside the UI and simulation hot paths. The Windows module owns the native wait integration and UI-thread cleanup. UI-affine resources reachable after the deadline remain under custody until safe release. Duplicate requests return the original deadline and cannot clear a stronger safety classification.

In the composition root, build the shared clock and safety authority before module initialization. Establish Diagnostics reporting capacity and ECS capture/writer capabilities before enabling their consumers. Windows readiness depends on its UI and input setup and does not wait for Simulation. Jobs readiness depends on its capture capability and does not wait for a tick. The host enables the simulation loop after its required input, pipeline and reporting providers reach readiness. Stop and dispose in reverse dependency order.

## Platform-neutral input

```csharp
public enum InputEventKind
{
    Pressed = 1,
    Released,
    PointerMoved,
    WheelScrolled,
    FocusCleared
}

public enum InputDevice
{
    Keyboard = 1,
    Mouse,
    Host
}

public enum InputControl
{
    None = 0,
    A = 1, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Digit0 = 30, Digit1, Digit2, Digit3, Digit4,
    Digit5, Digit6, Digit7, Digit8, Digit9,
    Escape = 50, Tab, Enter, Space, Backspace, Delete, Insert,
    Home, End, PageUp, PageDown, Up, Down, Left, Right,
    LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,
    F1 = 80, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    PointerPrimary = 100, PointerSecondary, PointerMiddle, PointerAuxiliary1, PointerAuxiliary2,
    WheelVertical, WheelHorizontal,
    Minus = 120, Equals, LeftBracket, RightBracket, Backslash, Semicolon, Apostrophe,
    Grave, Comma, Period, Slash, CapsLock, NumLock, ScrollLock, PrintScreen, Pause,
    LeftSystem, RightSystem, Menu,
    Numpad0 = 150, Numpad1, Numpad2, Numpad3, Numpad4, Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
    NumpadAdd, NumpadSubtract, NumpadMultiply, NumpadDivide, NumpadDecimal, NumpadEnter
}

public readonly record struct InputPoint(int X, int Y);

public readonly record struct InputEvent(
    InputEventKind Kind,
    InputDevice Device,
    InputControl Control,
    ulong Sequence,
    long Timestamp,
    InputPoint Position,
    int WheelDelta);

public sealed record InputSnapshot(
    ulong LastSequence,
    ImmutableArray<InputEvent> Events,
    ImmutableArray<InputControl> HeldControls,
    InputPoint? PointerPosition,
    bool TransitionLoss);

public interface IInputSource
{
    InputSnapshot Capture();
}

public enum HostWindowPhase { Starting = 1, Open, Minimized, Closing, Closed }

public readonly record struct HostWindowSnapshot(
    HostWindowPhase Phase,
    int ClientWidth,
    int ClientHeight,
    bool Focused,
    ulong Revision);

public interface IHostWindowStateSource
{
    HostWindowSnapshot Read();
}
```

`ImmutableArray<T>` is `System.Collections.Immutable.ImmutableArray<T>`. Capture returns non-default arrays in one coherent snapshot. It drains the retained ordered transitions for the single simulation consumer and reads current held state independently of transition retention. Held controls are unique and sorted by their numeric code. Sequence starts at one and is authoritative when timestamps tie. Exhaustion is a typed runtime fault. Timestamp comes from the same injected monotonic clock as scheduler samples. `LastSequence` is the highest input event sequence generated at capture, including dropped transitions, or zero before the first event. `TransitionLoss` identifies loss since the previous capture.

Keyboard and mouse presses/releases carry a valid `Control`. Mouse movement carries client-relative coordinates. Wheel events identify the wheel axis and carry signed normalized 1/120-detent units. A focus clear has device `Host` and control `None`. Unused payload fields are zero. Focus loss or close immediately clears held state and resets pointer position to unknown even if there is no transition space. An unsupported native control emits no input event. L1 validates a positive transition capacity at initialization and owns the platform mapping and producer.

Resize, minimize and focus also update the coherent host window snapshot. The revision advances on each state change without wrap. Zero client size before creation or while minimized is valid. A snapshot may coalesce size changes between reads. Closing latches the independent stop request and prevents returning to `Open`. Readers receive no native handles and run no callbacks on the message pump.

## Tick handoff and public Simulation operations

```csharp
// Groundwork.Core
public enum TickPhase
{
    AcquireInput = 1,
    BeginTick,
    Gameplay,
    Base,
    CommitResults,
    PublishState
}

public readonly record struct FixedStep(int Numerator, int Denominator);
public readonly record struct TickContext(ulong Tick, FixedStep BaseStep, InputSnapshot Input);

public enum BarrierStatus { Committed = 1, Unchanged, Stopped }
public readonly record struct BarrierOutcome(BarrierStatus Status, BarrierStamp? Stamp);

public enum ObserverPublication { Published = 1, Blocked, PreparationFailed }

public interface ITickPipeline
{
    ValueTask<BarrierOutcome> BeginTickAsync(TickContext tick, CancellationToken cancellationToken);
    ValueTask RunGameplayAsync(TickContext tick, FixedStep gameplayStep, CancellationToken cancellationToken);
    ValueTask RunBaseAsync(TickContext tick, CancellationToken cancellationToken);
    ValueTask<BarrierOutcome> CommitResultsAsync(TickContext tick, CancellationToken cancellationToken);
    ObserverPublication TryPublishState(TickContext tick);
}

// Groundwork.Simulation
public sealed record SimulationOptions(
    int BaseRateHz,
    int GameplayRateHz,
    int MaxStepsPerIteration,
    TimeSpan MaxBacklog);

public enum SimulationState { Running = 1, Paused, Stopped }

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
```

Only the scheduler coordinator uses `ITickPipeline`. A `FixedStep` has strictly positive numerator and denominator. The base step is `1 / BaseRateHz` seconds and the eligible gameplay step is `1 / GameplayRateHz` seconds. Tick zero means no completed tick. The first tick is one. Default options are 60 Hz base, 20 Hz gameplay, at most five steps per iteration and 250 ms backlog. Reject nonpositive configuration, a nonintegral base-to-gameplay rate ratio, a cap below one complete step, or a clock/options range that cannot support the checked `Int128` arithmetic.

The scheduler captures input once per tick, calls `BeginTickAsync`, runs gameplay on eligible ticks and then base work, calls `CommitResultsAsync`, and attempts observer publication. Both system phases see the same committed state after `BeginTick`. `Committed` and `Unchanged` carry the authoritative stamp after the barrier. Both advance the barrier epoch, including a no-op. `Committed` means the committed world revision changed. Queue and receipt transitions remain authoritative even when the world revision stays unchanged. `Stopped` indicates an uncompleted barrier and fences the loop. The existing writer state and operational error remain separately observable. Neither cancellation nor a thrown phase callback certifies that a barrier was rolled back.

The successful observer swap settles the tick and its debit without an intervening cancellation point. Terminal close immediately competes with each publication, including mid-tick. If interrupted after a completed barrier, the first implementation stops without replay, preserves the prior observer snapshot and reports the last actual writer revision. User and recovery pauses are independent. Resume clears only a user pause, and the clock origin resets only after both pause causes clear. Concurrent advance and callback reentry fail explicitly.

`RemainingScaledTime` and `DroppedScaledTime` use units of `elapsedClockUnits * BaseRateHz * TimeSpan.TicksPerSecond`. A step debits `Clock.TimestampFrequency * TimeSpan.TicksPerSecond`. Preserve exact cap-before-service accounting and clamp the rounded interpolation value below one. `NextWakeTimestamp` is absent while paused or terminal, immediately due if debt funds a complete step, or the last sample plus the checked ceiling of the time still needed for a step. Host control changes also wake the coordinator. Runtime limits, external shutdown and operation faults travel through the safety contract.

`DebtBeforeScaledTime` is retained debt before admitting the iteration's elapsed time. It and `ElapsedScaledTime` use the same units as remaining and dropped time. Include the final running sample when pause becomes effective, with no step applied for that sample. While already paused, elapsed and dropped values are zero. An overload episode starts on the first cap discard or on a discard after a running sample without a discard. The count saturates explicitly, and resume starts a fresh episode boundary without clearing the cumulative count. Use the retained Simulation wake for user controls and recovery so an already-signaled notification survives the transition into a paused wait.
