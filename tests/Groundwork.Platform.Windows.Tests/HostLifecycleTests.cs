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

    private sealed class NoOwnership : IRuntimeOwnership
    {
        public IRuntimeWorkLease? TryTrack(object retainedOwner) => null;
    }
}
