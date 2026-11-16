using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    private TerrainTiming? _terrainTiming;

    // An opt-in offscreen measurement uses one query pair and waits for retirement
    // before reusing it. The normal renderer never allocates these resources.
    internal void EnableTerrainTimingForTests()
    {
        if (_device == null || _queue == null || !IsIdle || _terrainTiming is not null)
            throw new InvalidOperationException("Terrain timing requires an idle device without an active query heap.");

        _terrainTiming = new(_device, _queue);
    }

    internal double ReadTerrainTimingForTests(ulong sequence)
    {
        if (sequence == 0 || _terrainTiming is not { } timing || timing.Sequence != sequence || !IsIdle)
            throw new InvalidOperationException("No retired terrain measurement matches this frame.");

        return timing.ReadMicroseconds();
    }

    private sealed class TerrainTiming : IDisposable
    {
        private const uint QueryCount = 2;
        private const int ByteCount = (int)QueryCount * sizeof(ulong);
        private readonly ulong _frequency;
        private ID3D12QueryHeap* _heap;
        private ID3D12Resource* _results;

        internal TerrainTiming(ID3D12Device* device, ID3D12CommandQueue* queue)
        {
            ulong frequency = 0;
            Check(queue->GetTimestampFrequency(&frequency), "Get direct queue timestamp frequency");
            if (frequency == 0)
                throw new InvalidOperationException("The direct queue returned a zero timestamp frequency.");
            _frequency = frequency;

            var description = new D3D12_QUERY_HEAP_DESC
            {
                Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_TIMESTAMP,
                Count = QueryCount,
            };
            ID3D12QueryHeap* heap = null;
            Check(device->CreateQueryHeap(&description, __uuidof<ID3D12QueryHeap>(), (void**)&heap), "Create terrain timestamp heap");
            _heap = heap;
            try
            {
                _results = CreateResource(device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK,
                    Buffer(ByteCount), D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal ulong Sequence { get; set; }

        internal void Begin(ID3D12GraphicsCommandList* commands) =>
            commands->EndQuery(_heap, D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP, 0);

        internal void End(ID3D12GraphicsCommandList* commands)
        {
            const D3D12_QUERY_TYPE type = D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP;
            commands->EndQuery(_heap, type, 1);
            commands->ResolveQueryData(_heap, type, 0, QueryCount, _results, 0);
        }

        internal double ReadMicroseconds()
        {
            var range = new D3D12_RANGE { End = ByteCount };
            void* mapped = null;
            Check(_results->Map(0, &range, &mapped), "Map terrain timestamp results");
            try
            {
                var ticks = (ulong*)mapped;
                if (ticks[1] < ticks[0])
                    throw new InvalidOperationException("Terrain timestamps were recorded out of order.");
                return (ticks[1] - ticks[0]) * 1_000_000d / _frequency;
            }
            finally
            {
                D3D12_RANGE written = default;
                _results->Unmap(0, &written);
            }
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
