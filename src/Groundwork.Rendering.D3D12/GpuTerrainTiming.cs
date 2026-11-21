using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal readonly record struct ShadowGpuTiming(double FrameMicroseconds, double TerrainMicroseconds,
    double NearShadowMicroseconds, double FarShadowMicroseconds, int ShadowPassCount);

internal readonly record struct SkyGpuTiming(double FrameMicroseconds, double SkyMicroseconds, bool HasSky,
    ulong FrameStart = 0, ulong FrameEnd = 0, ulong SkyStart = 0, ulong SkyEnd = 0, ulong Frequency = 0,
    bool HasLookupSplit = false, double LookupComputeMicroseconds = 0, double LookupDrawBarrierMicroseconds = 0);

internal sealed unsafe partial class GpuDevice
{
    private TerrainTiming? _terrainTiming;

    // An opt-in measurement waits for retirement before reusing its query heap.
    // The normal renderer never allocates these resources.
    internal void EnableTerrainTimingForTests() => EnableTiming(measureShadows: false);

    internal void EnableShadowTimingForTests() => EnableTiming(measureShadows: true);

    internal void EnableSkyTimingForTests() => EnableTiming(measureShadows: true, measureSky: true);

    internal ulong SkyTimestampFrequencyForTests => _terrainTiming is { MeasuresSky: true } timing
        ? timing.Frequency : throw new InvalidOperationException("Sky timing is not enabled.");

    private void EnableTiming(bool measureShadows, bool measureSky = false)
    {
        if (_device == null || _queue == null || !IsIdle || _terrainTiming is not null)
            throw new InvalidOperationException("Timing requires an idle device without an active query heap.");

        _terrainTiming = new(_device, _queue, measureShadows, measureSky);
        if (measureSky && _retirementHooks?.AfterSkyClockCalibration is { } calibrated)
        {
            ulong gpu = 0, cpu = 0;
            Check(_queue->GetClockCalibration(&gpu, &cpu), "Calibrate diagnostic GPU and CPU clocks");
            calibrated(gpu, cpu, _terrainTiming.Frequency);
        }
    }

    internal SkyGpuTiming ReadSkyTimingForTests(ulong sequence)
    {
        if (sequence == 0 || _terrainTiming is not { MeasuresSky: true } timing || timing.Sequence != sequence || !IsIdle)
            throw new InvalidOperationException("No retired sky measurement matches this frame.");
        return timing.ReadSkyMicroseconds();
    }

    internal double ReadTerrainTimingForTests(ulong sequence)
    {
        if (sequence == 0 || _terrainTiming is not { MeasuresShadows: false } timing || timing.Sequence != sequence || !IsIdle)
            throw new InvalidOperationException("No retired terrain measurement matches this frame.");

        return timing.ReadTerrainMicroseconds();
    }

    internal ShadowGpuTiming ReadShadowTimingForTests(ulong sequence)
    {
        if (sequence == 0 || _terrainTiming is not { MeasuresShadows: true } timing || timing.Sequence != sequence || !IsIdle)
            throw new InvalidOperationException("No retired shadow measurement matches this frame.");

        return timing.ReadShadowMicroseconds();
    }

    // Query zero always starts the frame in shadow timing mode. Other zero offsets mean
    // the corresponding pass was absent. Only the layout of a submitted attempt is published.
    private struct ShadowTimingLayout
    {
        internal uint QueryCount;
        internal uint FrameEnd;
        internal uint TerrainStart;
        internal uint NearStart;
        internal uint FarStart;
        internal uint SkyStart;
        internal uint SkyEnd;
        internal uint SkyLookupComputeEnd;
    }

    private sealed class TerrainTiming : IDisposable
    {
        private const uint TerrainQueryCount = 2;
        private const uint ShadowQueryCount = 8;
        private const D3D12_QUERY_TYPE Timestamp = D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP;
        private readonly ulong _frequency;
        private ID3D12QueryHeap* _heap;
        private ID3D12Resource* _results;
        private ShadowTimingLayout _submitted;

