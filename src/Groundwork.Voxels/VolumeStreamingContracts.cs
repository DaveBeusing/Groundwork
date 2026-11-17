using System.Diagnostics;
using Groundwork.Core;

namespace Groundwork.Voxels;

public readonly record struct VolumeViewIdentity(ulong RendererEpoch, ulong View, PlanetLocalFrame Frame);
public enum VolumeAdmission { Accepted, Backpressure, Stale, Closed, Invalid }
public enum VolumeEventKind { Admitted, Backpressure, WorkReady, Discarded, Commit, Rejected, ViewChanged, Registered, Accepted, Opened, Stopped, MovementCommit, MovementRejected, EditQueued, EditRejected, NativeReady, CheckpointReserved, CheckpointDenied, CheckpointCaptured }
public readonly record struct VolumeMovementEvidence(BarrierStamp AttemptStamp, PlanetWalkState Before, double Forward, double Right, PlanetWalkResult Result)
{
    public PlanetVector? ClimbDisplacement { get; init; }
}
public readonly record struct VolumeStreamingEvent(ulong Sequence, VolumeEventKind Kind, ulong Generation, BarrierStamp? Stamp,
    VolumeViewIdentity View = default, ulong TerrainRevision = 0, ulong Frame = 0, VolumeMovementEvidence? Movement = null, VolumeBrushIntent? Edit = null, VolumeEditStatus? EditStatus = null)
{
    public VolumeOreInventoryConflict? OreInventoryConflict { get; init; }

    /// <summary>Optional Stopwatch timestamp in this process. Zero means no timing was recorded.</summary>
    public long MonotonicTimestamp { get; init; }
}
public readonly record struct VolumeOreInventoryConflict(BatchId Batch, ComponentTarget Inventory, ResourceKey? Scope);
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
    public VolumeOreDepletion? CommittedOreDepletion => Generation.OreDepletion.Deposits > 0 ? Generation.OreDepletion : null;
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
    internal PlanetBrickStore Store { get; } = store;
    public VolumeMeshPlan Plan { get; } = plan;
    public PlanetVolumeMesh[] Meshes { get; } = meshes;
    public void Dispose()
    {
        // Eviction revokes new readers while plan leases continue to pin the payload.
        for (var z = 0; z < Plan.Depth; z++)
            for (var y = 0; y < Plan.Height; y++)
                for (var x = 0; x < Plan.Width; x++)
                    Store.Evict(new LatticeBrick(Plan.Minimum.X + x, Plan.Minimum.Y + y, Plan.Minimum.Z + z));
        Plan.Dispose();
    }
}

