using Groundwork.Core;

namespace Groundwork.Voxels;

public readonly record struct VolumeViewIdentity(ulong RendererEpoch, ulong View, PlanetLocalFrame Frame);
public enum VolumeAdmission { Accepted, Backpressure, Stale, Closed, Invalid }
public enum VolumeEventKind { Admitted, Backpressure, WorkReady, Discarded, Commit, Rejected, ViewChanged, Registered, Accepted, Opened, Stopped, MovementCommit, MovementRejected, EditQueued, EditRejected }
public readonly record struct VolumeMovementEvidence(BarrierStamp AttemptStamp, PlanetWalkState Before, double Forward, double Right, PlanetWalkResult Result);
public readonly record struct VolumeStreamingEvent(ulong Sequence, VolumeEventKind Kind, ulong Generation, BarrierStamp? Stamp,
    VolumeViewIdentity View = default, ulong TerrainRevision = 0, ulong Frame = 0, VolumeMovementEvidence? Movement = null, VolumeBrushIntent? Edit = null, VolumeEditStatus? EditStatus = null);
public readonly record struct VolumePublicationInfo(ulong Generation, ulong TerrainRevision,
    VolumeViewIdentity View, LatticeBrick Minimum, int Width, string PlanIdentity);
public readonly record struct VolumeStreamingStatistics(VolumeResources Used, VolumeResources Peak,
    int Generations, int Readers, bool MovementHeld, bool Stopped);

/// <summary>Core's immutable composed root. Gameplay updates preserve the current terrain attachment.</summary>
public sealed class VolumeWorldRoot : IWorldStateRoot
{
    internal VolumeWorldRoot(IWorldStateRoot state, VolumeStreamingCoordinator owner, VolumeGeneration generation, PlanetWalkState? player = null)
    {
        Player = player;
        State = state;
        Owner = owner;
        Generation = generation;
    }
    public PlanetWalkState? Player { get; }
    public IWorldStateRoot State { get; }
    public WorldId World => State.World;
    internal VolumeStreamingCoordinator Owner { get; }
    internal VolumeGeneration Generation { get; }
    public VolumePublicationInfo Terrain => Generation.Info;
    public VolumeBrushIntent? CommittedEdit => Generation.Edit;
    public VolumeWorldRoot WithState(IWorldStateRoot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.World != World)
            throw new ArgumentException("World identity cannot change.", nameof(state));
        return new VolumeWorldRoot(state, Owner, Generation, Player);
    }
}

internal sealed class VolumeArtifacts(PlanetBrickStore store, VolumeMeshPlan plan, PlanetVolumeMesh[] meshes) : IDisposable
{
    public VolumeMeshPlan Plan { get; } = plan;
    public PlanetVolumeMesh[] Meshes { get; } = meshes;
    public void Dispose()
    {
        // Eviction revokes new readers while plan leases continue to pin the payload.
        for (var z = 0; z < Plan.Depth; z++)
            for (var y = 0; y < Plan.Height; y++)
                for (var x = 0; x < Plan.Width; x++)
                    store.Evict(new LatticeBrick(Plan.Minimum.X + x, Plan.Minimum.Y + y, Plan.Minimum.Z + z));
        Plan.Dispose();
    }
}

internal sealed class VolumeGeneration(ulong id, VolumeWorkClass kind, VolumeStreamingSelection selection,
    ulong sourceRevision, ulong targetRevision, VolumeViewIdentity view, long meshLimit, VolumeResources resources)
{
    public ulong Id { get; } = id;
    public VolumeWorkClass Kind { get; } = kind;
    public VolumeStreamingSelection Selection { get; } = selection;
    public ulong SourceRevision { get; } = sourceRevision;
    public ulong TargetRevision { get; } = targetRevision;
    public VolumeViewIdentity View { get; } = view;
    public long MeshLimit { get; } = meshLimit;
    public VolumeResources Resources { get; set; } = resources;
    public VolumeWorkClass Envelope { get; set; } = kind;
    public VolumeArtifacts? Artifacts { get; set; }
    public VolumeEditOverlay? Overlay { get; set; }
    public VolumeBrushIntent? Edit { get; set; }
    public RootChangePermit? Permit { get; set; }
    public bool Outstanding { get; set; } = true;
    public volatile bool Cancelled;
    public bool Released { get; set; }
    public bool Retired { get; set; }
    public int Readers { get; set; }
    public VolumePublicationInfo Info { get; set; }
}

