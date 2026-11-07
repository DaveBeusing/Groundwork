using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12;

public sealed partial class D3D12Renderer
{
    private readonly bool _planetTerrain;
    private WeakReference<PlanetTerrainLease>? _submittedTerrain;
    private PlanetTerrainStamp _submittedTerrainStamp;

    /// <summary>Creates a renderer whose terrain regions and camera frames are published together.</summary>
    public static D3D12Renderer CreateWithPlanetTerrain(WindowsHostModule window, IRuntimeOwnership ownership,
        D3D12DiagnosticsOptions diagnostics = default, bool useWarp = false,
        TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null) =>
        new(window, ownership, diagnostics, useWarp, startupTimeout, operationTimeout, null);

    internal D3D12Renderer(WindowsHostModule window, IRuntimeOwnership ownership,
        D3D12DiagnosticsOptions diagnostics, bool useWarp, TimeSpan? startupTimeout, TimeSpan? operationTimeout,
        GpuRetirementTestHooks? retirementHooks, Action<GpuDevice>? beforeMeshInstallation = null)
        : this(diagnostics, window, ownership, useWarp, startupTimeout, operationTimeout)
    {
        _planetTerrain = true;
        _admission = RendererFrameAdmission.CreateWithPairedRegionFrames();
        _initialGeometry = new RenderMeshRegion([new(RenderMesh.Create([], []), Vector3.Zero)]);
        _retirementHooks = retirementHooks;
        _beforeMeshInstallation = beforeMeshInstallation;
    }

    /// <summary>Copies a complete staged terrain lease and the camera in its local origin into a bounded submission.</summary>
    /// <remarks>Use one terrain coordinator per renderer. Submit and check from the terrain owner.
    /// After acceptance, keep the lease until its matching present is acknowledged or the renderer has stopped.
    /// Dispose a rejected lease when abandoning that submission.</remarks>
    public MeshReplacementSubmissionResult TrySubmitPlanetTerrain(PlanetTerrainLease lease, ulong sequence,
        Vector3 forward, Vector3 up, float verticalFieldOfView, float nearPlane, float farPlane,
        Vector4 tint, Vector4 clearColor) =>
        SubmitPlanetTerrain(lease, sequence, forward, up, verticalFieldOfView, nearPlane, farPlane, tint, clearColor, null);

    /// <summary>Submits terrain with RGBA8 colors sampled by canonical vertex key. Red occupies the lowest byte.</summary>
    /// <remarks>The sampler runs synchronously on the terrain owner. Use the same color for shared samples
    /// and a stable palette for the submitted generation. The scene tint multiplies the vertex colors.
    /// Keep the lease stable and avoid reentering submission from the sampler.</remarks>
    public MeshReplacementSubmissionResult TrySubmitPlanetTerrain(PlanetTerrainLease lease, ulong sequence,
        Vector3 forward, Vector3 up, float verticalFieldOfView, float nearPlane, float farPlane,
        Vector4 tint, Vector4 clearColor, Func<PlanetSampleKey, uint> sampleColor)
    {
        ArgumentNullException.ThrowIfNull(sampleColor);
        return SubmitPlanetTerrain(lease, sequence, forward, up, verticalFieldOfView, nearPlane, farPlane, tint, clearColor, sampleColor);
    }

    private MeshReplacementSubmissionResult SubmitPlanetTerrain(PlanetTerrainLease lease, ulong sequence,
        Vector3 forward, Vector3 up, float verticalFieldOfView, float nearPlane, float farPlane,
        Vector4 tint, Vector4 clearColor, Func<PlanetSampleKey, uint>? sampleColor)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!_planetTerrain)
            return MeshReplacementSubmissionResult.InvalidRegion;
        if (Read().Lifecycle is RendererLifecycle.Stopping or RendererLifecycle.Stopped)
            return MeshReplacementSubmissionResult.Closed;

        var frame = CreatePlanetFrame(lease, sequence, forward, up, verticalFieldOfView, nearPlane, farPlane, tint, clearColor);
        if (!frame.IsValid)
            return MeshReplacementSubmissionResult.InvalidRegion;

        var region = PackPlanetTerrain(lease, sampleColor);
        if (!lease.TryPinPresentation())
            return MeshReplacementSubmissionResult.InvalidRegion;

        var result = _admission.MeshRegionReplacement!.TrySubmitPairedRegion(lease.Generation, region, frame);
        if (result == MeshReplacementSubmissionResult.Accepted)
        {
            _submittedTerrain = new WeakReference<PlanetTerrainLease>(lease);
            _submittedTerrainStamp = lease.Stamp;
            Wake();
        }
        return result;
    }

    /// <summary>Checks a submitted lease and sequence before its owner acknowledges collision publication.</summary>
    public bool WasPlanetTerrainPresented(PlanetTerrainLease lease, ulong sequence) =>
        TryReadPresentedPlanetTerrain(lease, sequence, out _);

    /// <summary>Reports the stamp of the exact mesh and camera frame that successfully presented.</summary>
    public bool TryReadPresentedPlanetTerrain(PlanetTerrainLease lease, ulong sequence, out PlanetTerrainStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(lease);
        stamp = default;
        if (_submittedTerrain?.TryGetTarget(out var submitted) != true || !ReferenceEquals(submitted, lease) ||
            !_admission.MeshRegionReplacement!.WasPresented(_submittedTerrainStamp.Generation, sequence))
            return false;

        stamp = _submittedTerrainStamp;
        return true;
    }

    /// <summary>Updates the view from an active lease without rebuilding its geometry.</summary>
    public RenderSubmissionResult TrySubmitPlanetCamera(PlanetTerrainLease lease, ulong sequence,
        Vector3 forward, Vector3 up, float verticalFieldOfView, float nearPlane, float farPlane,
        Vector4 tint, Vector4 clearColor)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!_planetTerrain || lease.Stamp != _submittedTerrainStamp)
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

    internal static RenderMeshRegion PackPlanetTerrain(PlanetTerrainLease lease, Func<PlanetSampleKey, uint>? sampleColor = null)
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
            var colors = sampleColor is null ? [] : new uint[vertexCount];
            var vertexOffset = 0;
            var indexOffset = 0;
            for (var patch = first; patch < end; patch++)
            {
                var patchPositions = lease.GetPositions(patch);
                var patchIndices = lease.GetIndices(patch);
                patchPositions.CopyTo(positions.AsSpan(vertexOffset));
                if (sampleColor is not null)
                {
                    var samples = lease.GetSamples(patch);
                    for (var i = 0; i < samples.Length; i++)
                        colors[vertexOffset + i] = sampleColor(samples[i]);
                }
                foreach (var index in patchIndices)
                    indices[indexOffset++] = index + (uint)vertexOffset;
                vertexOffset += patchPositions.Length;
            }

            instances[batch] = new RenderMeshInstance(RenderMesh.Create(positions, indices, colors), Vector3.Zero);
        }

        return new RenderMeshRegion(instances);
    }
}
