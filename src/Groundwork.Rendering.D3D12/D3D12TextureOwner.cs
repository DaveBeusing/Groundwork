using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12;

/// <summary>Actual native allocations and logical upload traffic for the static texture route.</summary>
/// <remarks>Optional copies run in separate queue turns, so scene submissions themselves upload no texture bytes.</remarks>
public readonly record struct D3D12TextureDiagnostics(long ResidentAndPendingBytes, long StagingBytes,
    long NativeHighWaterBytes, ulong UploadedBytes, ulong LastCopyBytes);

public sealed partial class D3D12Renderer
{
    private readonly Lock _textureDiagnosticsGate = new();
    private D3D12TextureDiagnostics _textureDiagnostics;
    private D3D12TextureHandoff? _textures;
    private TextureOwner? _textureOwner;
    private D3D12TextureView _textureFrameView;
    private D3D12TextureTicket? _textureFrameTicket;
    private GpuTerrainMaterialSet? _textureFrameMaterials;
    internal Action<D3D12TextureTicket, WaitHandle>? BeforeTextureUploadForTests { private get; init; }

    /// <summary>Attach a static cooked texture stream before starting a volume renderer.</summary>
    public void AttachTextureHandoff(D3D12TextureHandoff stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (_lifecycle)
        {
            if (_volume is null || _threadStarted || Stopping || _textures is not null)
                throw new InvalidOperationException("Attach a fresh texture stream before starting a volume renderer.");
            stream.BindOwner(_volume.WorldIdentity, Wake, _volumeAppearanceGeometry ? _volumeEpoch : 0);
            _textures = stream;
        }
    }

    public D3D12TextureDiagnostics ReadTextureDiagnostics()
    {
        lock (_textureDiagnosticsGate)
            return _textureDiagnostics;
    }

    private void PublishTextureDiagnostics(GpuDevice gpu)
    {
        var current = gpu.TextureStatistics;
        lock (_textureDiagnosticsGate)
        {
            var copyBytes = current.UploadedBytes - _textureDiagnostics.UploadedBytes;
            _textureDiagnostics = new(current.NativeBytes, current.StagingBytes, current.NativePeakBytes,
                current.UploadedBytes, copyBytes == 0 ? _textureDiagnostics.LastCopyBytes : copyBytes);
        }
    }

    private bool RefreshTextures(GpuDevice gpu)
    {
        if (_textureOwner is not { } owner)
            return false;
        var changed = owner.Refresh(CurrentTextureView(), out var released);
        if (released)
            PublishTextureDiagnostics(gpu);
        return changed;
    }

    private D3D12TextureView CurrentTextureView()
    {
        if (_volume is null)
            return CurrentPlanetTextureView();
        lock (_volumeGate)
        {
            if (!VolumeAdmissionOpen || _volumeInstalled is not { } installed || !_volume!.IsCurrentFrame(installed))
                return default;
            var info = installed.Info;
            return new(_volume.WorldIdentity, info.View, info.TerrainRevision, info.Generation);
        }
    }

    private bool HasProtectedTextureCompetition()
    {
        if (_volume is null)
            return HasProtectedPlanetTextureCompetition();
        lock (_volumeGate)
            return _volumeStage is not null || _volumeStaged is not null ||
                _volumeInstalled is { } installed && Read().LastPresentedSequence < installed.Frame;
    }

    private bool ServiceOptionalTextures(GpuDevice gpu, WindowsPresentationTargetLease target, RendererWatchdog watchdog,
        FirstContentRetry? retry, ref RendererStage stage)
    {
        if (_textureOwner is not { } owner || retry.HasValue || ShouldStop(target) ||
            HasProtectedTextureCompetition())
            return true;

        var view = CurrentTextureView();
        if (owner.TryTake(view, out var transfer))
        {
            stage = RendererStage.TextureUpload;
            if (!watchdog.Begin(stage, null, _operationTimeout))
            {
                owner.ReleaseUnsubmitted(transfer.Ticket);
                return false;
            }
            if (BeforeTextureUploadForTests is { } before)
            {
                try { before(transfer.Ticket, _wake!); }
                catch
                {
                    owner.ReleaseUnsubmitted(transfer.Ticket);
                    throw;
                }
            }
            try
            {
                if (owner.TrySubmit(transfer, view, () => !ShouldStop(target) &&
                    !HasProtectedTextureCompetition() && CurrentTextureView() == view && watchdog.IsWithinDeadline))
                    return true;
            }
            finally
            {
                PublishTextureDiagnostics(gpu);
            }
            if (!watchdog.Complete())
                return false;
        }

        // An rejected or superseded head may spend the bounded turn without
        // submitting a copy. Preserve one wake only when work can still advance.
        if (owner.HasReady(CurrentTextureView()) && !HasProtectedTextureCompetition())
            Wake();
        return true;
    }

