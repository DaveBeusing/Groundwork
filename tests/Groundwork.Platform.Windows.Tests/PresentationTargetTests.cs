using Groundwork.Core;

namespace Groundwork.Platform.Windows.Tests;

[TestClass]
public sealed class PresentationTargetTests
{
    private static readonly nint Window = new(1);
    private static readonly HostWindowSnapshot Open = new(HostWindowPhase.Open, 800, 600, false, 1);

    [TestMethod]
    public async Task ExclusiveLeaseDefersReleaseUntilTheAdmittedOperationEnds()
    {
        await using var fixture = new HostFixture();
        using var target = new PresentationTarget(fixture.Runtime.RuntimeOwnership);
        Assert.IsNull(target.TryAcquire());
        target.Open(Window, Open);

        using var lease = target.TryAcquire();
        Assert.IsNotNull(lease);
        Assert.IsNull(target.TryAcquire());
        using var operation = lease.TryBeginOperation();
        Assert.IsNotNull(operation);
        Assert.AreEqual(Window, operation.WindowHandle);
        Assert.AreEqual(Open, operation.Snapshot);
        Assert.IsNull(lease.TryBeginOperation());

        lease.Dispose();
        lease.Dispose();
        Assert.AreEqual(PresentationTargetRevocation.Released, await lease.Revoked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsNull(lease.TryBeginOperation());
        Assert.IsNull(target.TryAcquire());
        Assert.IsNotNull(target.DrainWake);

        operation.Dispose();
        operation.Dispose();
        Assert.IsNull(target.DrainWake);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = operation.WindowHandle);
        Assert.IsNull(lease.TryBeginOperation());

        using var replacement = target.TryAcquire();
        Assert.IsNotNull(replacement);
        Assert.IsNull(lease.TryBeginOperation());
        using var replacementOperation = replacement.TryBeginOperation();
        Assert.IsNotNull(replacementOperation);
        Assert.AreEqual(Window, replacementOperation.WindowHandle);
    }

    [TestMethod]
    public async Task ResizeUpdatesCoalesceAndWakeAnObserverOnMinimizeAndRestore()
    {
        await using var fixture = new HostFixture();
        using var target = new PresentationTarget(fixture.Runtime.RuntimeOwnership);
        target.Open(Window, Open);
        using var lease = target.TryAcquire();
        Assert.IsNotNull(lease);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        target.Publish(Open with { ClientWidth = 900, Revision = 2 });
        var latest = Open with { ClientWidth = 1024, ClientHeight = 768, Revision = 3 };
        target.Publish(latest);
        Assert.AreEqual(latest, await lease.WaitForChangeAsync(Open.Revision, watchdog.Token));

        var minimized = new HostWindowSnapshot(HostWindowPhase.Minimized, 0, 0, false, 4);
        var pending = lease.WaitForChangeAsync(latest.Revision, watchdog.Token).AsTask();
        Assert.IsFalse(pending.IsCompleted);
        target.Publish(minimized);
        Assert.AreEqual(minimized, await pending.WaitAsync(watchdog.Token));

        using var cancellation = new CancellationTokenSource();
        var cancelled = lease.WaitForChangeAsync(minimized.Revision, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.WaitAsync(watchdog.Token));

        var restored = latest with { Revision = 5 };
        target.Publish(restored);
        Assert.AreEqual(restored, await lease.WaitForChangeAsync(minimized.Revision, watchdog.Token));
    }

