using Groundwork.Core;

namespace Groundwork.Simulation.Tests;

[TestClass]
public sealed class FixedStepSimulationTests
{
    [TestMethod]
    public async Task OddClockFrequencyAndJitterProduceExactlySixtyTicksAndTwentyGameplaySteps()
    {
        using var run = new SimulationFixture(1001);
        await run.AdvanceAsync();
        for (int sample = 1; sample <= 100; sample++)
        {
            run.Clock.Timestamp = sample * 1001 / 100;
            var iteration = await run.AdvanceAsync();
            AssertConserved(iteration, run.Clock.TimestampFrequency);
            Assert.AreEqual(Int128.Zero, iteration.DroppedScaledTime);
        }

        var final = await run.AdvanceAsync();
        Assert.AreEqual(60UL, final.CompletedTick);
        Assert.AreEqual(Int128.Zero, final.RemainingScaledTime);
        Assert.AreEqual(60UL, run.Input.Count);
        Assert.HasCount(20, run.Pipeline.Calls.Where(call => call.Phase == TickPhase.Gameplay).ToArray());
        for (ulong tick = 1; tick <= 60; tick++)
        {
            var actual = run.Pipeline.Calls.Where(call => call.Tick == tick).ToArray();
            TickPhase[] expected = tick % 3 == 0
                ? [TickPhase.BeginTick, TickPhase.Gameplay, TickPhase.Base, TickPhase.CommitResults, TickPhase.PublishState]
                : [TickPhase.BeginTick, TickPhase.Base, TickPhase.CommitResults, TickPhase.PublishState];
            CollectionAssert.AreEqual(expected, actual.Select(call => call.Phase).ToArray());
            Assert.IsTrue(actual.All(call => call.Input == tick));
            Assert.IsTrue(actual.All(call => call.Step == new FixedStep(1, call.Phase == TickPhase.Gameplay ? 20 : 60)));
        }
    }

    [TestMethod]
    public async Task CapIncludesRetainedDebtAndZeroElapsedIterationsCanServiceIt()
    {
        using var run = new SimulationFixture(60000);
        await run.AdvanceAsync();
        var step = (Int128)60000 * TimeSpan.TicksPerSecond;

        run.Clock.Timestamp = 11000;
        var first = await run.AdvanceAsync();
        Assert.AreEqual(5, first.AppliedSteps);
        Assert.AreEqual(6 * step, first.RemainingScaledTime);
        Assert.AreEqual(11000L, first.NextWakeTimestamp);

        run.Clock.Timestamp += 18000;
        var overload = await run.AdvanceAsync();
        Assert.AreEqual(6 * step, overload.DebtBeforeScaledTime);
        Assert.AreEqual(9 * step, overload.DroppedScaledTime);
        Assert.AreEqual(10 * step, overload.RemainingScaledTime);
        Assert.AreEqual(5, overload.AppliedSteps);
        Assert.AreEqual(1UL, overload.OverloadEpisodeCount);
        Assert.IsTrue(overload.OverloadActive);

        var next = await run.AdvanceAsync();
        var last = await run.AdvanceAsync();
        Assert.AreEqual(5, next.AppliedSteps);
        Assert.AreEqual(5, last.AppliedSteps);
        Assert.AreEqual(Int128.Zero, last.ElapsedScaledTime);
        Assert.AreEqual(Int128.Zero, last.RemainingScaledTime);
        Assert.AreEqual(20UL, last.CompletedTick);
        Assert.AreEqual(30000L, last.NextWakeTimestamp);
        Assert.IsFalse(last.OverloadActive);
        foreach (var result in new[] { first, overload, next, last })
            AssertConserved(result, run.Clock.TimestampFrequency);
    }

