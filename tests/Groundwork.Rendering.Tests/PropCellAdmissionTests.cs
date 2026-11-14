using System.Numerics;
using System.Runtime.InteropServices;

namespace Groundwork.Rendering.Tests;

[TestClass]
public sealed class PropCellAdmissionTests
{
    private static readonly PropCellKey First = new(-1, 0, 0);
    private static readonly PropCellKey Second = new(0, 0, 0);
    private static readonly PropPoint Origin = new(999_999_999_000, 0, 0);
    private static readonly PropPublication Initial = new(new Guid("4601ac86-e628-46f1-8b7d-e458ce31618a"), 1, 1, 1);
    private static readonly PropPlacement Placement = new(7, 0, Vector3.Zero, Quaternion.Identity, 1, 0xff224466);
    private static readonly PropFrustumPlane[] Frustum =
    [
        new(Vector3.UnitX, 64), new(-Vector3.UnitX, 64),
        new(Vector3.UnitY, 64), new(-Vector3.UnitY, 64),
        new(Vector3.UnitZ, 64), new(-Vector3.UnitZ, 64)
    ];

    [TestMethod]
    public void IdentityAndPayloadSurviveReloadWhileReferencesSortByCanonicalCell()
    {
        var owner = Create();
        owner.Publish(Initial, [Second, First]);
        Install(owner, Second, Origin);
        Install(owner, First, Origin);
        var visible = new VisibleProp[2];
        Assert.AreEqual(2, owner.Select(Origin, Frustum, visible));
        Assert.AreEqual(First, visible[0].Id.Cell);
        var original = visible[0];
        Assert.IsTrue(owner.TryRead(original, out var packed, out var origin));
        Assert.AreEqual(32, Marshal.SizeOf<PropInstanceData>());
        Assert.AreEqual(Origin, origin);
        Assert.AreEqual(Quaternion.Identity, packed.DecodeRotation());
        Assert.AreEqual(Placement.Tint, packed.Tint);
        Assert.IsTrue(owner.TryEvict(First));
        Assert.IsFalse(owner.TryRead(original, out _, out _));
        owner.Publish(Initial with { CoverageGeneration = 2 }, [First, Second]);
        Install(owner, First, Origin);
        Assert.AreEqual(2, owner.Select(Origin, Frustum, visible));
        Assert.AreEqual(original.Id, visible[0].Id);
    }

    [TestMethod]
    public void CoverageAndTerrainChangesRejectStaleWorkAndSuppressOnlyProvenInvalidCells()
    {
        var owner = Create();
        owner.Publish(Initial, [First, Second]);
        Install(owner, First, Origin);
        Install(owner, Second, Origin);
        Assert.AreEqual(PropCellResult.Accepted, owner.TryRequest(First, 1, out var stale));
        Assert.AreEqual(PropCellResult.Accepted, owner.TryStage(stale, Origin, [Placement]));
        owner.Publish(Initial with { CoverageGeneration = 2 }, [First, Second]);
        Assert.AreEqual(PropCellResult.Stale, owner.TryInstall(stale));
        Assert.AreEqual(PropCellResult.Stale, owner.CopyStaged(stale, [], out _, out _));
        Assert.AreEqual(PropCellResult.Stale, owner.TryStage(stale, Origin, [Placement]));
        Assert.AreEqual(2, owner.Usage.ResidentInstances);

        owner.Publish(Initial with { TerrainRevision = 2, CoverageGeneration = 3 }, [First, Second], new HashSet<PropCellKey> { First });
        var visible = new VisibleProp[2];
        Assert.AreEqual(1, owner.Select(Origin, Frustum, visible));
        Assert.AreEqual(Second, visible[0].Id.Cell);
        owner.Publish(Initial with { TerrainRevision = 2, CoverageGeneration = 4 }, [First]);
        Assert.AreEqual(0, owner.Select(Origin, Frustum, visible));
        Assert.AreEqual(PropCellResult.MissingCoverage, owner.TryRequest(Second, 1, out _));
        Assert.ThrowsExactly<ArgumentException>(() => owner.Publish(Initial, [Second]));
    }

    [TestMethod]
    public void QuotasPreserveResidentsAndReleaseReservationsOnCancellation()
    {
        var owner = Create(resident: 1);
        owner.Publish(Initial, [First, Second]);
        Install(owner, First, Origin);
        Assert.AreEqual(PropCellResult.Quota, owner.TryRequest(Second, 3, out _));
        Assert.AreEqual(PropCellResult.Accepted, owner.TryRequest(Second, 1, out var request));
        Assert.AreEqual(PropCellResult.Pending, owner.TryRequest(Second, 1, out _));
        Assert.AreEqual(PropCellResult.Quota, owner.TryRequest(First, 1, out _));
        Assert.AreEqual(PropCellResult.Quota, owner.TryStage(request, Origin, [Placement, Placement with { Ordinal = 8 }]));
        Assert.AreEqual(PropCellResult.Invalid, owner.TryStage(request, Origin, [Placement with { Scale = float.NaN }]));
        Assert.AreEqual(PropCellResult.Accepted, owner.TryStage(request, Origin, [Placement]));
        Assert.AreEqual(PropCellResult.Quota, owner.TryInstall(request));
        Assert.AreEqual(new PropCellUsage(2, 1, 1, 1), owner.Usage);
        Assert.IsTrue(owner.TryCancel(request));
        Assert.AreEqual(new PropCellUsage(1, 0, 0, 1), owner.Usage);
        Assert.AreEqual(2, owner.Peak.Cells);
        Assert.AreEqual(PropCellResult.Stale, owner.TryInstall(request));
    }

