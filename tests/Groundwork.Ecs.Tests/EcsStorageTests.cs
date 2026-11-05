using System.Diagnostics;
using Groundwork.Core;

namespace Groundwork.Ecs.Tests;

[TestClass]
public sealed class EcsStorageTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void MaximumGenerationRetiresTheSlotRatherThanWrapping()
    {
        var root = MakeRoot(2);
        Assert.IsTrue(root.TryCreate(null, out root, out var first));
        var last = first with { Generation = uint.MaxValue };
        root = root with { Slots = root.Slots.SetItem(first.Slot, root.Slots[first.Slot] with { Generation = uint.MaxValue }) };
        Assert.IsTrue(root.TryDestroy(last, root.Slots[last.Slot], out root));
        Assert.IsTrue(root.TryCreate(null, out root, out var next));
        Assert.AreNotEqual(last.Slot, next.Slot);
        Assert.AreEqual(ComponentReadStatus.Stale, root.Read<Position>(last).Status);
        Assert.IsFalse(root.TryCreate(null, out var unchanged, out _));
        Assert.AreSame(root, unchanged);
    }

    [TestMethod]
    public void RandomizedCreateDestroyAddReplaceAndRemoveMatchAnIndependentModel()
    {
        var root = MakeRoot(8);
        var live = new Dictionary<EntityId, int?>();
        var issued = new HashSet<EntityId>();
        var random = new Random(20261105);
        for (int index = 0; index < 500; index++)
        {
            if (live.Count == 0 || live.Count < 8 && random.Next(3) == 0)
            {
                Assert.IsTrue(root.TryCreate(null, out root, out var created));
                Assert.IsTrue(issued.Add(created));
                live.Add(created, null);
            }
            else
            {
                var entity = live.Keys.ElementAt(random.Next(live.Count));
                var slot = root.Slots[entity.Slot];
                switch (random.Next(3))
                {
                    case 0:
                        Assert.IsTrue(root.TryDestroy(entity, slot, out root));
                        live.Remove(entity);
                        Assert.AreEqual(ComponentReadStatus.Stale, root.Read<Position>(entity).Status);
                        break;
                    case 1:
                        Assert.IsTrue(root.TryWrite(entity, slot, EcsFixture.Positions,
                            EcsFixture.Registry.Encode(new Position(index)), out root));
                        live[entity] = index;
                        break;
                    default:
                        if (live[entity].HasValue)
                        {
                            Assert.IsTrue(root.TryWrite(entity, slot, EcsFixture.Positions, default, out root));
                            live[entity] = null;
                            Assert.AreEqual(ComponentReadStatus.Missing, root.Read<Position>(entity).Status);
                        }
                        break;
                }
            }

            CollectionAssert.AreEqual(live.Where(item => item.Value.HasValue).OrderBy(item => item.Key.Slot)
                .Select(item => new EcsComponentRow<Position>(item.Key, new Position(item.Value.GetValueOrDefault()))).ToArray(),
                root.Enumerate<Position>().ToArray());
        }
    }

    [TestMethod]
    public void RegistrationRejectsNativeHandlesAndTheirContainingValues()
    {
        var registry = new ComponentRegistryBuilder();
        var id = new ComponentTypeId(1);
        Assert.ThrowsExactly<ArgumentException>(() => registry.Register<nint>(id));
        Assert.ThrowsExactly<ArgumentException>(() => registry.Register<HandleLike>(id));
        Assert.AreEqual(0, registry.Build().Count);
    }

    [TestMethod]
    [TestCategory("EcsBenchmark")]
    public void StorageBaselineReportsCreateLookupQueryAndDelete()
    {
        const int entities = 1024;
        const int repetitions = 64;
        _ = AddPosition(MakeRoot(1), 1, out _).Enumerate<Position>().Single();
        var root = MakeRoot(entities);
        var ids = new EntityId[entities];
        long baselineHeap = GC.GetTotalMemory(forceFullCollection: true);
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < entities; index++)
            root = AddPosition(root, index, out ids[index]);
        Log("create and add", entities, started, allocationStart);
        long heapDelta = GC.GetTotalMemory(forceFullCollection: true) - baselineHeap;
        TestContext.WriteLine($"Live root charges {root.ChargedBytes} bytes. Approximate heap delta {heapDelta} bytes. " +
            $"Runtime {Environment.Version}, {Environment.OSVersion}, {Environment.ProcessorCount} logical processors.");

        long sum = 0;
        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        for (int repeat = 0; repeat < repetitions; repeat++)
        foreach (var id in ids)
            sum += root.Read<Position>(id).Value.X;
        Log("point lookup", entities * repetitions, started, allocationStart);
        long expected = (long)entities * (entities - 1) / 2 * repetitions;
        Assert.AreEqual(expected, sum);

        sum = 0;
        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        for (int repeat = 0; repeat < repetitions; repeat++)
        foreach (var row in root.Enumerate<Position>())
            sum += row.Value.X;
        Log("sequential query row", entities * repetitions, started, allocationStart);
        Assert.AreEqual(expected, sum);

        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        foreach (var id in ids)
        {
            if (!root.TryDestroy(id, root.Slots[id.Slot], out root))
                throw new InvalidOperationException("Benchmark deletion unexpectedly failed.");
        }
        Log("delete", entities, started, allocationStart);
        Assert.IsEmpty(root.Enumerate<Position>());
    }

    private void Log(string name, int count, long started, long allocationStart)
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        TestContext.WriteLine($"{name}: {count} operations, {count / seconds:N0} ops/s, {bytes} allocated bytes.");
    }

    private static EcsRoot MakeRoot(int slots) => EcsRoot.Create(new WorldId(1), EcsFixture.Registry,
        new EcsLimits(slots, 2, 0, 1048576, 134217728), null);

    private static EcsRoot AddPosition(EcsRoot root, int value, out EntityId id)
    {
        if (!root.TryCreate(null, out root, out id) ||
            !root.TryWrite(id, root.Slots[id.Slot], EcsFixture.Positions, EcsFixture.Registry.Encode(new Position(value)), out root))
            throw new InvalidOperationException("Benchmark creation unexpectedly failed.");
        return root;
    }

    private readonly record struct HandleLike(nint Value);
}