    [TestMethod]
    public async Task PauseAccountsForItsFinalSampleAndResumeWaitsForBothPauseCauses()
    {
        using var run = new SimulationFixture(60000);
        await run.AdvanceAsync();
        var step = (Int128)60000 * TimeSpan.TicksPerSecond;
        run.Clock.Timestamp = 1500;
        run.Driver.RequestPause();
        Assert.IsTrue(run.Wake.Handle.WaitOne(TimeSpan.Zero));
        var paused = await run.AdvanceAsync();
        Assert.AreEqual(SimulationState.Paused, paused.State);
        Assert.AreEqual(0, paused.AppliedSteps);
        Assert.AreEqual(step + step / 2, paused.RemainingScaledTime);
        Assert.AreEqual(paused.RemainingScaledTime, paused.ElapsedScaledTime);
        Assert.IsNull(paused.NextWakeTimestamp);

        run.Safety.Conditions = SafetyCondition.RecoveryPaused;
        run.Clock.Timestamp += 600000;
        run.Driver.RequestResume();
        Assert.IsTrue(run.Wake.Handle.WaitOne(TimeSpan.Zero));
        var held = await run.AdvanceAsync();
        Assert.AreEqual(SimulationState.Paused, held.State);
        Assert.AreEqual(Int128.Zero, held.ElapsedScaledTime);
        Assert.AreEqual(paused.RemainingScaledTime, held.RemainingScaledTime);

        run.Safety.Conditions = SafetyCondition.None;
        var resumed = await run.AdvanceAsync();
        Assert.AreEqual(1UL, resumed.CompletedTick);
        Assert.AreEqual(Int128.Zero, resumed.ElapsedScaledTime);
        Assert.AreEqual(step / 2, resumed.RemainingScaledTime);
        Assert.AreEqual(0.5, resumed.Interpolation);
        Assert.AreEqual(run.Clock.Timestamp + 500, resumed.NextWakeTimestamp);
    }

    [TestMethod]
    public async Task RecoveryPauseObservedAfterPublicationAccountsForTheFinalRunningInterval()
    {
        using var run = new SimulationFixture(60000);
        await run.AdvanceAsync();
        run.Clock.Timestamp = 1000;
        run.Pipeline.OnPhase = phase =>
        {
            if (phase == TickPhase.PublishState)
            {
                run.Clock.Timestamp += 500;
                run.Safety.Conditions = SafetyCondition.RecoveryPaused;
            }
        };

        var paused = await run.AdvanceAsync();
        var step = (Int128)60000 * TimeSpan.TicksPerSecond;
        Assert.AreEqual(SimulationState.Paused, paused.State);
        Assert.AreEqual(1UL, paused.CompletedTick);
        Assert.AreEqual(step + step / 2, paused.ElapsedScaledTime);
        Assert.AreEqual(step / 2, paused.RemainingScaledTime);
        AssertConserved(paused, run.Clock.TimestampFrequency);

        run.Clock.Timestamp += 60000;
        run.Safety.Conditions = SafetyCondition.None;
        var resumed = await run.AdvanceAsync();
        Assert.AreEqual(Int128.Zero, resumed.ElapsedScaledTime);
        Assert.AreEqual(step / 2, resumed.RemainingScaledTime);
        Assert.AreEqual(run.Clock.Timestamp + 500, resumed.NextWakeTimestamp);
    }

    [TestMethod]
    public async Task FractionalWakeRoundsUpAndInterpolationCannotRoundToOne()
    {
        using var run = new SimulationFixture(long.MaxValue, new SimulationOptions(1, 1, 1, TimeSpan.FromSeconds(1)));
        var first = await run.AdvanceAsync();
        Assert.AreEqual(long.MaxValue, first.NextWakeTimestamp);
        run.Clock.Timestamp = long.MaxValue - 1;
        var fractional = await run.AdvanceAsync();
        Assert.AreEqual(0, fractional.AppliedSteps);
        Assert.AreEqual(Math.BitDecrement(1), fractional.Interpolation);
        Assert.AreEqual(long.MaxValue, fractional.NextWakeTimestamp);
    }

    [TestMethod]
    public async Task CancellationAfterCommitFencesTheIncompleteTickWithoutReplay()
    {
        using var run = new SimulationFixture(60);
        using var cancellation = new CancellationTokenSource();
        await run.AdvanceAsync();
        run.Clock.Timestamp = 1;
        run.Pipeline.OnPhase = phase =>
        {
            if (phase == TickPhase.CommitResults)
                cancellation.Cancel();
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => run.Driver.AdvanceAsync(cancellation.Token).AsTask());
        Assert.AreEqual(BarrierKind.CommitResults, run.Driver.LastConfirmedBarrier?.Kind);
        Assert.IsFalse(run.Pipeline.Calls.Any(call => call.Phase == TickPhase.PublishState));
        Assert.AreEqual(FaultClass.Cancelled, run.Faults.LastClass);
        var after = await run.AdvanceAsync();
        Assert.AreEqual(SimulationState.Stopped, after.State);
        Assert.AreEqual(0UL, after.CompletedTick);
        Assert.AreEqual(1UL, run.Input.Count);
    }