internal sealed class VolumeGeneration(ulong id, VolumeWorkClass kind, VolumeStreamingSelection selection,
    ulong sourceRevision, ulong targetRevision, VolumeViewIdentity view, long meshLimit, VolumeResources resources,
    IPlanetBrickSource source)
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
    public VolumeOreDepletion OreDepletion { get; set; }
    public ulong EditReadyTick { get; set; }
    public ulong? NativeReadyTick { get; set; }
    public RootChangePermit? Permit { get; set; }
    public bool Outstanding { get; set; } = true;
    public bool IsPrivate { get; init; }
    public ulong? AheadBaseRevision { get; set; }
    public IPlanetBrickSource Source { get; } = source;
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
    private readonly int _overlayLimit;
    private int _state;
    internal VolumeStreamingWork(VolumeStreamingCoordinator owner, VolumeGeneration generation, int overlayLimit)
    {
        _owner = owner;
        _generation = generation;
        _overlayLimit = overlayLimit;
    }
    public ulong Generation => _generation.Id;
    public bool IsCancellationRequested => _owner.IsCancelled(_generation);

    public bool Execute(CancellationToken cancellationToken = default) =>
        ExecuteCore(false, false, null, out _, cancellationToken);

    /// <summary>Executes with compatible immutable data from completed private work when available.</summary>
    /// <remarks>Keep the private work alive until this call completes. Unavailable or incompatible data is rebuilt.</remarks>
    public bool ExecuteReusing(VolumeStreamingWork privateWork, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateWork);
        return ExecuteCore(false, false, privateWork, out _, cancellationToken);
    }

    /// <summary>Executes the job and captures phase timings if the coordinator accepts the result.</summary>
    /// <remarks>The profile is default when execution returns <see langword="false"/> or throws.</remarks>
    public bool ExecuteProfiled(out VolumeStreamingWorkProfile profile, CancellationToken cancellationToken = default)
    {
        profile = default;
        var accepted = ExecuteCore(true, false, null, out var result, cancellationToken);
        profile = result.Timing;
        return accepted;
    }

    /// <summary>Executes the job and captures detailed brick timings and detached output fingerprints on success.</summary>
    /// <remarks>The profile is default when execution returns <see langword="false"/> or throws.</remarks>
    public bool ExecuteDetailedProfiled(out VolumeStreamingWorkDetailedProfile profile,
        CancellationToken cancellationToken = default) => ExecuteCore(true, true, null, out profile, cancellationToken);

    /// <summary>Executes with optional private reuse and reports the work reused by an accepted job.</summary>
    public bool ExecuteDetailedProfiled(VolumeStreamingWork privateWork, out VolumeStreamingWorkDetailedProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateWork);
        return ExecuteCore(true, true, privateWork, out profile, cancellationToken);
    }

    /// <summary>Executes and profiles a private build using compatible bricks from the committed terrain when available.</summary>
    /// <remarks>Unavailable bricks are sampled. The build prepares its own mesh plan and meshes.</remarks>
    public bool ExecuteDetailedProfiledReusingPublishedBricks(out VolumeStreamingWorkDetailedProfile profile,
        CancellationToken cancellationToken = default) =>
        ExecuteCore(true, true, null, out profile, cancellationToken, PublishedReuse.Bricks);

    /// <summary>Executes and profiles a private build using compatible committed terrain when available.</summary>
    /// <remarks>An exact request window and complete brick reuse permit reuse of the validated mesh plan and meshes.
    /// Other windows build their own plan and meshes. Missing bricks are sampled.</remarks>
    public bool ExecuteDetailedProfiledReusingPublishedTerrain(out VolumeStreamingWorkDetailedProfile profile,
        CancellationToken cancellationToken = default) =>
        ExecuteCore(true, true, null, out profile, cancellationToken, PublishedReuse.Terrain);

    private enum PublishedReuse { None, Bricks, Terrain }

    private bool ExecuteCore(bool measure, bool captureDetails, VolumeStreamingWork? privateWork,
        out VolumeStreamingWorkDetailedProfile profile,
        CancellationToken cancellationToken, PublishedReuse publishedReuse = PublishedReuse.None)
    {
        profile = default;
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return false;
        VolumeArtifacts? artifacts = null;
        try
        {
            var started = measure ? Stopwatch.GetTimestamp() : 0;
            Check(cancellationToken);
            VolumeArtifacts? donor = null;
            VolumeArtifacts? published = null;
            VolumeStreamingSelection? publishedSelection = null;
            using var borrow = publishedReuse != PublishedReuse.None
                ? _owner.TryBorrowPublished(_generation, out published, out publishedSelection)
                : privateWork is not null && ReferenceEquals(_owner, privateWork._owner)
                    ? _owner.TryBorrowPrivate(_generation, privateWork._generation, out donor)
                    : null;
            var brickDonor = published?.Store ?? donor?.Store;
            // Matching canonical requests also fix the window bounds used by the shared overlay's Refine.
            var canReusePublishedPlan = publishedReuse == PublishedReuse.Terrain && publishedSelection is not null &&
                _generation.Selection.Requests.SequenceEqual(publishedSelection.Requests);
            var overlay = _generation.Overlay;
            VolumeOreDepletion depletion = default;
            void CheckEditCancellation()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_generation.Cancelled)
                    throw new OperationCanceledException("Terrain edit was superseded or stopped.");
            }
            if (_generation.Edit is { } edit)
            {
                overlay = overlay!.Apply(edit, _overlayLimit, CheckEditCancellation, out depletion);
                if (overlay is null)
                    return _owner.Complete(_generation, null);
            }
            ReadOnlySpan<VolumeMeshRequest> requests = overlay is { Count: > 0 }
                ? overlay.Refine(_generation.Selection, CheckEditCancellation)
                : _generation.Selection.Requests;
            var overlayPrepared = measure ? Stopwatch.GetTimestamp() : 0;
            var store = new PlanetBrickStore((IPlanetBrickSource?)overlay ?? _generation.Source,
                (long)requests.Length * PlanetBrickStore.BrickPayloadBytes, requests.Length);
            VolumeMeshPlan? plan = null;
            long bricksGenerated, planCreated, meshBuilt;
            VolumeBrickGenerationTiming brickTiming = default;
            ReadOnlyMemory<VolumeBrickWorkFingerprint> brickFingerprints = default;
            ReadOnlyMemory<VolumeMeshWorkFingerprint> meshFingerprints = default;
            var brickElapsed = captureDetails ? new long[requests.Length] : null;
            var meshElapsed = captureDetails ? new long[_generation.Selection.MeshCount] : null;
            var brickHits = captureDetails && brickDonor is not null ? new bool[requests.Length] : null;
            var meshHits = captureDetails && (donor is not null || canReusePublishedPlan)
                ? new bool[_generation.Selection.MeshCount] : null;
            var reusedMeshPlan = false;
            try
            {
                for (var requestIndex = 0; requestIndex < requests.Length; requestIndex++)
                {
                    Check(cancellationToken);
                    var request = requests[requestIndex];
                    var brickStart = brickElapsed is not null ? Stopwatch.GetTimestamp() : 0;
                    if (brickDonor is not null && !AffectedByEdit(_generation.Edit, request.Brick.Vertex(0, 0, 0), mesh: false) &&
                        store.TryImport(request.Brick, brickDonor))
                    {
                        if (brickHits is not null)
                        {
                            brickHits[requestIndex] = true;
                            brickElapsed![requestIndex] = Stopwatch.GetTimestamp() - brickStart;
                        }
                        continue;
                    }

                    canReusePublishedPlan = false;
                    if (brickElapsed is not null)
                    {
                        if (!store.TryGenerateProfiled(request.Brick, out var timing, cancellationToken))
                            throw new InvalidOperationException("Reserved brick quota is insufficient.");
                        brickElapsed[requestIndex] = Stopwatch.GetTimestamp() - brickStart;
                        brickTiming += timing;
                    }
                    else if (!store.TryGenerate(request.Brick, cancellationToken))
                        throw new InvalidOperationException("Reserved brick quota is insufficient.");
                }
                bricksGenerated = measure ? Stopwatch.GetTimestamp() : 0;
                if (canReusePublishedPlan)
                    plan = published!.Plan.TryRebind(store, _generation.TargetRevision, long.MaxValue);
                reusedMeshPlan = plan is not null;
                plan ??= VolumeMeshPlan.TryCreate(store, requests, _generation.TargetRevision, long.MaxValue, overlay?.Digest)
                    ?? throw new InvalidOperationException("Reserved plan leases are unavailable.");
                planCreated = measure ? Stopwatch.GetTimestamp() : 0;
                var meshes = new PlanetVolumeMesh[_generation.Selection.MeshCount];
                long remaining = _generation.MeshLimit;
                var index = 0;
                for (var z = 0; z < plan.Depth; z += 2)
                    for (var y = 0; y < plan.Height; y += 2)
                        for (var x = 0; x < plan.Width; x += 2)
                        {
                            Check(cancellationToken);
                            var minimum = new LatticeBrick(plan.Minimum.X + x, plan.Minimum.Y + y, plan.Minimum.Z + z);
                            var meshStart = meshElapsed is not null ? Stopwatch.GetTimestamp() : 0;
                            PlanetVolumeMesh mesh;
                            if (reusedMeshPlan || (donor is not null &&
                                !AffectedByEdit(_generation.Edit, minimum.Vertex(0, 0, 0), mesh: true) &&
                                plan.HasSameMeshLayout(donor.Plan, minimum)))
                            {
                                var reused = (reusedMeshPlan ? published! : donor!).Meshes[index];
                                if ((long)reused.Triangles.Length * PlanetVolumeMesher.TrianglePayloadBytes > remaining)
                                    throw new InvalidOperationException("Mesh exceeds the reserved payload.");
                                mesh = reused.ForPlan(plan);
                                if (meshHits is not null)
                                    meshHits[index] = true;
                            }
                            else
                            {
                                mesh = PlanetVolumeMesher.Build(plan, minimum, remaining)
                                    ?? throw new InvalidOperationException("Mesh exceeds the reserved payload.");
                            }
                            if (meshElapsed is not null)
                                meshElapsed[index] = Stopwatch.GetTimestamp() - meshStart;
                            remaining -= (long)mesh.Triangles.Length * PlanetVolumeMesher.TrianglePayloadBytes;
                            meshes[index++] = mesh;
                        }
                artifacts = new VolumeArtifacts(store, plan, meshes);
                plan = null;
                meshBuilt = measure ? Stopwatch.GetTimestamp() : 0;
                if (brickElapsed is not null && meshElapsed is not null)
                    (brickFingerprints, meshFingerprints) = CaptureFingerprints(store, requests, brickElapsed,
                        meshes, meshElapsed, brickHits, meshHits, cancellationToken);
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
            var evidenceFinished = captureDetails ? Stopwatch.GetTimestamp() : 0;
            Check(cancellationToken);
            var accepted = _owner.Complete(_generation, artifacts, overlay, depletion);
            artifacts = null;
            if (measure && accepted)
            {
                var timing = new VolumeStreamingWorkProfile(started, overlayPrepared, bricksGenerated,
                    planCreated, meshBuilt, Stopwatch.GetTimestamp());
                profile = new(timing, brickTiming, evidenceFinished, brickFingerprints, meshFingerprints)
                {
                    ReuseSourceGeneration = borrow?.Info.Generation ?? 0,
                    ReusedMeshPlan = reusedMeshPlan
                };
            }
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

    private (VolumeBrickWorkFingerprint[] Bricks, VolumeMeshWorkFingerprint[] Meshes) CaptureFingerprints(
        PlanetBrickStore store, ReadOnlySpan<VolumeMeshRequest> requests, long[] brickElapsed,
        PlanetVolumeMesh[] meshes, long[] meshElapsed, bool[]? brickHits, bool[]? meshHits,
        CancellationToken cancellationToken)
    {
        var bricks = new VolumeBrickWorkFingerprint[requests.Length];
        for (var index = 0; index < requests.Length; index++)
        {
            Check(cancellationToken);
            var request = requests[index];
            var (density, material) = store.Fingerprint(request.Brick);
            bricks[index] = new(request.Brick, request.Stride, density, material, brickElapsed[index])
            {
                Reused = brickHits?[index] == true
            };
        }
        var geometry = new VolumeMeshWorkFingerprint[meshes.Length];
        for (var index = 0; index < meshes.Length; index++)
        {
            Check(cancellationToken);
            geometry[index] = VolumeMeshWorkFingerprint.Capture(meshes[index], meshElapsed[index], cancellationToken) with
            {
                Reused = meshHits?[index] == true
            };
        }
        return (bricks, geometry);
    }

    private static bool AffectedByEdit(VolumeBrushIntent? edit, LatticeCoordinate origin, bool mesh)
    {
        if (edit is not { } brush)
            return false;

        // Bricks own the upper density face and half-open material cells. Meshing also samples
        // boundary materials and reads a one-vertex halo for gradients. The donor has the pre-edit
        // overlay, so only this brush and the cells incident to its vertices can differ.
        var cells = mesh ? 2 * PlanetBrickStore.CellEdge : PlanetBrickStore.CellEdge;
        var halo = mesh ? 1 : 0;
        return Intersects(Offset(origin, -halo), Offset(origin, cells + halo), brush.Minimum, brush.Maximum) ||
            Intersects(origin, Offset(origin, cells - 1 + halo), Offset(brush.Minimum, -1), brush.Maximum);

        static LatticeCoordinate Offset(LatticeCoordinate point, int amount) =>
            new(point.X + amount, point.Y + amount, point.Z + amount);

        static bool Intersects(LatticeCoordinate low, LatticeCoordinate high,
            LatticeCoordinate otherLow, LatticeCoordinate otherHigh) =>
            low.X <= otherHigh.X && otherLow.X <= high.X &&
            low.Y <= otherHigh.Y && otherLow.Y <= high.Y &&
            low.Z <= otherHigh.Z && otherLow.Z <= high.Z;
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
        if (_generation.IsPrivate)
        {
            _owner.CancelPrivate(_generation);
            if (state == 0)
                _owner.Complete(_generation, null);
            return;
        }
        if (state == 2)
            return;
        _generation.Cancelled = true;
        if (state == 0)
            _owner.Complete(_generation, null);
    }
}