    [TestMethod]
    public void VisibilityUsesScaledBoundsAndHysteresisAtCanonicalDistances()
    {
        var owner = Create();
        owner.Publish(Initial, [First]);
        Install(owner, First, Origin);
        var scratch = new VisibleProp[1];
        Check(12, 1);
        Check(9, 0);
        Check(12, 0);
        Check(15, 0);
        Check(16, 1);
        Check(12, 1);

        var clip = Frustum.ToArray();
        clip[0] = new(Vector3.UnitX, -2);
        Assert.AreEqual(1, owner.Select(Origin, clip, scratch));
        clip[0] = new(Vector3.UnitX, -2.01);
        Assert.AreEqual(0, owner.Select(Origin, clip, scratch));
        Assert.AreEqual(0, owner.Select(Origin with { Y = 41 }, Frustum, scratch));
        Assert.ThrowsExactly<ArgumentException>(() => owner.Select(Origin, Frustum, []));
        Assert.AreEqual(1, owner.Select(Origin, Frustum, scratch));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 32; i++)
            owner.Select(Origin, Frustum, scratch);
        Assert.AreEqual(allocated, GC.GetAllocatedBytesForCurrentThread());

        void Check(double distance, byte lod)
        {
            Assert.AreEqual(1, owner.Select(Origin with { Y = distance }, Frustum, scratch));
            Assert.AreEqual(lod, scratch[0].Lod);
        }
    }

    [TestMethod]
    public async Task CoverageChurnHoldsWorkerQuotaUntilSettlement()
    {
        var owner = Create();
        owner.Publish(Initial, [First, Second]);
        Install(owner, First, Origin);
        Assert.AreEqual(PropCellResult.Accepted, owner.TryRequest(Second, 2, out var blocked));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<PropPlacement[]> PlaceAsync()
        {
            await release.Task;
            return [Placement];
        }

        var worker = PlaceAsync();
        try
        {
            for (ulong generation = 2; generation <= 64; generation++)
            {
                if (generation % 2 == 0)
                    owner.Publish(Initial with { CoverageGeneration = generation }, [First]);
                else
                    owner.Publish(Initial with { CoverageGeneration = generation }, [First, Second]);

                Assert.IsFalse(worker.IsCompleted);
                Assert.AreEqual(PropCellResult.Stale, owner.TryStage(blocked, Origin, [Placement]));
                Assert.AreEqual(PropCellResult.Stale, owner.TryInstall(blocked));
                Assert.AreEqual(PropCellResult.Quota, owner.TryRequest(First, 1, out _));
                Assert.AreEqual(new PropCellUsage(1, 1, 2, 1), owner.Usage);
            }
        }
        finally
        {
            release.SetResult();
        }

        Assert.AreEqual(PropCellResult.Stale, owner.TryStage(blocked, Origin, await worker));
        Assert.IsTrue(owner.CompleteWorker(blocked));
        Assert.IsFalse(owner.CompleteWorker(blocked));
        Assert.AreEqual(new PropCellUsage(1, 0, 0, 1), owner.Usage);
        Assert.AreEqual(1, owner.Peak.PendingCells);
        Assert.AreEqual(2, owner.Peak.PendingInstances);
        Assert.AreEqual(PropCellResult.Accepted, owner.TryRequest(First, 1, out var next));
        Assert.IsFalse(owner.CompleteWorker(blocked));
        Assert.IsTrue(owner.CompleteWorker(next));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancellationAndEvictionHoldUnstagedReservations(bool evict)
    {
        var owner = Create();
        owner.Publish(Initial, [First, Second]);
        Assert.AreEqual(PropCellResult.Accepted, owner.TryRequest(First, 1, out var request));
        Assert.IsTrue(evict ? owner.TryEvict(First) : owner.TryCancel(request));
        Assert.AreEqual(PropCellResult.Stale, owner.TryStage(request, Origin, [Placement]));
        Assert.AreEqual(PropCellResult.Quota, owner.TryRequest(Second, 1, out _));
        Assert.IsTrue(owner.CompleteWorker(request));
        Assert.AreEqual(default(PropCellUsage), owner.Usage);
        Install(owner, Second, Origin);
    }

    private static PropCellAdmission Create(int resident = 2) =>
        new(new(2, 1, 2, resident, 2), 42, 1, [new(1, 3, 2, 40, 10, 15)]);

    private static void Install(PropCellAdmission owner, PropCellKey key, PropPoint origin)
    {
        Assert.AreEqual(PropCellResult.Accepted, owner.TryRequest(key, 1, out var request));
        Assert.AreEqual(PropCellResult.Accepted, owner.TryStage(request, origin, [Placement]));
        Assert.AreEqual(PropCellResult.Quota, owner.CopyStaged(request, [], out var count, out _));
        Assert.AreEqual(1, count);
        Span<PropInstanceData> upload = stackalloc PropInstanceData[count];
        Assert.AreEqual(PropCellResult.Accepted, owner.CopyStaged(request, upload, out _, out var canonical));
        Assert.AreEqual(origin, canonical);
        Assert.AreEqual(Placement.Tint, upload[0].Tint);
        Assert.AreEqual(PropCellResult.Accepted, owner.TryInstall(request));
    }
}