    [TestMethod]
    public async Task CancellationDuringSuccessfulPublicationStillSettlesTheTick()
    {
        using var run = new SimulationFixture(60000);
        using var cancellation = new CancellationTokenSource();
        await run.AdvanceAsync();
        run.Clock.Timestamp = 18000;
        run.Pipeline.OnPhase = phase =>
        {
            if (phase == TickPhase.PublishState)
                cancellation.Cancel();
        };

        var iteration = await run.Driver.AdvanceAsync(cancellation.Token);
        var step = (Int128)60000 * TimeSpan.TicksPerSecond;
        Assert.AreEqual(1UL, iteration.CompletedTick);
        Assert.AreEqual(1, iteration.AppliedSteps);
        Assert.AreEqual(14 * step, iteration.RemainingScaledTime);
        Assert.AreEqual(3 * step, iteration.DroppedScaledTime);
        AssertConserved(iteration, run.Clock.TimestampFrequency);
        Assert.AreEqual(0, run.Faults.Count);
    }

    [TestMethod]
    public async Task StopAfterTheObserverSwapKeepsThePublishedTickAndSuppressesWake()
    {
        using var run = new SimulationFixture(60);
        await run.AdvanceAsync();
        run.Clock.Timestamp = 1;
        run.Pipeline.OnPhase = phase =>
        {
            if (phase == TickPhase.PublishState)
                run.Safety.Conditions = SafetyCondition.TerminalStop;
        };

        var stopped = await run.AdvanceAsync();
        Assert.AreEqual(SimulationState.Stopped, stopped.State);
        Assert.AreEqual(1UL, stopped.CompletedTick);
        Assert.AreEqual(1, stopped.AppliedSteps);
        Assert.IsNull(stopped.NextWakeTimestamp);
    }

    [TestMethod]
    public async Task StopBetweenWriterBarriersPreventsCommitAndObserverPublication()
    {
        using var run = new SimulationFixture(60);
        await run.AdvanceAsync();
        run.Clock.Timestamp = 1;
        run.Pipeline.OnPhase = phase =>
        {
            if (phase == TickPhase.Base)
                run.Safety.Conditions = SafetyCondition.TerminalStop;
        };

        var stopped = await run.AdvanceAsync();
        Assert.AreEqual(SimulationState.Stopped, stopped.State);
        Assert.AreEqual(0UL, stopped.CompletedTick);
        Assert.AreEqual(BarrierKind.BeginTick, run.Driver.LastConfirmedBarrier?.Kind);
        Assert.IsFalse(run.Pipeline.Calls.Any(call => call.Phase is TickPhase.CommitResults or TickPhase.PublishState));
    }

    [TestMethod]
    public async Task RecoveryPauseDuringAnActiveTickCannotReplayItsBeginBarrier()
    {
        using var run = new SimulationFixture(60);
        await run.AdvanceAsync();
        run.Clock.Timestamp = 1;
        run.Pipeline.OnPhase = phase =>
        {
            if (phase == TickPhase.BeginTick)
                run.Safety.Conditions = SafetyCondition.RecoveryPaused;
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.AdvanceAsync().AsTask());
        Assert.AreEqual(BarrierKind.BeginTick, run.Driver.LastConfirmedBarrier?.Kind);
        run.Safety.Conditions = SafetyCondition.None;
        run.Driver.RequestResume();
        Assert.AreEqual(SimulationState.Stopped, (await run.AdvanceAsync()).State);
        Assert.HasCount(1, run.Pipeline.Calls);
    }

