using System.Buffers.Binary;
using System.Security.Cryptography;
using Groundwork.Core;
using Groundwork.Core.Tests;
using Groundwork.Jobs;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetJobDeterminismTests
{
    private const int BrickCount = 3;
    private const double SmallRadius = 1.0 / 32;
    private const double Cell = 1.0 / 1024;
    private static readonly PlanetLandformRecipe Recipe =
        new(61, -512, 1280, new(17, 1536), new(11, 1024), new(5, 192), 13, 7, 19);

    // Regression vectors captured from the serial Release fixture on Windows x64.
    [TestMethod]
    [DataRow(0UL, SmallRadius, "C7819A51794FA02DE3B5AFFFBD730C8CCDE056C21749CF31ED48427B33D6BD2B")]
    [DataRow(42UL, SmallRadius, "061490FF3526024D1828819F58040FF3BF27D3E51E786567FF53513FA26F8F22")]
    [DataRow(ulong.MaxValue, SmallRadius, "F625743D4BC80A25785DC3126624A3F0954F61A60086D0C056CF0B0CD4881DCF")]
    [DataRow(0UL, PlanetDescriptor.MaximumRadiusMeters, "348F25E9B918686D622DED507D462AF1A188E601BFDFFE316F5845F8F95F66D7")]
    [DataRow(42UL, PlanetDescriptor.MaximumRadiusMeters, "AFCA937C75D522D41BA41A6DC99D4CF9AA8BDAF37D2931A1BE024B8E821B45B7")]
    [DataRow(ulong.MaxValue, PlanetDescriptor.MaximumRadiusMeters, "E4FB30FC37A26E4AAA8E0E0320DEE77309D7C7FFE6736DB415FCB1ACE099CD78")]
    public async Task ExtremeRadiusBricksKeepGoldenBytesAcrossJobOrders(ulong seed, double radius, string golden)
    {
        var descriptor = new PlanetDescriptor(new("00112233-4455-6677-8899-aabbccddeeff"),
            new("10213243-5465-7687-98a9-bacbdcedfe0f"), seed, PlanetDensityField.GeneratorVersion,
            PlanetDensityField.TerrainSchemaVersion, 1, Recipe.ParameterSha256, radius, Cell);

        var positive = CheckRebase(descriptor, radius);
        var negative = CheckRebase(descriptor, -radius);
        Assert.AreEqual(checked((long)(radius / Cell)), positive.Vertex.X);
        Assert.AreEqual(-positive.Vertex.X, negative.Vertex.X);
        var right = positive.Vertex.Locate().Brick;
        var left = right with { X = right.X - 1 };
        LatticeBrick[] bricks = [left, right, negative.Vertex.Locate().Brick];
        var field = new PlanetDensityField(descriptor, Recipe);

        var serial = await RunJobs(field, bricks, [0, 1, 2], parallel: false);
        var reversed = await RunJobs(field, bricks, [2, 1, 0], parallel: false);
        var parallel = await RunJobs(field, bricks, [1, 2, 0], parallel: true);
        Assert.AreEqual(serial, reversed, "Reversed submission changed the canonical brick payload.");
        Assert.AreEqual(serial, parallel, "Parallel execution and reordered completion changed the canonical brick payload.");
        Assert.AreEqual(golden, serial);
    }

    private static PlanetLatticeSample CheckRebase(PlanetDescriptor descriptor, double signedRadius)
    {
        var position = new PlanetVector(signedRadius + Cell / 4, -Cell / 4, Cell / 4);
        var original = PlanetLocalFrame.Create(new(signedRadius - 24, 0, 0));
        Assert.IsTrue(original.TryRebase(position, out var rebased));
        var sample = descriptor.SampleAt(position);
        Assert.AreEqual(new LatticeCoordinate(sample.Vertex.X, 0, 0), sample.Vertex);
        Assert.AreEqual(sample, descriptor.SampleAt(original.ToPlanet(original.ToLocal(position))));
        Assert.AreEqual(sample, descriptor.SampleAt(rebased.ToPlanet(rebased.ToLocal(position))));
        Assert.AreEqual(position, rebased.ToPlanetDouble(rebased.ToLocalDouble(position)));
        return sample;
    }

    private static async Task<string> RunJobs(PlanetDensityField field, LatticeBrick[] bricks, int[] order, bool parallel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var runtime = new CoreRuntimeFixture(new JobClock());
        var world = runtime.OpenWorld();
        var stamp = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 1, BarrierKind.BeginTick);
        var phase = world.Publication.Phases.TryOpen(stamp, TickPhase.Base).Phase;
        Assert.IsNotNull(phase);
        var store = new PlanetBrickStore(field, BrickCount * PlanetBrickStore.BrickPayloadBytes, BrickCount);
        var entered = Gates();
        var startSampling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sampled = Gates();
        var finish = Gates();
        var handlers = Enumerable.Range(0, BrickCount).ToDictionary(
            index => Handler(index), index => (JobHandler)(async (_, cancellation) =>
            {
                if (parallel)
                {
                    entered[index].SetResult();
                    await startSampling.Task.WaitAsync(cancellation);
                }
                if (!store.TryGenerate(bricks[index], cancellation))
                    throw new InvalidOperationException("The Q02 brick reservation was denied.");
                if (parallel)
                {
                    sampled[index].TrySetResult();
                    await finish[index].Task.WaitAsync(cancellation);
                }
            }));
        var scheduler = new JobScheduler(stamp.World, new JobSchedulerLimits(BrickCount, parallel ? BrickCount : 1),
            world.Publication.Execution, new NoInput(), world.Ownership, world.Reporter, runtime.Clock, handlers);
        var handles = new IJobExecutionHandle?[BrickCount];

        try
        {
            for (var ordinal = 0; ordinal < order.Length; ordinal++)
            {
                var index = order[ordinal];
                var result = scheduler.TrySubmit(new JobSubmission(
                    new SourceKey(stamp.World, 1, TickPhase.Base, 89, 0, (ulong)ordinal + 1),
                    Handler(index), JobInputKind.None, JobResultKind.Commandless, [], [], [], [], [], default), phase);
                Assert.AreEqual(JobAdmissionStatus.Accepted, result.Status);
                Assert.IsInstanceOfType<IJobExecutionHandle>(result.Handle);
                handles[index] = (IJobExecutionHandle)result.Handle;
                if (!parallel)
                {
                    await scheduler.DispatchReadyAsync(phase, token);
                    await AssertSucceeded(handles[index]!, token);
                }
            }

            if (parallel)
            {
                await scheduler.DispatchReadyAsync(phase, token);
                await Task.WhenAll(entered.Select(gate => gate.Task)).WaitAsync(token);
                startSampling.SetResult();
                await Task.WhenAll(sampled.Select(gate => gate.Task)).WaitAsync(token);
                Assert.AreEqual(BrickCount, scheduler.ReadMetrics().OccupiedWorkerSlots);
                // Each worker has generated its brick. Keep all three live, then force completion
                // in an order different from either serial run and the parallel submission order.
                foreach (var index in new[] { 2, 0, 1 })
                {
                    Assert.IsFalse(handles[index]!.ExecutionCompletion.IsCompleted);
                    finish[index].SetResult();
                    await AssertSucceeded(handles[index]!, token);
                }
            }
        }
        finally
        {
            startSampling.TrySetResult();
            foreach (var gate in finish)
                gate.TrySetResult();
            world.Publication.Phases.Close(phase);
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await scheduler.StopAsync(world.CreateBorrowedDrainDeadline(), cleanup.Token);
            }
            finally
            {
                foreach (var handle in handles)
                    handle?.Dispose();
            }
        }

        var settled = scheduler.ReadMetrics();
        Assert.AreEqual((long)BrickCount, settled.Succeeded);
        Assert.AreEqual(0, settled.OccupiedWorkerSlots);
        Assert.AreEqual(0, settled.Queued);
        Assert.AreEqual(BrickCount * (long)PlanetBrickStore.BrickPayloadBytes, store.ChargedPayloadBytes);
        var digest = HashBricks(store, bricks);
        foreach (var brick in bricks)
            Assert.IsTrue(store.Evict(brick));
        Assert.AreEqual(0L, store.ChargedPayloadBytes);
        return digest;
    }

    private static JobHandlerId Handler(int index) => new((uint)index + 1);

    private static TaskCompletionSource[] Gates() => Enumerable.Range(0, BrickCount)
        .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

    private static async Task AssertSucceeded(IJobExecutionHandle handle, CancellationToken token) =>
        Assert.AreEqual(JobExecutionStatus.Succeeded, (await handle.ExecutionCompletion.WaitAsync(token)).Status);

    // Frame each brick with its scoped lattice origin, then append density vertices and material
    // cells in x-fastest order with explicit little-endian encodings.
    private static string HashBricks(PlanetBrickStore store, LatticeBrick[] bricks)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[PlanetLatticeSample.EncodedSize];
        var payload = new byte[PlanetBrickStore.BrickPayloadBytes];
        using var left = store.TryAcquire(bricks[0]);
        using var right = store.TryAcquire(bricks[1]);
        Assert.IsNotNull(left);
        Assert.IsNotNull(right);
        Assert.IsLessThan(0L, left.ReadDensity(0, 0, 0));
        Assert.IsGreaterThan(0L, right.ReadDensity(32, 0, 0));
        for (var z = 0; z < PlanetBrickStore.VertexEdge; z++)
        for (var y = 0; y < PlanetBrickStore.VertexEdge; y++)
            Assert.AreEqual(left.ReadDensity(32, y, z), right.ReadDensity(0, y, z));

        foreach (var brick in bricks)
        {
            using var lease = store.TryAcquire(brick);
            Assert.IsNotNull(lease);
            new PlanetLatticeSample(store.Descriptor.WorldId, store.Descriptor.PlanetId, brick.Vertex(0, 0, 0)).Write(header);
            hash.AppendData(header);
            var offset = 0;
            for (var z = 0; z < PlanetBrickStore.VertexEdge; z++)
            for (var y = 0; y < PlanetBrickStore.VertexEdge; y++)
            for (var x = 0; x < PlanetBrickStore.VertexEdge; x++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), lease.ReadDensity(x, y, z));
                offset += sizeof(long);
            }
            for (var z = 0; z < PlanetBrickStore.CellEdge; z++)
            for (var y = 0; y < PlanetBrickStore.CellEdge; y++)
            for (var x = 0; x < PlanetBrickStore.CellEdge; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), lease.ReadMaterial(x, y, z));
                offset += sizeof(ushort);
            }
            hash.AppendData(payload);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class JobClock : CoreRuntimeFixture.TestClock { }

    private sealed class NoInput : IJobInputSource
    {
        public JobInputCapture TryCapture(in JobInputRequest request) =>
            throw new AssertFailedException("Commandless terrain jobs do not capture input.");
    }
}
