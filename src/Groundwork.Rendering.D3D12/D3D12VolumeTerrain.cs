using System.Collections.Immutable;
using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12;

/// <summary>Authoritative camera coordinates and the terrain view used to project them.</summary>
public readonly record struct VolumeCamera(VolumeViewIdentity View, PlanetVector Position, Vector3 Forward,
    Vector3 Up, float FieldOfView, float NearPlane, float FarPlane, Vector4 Tint, Vector4 ClearColor)
{
    /// <summary>Optional view-depth fog for this camera submission.</summary>
    public RenderFog? Fog { get; init; }

    /// <summary>Optional directional sunlight for volume terrain.</summary>
    public RenderSunlight? Sunlight { get; init; }

    /// <summary>Enables near terrain shadow maps for this sunlit camera.</summary>
    public bool TerrainShadows { get; init; }

    /// <summary>Optional canonical sky for this volume view.</summary>
    public RenderSky? Sky { get; init; }
}

/// <summary>Returns packed RGBA8 (red in the low byte), or null to reject the candidate.</summary>
/// <remarks>Use an immutable, thread-safe policy with deterministic, non-blocking and allocation-free evaluation.</remarks>
public delegate uint? VolumeTerrainColorEvaluator(PlanetVector positionMeters, ushort material);

public sealed partial class D3D12Renderer
{
    private readonly VolumeStreamingCoordinator? _volume;
    private readonly ulong _volumeEpoch;
    private readonly VolumeTerrainColorEvaluator? _volumeColors;
    private readonly VolumeRendererAuthority? _volumeAuthority;
    private readonly Lock _volumeGate = new();
    private bool _volumePacking;
    private VolumeStage? _volumeStage;
    private VolumeFrameRegistration? _volumeStaged;
    private VolumeFrameRegistration? _volumeInstalled;
    private VolumeMask? _volumeMask;
    private VolumeMask? _volumeConfirmedMask;
    private int _volumeClosed;
    private bool VolumeAdmissionOpen => Volatile.Read(ref _volumeClosed) == 0 && _meshRuntimeFence is { IsOpen: true };

    private sealed record VolumeMask(VolumeViewIdentity View, ulong Sequence);

    private sealed class VolumeStage(VolumeStreamingLease lease, RenderMeshRegion region, long nativeLimit)
    {
        public VolumeStreamingLease Lease { get; } = lease;
        public RenderMeshRegion Region { get; } = region;
        public long NativeLimit { get; } = nativeLimit;
        // Only the render owner creates, transfers or disposes this handle.
        public GpuDevice.MeshGeometry? Native { get; set; }
        public VolumeFrameRegistration? Registration { get; set; }
        public bool Rejected { get; set; }
    }

    /// <summary>Creates the WP11 provider for a single streaming authority and renderer epoch.</summary>
    public static D3D12Renderer CreateWithVolumeTerrain(WindowsHostModule window, IRuntimeOwnership ownership,
        VolumeStreamingCoordinator terrain, ulong rendererEpoch, D3D12DiagnosticsOptions diagnostics = default,
        bool useWarp = false, TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null) =>
        new(window, ownership, terrain, rendererEpoch, diagnostics, useWarp, startupTimeout, operationTimeout, null);

    /// <summary>Creates a volume renderer with a fixed terrain color policy.</summary>
    public static D3D12Renderer CreateWithColoredVolumeTerrain(WindowsHostModule window, IRuntimeOwnership ownership,
        VolumeStreamingCoordinator terrain, ulong rendererEpoch, VolumeTerrainColorEvaluator colorEvaluator,
        D3D12DiagnosticsOptions diagnostics = default, bool useWarp = false,
        TimeSpan? startupTimeout = null, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(colorEvaluator);
        return new(window, ownership, terrain, rendererEpoch, diagnostics, useWarp, startupTimeout, operationTimeout, null, colorEvaluator);
    }