    private void CaptureFrameTexture(GpuDevice gpu, in RenderFrame frame)
    {
        if (!frame.Scene.HasValue || _textureOwner is not { } owner)
            return;
        var view = CurrentTextureView();
        if (gpu.ActiveMeshGeometry is { HasTerrainAppearance: true } && owner.CaptureTerrainMaterials(view) is { } materials)
        {
            _textureFrameView = view;
            _textureFrameMaterials = materials;
            gpu.SelectTerrainMaterials(materials, _textures);
            return;
        }
        if (owner.TryResolve(view, out var ticket, out var binding) &&
            gpu.SetFrameTexture(binding, view.View.RendererEpoch))
        {
            _textureFrameView = view;
            _textureFrameTicket = ticket;
        }
    }

    internal Lock? RecordedTextureGate => _textureFrameTicket.HasValue || _textureFrameMaterials is not null ? _textures!.Gate : null;

    internal bool RecordedTextureIsCurrent() => _textureFrameMaterials is { } materials
        ? CurrentTextureView() == materials.View && _textureOwner!.IsCurrent(materials)
        : _textureFrameTicket is not { } ticket || _textures!.TryGetDrawableResident(_textureFrameView, out var selected) && selected == ticket;

    internal void ClearFrameTexture(GpuDevice gpu)
    {
        if (!_textureFrameTicket.HasValue && _textureFrameMaterials is null)
            return;
        _textureFrameTicket = null;
        _textureFrameMaterials = null;
        _textureFrameView = default;
        gpu.SetFrameTexture(default, 0);
        gpu.SelectTerrainMaterials(null, null);
    }

    private sealed class TextureOwner(D3D12TextureHandoff stream, GpuDevice gpu)
    {
        private readonly Dictionary<D3D12TextureTicket, GpuTextureIdentity> _resident = new(GpuDevice.MaxTextureSlots);
        private D3D12TextureTicket? _copy;
        private GpuTextureIdentity _selected;
        private GpuTerrainMaterialSet? _materials;
        private D3D12TerrainTextureBinding[]? _appearanceScratch;
        private GpuTerrainMap[]? _mapScratch;

        internal bool HasSubmittedCopy => gpu.HasTextureCopy;
        internal bool HasReady(D3D12TextureView view) => stream.HasReady(view);

        internal bool TryResolve(D3D12TextureView view, out D3D12TextureTicket ticket, out GpuTextureIdentity binding)
        {
            binding = default;
            return stream.TryGetDrawableResident(view, out ticket) && _resident.TryGetValue(ticket, out binding);
        }

        internal bool Refresh(D3D12TextureView view, out bool released)
        {
            released = false;
            foreach (var ticket in _resident.Keys)
            {
                if (!stream.IsCurrent(ticket, view))
                {
                    ReleaseResident(ticket);
                    released = true;
                }
            }
            TryResolve(view, out _, out var next);
            var previousMaterials = _materials;
            _materials = gpu.ActiveMeshGeometry is { HasTerrainAppearance: true } ? CaptureTerrainMaterials(view) : null;
            var changed = next != _selected || !ReferenceEquals(previousMaterials, _materials);
            _selected = next;
            return changed;
        }

