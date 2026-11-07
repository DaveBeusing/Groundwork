using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12;

public sealed partial class D3D12Renderer
{
    private readonly bool _planetTerrain;
    private WeakReference<PlanetTerrainLease>? _submittedTerrain;

    /// <summary>Creates a renderer whose terrain regions and camera frames are published together.</summary>
    public static D3D12Renderer CreateWithPlanetTerrain(WindowsHostModule window, IRuntimeOwnership ownership,
        D3D12DiagnosticsOptions diagnostics = default, bool useWarp = false,
        TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null) =>
        new(window, ownership, diagnostics, useWarp, startupTimeout, operationTimeout, null);

    internal D3D12Renderer(WindowsHostModule window, IRuntimeOwnership ownership,
        D3D12DiagnosticsOptions diagnostics, bool useWarp, TimeSpan? startupTimeout, TimeSpan? operationTimeout,
        GpuRetirementTestHooks? retirementHooks)
        : this(diagnostics, window, ownership, useWarp, startupTimeout, operationTimeout)
    {
        _planetTerrain = true;
        _admission = RendererFrameAdmission.CreateWithPairedRegionFrames();
        _initialGeometry = new RenderMeshRegion([new(RenderMesh.Create([], []), Vector3.Zero)]);
        _retirementHooks = retirementHooks;
    }

    /// <summary>Copies a complete staged terrain lease and the camera in its local origin into a bounded submission.</summary>
    /// <remarks>Use one terrain coordinator per renderer. Submit and check from the terrain owner.
    /// After acceptance, keep the lease until its matching present is acknowledged or the renderer has stopped.
    /// Dispose a rejected lease when abandoning that submission.</remarks>
    public MeshReplacementSubmissionResult TrySubmitPlanetTerrain(PlanetTerrainLease lease, ulong sequence,
        Vector3 forward, Vector3 up, float verticalFieldOfView, float nearPlane, float farPlane,
        Vector4 tint, Vector4 clearColor)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!_planetTerrain)
            return MeshReplacementSubmissionResult.InvalidRegion;
        if (Read().Lifecycle is RendererLifecycle.Stopping or RendererLifecycle.Stopped)
            return MeshReplacementSubmissionResult.Closed;

        var frame = CreatePlanetFrame(lease, sequence, forward, up, verticalFieldOfView, nearPlane, farPlane, tint, clearColor);
        if (!frame.IsValid || !lease.TryPinPresentation())
            return MeshReplacementSubmissionResult.InvalidRegion;

        var result = _admission.MeshRegionReplacement!.TrySubmitPairedRegion(lease.Generation, PackPlanetTerrain(lease), frame);
        if (result == MeshReplacementSubmissionResult.Accepted)
        {
            _submittedTerrain = new WeakReference<PlanetTerrainLease>(lease);
            Wake();
        }
        return result;
    }

    /// <summary>Checks a submitted lease and sequence before its owner acknowledges collision publication.</summary>
    public bool WasPlanetTerrainPresented(PlanetTerrainLease lease, ulong sequence)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return _submittedTerrain?.TryGetTarget(out var submitted) == true && ReferenceEquals(submitted, lease) &&
            _admission.MeshRegionReplacement!.WasPresented(lease.Generation, sequence);
    }

    /// <summary>Updates the view from an active lease without rebuilding its geometry.</summary>
    public RenderSubmissionResult TrySubmitPlanetCamera(PlanetTerrainLease lease, ulong sequence,
        Vector3 forward, Vector3 up, float verticalFieldOfView, float nearPlane, float farPlane,
        Vector4 tint, Vector4 clearColor)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!_planetTerrain)
            return RenderSubmissionResult.InvalidFrame;

        var frame = CreatePlanetFrame(lease, sequence, forward, up, verticalFieldOfView, nearPlane, farPlane, tint, clearColor);
        var result = _admission.MeshRegionReplacement!.TrySubmitPairedCamera(lease.Generation, frame);
        if (result is RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced)
            Wake();
        return result;
    }

    internal static RenderFrame CreatePlanetFrame(PlanetTerrainLease lease, ulong sequence, Vector3 forward, Vector3 up,
        float verticalFieldOfView, float nearPlane, float farPlane, Vector4 tint, Vector4 clearColor) =>
        new(sequence, clearColor, new RenderScene(Matrix4x4.Identity, tint,
            Matrix4x4.CreateLookTo(lease.CameraPosition, forward, up), verticalFieldOfView, nearPlane, farPlane));

    internal static RenderMeshRegion PackPlanetTerrain(PlanetTerrainLease lease)
    {
        const int patchesPerMesh = 64;
        var instances = new RenderMeshInstance[(lease.MeshCount + patchesPerMesh - 1) / patchesPerMesh];
        for (var batch = 0; batch < instances.Length; batch++)
        {
            var first = batch * patchesPerMesh;
            var end = Math.Min(first + patchesPerMesh, lease.MeshCount);
            var vertexCount = 0;
            var indexCount = 0;
            for (var patch = first; patch < end; patch++)
            {
                vertexCount += lease.GetPositions(patch).Length;
                indexCount += lease.GetIndices(patch).Length;
            }

            var positions = new Vector3[vertexCount];
            var indices = new uint[indexCount];
            var vertexOffset = 0;
            var indexOffset = 0;
            for (var patch = first; patch < end; patch++)
            {
                var patchPositions = lease.GetPositions(patch);
                var patchIndices = lease.GetIndices(patch);
                patchPositions.CopyTo(positions.AsSpan(vertexOffset));
                foreach (var index in patchIndices)
                    indices[indexOffset++] = index + (uint)vertexOffset;
                vertexOffset += patchPositions.Length;
            }

            instances[batch] = new RenderMeshInstance(RenderMesh.Create(positions, indices), Vector3.Zero);
        }

        return new RenderMeshRegion(instances);
    }
}
