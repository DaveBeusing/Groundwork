using Groundwork.Core;

namespace Groundwork.Platform.Windows.Tests;

[TestClass]
public sealed class HostLifecycleTests
{
    [TestMethod]
    public async Task RejectsInvalidTransitionCapacityAndDisposesBeforeInitialization()
    {
        await using var fixture = new HostFixture(new WindowsHostOptions("Invalid", 300, 200, 0));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => fixture.InitializeAsync().AsTask());
        Assert.IsTrue(fixture.Host.Capture().Events.IsEmpty);
        await fixture.Host.DisposeAsync();
        Assert.AreEqual(HostWindowPhase.Closed, fixture.Host.Read().Phase);
    }

    [TestMethod]
    public async Task StopBeforeStartupIsTerminalAndIdempotent()
    {
        await using var fixture = new HostFixture();
        await fixture.InitializeAsync();
        await fixture.Host.StopAsync(CancellationToken.None);
        var closed = fixture.Host.Read();
        Assert.AreEqual(HostWindowPhase.Closed, closed.Phase);
        Assert.IsTrue(fixture.Runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
        await fixture.Host.StopAsync(CancellationToken.None);
        Assert.AreEqual(closed, fixture.Host.Read());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Host.StartAsync(CancellationToken.None).AsTask());
    }

    [TestMethod]
    public async Task DeniedOwnershipFaultsStartupAndClosesWithoutStartingNativeThread()
    {
        await using var fixture = new HostFixture(ownership: new NoOwnership());
        await fixture.InitializeAsync();
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Host.StartAsync(CancellationToken.None).AsTask());
        await fixture.Host.StopAsync(CancellationToken.None);
        Assert.AreSame(error, fixture.Host.Failure?.Primary?.Exception.SourceException);
        Assert.AreEqual(HostWindowPhase.Closed, fixture.Host.Read().Phase);
        Assert.IsTrue(fixture.Runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
    }

    [TestMethod]
    public async Task CancelledStartCanBeCleanedUpWithoutOpeningAWindow()
    {
        await using var fixture = new HostFixture();
        await fixture.InitializeAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => fixture.Host.StartAsync(new CancellationToken(true)).AsTask());
        await fixture.Host.DisposeAsync();
        Assert.AreEqual(HostWindowPhase.Closed, fixture.Host.Read().Phase);
        Assert.IsNull(fixture.Host.Failure);
    }

    [TestMethod]
    public async Task StopKeepsCoreShutdownBudgetAfterTheInputClockHasBeenIdle()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ownership = new PausedAdmission();
        var shutdown = TimeSpan.FromSeconds(1);
        await using var fixture = new HostFixture(ownership: ownership, clock: new FrozenClock(), shutdown: shutdown);
        ownership.Inner = fixture.Runtime.RuntimeOwnership;
        await fixture.InitializeAsync(watchdog.Token);
        var start = Task.Run(() => fixture.Host.StartAsync(watchdog.Token).AsTask());
        Task stop;
        try
        {
            await ownership.Entered.Task.WaitAsync(watchdog.Token);
            // No UI thread or input samples yet. The elapsed idle time exceeds the entire shutdown budget.
            await Task.Delay(shutdown + TimeSpan.FromMilliseconds(100), watchdog.Token);
            stop = fixture.Host.StopAsync(watchdog.Token).AsTask();
            Assert.IsFalse(stop.IsCompleted);
        }
        finally
        {
            ownership.Resume.Set();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() => start);
        await stop;
        Assert.AreEqual(HostWindowPhase.Closed, fixture.Host.Read().Phase);
        Assert.IsNull(fixture.Host.Failure);
        Assert.AreEqual(ShutdownSettlement.Released, fixture.Runtime.SettleStop());
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override long GetTimestamp() => 1;
    }

    private sealed class PausedAdmission : IRuntimeOwnership, IDisposable
    {
        internal IRuntimeOwnership Inner { get; set; } = null!;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Resume { get; } = new();

        public IRuntimeWorkLease? TryTrack(object retainedOwner)
        {
            var lease = Inner.TryTrack(retainedOwner);
            Entered.SetResult();
            Resume.Wait(TimeSpan.FromSeconds(5));
            return lease;
        }

        public void Dispose() => Resume.Dispose();
    }

    private sealed class NoOwnership : IRuntimeOwnership
    {
        public IRuntimeWorkLease? TryTrack(object retainedOwner) => null;
    }
}
