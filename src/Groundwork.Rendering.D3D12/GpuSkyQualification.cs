using System.Diagnostics;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    private ulong _skyTimingPendingFence;
    private ulong _skyTimingPendingSequence;
    private long _skyRecordStarted;
    private long _skyRecordAllocationStart;
    private double _skyRecordMicroseconds;
    private long _skyRecordAllocatedBytes;
    private int _skyRecordDrawCount;

    private void ResetSkyRecordMeasurementForTests()
    {
        if (_retirementHooks?.AfterSkyRecordMeasured is null)
            return;
        _skyRecordMicroseconds = 0;
        _skyRecordAllocatedBytes = 0;
        _skyRecordDrawCount = 0;
    }

    private void BeginSkyRecordMeasurementForTests()
    {
        if (_retirementHooks?.AfterSkyRecordMeasured is null)
            return;
        _skyRecordAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        _skyRecordStarted = Stopwatch.GetTimestamp();
    }

    private void EndSkyRecordMeasurementForTests()
    {
        if (_retirementHooks?.AfterSkyRecordMeasured is null)
            return;
        _skyRecordMicroseconds = Stopwatch.GetElapsedTime(_skyRecordStarted).TotalMicroseconds;
        _skyRecordAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _skyRecordAllocationStart;
    }

    private void PublishSkyQualificationForTests(ulong sequence, ulong fence)
    {
        _retirementHooks?.AfterSkyRecordMeasured?.Invoke(sequence, fence, _skyRecordMicroseconds,
            _skyRecordAllocatedBytes, _skyRecordDrawCount);
        if ((_retirementHooks?.AfterSkyTimingRetired is null && _retirementHooks?.AfterSkyTimingDetailsRetired is null) ||
            _terrainTiming is not { MeasuresSky: true })
            return;
        if (_skyTimingPendingFence != 0)
            throw new InvalidOperationException("A retired sky timing must be drained before replacing its query results.");
        _skyTimingPendingFence = fence;
        _skyTimingPendingSequence = sequence;
    }

    private void DrainRetiredSkyTimingForTests(ulong completed)
    {
        if (completed == ulong.MaxValue || _skyTimingPendingFence == 0 || completed < _skyTimingPendingFence ||
            !_retirement.IsIdle(completed) || _terrainTiming is not { MeasuresSky: true } timing)
            return;
        var sequence = _skyTimingPendingSequence;
        var fence = _skyTimingPendingFence;
        var result = timing.ReadSkyMicroseconds();
        // Clear before calling the observer so a reentrant diagnostic read does
        // not emit twice. Results are never mapped before their actual fence.
        _skyTimingPendingFence = 0;
        _skyTimingPendingSequence = 0;
        _retirementHooks?.AfterSkyTimingRetired?.Invoke(sequence, fence, result.FrameMicroseconds,
            result.SkyMicroseconds, result.HasSky);
        _retirementHooks?.AfterSkyTimingDetailsRetired?.Invoke(sequence, fence, result.FrameStart,
            result.FrameEnd, result.SkyStart, result.SkyEnd, result.Frequency);
    }

    internal GpuFrameSubmitResult TryRenderSkyTimingForTests(GpuTarget target, in RenderFrame frame)
    {
        if (_terrainTiming is not { MeasuresSky: true } || !IsIdle)
            throw new InvalidOperationException("Sky timing requires an enabled timer and retired previous frame.");
        if (_presentReadback is null || !_presentReadback.Matches(this, target))
        {
            var readback = new Readback(this, target);
            _presentReadback?.Dispose();
            _presentReadback = readback;
        }
        // Keep the production plan's readback terminal. Its GPU copy is after
        // the frame end timestamp; no image mapping/copy runs on the CPU.
        return TryRender(target, frame, null, _presentReadback, owner: null);
    }

    // Persistent offscreen target for timing: no per-sample target creation or
    // CPU image transfer. The caller must retire submissions before disposal.
    internal GpuTarget CreateSkyTimingTargetForTests(int width, int height)
    {
        ID3D12Resource* color = null;
        try
        {
            var clear = new D3D12_CLEAR_VALUE { Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM };
            color = CreateResource(_device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
                Texture(width, height, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                    D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET),
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, &clear);
            var target = Attach(color, width, height);
            color = null;
            return target;
        }
        finally
        {
            Release(color);
        }
    }
}
