namespace Groundwork.Ecs;

/// <summary>A composition-owned aggregate logical storage budget shared across ECS worlds and runtime restarts.</summary>
public sealed class EcsStorageBudget
{
    private readonly object _gate = new();
    private long _reserved;

    public EcsStorageBudget(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        CapacityBytes = capacityBytes;
    }

    public long CapacityBytes { get; }

    public long ReservedBytes
    {
        get
        {
            lock (_gate)
                return _reserved;
        }
    }

    internal Reservation? TryReserve(long bytes)
    {
        lock (_gate)
        {
            if (bytes > CapacityBytes - _reserved)
                return null;
            _reserved += bytes;
            return new Reservation(this, bytes);
        }
    }

    private void Release(long bytes)
    {
        lock (_gate)
            _reserved -= bytes;
    }

    internal sealed class Reservation(EcsStorageBudget budget, long bytes)
    {
        private EcsStorageBudget? _budget = budget;

        internal void Release() => Interlocked.Exchange(ref _budget, null)?.Release(bytes);
    }
}
