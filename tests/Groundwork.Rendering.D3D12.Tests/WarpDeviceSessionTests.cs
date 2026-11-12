namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class WarpDeviceSessionTests
{
    [TestMethod]
    public unsafe void SequentialOwnersReuseTheDeviceAndExcludeConcurrentOwners()
    {
        using var session = new D3D12DeviceSession(useWarp: true);
        nint device;
        using (var first = new GpuDevice(warp: true, session: session))
        {
            device = (nint)first.Device;
            Assert.AreNotEqual((nint)0, device);
            Assert.ThrowsExactly<InvalidOperationException>(() => new GpuDevice(warp: true, session: session));
        }

        using var second = new GpuDevice(warp: true, session: session);
        Assert.AreEqual(device, (nint)second.Device);
        Assert.IsTrue(second.Adapter.IsSoftware);
    }

    [TestMethod]
    public void DisposingTheSessionClosesNewAdmissions()
    {
        using var session = new D3D12DeviceSession(useWarp: true);
        using var current = new GpuDevice(warp: true, session: session);
        session.Dispose();
        Assert.IsTrue(current.IsIdle);
        Assert.ThrowsExactly<ObjectDisposedException>(() => new GpuDevice(warp: true, session: session));
    }
}
