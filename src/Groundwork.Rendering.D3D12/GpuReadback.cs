using System.Diagnostics;
using TerraFX.Interop.DirectX;
using static Groundwork.Rendering.D3D12.GpuNative;

namespace Groundwork.Rendering.D3D12;

internal sealed unsafe partial class GpuDevice
{
    /// <summary>Exercises the production frame pipeline without a window or swap chain.</summary>
    internal static OffscreenFrame RenderOffscreen(int width, int height, in RenderFrame frame, RenderScene? subsequent = null)
    {
        var gpu = new GpuDevice(warp: true);
        try
        {
            return RenderOffscreen(gpu, width, height, frame, subsequent);
        }
        finally
        {
            if (gpu.IsIdle || gpu.IsRemoved)
                gpu.Dispose();
        }
    }

    internal static OffscreenFrame RenderOffscreen(GpuDevice gpu, int width, int height, in RenderFrame frame, RenderScene? subsequent = null)
    {
        ID3D12Resource* color = null;
        GpuTarget? target = null;
        Readback? readback = null;
        try
        {
            color = CreateResource(gpu._device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
                Texture(width, height, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET),
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON);
            target = gpu.Attach(color, width, height);
            color = null;
            readback = new Readback(gpu, target);
            if (gpu.TryRender(target, frame, subsequent, readback, owner: null) != GpuFrameSubmitResult.Submitted)
                throw new InvalidOperationException("The offscreen frame could not be recorded and submitted.");

            long started = Stopwatch.GetTimestamp();
            while (!gpu.IsIdle)
            {
                if (gpu.IsRemoved)
                    throw new GpuNativeException("Read GPU frame after device removal", gpu.RemovedReason);
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException("GPU readback did not retire.");
                Thread.Sleep(1);
            }

            return readback.CopyPixels(width, height);
        }
        finally
        {
            // Do not release a test submission's resources while native work may still reference them.
            if (gpu.IsIdle || gpu.IsRemoved)
            {
                readback?.Dispose();
                target?.Dispose();
                Release(color);
            }
        }
    }

    private Readback? _presentReadback;
    private int _presentReadbackWidth;
    private int _presentReadbackHeight;

    internal GpuFrameSubmitResult TryRenderWithPresentReadback(GpuTarget target, in RenderFrame frame, D3D12Renderer owner)
    {
        if (_presentReadback is null || !_presentReadback.Matches(this, target))
        {
            // The owner retires the previous submission before acquiring another back buffer.
            var readback = new Readback(this, target);
            _presentReadback?.Dispose();
            _presentReadback = readback;
            _presentReadbackWidth = target.Width;
            _presentReadbackHeight = target.Height;
        }
        return TryRender(target, frame, null, _presentReadback, owner);
    }

    internal OffscreenFrame ReadRetiredPresent() =>
        _presentReadback!.CopyPixels(_presentReadbackWidth, _presentReadbackHeight);

    private sealed class Readback : IDisposable
    {
        private readonly GpuDevice _owner;
        private readonly ulong _depthGeneration;
        private readonly int _width;
        private readonly int _height;
        private ID3D12Resource* _buffer;
        private readonly D3D12_PLACED_SUBRESOURCE_FOOTPRINT _color;
        private readonly D3D12_PLACED_SUBRESOURCE_FOOTPRINT _depth;
        private readonly ulong _bytes;

