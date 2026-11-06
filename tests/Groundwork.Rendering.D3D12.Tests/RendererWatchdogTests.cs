namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class RendererWatchdogTests
{
    [TestMethod]
    [DataRow((uint)RendererStage.Resize)]
    [DataRow((uint)RendererStage.Present)]
    public async Task TimeoutDoesNotWaitForBlockedNativeOperation(uint blockedStage)
    {
        var timedOut = new TaskCompletionSource<(RendererStage, ulong?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchdog = new RendererWatchdog((stage, sequence) => timedOut.TrySetResult((stage, sequence)));
        var owner = Task.Run(async () =>
        {
            Assert.IsTrue(watchdog.Begin((RendererStage)blockedStage, 17, TimeSpan.FromMilliseconds(100)));
            await unblock.Task.ConfigureAwait(false);
            watchdog.Complete();
        });

        try
        {
            Assert.AreEqual(((RendererStage)blockedStage, (ulong?)17),
                await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
            Assert.IsFalse(owner.IsCompleted);
            Assert.IsFalse(watchdog.Begin(RendererStage.Submit, 18, TimeSpan.FromSeconds(1)));
        }
        finally
        {
            unblock.TrySetResult();
            await owner.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public void CompletedOperationCanHandOffToAnotherDeadline()
    {
        using var watchdog = new RendererWatchdog((_, _) => Assert.Fail("A completed operation expired."));
        Assert.IsTrue(watchdog.Begin(RendererStage.Startup, null, TimeSpan.FromSeconds(10)));
        watchdog.Complete();
        Assert.IsTrue(watchdog.Begin(RendererStage.Submit, 1, TimeSpan.FromSeconds(10)));
        Assert.IsTrue(watchdog.TryAdvance(RendererStage.Retire));
        watchdog.Complete();
    }

    [TestMethod]
    public async Task ClaimedExpiryDeniesPresentBeforeTheFaultCallbackReturns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var watchdog = new RendererWatchdog((_, _) =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        try
        {
            Assert.IsTrue(watchdog.Begin(RendererStage.Submit, 1, TimeSpan.FromMilliseconds(100)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(watchdog.TryAdvance(RendererStage.Present));
            Assert.IsFalse(watchdog.Complete());
        }
        finally { release.Set(); }
    }
}
