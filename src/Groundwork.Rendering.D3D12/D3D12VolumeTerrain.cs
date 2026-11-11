using System.Collections.Immutable;
using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12;

/// <summary>Authoritative camera coordinates and the terrain view used to project them.</summary>
public readonly record struct VolumeCamera(VolumeViewIdentity View, PlanetVector Position, Vector3 Forward,
    Vector3 Up, float FieldOfView, float NearPlane, float FarPlane, Vector4 Tint, Vector4 ClearColor);

public sealed partial class D3D12Renderer
{
    private readonly VolumeStreamingCoordinator? _volume;
    private readonly ulong _volumeEpoch;
    private readonly Lock _volumeGate = new();
    private VolumeFrameRegistration? _volumeStaged;
    private VolumeFrameRegistration? _volumeInstalled;
    private VolumeMask? _volumeMask;
    private bool _volumeClosed;

    private sealed record VolumeMask(VolumeViewIdentity View, ulong Sequence);

    /// <summary>Creates the WP11 provider for a single streaming authority and renderer epoch.</summary>
    public static D3D12Renderer CreateWithVolumeTerrain(WindowsHostModule window, IRuntimeOwnership ownership,
        VolumeStreamingCoordinator terrain, ulong rendererEpoch, D3D12DiagnosticsOptions diagnostics = default,
        bool useWarp = false, TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null) =>
        new(window, ownership, terrain, rendererEpoch, diagnostics, useWarp, startupTimeout, operationTimeout, null);

    internal D3D12Renderer(WindowsHostModule window, IRuntimeOwnership ownership,
        VolumeStreamingCoordinator terrain, ulong rendererEpoch, D3D12DiagnosticsOptions diagnostics,
        bool useWarp, TimeSpan? startupTimeout, TimeSpan? operationTimeout, GpuRetirementTestHooks? hooks)
        : this(diagnostics, window, ownership, useWarp, startupTimeout, operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentOutOfRangeException.ThrowIfZero(rendererEpoch);
        _volume = terrain;
        _volumeEpoch = rendererEpoch;
        _retirementHooks = hooks;
        _admission = RendererFrameAdmission.CreateWithPairedRegionFrames();
        _initialGeometry = new RenderMeshRegion([new(RenderMesh.Create([], []), Vector3.Zero)]);
    }

    /// <summary>Requests a neutral mask. The provider confirms it after successful Present and queue retirement.</summary>
    public RenderSubmissionResult TrySubmitVolumeMask(VolumeViewIdentity view, ulong sequence, Vector4 clearColor)
    {
        RenderSubmissionResult result;
        lock (_volumeGate)
        {
            if (_volumeClosed)
                return RenderSubmissionResult.Closed;
            if (_volume is null || view.RendererEpoch != _volumeEpoch || !_volume.CanRequestMask(view))
                return RenderSubmissionResult.InvalidFrame;
            var previous = _volumeMask;
            _volumeMask = new(view, sequence);
            result = _admission.TrySubmitPairedMask(new(sequence, clearColor));
            if (result is not (RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced))
                _volumeMask = previous;
        }

        if (result is RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced)
            Wake();
        return result;
    }