    internal D3D12Renderer(WindowsHostModule window, IRuntimeOwnership ownership,
        VolumeStreamingCoordinator terrain, ulong rendererEpoch, D3D12DiagnosticsOptions diagnostics,
        bool useWarp, TimeSpan? startupTimeout, TimeSpan? operationTimeout, GpuRetirementTestHooks? hooks,
        VolumeTerrainColorEvaluator? colorEvaluator = null)
        : this(diagnostics, window, ownership, useWarp, startupTimeout, operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentOutOfRangeException.ThrowIfZero(rendererEpoch);
        _volumeAuthority = terrain.TryBindRenderer(rendererEpoch)
            ?? throw new InvalidOperationException("The renderer epoch must be fresh, held and exclusively bound.");
        _volume = terrain;
        _volumeEpoch = rendererEpoch;
        _volumeColors = colorEvaluator;
        _retirementHooks = hooks;
        _admission = RendererFrameAdmission.CreateWithPairedRegionFrames();
        _initialGeometry = new RenderMeshRegion([new(RenderMesh.Create([], []), Vector3.Zero)]);
    }

    /// <summary>Requests a neutral mask. The provider confirms it after successful Present and queue retirement.</summary>
    public RenderSubmissionResult TrySubmitVolumeMask(VolumeViewIdentity view, ulong sequence, Vector4 clearColor) =>
        TrySubmitVolumeMask(view, sequence, clearColor, null);

    /// <summary>Presents a sky or clear color while terrain publication is held.</summary>
    public RenderSubmissionResult TrySubmitVolumeMask(VolumeViewIdentity view, ulong sequence, Vector4 clearColor, RenderSky? sky)
    {
        RenderSubmissionResult result;
        lock (_volumeGate)
        {
            if (!VolumeAdmissionOpen)
                return RenderSubmissionResult.Closed;
            if (_volume is null || view.RendererEpoch != _volumeEpoch || !_volume.CanRequestMask(view))
                return RenderSubmissionResult.InvalidFrame;
            var previous = _volumeMask;
            _volumeMask = new(view, sequence);
            result = _admission.TrySubmitPairedMask(new(sequence, clearColor) { Sky = sky });
            if (result is not (RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced))
                _volumeMask = previous;
        }

        if (result is RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced)
            Wake();
        return result;
    }

    /// <summary>Reserves and packs one private candidate, then schedules its native preparation.</summary>
    public MeshReplacementSubmissionResult TryStageVolumeTerrain(ulong generation)
    {
        VolumeStreamingLease? lease;
        VolumeResources credits;
        long packingBytes;
        lock (_volumeGate)
        {
            if (!VolumeAdmissionOpen)
                return MeshReplacementSubmissionResult.Closed;
            if (_volume is null || Read().Lifecycle != RendererLifecycle.Running)
                return MeshReplacementSubmissionResult.NotRunning;
            if (_volumePacking || _volumeStage is not null || _volumeStaged is not null)
                return MeshReplacementSubmissionResult.Backpressure;
            lease = _volume.TryAcquirePreparedGeometry(_volumeAuthority!, generation, out credits, out packingBytes);
            if (lease is null)
                return MeshReplacementSubmissionResult.StaleGeneration;
            _volumePacking = true;
        }

        try
        {
            if (lease.Info.View.RendererEpoch != _volumeEpoch)
                return MeshReplacementSubmissionResult.StaleGeneration;

            var nativeLimit = NativeLimit(credits);
            var region = PackVolumeTerrain(lease.Meshes, lease.Info.View.Frame, nativeLimit, packingBytes, _volumeColors, out var colorRejected);
            if (region is null)
                return colorRejected ? MeshReplacementSubmissionResult.InvalidRegion : MeshReplacementSubmissionResult.Backpressure;
            lock (_volumeGate)
            {
                if (!VolumeAdmissionOpen || !_volume.IsCurrentPresentationCandidate(lease.Info))
                    return MeshReplacementSubmissionResult.StaleGeneration;
                _volumeStage = new(lease, region, nativeLimit);
                lease = null;
            }

            Wake();
            return MeshReplacementSubmissionResult.Accepted;
        }
        catch (ArgumentException)
        {
            // A valid lattice need not fit a particular float render origin.
            return MeshReplacementSubmissionResult.InvalidRegion;
        }
        finally
        {
            lease?.Dispose();
            lock (_volumeGate)
                _volumePacking = false;
        }
    }

