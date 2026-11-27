namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class NativePresentTraceTests
{
    [TestMethod]
    public void NativePresentBufferKeepsExactRecordsAndReportsDrops()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new D3D12NativePresentTrace(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new D3D12NativePresentTrace(int.MaxValue));
        var firstLifetime = D3D12NativePresentTrace.NewSwapChainLifetime();
        var secondLifetime = D3D12NativePresentTrace.NewSwapChainLifetime();
        Assert.IsGreaterThan(firstLifetime, secondLifetime);
        var trace = new D3D12NativePresentTrace(2);
        var first = new D3D12NativePresentMarker(firstLifetime, 1, 11, 13, 17, 19, 1, 23, 29, 1, 0, 0);
        var second = first with { SwapChainLifetime = secondLifetime, BeforeQpc = 31, AfterQpc = 37 };
        trace.Record(first);
        trace.Record(second);
        trace.Record(second with { CallOrdinal = 2, QueueFence = 2, HResult = -1 });
        var records = trace.Read(out var dropped);
        Assert.AreEqual(1L, dropped);
        CollectionAssert.AreEqual(new[] { first, second }, records);
        records[0] = default;
        Assert.AreEqual(first, trace.Read(out _)[0]);
    }
}
