using Groundwork.Core;

namespace Groundwork.Voxels;

public enum VolumePhysicsReadStatus { Completed, StaleTick, CoverageUnavailable, ReaderQuota, Stopped }

/// <summary>A synchronous, borrowed view of the terrain used to plan one physics tick.</summary>
public readonly ref struct VolumePhysicsRead
{
    private readonly VolumeGeneration? _generation;

    internal VolumePhysicsRead(VolumeGeneration generation, BarrierStamp stamp)
    {
        _generation = generation;
        Stamp = stamp;
    }

    internal double CellSizeMeters => _generation?.Selection.Descriptor.CellSizeMeters ?? 0;

    public BarrierStamp Stamp { get; }
    public VolumePublicationInfo Terrain => _generation?.Info ?? default;

    public VolumeSweepResult Sweep(VolumeCapsule body, PlanetVector displacement, VolumeQueryLimits limits) =>
        _generation is { } generation ? VolumeCollision.Sweep(generation, body, displacement, limits) :
            new(VolumeQueryStatus.Invalid, 0, default, default, 0);
}

public delegate void VolumePhysicsReadAction<T>(scoped VolumePhysicsRead read, ref T state);

public sealed partial class VolumeStreamingCoordinator
{
    /// <summary>
    /// Borrows the current BeginTick terrain for synchronous planning. Cache the callback and pass scratch
    /// by reference. Callback results are provisional and must be discarded unless the scope completes.
    /// </summary>
    public VolumePhysicsReadStatus ReadPhysicsTick<T>(BarrierStamp stamp, ref T state, VolumePhysicsReadAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        VolumeGeneration generation;
        lock (_gate)
        {
            var status = GetPhysicsTerrain(stamp, out var current);
            if (status != VolumePhysicsReadStatus.Completed)
                return status;
            generation = current!;
            if (!TryChargeReader(generation))
                return VolumePhysicsReadStatus.ReaderQuota;
        }

        try
        {
            action(new VolumePhysicsRead(generation, stamp), ref state);
            lock (_gate)
            {
                var status = GetPhysicsTerrain(stamp, out var current);
                return status == VolumePhysicsReadStatus.Completed && current != generation
                    ? VolumePhysicsReadStatus.StaleTick : status;
            }
        }
        finally
        {
            ReleaseReader(generation);
        }
    }

    private VolumePhysicsReadStatus GetPhysicsTerrain(BarrierStamp stamp, out VolumeGeneration? generation)
    {
        Refresh();
        generation = _active;
        if (_stopped)
            return VolumePhysicsReadStatus.Stopped;
        var current = _world.Publication.Committed.Read();
        if (stamp.Kind != BarrierKind.BeginTick || stamp != current.Stamp)
            return VolumePhysicsReadStatus.StaleTick;
        var safety = _world.State.Read();
        return safety.Conditions == SafetyCondition.None && safety.PendingRequiredEvidenceRecords == 0 &&
            _movementOpen && generation is not null && generation.View == _view &&
            current.Root is VolumeWorldRoot root && root.Owner == this && root.Generation == generation
            ? VolumePhysicsReadStatus.Completed : VolumePhysicsReadStatus.CoverageUnavailable;
    }

    /// <summary>Rechecks the value identity of a successful read before accepting its proposals.</summary>
    public bool IsCurrentPhysicsTerrain(BarrierStamp stamp, VolumePublicationInfo terrain)
    {
        lock (_gate)
            return GetPhysicsTerrain(stamp, out var generation) == VolumePhysicsReadStatus.Completed &&
                generation!.Info == terrain;
    }
}