    [TestMethod]
    public async Task PipelineFailurePreservesThePrimaryExceptionAndFencesFurtherTicks()
    {
        using var run = new SimulationFixture(60);
        await run.AdvanceAsync();
        run.Clock.Timestamp = 1;
        var primary = new InvalidOperationException("phase failure");
        run.Pipeline.OnPhase = phase =>
        {
            if (phase == TickPhase.Base)
                throw primary;
        };

        Assert.AreSame(primary, await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.AdvanceAsync().AsTask()));
        Assert.AreEqual(FaultClass.UnknownCritical, run.Faults.LastClass);
        Assert.AreEqual(TickPhase.Base, run.Faults.LastContext.Phase);
        Assert.AreEqual(SimulationState.Stopped, (await run.AdvanceAsync()).State);
        Assert.AreEqual(1UL, run.Input.Count);
    }

    [TestMethod]
    public async Task WorldReporterRetainsEvidenceFromBeforeTheFirstBarrier()
    {
        using var run = new SimulationFixture(60);
        var world = run.OpenWorld(out var id);
        var driver = run.CreateWithReporter(world.Reporter, id);
        await driver.AdvanceAsync(CancellationToken.None);
        run.Clock.Timestamp = 1;
        var primary = new InvalidOperationException("input failure");
        run.Input.Failure = primary;

        Assert.AreSame(primary, await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => driver.AdvanceAsync(CancellationToken.None).AsTask()));
        var state = world.State.Read();
        Assert.IsFalse(state.Conditions.HasFlag(SafetyCondition.RequiredEvidenceLost));
        var evidence = new RequiredFaultEvidence[1];
        Assert.AreEqual(FaultEvidenceReadStatus.Copied, world.Evidence.CopyPending(state.PendingFaultGeneration, evidence, out var count));
        Assert.AreEqual(1, count);
        Assert.AreEqual(id, evidence[0].Context.World);
        Assert.AreEqual(TickPhase.AcquireInput, evidence[0].Context.Phase);
    }

    [TestMethod]
    public async Task RuntimeReporterRetainsEvidenceAfterAWorldBarrier()
    {
        using var run = new SimulationFixture(60);
        var driver = run.CreateWithReporter(run.Runtime.Reporter);
        await driver.AdvanceAsync(CancellationToken.None);
        run.Clock.Timestamp = 1;
        var primary = new InvalidOperationException("base phase failure");
        run.Pipeline.OnPhase = phase =>
        {
            if (phase == TickPhase.Base)
                throw primary;
        };

        Assert.AreSame(primary, await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => driver.AdvanceAsync(CancellationToken.None).AsTask()));
        Assert.AreEqual(BarrierKind.BeginTick, driver.LastConfirmedBarrier?.Kind);
        var state = run.Runtime.State.Read();
        Assert.IsTrue(state.Conditions.HasFlag(SafetyCondition.TerminalStop));
        Assert.IsFalse(state.Conditions.HasFlag(SafetyCondition.RequiredEvidenceLost));
        Assert.AreEqual(0UL, state.EvidenceLossCount);
    }

    [TestMethod]
    public async Task CallbackReentryFailsWithoutDisplacingTheActiveIteration()
    {
        using var run = new SimulationFixture(60);
        await run.AdvanceAsync();
        run.Clock.Timestamp = 1;
        run.Pipeline.OnPhase = phase =>
        {
            if (phase == TickPhase.BeginTick)
            {
                run.Pipeline.OnPhase = null;
                Assert.ThrowsExactly<InvalidOperationException>(() => run.AdvanceAsync().AsTask().GetAwaiter().GetResult());
            }
        };

        Assert.AreEqual(1UL, (await run.AdvanceAsync()).CompletedTick);
    }

    [TestMethod]
    public async Task ClockRollbackFailsClosed()
    {
        using var run = new SimulationFixture(60);
        run.Clock.Timestamp = 5;
        await run.AdvanceAsync();
        run.Clock.Timestamp = 4;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.AdvanceAsync().AsTask());
        Assert.AreEqual(FaultClass.UnknownCritical, run.Faults.LastClass);
        Assert.AreEqual(SimulationState.Stopped, (await run.AdvanceAsync()).State);
    }

    [TestMethod]
    public void InvalidOptionsAreRejected()
    {
        using var run = new SimulationFixture(60);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => run.CreateDriver(new SimulationOptions(60, 17, 5, TimeSpan.FromMilliseconds(250))));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => run.CreateDriver(new SimulationOptions(60, 20, 5, TimeSpan.FromMilliseconds(10))));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => run.CreateDriver(new SimulationOptions(60, 20, 0, TimeSpan.FromMilliseconds(250))));
    }

    private static void AssertConserved(SimulationIteration result, long frequency)
    {
        var debit = (Int128)result.AppliedSteps * frequency * TimeSpan.TicksPerSecond;
        Assert.AreEqual(result.DebtBeforeScaledTime + result.ElapsedScaledTime, debit + result.RemainingScaledTime + result.DroppedScaledTime);
    }
}