        internal Readback(GpuDevice owner, GpuTarget target)
        {
            _owner = owner;
            _depthGeneration = owner._depthGeneration;
            _width = target.Width;
            _height = target.Height;
            var device = owner._device;
            var colorDescription = target.Buffer->GetDesc();
            var depthDescription = owner._depth->GetDesc();
            D3D12_PLACED_SUBRESOURCE_FOOTPRINT colorFootprint, depthFootprint;
            ulong colorBytes, depthBytes;
            device->GetCopyableFootprints(&colorDescription, 0, 1, 0, &colorFootprint, null, null, &colorBytes);
            var depthOffset = (colorBytes + 511) & ~511UL;
            device->GetCopyableFootprints(&depthDescription, 0, 1, depthOffset, &depthFootprint, null, null, &depthBytes);
            _color = colorFootprint;
            _depth = depthFootprint;
            _bytes = Math.Max(depthOffset + depthBytes, _depth.Offset + (ulong)_depth.Footprint.RowPitch * depthDescription.Height);
            if (!ContainsLogicalRows(_color, _depth.Offset) || !ContainsLogicalRows(_depth, _bytes))
                throw new InvalidOperationException("The native copy footprints do not cover the declared color and depth rows.");
            _buffer = CreateResource(device, D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK,
                Buffer(_bytes), D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST);
        }

        internal bool Matches(GpuDevice owner, GpuTarget target) =>
            _buffer != null && ReferenceEquals(_owner, owner) && _depthGeneration == owner._depthGeneration &&
            target.Width == _width && target.Height == _height;

        private bool ContainsLogicalRows(D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint, ulong end)
        {
            var rowBytes = checked((uint)_width * sizeof(uint));
            return footprint.Footprint.Width == (uint)_width && footprint.Footprint.Height == (uint)_height &&
                footprint.Footprint.Depth == 1 && footprint.Footprint.RowPitch >= rowBytes &&
                footprint.Offset <= end &&
                (ulong)(_height - 1) * footprint.Footprint.RowPitch + rowBytes <= end - footprint.Offset;
        }

        internal void Record(ID3D12GraphicsCommandList* commands, ID3D12Resource* color, ID3D12Resource* depth)
        {
            Transition(commands, color, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
            Transition(commands, depth, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE);
            Copy(commands, color, _color);
            Copy(commands, depth, _depth);
            Transition(commands, color, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PRESENT);
            Transition(commands, depth, D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE);
        }

        private void Copy(ID3D12GraphicsCommandList* commands, ID3D12Resource* source, D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint)
        {
            var src = new D3D12_TEXTURE_COPY_LOCATION
            {
                pResource = source,
                Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
            };
            var dst = new D3D12_TEXTURE_COPY_LOCATION
            {
                pResource = _buffer,
                Type = D3D12_TEXTURE_COPY_TYPE.D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
            };
            dst.PlacedFootprint = footprint;
            commands->CopyTextureRegion(&dst, 0, 0, 0, &src, null);
        }

        internal OffscreenFrame CopyPixels(int width, int height)
        {
            var colors = new byte[checked(width * height * 4)];
            var depths = new float[checked(width * height)];
            var range = new D3D12_RANGE { End = (nuint)_bytes };
            void* mapped = null;
            Check(_buffer->Map(0, &range, &mapped), "Map frame readback");
            try
            {
                for (int row = 0; row < height; row++)
                {
                    var color = (byte*)mapped + _color.Offset + row * _color.Footprint.RowPitch;
                    var depth = (byte*)mapped + _depth.Offset + row * _depth.Footprint.RowPitch;
                    // The portable readback pass declares packed texels only. Never expose the
                    // bytes between rows, the two planes, or the end of the native resource.
                    new ReadOnlySpan<byte>(color, width * 4).CopyTo(colors.AsSpan(row * width * 4, width * 4));
                    new ReadOnlySpan<float>(depth, width).CopyTo(depths.AsSpan(row * width, width));
                }
            }
            finally
            {
                var unwritten = new D3D12_RANGE();
                _buffer->Unmap(0, &unwritten);
            }
            return new OffscreenFrame(width, colors, depths);
        }

        public void Dispose()
        {
            Release(_buffer);
            _buffer = null;
        }
    }
}

internal sealed class OffscreenFrame(int width, byte[] colors, float[] depths)
{
    internal ReadOnlySpan<byte> ColorAt(int x, int y) => colors.AsSpan((y * width + x) * 4, 4);
    internal float DepthAt(int x, int y) => depths[y * width + x];
}