        internal GpuTerrainMaterialSet? CaptureTerrainMaterials(D3D12TextureView view)
        {
            var bindings = _appearanceScratch ??= new D3D12TerrainTextureBinding[GpuTerrainMaterialSet.Capacity];
            if (!stream.TryCaptureTerrainAppearance(view, bindings, out var palette, out var count))
                return null;
            var cut = bindings.AsSpan(0, count);
            var ready = 0;
            try
            {
                if (_materials is { } previous && previous.Matches(view, palette!, cut))
                    return previous;

                var maps = _mapScratch ??= new GpuTerrainMap[GpuTerrainMaterialSet.Capacity];
                foreach (ref readonly var target in cut)
                {
                    if (target.Resident is not { } ticket)
                        continue;
                    if (!_resident.TryGetValue(ticket, out var native) || !gpu.TryGetTerrainMap(target.Target, native, view.View.RendererEpoch, out var map))
                        return null;
                    maps[ready++] = map;
                }
                return new GpuTerrainMaterialSet(view, palette!, cut, maps.AsSpan(0, ready));
            }
            finally
            {
                cut.Clear();
                _mapScratch.AsSpan(0, ready).Clear();
            }
        }

        internal bool IsCurrent(GpuTerrainMaterialSet materials)
        {
            var bindings = _appearanceScratch!;
            if (!stream.TryCaptureTerrainAppearance(materials.View, bindings, out var palette, out var count))
                return false;
            var cut = bindings.AsSpan(0, count);
            var current = materials.Matches(materials.View, palette!, cut);
            cut.Clear();
            return current;
        }

        internal bool TryTake(D3D12TextureView view, out D3D12TextureTransfer transfer) =>
            stream.TryTake(view, out transfer);

        internal bool TrySubmit(in D3D12TextureTransfer transfer, D3D12TextureView view, Func<bool> authorize)
        {
            if (!gpu.TryGetTextureRequirements(transfer.Texture, out var requirements) ||
                requirements.PayloadBytes > transfer.Reservation.PayloadBytes ||
                requirements.StagingAllocationBytes > transfer.Reservation.StagingBytes ||
                requirements.TextureAllocationBytes > transfer.Reservation.NativeBytes)
            {
                stream.Release(transfer.Ticket);
                return false;
            }

            // Install the custody marker before the first operation that can execute copy
            // work. A thrown or missing Signal leaves the charge here through teardown.
            var ticket = transfer.Ticket;
            _copy = ticket;
            var couldRetry = false;
            if (stream.IsCurrent(ticket, view) &&
                gpu.TrySubmitTexture(transfer.Texture, view.View.RendererEpoch, in requirements, out _, () =>
                {
                    // Seal the transfer's authority and retained ticket. Later
                    // invalidation can prevent installation while the queue owns
                    // that transfer. The driver does not run under the producer lock.
                    lock (stream.Gate)
                    {
                        var current = stream.IsCurrent(ticket, view);
                        couldRetry = current && !authorize();
                        return current && !couldRetry;
                    }
                }))
            {
                if (!stream.MarkCopySubmitted(ticket))
                    throw new InvalidOperationException("A submitted texture copy lost its custody ticket.");
                return true;
            }
            if (!couldRetry || !stream.TryReturnToReady(ticket, view))
                stream.Release(ticket);
            _copy = null;
            return false;
        }

        internal void ReleaseUnsubmitted(D3D12TextureTicket ticket) => stream.Release(ticket);

        internal bool TryRetire(D3D12TextureView view)
        {
            if (!gpu.TryRetireTextureCopy(out var binding))
                return false;

            var ticket = _copy ?? throw new InvalidOperationException("A texture copy retired without a custody ticket.");
            if (stream.TryInstall(ticket, view, out var predecessor))
            {
                _resident.Add(ticket, binding);
                if (predecessor != default)
                    ReleaseResident(predecessor);
            }
            else
            {
                ReleaseNative(binding);
                stream.Release(ticket);
            }
            _copy = null;
            return true;
        }

        private void ReleaseResident(D3D12TextureTicket ticket)
        {
            if (!_resident.TryGetValue(ticket, out var binding))
                throw new InvalidOperationException("A resident texture has no native binding.");
            ReleaseNative(binding);
            _resident.Remove(ticket);
            stream.Release(ticket);
        }

        private void ReleaseNative(GpuTextureIdentity binding)
        {
            if (!gpu.TryReleaseTexture(binding))
                throw new InvalidOperationException("Cannot release a texture before native retirement.");
        }

        internal void SettleAfterDeviceDisposal()
        {
            if (_copy is { } copy)
                stream.Release(copy);
            _copy = null;
            foreach (var ticket in _resident.Keys)
                stream.Release(ticket);
            _resident.Clear();
        }
    }
}