    /// <summary>Reports whether the owner has actually allocated and filled the private GPU buffer.</summary>
    public bool IsVolumeTerrainReady(ulong generation)
    {
        lock (_volumeGate)
            return VolumeAdmissionOpen && _volumeStage is { Native: not null } stage &&
                stage.Lease.Info.Generation == generation && _volume!.IsCurrentPresentationCandidate(stage.Lease.Info);
    }

    /// <summary>Prepares the Core swap after native preparation and a retired mask for that exact view.</summary>
    public PreparedBarrierPublication? TryPrepareVolumePublication(ulong generation, CandidateSet candidates,
        ImmutableArray<BatchDisposition> dispositions, out MeshReplacementSubmissionResult result,
        IWorldStateRoot? composedState = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_volumeGate)
        {
            result = MeshReplacementSubmissionResult.Closed;
            if (!VolumeAdmissionOpen || _volume is null)
                return null;
            result = MeshReplacementSubmissionResult.Backpressure;
            if (!IsVolumeTerrainReady(generation) || _volumeConfirmedMask?.View != _volumeStage!.Lease.Info.View)
                return null;

            result = MeshReplacementSubmissionResult.StaleGeneration;
            var prepared = _volume.TryPreparePublication(generation, candidates, dispositions, composedState, _volumeAuthority);
            if (prepared is not null)
                result = MeshReplacementSubmissionResult.Accepted;
            return prepared;
        }
    }

    /// <summary>Queues the prepared, committed generation and exact camera together.</summary>
    public MeshReplacementSubmissionResult TrySubmitVolumeTerrain(ulong generation, ulong sequence, in VolumeCamera camera)
    {
        MeshReplacementSubmissionResult result;
        lock (_volumeGate)
        {
            if (!VolumeAdmissionOpen)
                return MeshReplacementSubmissionResult.Closed;
            if (_volume is null || camera.View.RendererEpoch != _volumeEpoch ||
                !TryCreateVolumeFrame(camera, sequence, out var frame))
                return MeshReplacementSubmissionResult.InvalidRegion;
            if (Read().Lifecycle != RendererLifecycle.Running)
                return MeshReplacementSubmissionResult.NotRunning;
            if (_volumeInstalled?.Info.Generation == generation)
                return MeshReplacementSubmissionResult.DuplicateGeneration;
            if (_volumeStaged is not null)
                return MeshReplacementSubmissionResult.Backpressure;
            if (_volumeStage is not { Native: not null } stage || stage.Lease.Info.Generation != generation ||
                stage.Lease.Info.View != camera.View || sequence <= Read().LastAcceptedSequence)
                return MeshReplacementSubmissionResult.StaleGeneration;

            var registration = _volume.TryRegisterPreparedFrame(_volumeAuthority!, stage.Lease, sequence);
            if (registration is null)
                return MeshReplacementSubmissionResult.StaleGeneration;
            stage.Registration = registration;
            Volatile.Write(ref _volumeStaged, registration);
            result = _admission.MeshRegionReplacement!.TrySubmitPairedRegion(generation, stage.Region, frame);
            if (result != MeshReplacementSubmissionResult.Accepted)
            {
                stage.Rejected = true;
                Volatile.Write(ref _volumeStaged, null);
            }
        }

        Wake();
        return result;
    }

    /// <summary>Queues a camera update projected in the already presented generation's origin.</summary>
    public RenderSubmissionResult TrySubmitVolumeCamera(ulong generation, ulong sequence, in VolumeCamera camera)
    {
        RenderSubmissionResult result;
        lock (_volumeGate)
        {
            if (!VolumeAdmissionOpen)
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
            Matrix4x4.CreateLookTo(local, camera.Forward, camera.Up), camera.FieldOfView, camera.NearPlane, camera.FarPlane)
        {
            PropOrigin = new(camera.View.Frame.Origin.X, camera.View.Frame.Origin.Y, camera.View.Frame.Origin.Z),
            PropEye = new(camera.Position.X, camera.Position.Y, camera.Position.Z),
            Fog = camera.Fog,
            Sunlight = camera.Sunlight,
            ShadowOrigin = camera.TerrainShadows ? new(camera.View.Frame.Origin.X,
                camera.View.Frame.Origin.Y, camera.View.Frame.Origin.Z) : null,
        }) { Sky = camera.Sky };
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
        var payload = (long)triangles * 3 * (20 + sizeof(uint));
        var aligned = (payload + 65535) & ~65535L;
        return triangles <= MaximumVolumeTriangles && aligned <= nativeLimit;
    }

    internal static RenderMeshRegion? PackVolumeTerrain(ReadOnlySpan<PlanetVolumeMesh> meshes,
        PlanetLocalFrame origin, long nativeLimit, long packingBytes = long.MaxValue) =>
        PackVolumeTerrain(meshes, origin, nativeLimit, packingBytes, null, out _);

    internal static RenderMeshRegion? PackVolumeTerrain(ReadOnlySpan<PlanetVolumeMesh> meshes,
        PlanetLocalFrame origin, long nativeLimit, long packingBytes, VolumeTerrainColorEvaluator? colorEvaluator,
        out bool colorRejected)
    {
        colorRejected = false;
        var count = 0;
        foreach (var mesh in meshes)
        {
            if (mesh.Triangles.Length > MaximumVolumeTriangles - count)
                return null;
            count += mesh.Triangles.Length;
        }

        // Immutable positions, indices, colors and normals plus peak scratch for a batch.
        var packedBytes = (long)count * 3 * (12 + sizeof(uint) * 3);
        var scratchBytes = (long)Math.Min(count, VolumeTrianglesPerMesh) * 3 * (12 + sizeof(uint) * 3);
        if (!FitsVolumeGeometry(count, nativeLimit) || packedBytes + scratchBytes > packingBytes)
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
            var normals = new uint[vertices];
            for (var vertex = 0; vertex < vertices; vertex += 3)
            {
                while (triangleIndex == meshes[meshIndex].Triangles.Length)
                {
                    meshIndex++;
                    triangleIndex = 0;
                }

                var mesh = meshes[meshIndex];
                var triangle = mesh.Triangles[triangleIndex++];
                if (!WriteVertex(triangle.A, vertex) ||
                    !WriteVertex(triangle.B, vertex + 1) ||
                    !WriteVertex(triangle.C, vertex + 2))
                {
                    colorRejected = true;
                    return null;
                }

                bool WriteVertex(VolumeVertex source, int index)
                {
                    var cell = mesh.Descriptor.CellSizeMeters;
                    var position = new PlanetVector(
                        (mesh.Origin.X + (double)source.Position.X) * cell,
                        (mesh.Origin.Y + (double)source.Position.Y) * cell,
                        (mesh.Origin.Z + (double)source.Position.Z) * cell);
                    positions[index] = origin.ToLocal(position);
                    indices[index] = (uint)index;
                    normals[index] = PackedTerrainNormal.Encode(source.Normal);
                    if (colorEvaluator is null)
                    {
                        colors[index] = (source.Material % 3) switch
                        {
                            0 => 0xff4688b8u,
                            1 => 0xff50905au,
                            _ => 0xffa06b63u
                        };
                        return true;
                    }

                    var evaluated = EvaluateVolumeColor(colorEvaluator, position, source.Material);
                    if (!evaluated.HasValue)
                        return false;
                    colors[index] = evaluated.Value;
                    return true;
                }
            }

            instances[batch] = new(RenderMesh.CreateWithTerrainNormals(positions, indices, colors, normals), Vector3.Zero);
            count -= vertices / 3;
        }
        return new RenderMeshRegion(instances);
    }

    private static uint? EvaluateVolumeColor(VolumeTerrainColorEvaluator evaluator, PlanetVector position, ushort material)
    {
        try
        {
            return evaluator(position, material);
        }
        catch (Exception)
        {
            // An external policy failure invalidates only this private candidate.
            return null;
        }
    }

    private bool CanInstallVolume(ulong generation) => _volume is null ||
        Volatile.Read(ref _volumeStaged) is { } staged && staged.Info.Generation == generation && _volume.IsCurrentFrame(staged);

    private bool ServiceVolumeStage(GpuDevice gpu, RendererWatchdog watchdog, ref RendererStage stage)
    {
        VolumeStage? stale = null;
        VolumeStage? candidate = null;
        lock (_volumeGate)
        {
            if (_volumeStage is { } pending)
            {
                if (!VolumeAdmissionOpen)
                    CloseVolumeAdmission();
                if (pending.Rejected || !VolumeAdmissionOpen || !_volume!.IsCurrentPresentationCandidate(pending.Lease.Info))
                    stale = DetachVolumeStage();
                else if (pending.Native is null)
                    candidate = pending;
            }
        }
        DisposeVolumeStage(stale);
        if (candidate is null)
            return true;

        stage = RendererStage.MeshReplacement;
        if (!watchdog.Begin(stage, null, _operationTimeout))
            return false;
        _retirementHooks?.BeforeVolumeUpload?.Invoke(gpu);
        var prepared = gpu.PrepareMeshGeometry(candidate.Region, candidate.NativeLimit);
        if (!watchdog.Complete())
        {
            prepared.Dispose();
            return false;
        }
        lock (_volumeGate)
        {
            if (VolumeAdmissionOpen && _volume!.ConfirmPreparedGeometry(_volumeAuthority!, candidate.Lease.Info))
            {
                candidate.Native = prepared;
                return true;
            }
            if (!VolumeAdmissionOpen)
                CloseVolumeAdmission();
            stale = DetachVolumeStage();
        }
        prepared.Dispose();
        DisposeVolumeStage(stale);
        return true;
    }

    private GpuDevice.MeshGeometry? GetVolumeNativeCandidate(ulong generation)
    {
        lock (_volumeGate)
            return _volumeStage is { } stage && stage.Lease.Info.Generation == generation ? stage.Native : null;
    }

    private VolumeStage? DetachVolumeStage()
    {
        var stage = _volumeStage;
        _volumeStage = null;
        return stage;
    }

    private static void DisposeVolumeStage(VolumeStage? stage)
    {
        if (stage is null)
            return;
        stage.Native?.Dispose();
        stage.Registration?.Dispose();
        stage.Lease.Dispose();
    }

    private void SettleVolumeCandidate(ulong generation, bool installed, bool retry)
    {
        if (_volume is null || retry)
            return;
        VolumeStage? released = null;
        lock (_volumeGate)
        {
            if (_volumeStage is { } stage && stage.Lease.Info.Generation == generation)
            {
                if (installed)
                {
                    // Both the buffer and the existing reader move to the installed slots.
                    stage.Native = null;
                    stage.Registration = null;
                }
                released = DetachVolumeStage();
            }
            if (_volumeStaged is { } staged && staged.Info.Generation == generation)
            {
                Volatile.Write(ref _volumeStaged, null);
                if (installed)
                {
                    _volumeInstalled?.Dispose();
                    _volumeInstalled = staged;
                }
                else if (released is null)
                    staged.Dispose();
            }
        }
        DisposeVolumeStage(released);
    }

    private bool AuthorizeVolumeDraw(in RenderFrame frame)
    {
        if (_volume is null)
            return true;
        lock (_volumeGate)
        {
            return CanDrawVolume(frame) && (!frame.Scene.HasValue ||
                frame.Sequence == _volumeInstalled!.Frame || _volume.AuthorizeCameraFrame(_volumeInstalled, frame.Sequence));
        }
    }

    private bool CanDrawVolume(in RenderFrame frame)
    {
        if (_volume is null)
            return true;
        lock (_volumeGate)
        {
            return VolumeAdmissionOpen && (frame.Scene.HasValue
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
                if (_volume!.ConfirmMask(mask.View, _volumeAuthority, mask.Sequence))
                {
                    _volumeConfirmedMask = mask;
                    _admission.RecordPresented(frame.Sequence);
                }
            }
        }
    }

    private void CloseVolumeAdmission()
    {
        if (_volume is null || Volatile.Read(ref _volumeClosed) != 0)
            return;

        // Core revocation must win or lose at the authority gate without waiting on renderer work.
        // Publish local closure only after revocation so concurrent closers cannot bypass it.
        _volume.InvalidateRenderer(_volumeEpoch);
        Volatile.Write(ref _volumeClosed, 1);
    }

    private void ReleasePrivateVolumeCustody()
    {
        VolumeStage? stage;
        lock (_volumeGate)
            stage = DetachVolumeStage();
        DisposeVolumeStage(stage);
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