/// <summary>One bounded job. Execute outside the authority lock, or Dispose to settle undispatched work.</summary>
public sealed class VolumeStreamingWork : IDisposable
{
    private readonly VolumeStreamingCoordinator _owner;
    private readonly VolumeGeneration _generation;
    private readonly IPlanetBrickSource _source;
    private readonly int _overlayLimit;
    private int _state;
    internal VolumeStreamingWork(VolumeStreamingCoordinator owner, VolumeGeneration generation, IPlanetBrickSource source, int overlayLimit)
    {
        _owner = owner;
        _generation = generation;
        _source = source;
        _overlayLimit = overlayLimit;
    }
    public ulong Generation => _generation.Id;
    public bool IsCancellationRequested => _owner.IsCancelled(_generation);

    public bool Execute(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return false;
        VolumeArtifacts? artifacts = null;
        try
        {
            Check(cancellationToken);
            var overlay = _generation.Overlay;
            if (_generation.Edit is { } edit)
            {
                overlay = overlay!.Apply(edit, _overlayLimit, cancellationToken);
                if (overlay is null)
                    return _owner.Complete(_generation, null);
            }
            ReadOnlySpan<VolumeMeshRequest> requests = overlay is { Count: > 0 }
                ? overlay.Refine(_generation.Selection, cancellationToken)
                : _generation.Selection.Requests;
            var store = new PlanetBrickStore((IPlanetBrickSource?)overlay ?? _source, (long)requests.Length * PlanetBrickStore.BrickPayloadBytes, requests.Length);
            VolumeMeshPlan? plan = null;
            try
            {
                foreach (var request in requests)
                {
                    Check(cancellationToken);
                    if (!store.TryGenerate(request.Brick, cancellationToken))
                        throw new InvalidOperationException("Reserved brick quota is insufficient.");
                }
                plan = VolumeMeshPlan.TryCreate(store, requests, _generation.TargetRevision, long.MaxValue, overlay?.Digest)
                    ?? throw new InvalidOperationException("Reserved plan leases are unavailable.");
                var meshes = new PlanetVolumeMesh[_generation.Selection.MeshCount];
                long remaining = _generation.MeshLimit;
                var index = 0;
                for (var z = 0; z < plan.Depth; z += 2)
                    for (var y = 0; y < plan.Height; y += 2)
                        for (var x = 0; x < plan.Width; x += 2)
                        {
                            Check(cancellationToken);
                            var mesh = PlanetVolumeMesher.Build(plan,
                                new LatticeBrick(plan.Minimum.X + x, plan.Minimum.Y + y, plan.Minimum.Z + z), remaining)
                                ?? throw new InvalidOperationException("Mesh exceeds the reserved payload.");
                            remaining -= (long)mesh.Triangles.Length * PlanetVolumeMesher.TrianglePayloadBytes;
                            meshes[index++] = mesh;
                        }
                artifacts = new VolumeArtifacts(store, plan, meshes);
                plan = null;
            }
            finally
            {
                if (plan is not null || artifacts is null)
                {
                    foreach (var request in requests)
                        store.Evict(request.Brick);
                    plan?.Dispose();
                }
            }
            Check(cancellationToken);
            var accepted = _owner.Complete(_generation, artifacts, overlay);
            artifacts = null;
            return accepted;
        }
        catch (OperationCanceledException) when (_generation.Cancelled && !cancellationToken.IsCancellationRequested)
        {
            artifacts?.Dispose();
            artifacts = null;
            _owner.Complete(_generation, null);
            return false;
        }
        catch
        {
            artifacts?.Dispose();
            artifacts = null;
            _owner.Complete(_generation, null);
            throw;
        }
        finally
        {
            artifacts?.Dispose();
            Volatile.Write(ref _state, 2);
        }
    }

    private void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_owner.IsCancelled(_generation))
            throw new OperationCanceledException("Terrain work was superseded or stopped.");
    }

    public void Dispose()
    {
        var state = Interlocked.CompareExchange(ref _state, 2, 0);
        if (state == 2)
            return;
        _generation.Cancelled = true;
        if (state == 0)
            _owner.Complete(_generation, null);
    }
}