/// <summary>Bounded reader. Density, collision coverage identity, meshes and origin come from one publication.</summary>
public readonly struct VolumeCheckpointCut : IDisposable
{
    internal VolumeCheckpointCut(CommittedWorldView publication, VolumeStreamingLease terrain) =>
        (Publication, Terrain) = (publication, terrain);

    public CommittedWorldView Publication { get; }
    public VolumeStreamingLease? Terrain { get; }

    public void Dispose() => Terrain?.Dispose();
}
public sealed class VolumeCheckpointReservation : IDisposable
{
    private readonly VolumeStreamingCoordinator _owner;

    internal VolumeCheckpointReservation(VolumeStreamingCoordinator owner, CommittedCut cut)
    {
        _owner = owner;
        Cut = cut;
        Lease = new VolumeStreamingLease(owner);
    }

    internal VolumeStreamingLease Lease { get; }
    internal CommittedCut Cut { get; }
    internal CommittedWorldView? Bound { get; set; }

    /// <summary>Takes the finalized cut before the next tick, for transfer to the save worker.</summary>
    public VolumeCheckpointCut? TryTake() => _owner.TakeCheckpoint(this, out var publication) is { } terrain
        ? new VolumeCheckpointCut(publication, terrain) : null;

    public void Dispose() => _owner.CancelCheckpoint(this);
}
public sealed class VolumeStreamingLease : IDisposable
{
    private readonly object _gate = new();
    private readonly VolumeStreamingCoordinator _owner;
    private VolumeGeneration? _generation;
    public PlanetDescriptor? EditLineage { get; private set; }
    internal VolumeStreamingLease(VolumeStreamingCoordinator owner, VolumeGeneration generation)
    {
        _owner = owner;
        Attach(generation);
    }

    internal VolumeStreamingLease(VolumeStreamingCoordinator owner) => _owner = owner;

    internal void Attach(VolumeGeneration generation)
    {
        _generation = generation;
        Info = generation.Info;
        if (generation.Permit?.Status == RootChangeStatus.Committed)
        {
            EditLineage = generation.Overlay?.Descriptor;
            if (generation.Overlay is { } overlay)
                EditOverlayBytes = 8 + 32 * overlay.Count;
        }
    }
    public VolumePublicationInfo Info { get; private set; }

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

    /// <summary>Byte count of the canonical overlay on this committed lease.</summary>
    public int EditOverlayBytes { get; private set; }
    /// <summary>Writes the committed sparse overlay in canonical order for a checkpoint consumer.</summary>
    public bool TryWriteEditOverlay(Span<byte> destination, out int written)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_generation is null, this);
            written = 0;
            return EditLineage is not null && _generation.Overlay is { } overlay && overlay.TryWrite(destination, out written);
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