    /// <summary>Checks presentation capacity before preparing the Core terrain swap.</summary>
    public PreparedBarrierPublication? TryPrepareVolumePublication(ulong generation, CandidateSet candidates,
        ImmutableArray<BatchDisposition> dispositions, out MeshReplacementSubmissionResult result,
        IWorldStateRoot? composedState = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_volumeGate)
        {
            result = MeshReplacementSubmissionResult.Closed;
            if (_volumeClosed || _volume is null)
                return null;
            result = MeshReplacementSubmissionResult.NotRunning;
            if (Read().Lifecycle != RendererLifecycle.Running)
                return null;
            result = MeshReplacementSubmissionResult.StaleGeneration;
            if (!_volume.TryReadPreparedGeometry(generation, out var info, out var triangles, out var credits) ||
                info.View.RendererEpoch != _volumeEpoch)
                return null;
            if (!FitsVolumeGeometry(triangles, NativeLimit(credits)))
            {
                result = MeshReplacementSubmissionResult.Backpressure;
                return null;
            }

            var prepared = _volume.TryPreparePublication(generation, candidates, dispositions, composedState);
            if (prepared is not null)
                result = MeshReplacementSubmissionResult.Accepted;
            return prepared;
        }
    }

    /// <summary>Queues a committed mesh and its exact camera together. The provider owns the registration.</summary>
    public MeshReplacementSubmissionResult TrySubmitVolumeTerrain(ulong generation, ulong sequence, in VolumeCamera camera)
    {
        MeshReplacementSubmissionResult result;
        lock (_volumeGate)
        {
            if (_volumeClosed)
                return MeshReplacementSubmissionResult.Closed;
            if (_volume is null || camera.View.RendererEpoch != _volumeEpoch ||
                !TryCreateVolumeFrame(camera, sequence, out var frame))
                return MeshReplacementSubmissionResult.InvalidRegion;
            if (Read().Lifecycle != RendererLifecycle.Running)
                return MeshReplacementSubmissionResult.NotRunning;
            if (_volumeStaged is not null)
                return MeshReplacementSubmissionResult.Backpressure;
            if (_volumeInstalled?.Info.Generation == generation)
                return MeshReplacementSubmissionResult.DuplicateGeneration;
            if (sequence <= Read().LastAcceptedSequence)
                return MeshReplacementSubmissionResult.StaleGeneration;

            var registration = _volume.TryRegisterFrame(generation, camera.View, sequence);
            if (registration is null)
                return MeshReplacementSubmissionResult.StaleGeneration;
            try
            {
                var region = PackVolumeTerrain(registration.Meshes, camera.View.Frame, NativeLimit(registration.Resources));
                if (region is null)
                    return MeshReplacementSubmissionResult.Backpressure;
                if (!_volume.IsCurrentFrame(registration))
                    return MeshReplacementSubmissionResult.StaleGeneration;

                Volatile.Write(ref _volumeStaged, registration);
                result = _admission.MeshRegionReplacement!.TrySubmitPairedRegion(generation, region, frame);
                if (result == MeshReplacementSubmissionResult.Accepted)
                    registration = null;
            }
            finally
            {
                if (registration is not null)
                {
                    Volatile.Write(ref _volumeStaged, null);
                    registration.Dispose();
                }
            }
        }

        if (result == MeshReplacementSubmissionResult.Accepted)
            Wake();
        return result;
    }

    /// <summary>Queues a camera update projected in the already presented generation's origin.</summary>
    public RenderSubmissionResult TrySubmitVolumeCamera(ulong generation, ulong sequence, in VolumeCamera camera)
    {
        RenderSubmissionResult result;
        lock (_volumeGate)
        {
            if (_volumeClosed)
                return RenderSubmissionResult.Closed;
            if (_volume is null || _volumeInstalled is not { } installed || installed.Info.Generation != generation ||
                installed.Info.View != camera.View || !_volume.IsCurrentFrame(installed) ||
                !TryCreateVolumeFrame(camera, sequence, out var frame))
                return RenderSubmissionResult.InvalidFrame;
            result = _admission.MeshRegionReplacement!.TrySubmitPairedCamera(generation, frame);
        }

        if (result is RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced)
            Wake();
        return result;
    }

    internal static bool TryCreateVolumeFrame(in VolumeCamera camera, ulong sequence, out RenderFrame frame)
    {
        frame = default;
        if (camera.View.Frame.Revision == 0)
            return false;
        Vector3 local;
        try
        {
            local = camera.View.Frame.ToLocal(camera.Position);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        frame = new(sequence, camera.ClearColor, new RenderScene(Matrix4x4.Identity, camera.Tint,
            Matrix4x4.CreateLookTo(local, camera.Forward, camera.Up), camera.FieldOfView, camera.NearPlane, camera.FarPlane));
        return frame.IsValid;
    }

    private static long NativeLimit(VolumeResources credits) =>
        Math.Min(GpuDevice.MaximumNativeMeshBytes / 2, Math.Min(credits.NativeBytes,
            Math.Min(credits.UploadBytes, credits.RetirementBytes)));

    private const int VolumeTrianglesPerMesh = RenderMesh.MaximumVertexCount / 3;
    private const int MaximumVolumeTriangles = VolumeTrianglesPerMesh * RenderMeshRegion.MaximumInstanceCount;

    private static bool FitsVolumeGeometry(int triangles, long nativeLimit)
    {
        // D3D12's default buffer alignment. The native allocator also checks the actual charge.
        var payload = (long)triangles * 3 * (16 + sizeof(uint));
        var aligned = (payload + 65535) & ~65535L;
        return triangles <= MaximumVolumeTriangles && aligned <= nativeLimit;
    }

    internal static RenderMeshRegion? PackVolumeTerrain(ReadOnlySpan<PlanetVolumeMesh> meshes,
        PlanetLocalFrame origin, long nativeLimit)
    {
        var count = 0;
        foreach (var mesh in meshes)
        {
            if (mesh.Triangles.Length > MaximumVolumeTriangles - count)
                return null;
            count += mesh.Triangles.Length;
        }

        if (!FitsVolumeGeometry(count, nativeLimit))
            return null;

        var instances = new RenderMeshInstance[Math.Max(1, (count + VolumeTrianglesPerMesh - 1) / VolumeTrianglesPerMesh)];
        var meshIndex = 0;
        var triangleIndex = 0;
        for (var batch = 0; batch < instances.Length; batch++)
        {
            var vertices = Math.Min(count, VolumeTrianglesPerMesh) * 3;
            var positions = new Vector3[vertices];
            var indices = new uint[vertices];
            var colors = new uint[vertices];
            for (var vertex = 0; vertex < vertices; vertex += 3)
            {
                while (triangleIndex == meshes[meshIndex].Triangles.Length)
                {
                    meshIndex++;
                    triangleIndex = 0;
                }

                var mesh = meshes[meshIndex];
                var triangle = mesh.Triangles[triangleIndex++];
                WriteVertex(triangle.A, vertex);
                WriteVertex(triangle.B, vertex + 1);
                WriteVertex(triangle.C, vertex + 2);

                void WriteVertex(VolumeVertex source, int index)
                {
                    var cell = mesh.Descriptor.CellSizeMeters;
                    positions[index] = origin.ToLocal(new PlanetVector(
                        (mesh.Origin.X + (double)source.Position.X) * cell,
                        (mesh.Origin.Y + (double)source.Position.Y) * cell,
                        (mesh.Origin.Z + (double)source.Position.Z) * cell));
                    indices[index] = (uint)index;
                    colors[index] = (source.Material % 3) switch
                    {
                        0 => 0xff4688b8u,
                        1 => 0xff50905au,
                        _ => 0xffa06b63u
                    };
                }
            }

            instances[batch] = new(RenderMesh.Create(positions, indices, colors), Vector3.Zero);
            count -= vertices / 3;
        }
        return new RenderMeshRegion(instances);
    }

    private bool CanInstallVolume(ulong generation) => _volume is null ||
        Volatile.Read(ref _volumeStaged) is { } staged && staged.Info.Generation == generation && _volume.IsCurrentFrame(staged);

    private void SettleVolumeCandidate(ulong generation, bool installed, bool retry)
    {
        if (_volume is null || retry)
            return;
        lock (_volumeGate)
        {
            if (_volumeStaged is not { } staged || staged.Info.Generation != generation)
                return;
            Volatile.Write(ref _volumeStaged, null);
            if (installed)
            {
                _volumeInstalled?.Dispose();
                _volumeInstalled = staged;
            }
            else
                staged.Dispose();
        }
    }

    private bool CanDrawVolume(in RenderFrame frame)
    {
        if (_volume is null)
            return true;
        lock (_volumeGate)
        {
            return !_volumeClosed && _meshRuntimeFence is { IsOpen: true } && (frame.Scene.HasValue
                ? _volumeInstalled is { } installed && _volume.IsCurrentFrame(installed)
                : _volumeMask is { } mask && mask.Sequence == frame.Sequence && mask.View == _volume.View);
        }
    }

    private void RecordRetiredVolumeFrame(in RenderFrame frame)
    {
        lock (_volumeGate)
        {
            if (!CanDrawVolume(frame) || Read().LastPresentedSequence >= frame.Sequence)
                return;
            if (frame.Scene.HasValue)
            {
                var installed = _volumeInstalled!;
                if (frame.Sequence == installed.Frame && !_volume!.AcknowledgeFrame(installed))
                    return;
                _admission.MeshRegionReplacement!.RecordPresented(frame.Sequence, installed.Info.Generation);
            }
            else
            {
                var mask = _volumeMask!;
                if (_volume!.ConfirmMask(mask.View))
                    _admission.RecordPresented(frame.Sequence);
            }
        }
    }

    private void CloseVolumeAdmission()
    {
        if (_volume is null)
            return;
        lock (_volumeGate)
        {
            if (_volumeClosed)
                return;
            _volumeClosed = true;
            _volume.InvalidateRenderer(_volumeEpoch);
        }
    }

    private void ReleaseVolumeCustody()
    {
        lock (_volumeGate)
        {
            _volumeStaged?.Dispose();
            _volumeStaged = null;
            _volumeInstalled?.Dispose();
            _volumeInstalled = null;
        }
    }
}
