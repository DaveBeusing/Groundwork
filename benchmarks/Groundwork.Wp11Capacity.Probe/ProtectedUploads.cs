using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_STATES;

unsafe sealed partial class Gpu
{
    internal sealed class ProtectedUploads : IDisposable
    {
        private readonly Slot[] _slots;
        private ID3D12Resource* _upload;
        private byte* _mapped;

        internal ProtectedUploads(Gpu gpu, ulong editBytes, ulong recoveryBytes)
        {
            _slots = [new(editBytes), new(recoveryBytes)];
            try
            {
                var uploadBytes = Math.Max(editBytes, recoveryBytes);
                UploadBytes = gpu.AllocationSize(uploadBytes);
                _upload = gpu.Buffer(uploadBytes, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ);
                D3D12_RANGE empty = default;
                void* mapped = null;
                Check(_upload->Map(0, &empty, &mapped), "map protected staging");
                _mapped = (byte*)mapped;
                foreach (var slot in _slots)
                {
                    slot.Geometry = gpu.Buffer(slot.Bytes, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COMMON);
                    GeometryBytes += gpu.AllocationSize(slot.Bytes);
                }
            }
            catch { Dispose(); throw; }
        }

        internal ulong GeometryBytes { get; }
        internal ulong UploadBytes { get; }

        internal Buffers Stage(int index, uint marker, ReadOnlySpan<byte> payload)
        {
            var slot = _slots[index];
            if (_slots.Any(candidate => candidate.Staging) || slot.Leased || (ulong)payload.Length + 8 != slot.Bytes)
                throw new InvalidOperationException("Protected GPU slot is busy or has the wrong payload size.");

            var words = new Span<uint>(_mapped, checked((int)slot.Bytes / sizeof(uint)));
            words[0] = words[^1] = marker;
            payload.CopyTo(new Span<byte>(_mapped + 4, payload.Length));
            slot.Leased = true;
            slot.Staging = true;
            return new Buffers(slot.Geometry, _upload, slot.Bytes, marker,
                System.Runtime.InteropServices.MemoryMarshal.Read<uint>(payload),
                System.Runtime.InteropServices.MemoryMarshal.Read<uint>(payload[^4..]),
                () => slot.Staging = false,
                () => { slot.Leased = false; slot.Staging = false; });
        }

        public void Dispose()
        {
            foreach (var slot in _slots)
                Release(slot.Geometry);
            if (_mapped != null)
                _upload->Unmap(0, null);
            Release(_upload);
        }

        private sealed class Slot(ulong bytes)
        {
            internal ulong Bytes { get; } = bytes;
            internal ID3D12Resource* Geometry;
            internal bool Leased;
            internal bool Staging;
        }
    }
}
