using System.Diagnostics;
using System.Runtime.InteropServices;
using Groundwork.Core;
using Groundwork.Simulation;

const int warmup = 20000;
const int iterations = 100000;
var clock = new BenchmarkClock();
var quota = new ResourceQuota(1, 16,
    new JobQuota(8, 4, 16, 16, 4, 8, 16, 65536, 65536, 65536),
    new DiagnosticQuota(1, 16384, 12, 32768, 4, 4, 64, 1024));
var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, quota));
using var reservation = budget.TryReserveRuntime(quota) ?? throw new InvalidOperationException("Benchmark reservation unavailable.");
var duration = TimeSpan.FromSeconds(1);
var timing = new RuntimeTimingLimits(duration, duration, duration);
var runtime = RuntimeSafetyAuthority.Create(clock, timing, reservation);
try
{
    var context = new EngineModuleContext(clock, timing, runtime.Reporter, runtime.StopControl);
    var driver = new FixedStepSimulation(context, new ConstantInput(), new EmptyPipeline(), runtime.State, runtime.SimulationWake);
    await driver.AdvanceAsync(CancellationToken.None);
    await RunAsync(driver, clock, warmup);

    var before = GC.GetAllocatedBytesForCurrentThread();
    var start = Stopwatch.GetTimestamp();
    var thread = Environment.CurrentManagedThreadId;
    var last = await RunAsync(driver, clock, iterations);
    var elapsed = Stopwatch.GetElapsedTime(start);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    if (Environment.CurrentManagedThreadId != thread || last.CompletedTick != warmup + iterations ||
        last.RemainingScaledTime != 0 || last.DroppedScaledTime != 0)
        throw new InvalidOperationException("Benchmark did not settle exactly one tick per sample.");

    Console.WriteLine(FormattableString.Invariant($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}"));
    Console.WriteLine(FormattableString.Invariant($"{iterations} ticks after {warmup} warmup ticks. {elapsed.TotalNanoseconds / iterations:F1} ns/tick. {(double)allocated / iterations:F3} B/tick."));
}
finally
{
    runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
    runtime.SettleStop();
}

static async ValueTask<SimulationIteration> RunAsync(FixedStepSimulation driver, BenchmarkClock clock, int count)
{
    SimulationIteration result = default;
    for (int index = 0; index < count; index++)
    {
        clock.Timestamp++;
        result = await driver.AdvanceAsync(CancellationToken.None);
    }
    return result;
}

internal sealed class BenchmarkClock : TimeProvider
{
    public override long TimestampFrequency => 60;
    internal long Timestamp { get; set; }
    public override long GetTimestamp() => Timestamp;
}

internal sealed class ConstantInput : IInputSource
{
    private readonly InputSnapshot _snapshot = new(0, [], [], null, false);
    public InputSnapshot Capture() => _snapshot;
}

internal sealed class EmptyPipeline : ITickPipeline
{
    private ulong _epoch;

    public ValueTask<BarrierOutcome> BeginTickAsync(TickContext tick, CancellationToken cancellationToken) => Barrier(tick, BarrierKind.BeginTick);

    public ValueTask RunGameplayAsync(TickContext tick, FixedStep gameplayStep, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask RunBaseAsync(TickContext tick, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<BarrierOutcome> CommitResultsAsync(TickContext tick, CancellationToken cancellationToken) => Barrier(tick, BarrierKind.CommitResults);

    public ObserverPublication TryPublishState(TickContext tick) => ObserverPublication.Published;

    private ValueTask<BarrierOutcome> Barrier(TickContext tick, BarrierKind kind) =>
        ValueTask.FromResult(new BarrierOutcome(BarrierStatus.Unchanged, new BarrierStamp(new WorldId(1), tick.Tick, kind, ++_epoch, 0)));
}
