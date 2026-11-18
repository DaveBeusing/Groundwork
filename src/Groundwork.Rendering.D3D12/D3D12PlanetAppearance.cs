using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12;

public sealed partial class D3D12Renderer
{
    // Read the accepted stamp and the admission generation under one cut. The lock
    // order is appearance before frame admission; neither calls the texture stream.
    private readonly Lock _planetAppearanceGate = new();
    private PlanetTextureDomain? _planetTextureDomain;
    private PlanetAppearanceRegistration? _submittedPlanetAppearance;

    private sealed record PlanetTextureDomain(PlanetTerrainCoordinator Coordinator, PlanetDescriptor Descriptor, ulong RendererEpoch)
    {
        internal D3D12TextureView For(PlanetTerrainStamp stamp) => new(Descriptor.WorldId,
            new(RendererEpoch, stamp.Generation, stamp.Frame), Math.Max(1UL, stamp.EditRevision), stamp.Generation);
    }

    private readonly record struct PlanetAppearanceRegistration(D3D12TextureView View, RenderSunlight? Sunlight);

    /// <summary>Attaches a fresh cooked texture stream to a planet terrain renderer before it starts.</summary>
    /// <remarks>The descriptor identifies the one terrain coordinator's world and planet.
    /// Use a new positive epoch for a replacement renderer in that world.</remarks>
    public void AttachPlanetTextureHandoff(D3D12TextureHandoff stream, PlanetTerrainCoordinator coordinator,
        PlanetDescriptor descriptor, ulong rendererEpoch)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentOutOfRangeException.ThrowIfZero(rendererEpoch);
        lock (_lifecycle)
        {
            if (!_planetTerrain || _threadStarted || Stopping || _textures is not null)
                throw new InvalidOperationException("Attach a fresh texture stream before starting a planet terrain renderer.");

            var domain = new PlanetTextureDomain(coordinator, descriptor, rendererEpoch);
            stream.BindOwner(descriptor.WorldId, Wake, rendererEpoch);
            Volatile.Write(ref _planetTextureDomain, domain);
            _textures = stream;
        }
    }

    /// <summary>Returns the texture publication identity of the accepted appearance lease.</summary>
    /// <remarks>A producer may publish for the returned cut while its first frame is pending.
    /// Native uploads wait until that generation first presents. The unedited baseline maps
    /// to the positive texture revision 1. Generation and origin also identify the cut.</remarks>
    public bool TryGetExpectedPlanetTextureView(PlanetTerrainLease lease, out D3D12TextureView view)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_planetAppearanceGate)
        {
            view = default;
            if (_submittedPlanetAppearance is not { } accepted || !PlanetTextureAdmissionOpen ||
                _planetTextureDomain?.Coordinator.OwnsLease(lease) != true ||
                _submittedTerrain?.TryGetTarget(out var submitted) != true || !ReferenceEquals(lease, submitted))
                return false;

            view = accepted.View;
            return true;
        }
    }

    private bool PlanetTextureAdmissionOpen => _meshRuntimeFence is { IsOpen: true } && Read().Lifecycle == RendererLifecycle.Running;

    private D3D12TextureView CurrentPlanetTextureView()
    {
        if (_sessionOrbitTextureView.IsValid)
            return PlanetTextureAdmissionOpen && Volatile.Read(ref _sessionOrbitPresented) != 0
                ? _sessionOrbitTextureView : default;

        lock (_planetAppearanceGate)
        {
            if (_submittedPlanetAppearance is not { } accepted || !PlanetTextureAdmissionOpen)
                return default;

            var status = _admission.MeshRegionReplacement!.ReadMeshReplacement();
            return status.DesiredGeneration == accepted.View.CoverageGeneration && status.InstalledGeneration == status.DesiredGeneration
                ? accepted.View : default;
        }
    }

    private bool HasProtectedPlanetTextureCompetition()
    {
        if (_sessionOrbitTextureView.IsValid)
            return Volatile.Read(ref _sessionOrbitPresented) == 0;

        if (_planetTextureDomain is null || !PlanetTextureAdmissionOpen)
            return false;

        var status = _admission.MeshRegionReplacement!.ReadMeshReplacement();
        return status.DesiredGeneration != 0 &&
            (status.DesiredGeneration != status.InstalledGeneration || status.InstalledGeneration != status.LastPresentedGeneration);
    }
}