        internal TerrainTiming(ID3D12Device* device, ID3D12CommandQueue* queue, bool measureShadows, bool measureSky)
        {
            ulong frequency = 0;
            Check(queue->GetTimestampFrequency(&frequency), "Get direct queue timestamp frequency");
            if (frequency == 0)
                throw new InvalidOperationException("The direct queue returned a zero timestamp frequency.");
            _frequency = frequency;
            MeasuresShadows = measureShadows;
            MeasuresSky = measureSky;

            var queryCount = measureSky ? ShadowQueryCount + 3 : measureShadows ? ShadowQueryCount : TerrainQueryCount;
            var description = new D3D12_QUERY_HEAP_DESC
            {
                Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_TIMESTAMP,
                Count = queryCount,
            };
            ID3D12QueryHeap* heap = null;
            Check(device->CreateQueryHeap(&description, __uuidof<ID3D12QueryHeap>(), (void**)&heap), "Create GPU timestamp heap");
            _heap = heap;
            try
            {
                _results = CreateResource(device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK,
                    Buffer(queryCount * sizeof(ulong)), D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal bool MeasuresShadows { get; }
        internal bool MeasuresSky { get; }
        internal ulong Frequency => _frequency;
        internal ulong Sequence { get; private set; }

        internal uint Stamp(ID3D12GraphicsCommandList* commands, ref ShadowTimingLayout layout)
        {
            var index = layout.QueryCount++;
            commands->EndQuery(_heap, Timestamp, index);
            return index;
        }

        internal void BeginTerrain(ID3D12GraphicsCommandList* commands, ref ShadowTimingLayout layout)
        {
            if (MeasuresShadows)
                layout.TerrainStart = Stamp(commands, ref layout);
            else
                commands->EndQuery(_heap, Timestamp, 0);
        }

        internal void EndTerrain(ID3D12GraphicsCommandList* commands, ref ShadowTimingLayout layout)
        {
            if (MeasuresShadows)
                Stamp(commands, ref layout);
            else
            {
                commands->EndQuery(_heap, Timestamp, 1);
                Resolve(commands, TerrainQueryCount);
            }
        }

        internal void EndFrame(ID3D12GraphicsCommandList* commands, ref ShadowTimingLayout layout)
        {
            layout.FrameEnd = Stamp(commands, ref layout);
            Resolve(commands, layout.QueryCount);
        }

        internal void Publish(ulong sequence, bool hasTerrain, in ShadowTimingLayout layout)
        {
            _submitted = layout;
            Sequence = MeasuresShadows || hasTerrain ? sequence : 0;
        }

        internal double ReadTerrainMicroseconds()
        {
            var ticks = Map(TerrainQueryCount);
            try
            {
                return Microseconds(ticks, 0, 1);
            }
            finally
            {
                Unmap();
            }
        }

        internal ShadowGpuTiming ReadShadowMicroseconds()
        {
            var layout = _submitted;
            var ticks = Map(layout.QueryCount);
            try
            {
                var shadowPasses = (layout.NearStart == 0 ? 0 : 1) + (layout.FarStart == 0 ? 0 : 1);
                return new(Microseconds(ticks, 0, layout.FrameEnd), OptionalPassMicroseconds(ticks, layout.TerrainStart),
                    OptionalPassMicroseconds(ticks, layout.NearStart), OptionalPassMicroseconds(ticks, layout.FarStart),
                    shadowPasses);
            }
            finally
            {
                Unmap();
            }
        }

        internal SkyGpuTiming ReadSkyMicroseconds()
        {
            var layout = _submitted;
            var ticks = Map(layout.QueryCount);
            try
            {
                return new(Microseconds(ticks, 0, layout.FrameEnd),
                    layout.SkyStart == 0 ? 0 : Microseconds(ticks, layout.SkyStart, layout.SkyEnd), layout.SkyStart != 0,
                    ticks[0], ticks[layout.FrameEnd], layout.SkyStart == 0 ? 0 : ticks[layout.SkyStart],
                    layout.SkyStart == 0 ? 0 : ticks[layout.SkyEnd], _frequency,
                    layout.SkyLookupComputeEnd != 0,
                    layout.SkyLookupComputeEnd == 0 ? 0 : Microseconds(ticks, layout.SkyStart, layout.SkyLookupComputeEnd),
                    layout.SkyLookupComputeEnd == 0 ? 0 : Microseconds(ticks, layout.SkyLookupComputeEnd, layout.SkyEnd));
            }
            finally
            {
                Unmap();
            }
        }

        private void Resolve(ID3D12GraphicsCommandList* commands, uint count) =>
            commands->ResolveQueryData(_heap, Timestamp, 0, count, _results, 0);

        private ulong* Map(uint count)
        {
            var range = new D3D12_RANGE { End = count * sizeof(ulong) };
            void* mapped = null;
            Check(_results->Map(0, &range, &mapped), "Map GPU timestamp results");
            return (ulong*)mapped;
        }

        private void Unmap()
        {
            D3D12_RANGE written = default;
            _results->Unmap(0, &written);
        }

        private double OptionalPassMicroseconds(ulong* ticks, uint start) =>
            start == 0 ? 0 : Microseconds(ticks, start, start + 1);

        private double Microseconds(ulong* ticks, uint start, uint end)
        {
            if (ticks[end] < ticks[start])
                throw new InvalidOperationException("GPU timestamps were recorded out of order.");
            return (ticks[end] - ticks[start]) * 1_000_000d / _frequency;
        }

        public void Dispose()
        {
            Release(_results);
            Release(_heap);
            _results = null;
            _heap = null;
        }
    }
}
