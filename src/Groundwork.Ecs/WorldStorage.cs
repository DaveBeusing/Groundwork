using System.Collections.Immutable;
using Groundwork.Core;

namespace Groundwork.Ecs;

public sealed record EcsLimits(int MaxEntitySlots, int MaxComponentTypes, int MaxPartitions, long MaxWorldBytes,
    long MaxRetainedWorldBytes);

public readonly record struct EcsComponentRow<T>(EntityId Entity, T Value) where T : unmanaged;

internal readonly record struct ComponentCell(ulong Mutation, ImmutableArray<byte> Bytes)
{
    internal bool Present => !Bytes.IsDefault;
}

internal sealed record EntitySlot(uint Generation, bool Alive, PartitionId? Partition, ulong Mutation,
    ImmutableDictionary<ComponentTypeId, ComponentCell> Cells);

internal sealed record EcsPartition(ulong Revision, ImmutableHashSet<EntityId> Members);

internal sealed record EcsRoot(
    WorldId World,
    ComponentRegistry Registry,
    EcsLimits Limits,
    ImmutableSortedDictionary<uint, EntitySlot> Slots,
    ImmutableSortedSet<uint> Free,
    ImmutableDictionary<PartitionId, EcsPartition> Partitions,
    ulong Mutation,
    long ChargedBytes) : IWorldStateRoot
{
    private const long RootCharge = 256;
    private const long SlotCharge = 256;
    private const long CellCharge = 192;
    private const long PartitionCharge = 256;
    private const long MemberCharge = 192;

    internal static EcsRoot Create(WorldId world, ComponentRegistry registry, EcsLimits limits,
        IEnumerable<PartitionId>? partitions)
    {
        var map = ImmutableDictionary.CreateBuilder<PartitionId, EcsPartition>();
        long charged = RootCharge;
        if (charged > limits.MaxWorldBytes)
            throw new ArgumentOutOfRangeException(nameof(limits));
        foreach (var id in partitions ?? [])
        {
            if (id.Value == 0 || map.ContainsKey(id))
                throw new ArgumentException("Partition IDs must be unique and nonzero.", nameof(partitions));
            if (map.Count == limits.MaxPartitions || PartitionCharge > limits.MaxWorldBytes - charged)
                throw new ArgumentException("Partition capacity exceeded.", nameof(partitions));
            map.Add(id, new EcsPartition(1, []));
            charged += PartitionCharge;
        }

        return new EcsRoot(world, registry, limits, ImmutableSortedDictionary<uint, EntitySlot>.Empty,
            ImmutableSortedSet<uint>.Empty, map.ToImmutable(), 0, charged);
    }

    internal ComponentReadStatus Find(EntityId entity, out EntitySlot? slot)
    {
        slot = null;
        if (entity.World.Value == 0 || entity.Generation == 0)
            return ComponentReadStatus.InvalidId;
        if (entity.World != World)
            return ComponentReadStatus.ForeignWorld;
        if (!Slots.TryGetValue(entity.Slot, out var found) || !found.Alive || found.Generation != entity.Generation)
            return ComponentReadStatus.Stale;
        slot = found;
        return ComponentReadStatus.Present;
    }

    internal ComponentRead<T> Read<T>(EntityId entity) where T : unmanaged
    {
        var status = Find(entity, out var slot);
        if (slot is null)
            return new(status, default, null);
        if (!Registry.TryGetId<T>(out var component))
            return new(ComponentReadStatus.UndeclaredAccess, default, null);
        var cell = slot.Cells.GetValueOrDefault(component.Value);
        return cell.Present
            ? new(ComponentReadStatus.Present, ComponentRegistry.Decode<T>(cell.Bytes), null)
            : new(ComponentReadStatus.Missing, default, null);
    }

    internal IEnumerable<EcsComponentRow<T>> Enumerate<T>() where T : unmanaged
    {
        if (!Registry.TryGetId<T>(out var component))
            throw new InvalidOperationException("The component type is not registered.");
        foreach (var (index, slot) in Slots)
        {
            var cell = slot.Cells.GetValueOrDefault(component.Value);
            if (slot.Alive && cell.Present)
                yield return new EcsComponentRow<T>(new EntityId(World, index, slot.Generation), ComponentRegistry.Decode<T>(cell.Bytes));
        }
    }

    internal bool TryCreate(PartitionId? partition, out EcsRoot next, out EntityId entity)
    {
        next = this;
        entity = default;
        bool reuse = !Free.IsEmpty;
        if (Mutation == ulong.MaxValue || (!reuse && Slots.Count == Limits.MaxEntitySlots))
            return false;
        long charge = (reuse ? 0 : SlotCharge) + (partition.HasValue ? MemberCharge : 0);
        if (charge > Limits.MaxWorldBytes - ChargedBytes)
            return false;

        uint index = reuse ? Free.Min : (uint)Slots.Count;
        uint generation = reuse ? Slots[index].Generation + 1 : 1;
        entity = new EntityId(World, index, generation);
        var partitions = Partitions;
        if (partition is { } id)
        {
            var old = partitions[id];
            if (old.Revision == ulong.MaxValue)
                return false;
            partitions = partitions.SetItem(id, new(old.Revision + 1, old.Members.Add(entity)));
        }

        ulong mutation = Mutation + 1;
        var slot = new EntitySlot(generation, true, partition, mutation, ImmutableDictionary<ComponentTypeId, ComponentCell>.Empty);
        next = this with
        {
            Slots = Slots.SetItem(index, slot),
            Free = reuse ? Free.Remove(index) : Free,
            Partitions = partitions,
            Mutation = mutation,
            ChargedBytes = ChargedBytes + charge,
        };
        return true;
    }

    internal bool TryDestroy(EntityId entity, EntitySlot slot, out EcsRoot next)
    {
        next = this;
        if (Mutation == ulong.MaxValue)
            return false;
        var partitions = Partitions;
        long released = slot.Cells.Values.Sum(cell => CellCharge + (cell.Present ? cell.Bytes.Length : 0));
        if (slot.Partition is { } id)
        {
            var old = partitions[id];
            if (old.Revision == ulong.MaxValue)
                return false;
            partitions = partitions.SetItem(id, new(old.Revision + 1, old.Members.Remove(entity)));
            released += MemberCharge;
        }

        ulong mutation = Mutation + 1;
        next = this with
        {
            Slots = Slots.SetItem(entity.Slot, new EntitySlot(slot.Generation, false, null, mutation,
                ImmutableDictionary<ComponentTypeId, ComponentCell>.Empty)),
            Free = slot.Generation == uint.MaxValue ? Free : Free.Add(entity.Slot),
            Partitions = partitions,
            Mutation = mutation,
            ChargedBytes = ChargedBytes - released,
        };
        return true;
    }

    internal bool TryWrite(EntityId entity, EntitySlot slot, ComponentTypeId component, ImmutableArray<byte> payload,
        out EcsRoot next)
    {
        next = this;
        if (Mutation == ulong.MaxValue)
            return false;
        bool recorded = slot.Cells.TryGetValue(component, out var old);
        long charge = (recorded ? 0 : CellCharge) + (payload.IsDefault ? 0 : payload.Length) - (old.Present ? old.Bytes.Length : 0);
        if (charge > Limits.MaxWorldBytes - ChargedBytes)
            return false;

        ulong mutation = Mutation + 1;
        var owned = payload.IsDefault ? default : ImmutableArray.Create(payload.AsSpan());
        next = this with
        {
            Slots = Slots.SetItem(entity.Slot, slot with { Mutation = mutation, Cells = slot.Cells.SetItem(component, new(mutation, owned)) }),
            Mutation = mutation,
            ChargedBytes = ChargedBytes + charge,
        };
        return true;
    }
}