    [TestMethod]
    public async Task NativeDestructionRevokesAndWakesWithoutAnotherSizeRevision()
    {
        await using var fixture = new HostFixture();
        using var target = new PresentationTarget(fixture.Runtime.RuntimeOwnership);
        target.Open(Window, Open);
        using var lease = target.TryAcquire();
        Assert.IsNotNull(lease);
        using var operation = lease.TryBeginOperation();
        Assert.IsNotNull(operation);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = lease.WaitForChangeAsync(Open.Revision, watchdog.Token).AsTask();

        Assert.IsTrue(target.Revoke(PresentationTargetRevocation.NativeDestroyed));
        await pending.WaitAsync(watchdog.Token);
        Assert.AreEqual(PresentationTargetRevocation.NativeDestroyed, await lease.Revoked.WaitAsync(watchdog.Token));
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = operation.WindowHandle);
        Assert.IsNull(lease.TryBeginOperation());
        Assert.IsNull(target.TryAcquire());
        lease.Dispose();
        Assert.IsNotNull(target.DrainWake);
        operation.Dispose();
        Assert.IsNull(target.DrainWake);
    }

    [TestMethod]
    public async Task NativeDestructionInvalidatesAnAdmittedHandleEvenWhenClosingWasFirst()
    {
        await using var fixture = new HostFixture();
        using var target = new PresentationTarget(fixture.Runtime.RuntimeOwnership);
        target.Open(Window, Open);
        using var lease = target.TryAcquire();
        Assert.IsNotNull(lease);
        using var operation = lease.TryBeginOperation();
        Assert.IsNotNull(operation);

        target.Revoke(PresentationTargetRevocation.Closing);
        Assert.AreEqual(Window, operation.WindowHandle);
        target.Revoke(PresentationTargetRevocation.NativeDestroyed);

        Assert.AreEqual(PresentationTargetRevocation.Closing, await lease.Revoked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = operation.WindowHandle);
    }

    [TestMethod]
    public async Task CoreStopDeniesNewOperationsBeforeTheTargetObservesRevocation()
    {
        await using var fixture = new HostFixture();
        using var target = new PresentationTarget(fixture.Runtime.RuntimeOwnership);
        target.Open(Window, Open);
        using var lease = target.TryAcquire();
        Assert.IsNotNull(lease);

        fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);

        Assert.IsNull(lease.TryBeginOperation());
        Assert.IsFalse(lease.Revoked.IsCompleted);
        target.Revoke(PresentationTargetRevocation.Closing);
        Assert.AreEqual(PresentationTargetRevocation.Closing, await lease.Revoked.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task OperationCanRetryAfterRuntimeOwnershipCapacityIsReturned()
    {
        await using var fixture = new HostFixture();
        using var target = new PresentationTarget(fixture.Runtime.RuntimeOwnership);
        target.Open(Window, Open);
        using var lease = target.TryAcquire();
        Assert.IsNotNull(lease);

        var credits = new IRuntimeWorkLease?[8];
        try
        {
            for (var i = 0; i < credits.Length; i++)
            {
                credits[i] = fixture.Runtime.RuntimeOwnership.TryTrack(this);
                Assert.IsNotNull(credits[i]);
            }

            Assert.IsNull(lease.TryBeginOperation());
            Assert.IsFalse(lease.Revoked.IsCompleted);
            credits[0]!.Dispose();
            credits[0] = null;

            using var operation = lease.TryBeginOperation();
            Assert.IsNotNull(operation);
            Assert.AreEqual(Window, operation.WindowHandle);
        }
        finally
        {
            foreach (var credit in credits)
            {
                credit?.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task RevocationRejectsAnOperationWaitingBetweenCoreAndLocalAdmission()
    {
        await using var fixture = new HostFixture();
        using var ownership = new DelayedOwnership(fixture.Runtime.RuntimeOwnership);
        using var target = new PresentationTarget(ownership);
        target.Open(Window, Open);
        using var lease = target.TryAcquire();
        Assert.IsNotNull(lease);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var admission = Task.Run(lease.TryBeginOperation, watchdog.Token);
        try
        {
            await ownership.Acquired.Task.WaitAsync(watchdog.Token);
            Assert.IsTrue(target.Revoke(PresentationTargetRevocation.Closing));
            Assert.AreEqual(PresentationTargetRevocation.Closing, await lease.Revoked.WaitAsync(watchdog.Token));
        }
        finally
        {
            ownership.Resume.Set();
        }

        using var rejected = await admission.WaitAsync(watchdog.Token);
        Assert.IsNull(rejected);
        fixture.Runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        Assert.AreEqual(ShutdownSettlement.Released, fixture.Runtime.SettleStop(), "Rejected admission must release the Core permit.");
    }

    private sealed class DelayedOwnership(IRuntimeOwnership inner) : IRuntimeOwnership, IDisposable
    {
        internal TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Resume { get; } = new();

        public IRuntimeWorkLease? TryTrack(object retainedOwner)
        {
            var lease = inner.TryTrack(retainedOwner);
            Acquired.TrySetResult();
            if (Resume.Wait(TimeSpan.FromSeconds(5)))
            {
                return lease;
            }

            lease?.Dispose();
            throw new TimeoutException("The test did not resume presentation admission.");
        }

        public void Dispose() => Resume.Dispose();
    }
}