/// <summary>Bounded reader. Density, collision coverage identity, meshes and origin come from one publication.</summary>
public sealed class VolumeStreamingLease : IDisposable
{
    private readonly object _gate = new();
    private readonly VolumeStreamingCoordinator _owner;
    private VolumeGeneration? _generation;
    internal VolumeStreamingLease(VolumeStreamingCoordinator owner, VolumeGeneration generation)
    {
        _owner = owner;
        _generation = generation;
        Info = generation.Info;
    }
    public VolumePublicationInfo Info { get; }

    public ReadOnlySpan<PlanetVolumeMesh> Meshes
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_generation is null, this);
                return _generation.Artifacts!.Meshes;
            }
        }
    }

    public bool TryReadDensity(PlanetLatticeSample sample, out long density)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_generation is null, this);
            var generation = _generation;
            var descriptor = generation.Selection.Descriptor;
            var origin = generation.Selection.Minimum.Vertex(0, 0, 0);
            var x = (Int128)sample.Vertex.X - origin.X;
            var y = (Int128)sample.Vertex.Y - origin.Y;
            var z = (Int128)sample.Vertex.Z - origin.Z;
            var extent = generation.Selection.Width * 32;
            if (sample.WorldId != descriptor.WorldId || sample.PlanetId != descriptor.PlanetId ||
                x < 0 || y < 0 || z < 0 || x > extent || y > extent || z > extent)
            {
                density = default;
                return false;
            }
            density = generation.Artifacts!.Plan.Read(new System.Numerics.Vector3((float)x, (float)y, (float)z));
            return true;
        }
    }

    /// <summary>Diagnostic query against this retained immutable revision, including old revisions.</summary>
    public VolumeRayResult Raycast(PlanetVector start, PlanetVector end, Span<VolumeRayHit> hits,
        VolumeQueryLimits? limits = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_generation is null, this);
            return VolumeCollision.Ray(_generation, start, end, hits, limits ?? VolumeQueryLimits.Default);
        }
    }

    public VolumeSweepResult Sweep(VolumeCapsule capsule, PlanetVector displacement, VolumeQueryLimits? limits = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_generation is null, this);
            return VolumeCollision.Sweep(_generation, capsule, displacement, limits ?? VolumeQueryLimits.Default);
        }
    }

    /// <summary>Writes the committed sparse overlay in canonical order for a checkpoint consumer.</summary>
    public bool TryWriteEditOverlay(Span<byte> destination, out int written)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_generation is null, this);
            written = 0;
            return _generation.Overlay is { } overlay && overlay.TryWrite(destination, out written);
        }
    }

    internal VolumeFrameRegistration? TransferToFrame(VolumeStreamingCoordinator owner,
        VolumeRendererAuthority renderer, ulong frame)
    {
        lock (_gate)
        {
            if (_owner != owner || _generation is not { } generation)
                return null;
            var registration = owner.RegisterRetainedFrame(generation, renderer, frame);
            if (registration is not null)
                _generation = null; // The registration now owns the existing reader charge.
            return registration;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_generation is not { } generation)
                return;
            _generation = null;
            _owner.ReleaseReader(generation);
        }
    }
}

/// <summary>Exclusive provider authority for one renderer epoch.</summary>
public sealed class VolumeRendererAuthority
{
    internal VolumeRendererAuthority(ulong epoch) => Epoch = epoch;
    public ulong Epoch { get; }
}

/// <summary>Retains submitted geometry until the provider settles it, independently of view revocation.</summary>
public sealed class VolumeFrameRegistration : IDisposable
{
    internal VolumeFrameRegistration(VolumeStreamingCoordinator owner, VolumeGeneration generation,
        VolumeStreamingLease lease, ulong frame)
    {
        Owner = owner;
        Generation = generation;
        Lease = lease;
        Frame = frame;
    }
    internal VolumeStreamingCoordinator Owner { get; }
    internal VolumeGeneration Generation { get; }
    internal VolumeStreamingLease Lease { get; }
    internal bool Acknowledged { get; set; }
    internal ulong AcknowledgedTick { get; set; }
    internal ulong LastCameraFrame { get; set; }
    internal bool Disposed { get; set; }
    public ulong Frame { get; }
    public VolumePublicationInfo Info => Lease.Info;
    public ReadOnlySpan<PlanetVolumeMesh> Meshes => Lease.Meshes;
    public void Dispose() => Owner.ReleaseFrame(this);
}
