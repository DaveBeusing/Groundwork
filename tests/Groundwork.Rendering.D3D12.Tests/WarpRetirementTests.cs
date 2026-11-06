namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class WarpRetirementTests
{
    [TestMethod]
    public void DelayedCompletionKeepsTheInFlightAllocatorOwned()
    {
        var retirement = new GpuRetirement();
        ulong fence = retirement.BeginSubmission();
        retirement.CompleteSignal(fence);

        Assert.IsFalse(retirement.CanReuse(0, fence));
        Assert.IsFalse(retirement.IsIdle(0));
        Assert.IsTrue(retirement.CanReuse(fence, fence));
        Assert.IsTrue(retirement.IsIdle(fence));
    }

    [TestMethod]
    public void RemovedDeviceSentinelIsNotSuccessfulRetirement()
    {
        var retirement = new GpuRetirement();
        ulong fence = retirement.BeginSubmission();
        retirement.CompleteSignal(fence);

        Assert.IsFalse(retirement.CanReuse(ulong.MaxValue, fence));
        Assert.IsFalse(retirement.IsIdle(ulong.MaxValue));
    }

    [TestMethod]
    public void SubmissionWithoutSuccessfulSignalStaysOwned()
    {
        var retirement = new GpuRetirement();
        ulong first = retirement.BeginSubmission();
        retirement.CompleteSignal(first);
        retirement.BeginSubmission();

        // Simulate failed native Signal by leaving the submission uncommitted.
        Assert.IsFalse(retirement.CanReuse(first + 1, first));
        Assert.IsFalse(retirement.IsIdle(first + 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => retirement.BeginSubmission());
    }
}
