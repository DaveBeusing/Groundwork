using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class VolumeMeshPlanTests
{
    [TestMethod]
    public void IndependentNeighborhoodsJoinAcrossAllTwelveChartEdgesAndEightCorners()
    {
        var points = new HashSet<LatticeCoordinate>();
        foreach (var (face, edge, otherFace, otherEdge) in PlanetCoordinateTests.Seams)
        {
            var descriptor = Descriptor(64 * Math.Sqrt(2));
            var a = descriptor.SampleAt(new PlanetPatchKey(face, 0, 0, 0), edge, 8);
            var b = descriptor.SampleAt(new PlanetPatchKey(otherFace, 0, 0, 0), otherEdge, 8);
            Assert.AreEqual(a, b);
            Assert.IsTrue(points.Add(a.Vertex));
            Verify(descriptor, a.Vertex);
        }
        Assert.HasCount(12, points);

        var corners = new HashSet<LatticeCoordinate>();
        var cornerDescriptor = Descriptor(64 * Math.Sqrt(3));
        foreach (var face in Enum.GetValues<PlanetFace>())
        for (var u = 0; u <= 16; u += 16)
        for (var v = 0; v <= 16; v += 16)
            corners.Add(cornerDescriptor.SampleAt(new PlanetPatchKey(face, 0, 0, 0), u, v).Vertex);
        Assert.HasCount(8, corners);
        foreach (var corner in corners)
            Verify(cornerDescriptor, corner);
    }

    [TestMethod]
    public async Task PlanRetainsEvictedBricksAndReturnsQuotasAfterReadersFinish()
    {
        using var fixture = new Window(Descriptor(128), new LatticeCoordinate(0, 0, 0));
        var metadataBytes = fixture.Requests.Length * (long)System.Runtime.CompilerServices.Unsafe.SizeOf<VolumeMeshInput>();
        Assert.IsNull(VolumeMeshPlan.TryCreate(fixture.Store, fixture.Requests, 9, metadataBytes - 1));
        var plan = VolumeMeshPlan.TryCreate(fixture.Store, fixture.Requests, 9, metadataBytes);
        Assert.IsNotNull(plan);
        Assert.AreEqual(metadataBytes, plan.MetadataPayloadBytes);
        foreach (var request in fixture.Requests)
        {
            Assert.IsTrue(fixture.Store.Evict(request.Brick));
            Assert.IsNull(fixture.Store.TryAcquire(request.Brick));
        }
        Assert.AreEqual(64L * PlanetBrickStore.BrickPayloadBytes, fixture.Store.ChargedPayloadBytes);
        var mesh = PlanetVolumeMesher.Build(plan, plan.Minimum, long.MaxValue);
        Assert.IsNotNull(mesh);

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                var candidate = PlanetVolumeMesher.Build(plan, plan.Minimum, long.MaxValue);
                Assert.IsNotNull(candidate);
                Assert.AreEqual(Hash(mesh), Hash(candidate));
            }
            catch (ObjectDisposedException)
            {
                // Disposal won admission. No build may reacquire released inputs.
            }
        })).ToArray();
        plan.Dispose();
        await Task.WhenAll(tasks);
        Assert.AreEqual(0L, fixture.Store.ChargedPayloadBytes);
        Assert.ThrowsExactly<ObjectDisposedException>(() => PlanetVolumeMesher.Build(plan, plan.Minimum, long.MaxValue));
        Assert.IsGreaterThan(0, mesh.Triangles.Length);
    }

    [TestMethod]
    public void IncompleteCoverageLeasePressureAndDifferentRevisionsFailPredictably()
    {
        using var fixture = new Window(Descriptor(128), new LatticeCoordinate(0, 0, 0));
        using var held = fixture.Store.TryAcquire(fixture.Requests[0].Brick);
        Assert.IsNotNull(held);
        Assert.IsNull(VolumeMeshPlan.TryCreate(fixture.Store, fixture.Requests, 1, long.MaxValue));
        held.Dispose();

        using var plan = VolumeMeshPlan.TryCreate(fixture.Store, fixture.Requests, 1, long.MaxValue);
        Assert.IsNotNull(plan);
        var identity = plan.Identity;
        plan.Dispose();
        var shuffled = (VolumeMeshRequest[])fixture.Requests.Clone();
        new Random(94).Shuffle(shuffled);
        using var reordered = VolumeMeshPlan.TryCreate(fixture.Store, shuffled, 1, long.MaxValue);
        Assert.IsNotNull(reordered);
        Assert.AreEqual(identity, reordered.Identity);
        reordered.Dispose();
        using var nextRevision = VolumeMeshPlan.TryCreate(fixture.Store, fixture.Requests, 2, long.MaxValue);
        Assert.IsNotNull(nextRevision);
        Assert.AreNotEqual(identity, nextRevision.Identity);
        nextRevision.Dispose();

        Assert.IsTrue(fixture.Store.Evict(fixture.Requests[^1].Brick));
        Assert.IsNull(VolumeMeshPlan.TryCreate(fixture.Store, fixture.Requests, 3, long.MaxValue));
        Assert.ThrowsExactly<ArgumentException>(() =>
            VolumeMeshPlan.TryCreate(fixture.Store, fixture.Requests.AsSpan(0, 63), 3, long.MaxValue));
        // Failed acquisition returned all previously acquired lease slots.
        foreach (var request in fixture.Requests.AsSpan(0, 63))
        {
            using var reader = fixture.Store.TryAcquire(request.Brick);
            Assert.IsNotNull(reader);
        }
    }

    [TestMethod]
    public void FineToFineHaloAndIntersectingTransitionsRemainClosedWithZeroSamples()
    {
        using var fixture = new Window(Descriptor(128), new LatticeCoordinate(0, 0, 0), exactZeros: true);
        var requests = fixture.Requests;
        for (var i = 0; i < requests.Length; i++)
        {
            var key = requests[i].Brick;
            // The fine block crosses the x=0 neighborhood boundary and ends at x=32.
            requests[i] = requests[i] with { Stride = key.X < 1 && key.Y < 0 && key.Z < 0 ? 1 : 2 };
        }
        using var plan = VolumeMeshPlan.TryCreate(fixture.Store, requests, 19, long.MaxValue);
        Assert.IsNotNull(plan);
        var meshes = new PlanetVolumeMesh[8];
        for (var i = 0; i < meshes.Length; i++)
        {
            var key = new LatticeBrick(plan.Minimum.X + (i & 1) * 2,
                plan.Minimum.Y + ((i >> 1) & 1) * 2, plan.Minimum.Z + (i >> 2) * 2);
            var mesh = PlanetVolumeMesher.Build(plan, key, long.MaxValue);
            Assert.IsNotNull(mesh);
            meshes[i] = mesh;
        }
        Assert.AreEqual((byte)0, meshes[0].LodMask);
        Assert.AreEqual((byte)170, meshes[1].LodMask);
        AuditJoined(meshes);
    }
    private static void Verify(PlanetDescriptor descriptor, LatticeCoordinate shared)
    {
        using var fixture = new Window(descriptor, shared);
        using var plan = VolumeMeshPlan.TryCreate(fixture.Store, fixture.Requests, 11, long.MaxValue);
        Assert.IsNotNull(plan);
        var meshes = new PlanetVolumeMesh[8];
        var order = new[] { 7, 2, 5, 0, 6, 1, 4, 3 };
        Parallel.ForEach(order, i =>
        {
            var minimum = new LatticeBrick(plan.Minimum.X + (i & 1) * 2,
                plan.Minimum.Y + ((i >> 1) & 1) * 2, plan.Minimum.Z + (i >> 2) * 2);
            var mesh = PlanetVolumeMesher.Build(plan, minimum, long.MaxValue);
            Assert.IsNotNull(mesh);
            meshes[i] = mesh;
        });

        Assert.IsTrue(meshes.Any(m => m.LodMask != 0), "Fixture must retain coarse geometry.");
        Assert.IsTrue(meshes.Any(m => m.LodMask != 255), "Fixture must retain fine geometry.");
        foreach (var i in order.Reverse())
        {
            Assert.AreEqual(plan.Identity, meshes[i].PlanIdentity);
            var minimum = new LatticeBrick(plan.Minimum.X + (i & 1) * 2,
                plan.Minimum.Y + ((i >> 1) & 1) * 2, plan.Minimum.Z + (i >> 2) * 2);
            var serial = PlanetVolumeMesher.Build(plan, minimum, long.MaxValue);
            Assert.IsNotNull(serial);
            Assert.AreEqual(Hash(meshes[i]), Hash(serial));
            Assert.IsNull(PlanetVolumeMesher.Build(plan, minimum,
                (long)meshes[i].Triangles.Length * PlanetVolumeMesher.TrianglePayloadBytes - 1));
        }
        AuditJoined(meshes);
    }

    private static void AuditJoined(PlanetVolumeMesh[] meshes)
    {
        var edges = new Dictionary<(Point A, Point B), (int Count, int Balance)>();
        var vertices = new Dictionary<Point, (Vector3 Normal, ushort Material, int Owners)>();
        var sharedCount = 0;
        for (var i = 0; i < meshes.Length; i++)
        {
            var mesh = meshes[i];
            Assert.IsGreaterThan(0, mesh.Triangles.Length);
            foreach (var triangle in mesh.Triangles)
            {
                var a = Read(triangle.A);
                var b = Read(triangle.B);
                var c = Read(triangle.C);
                var cross = Vector3.Cross(triangle.B.Position - triangle.A.Position, triangle.C.Position - triangle.A.Position);
                Assert.AreNotEqual(Vector3.Zero, cross);
                Assert.IsGreaterThan(0f, Vector3.Dot(cross, triangle.A.Normal + triangle.B.Normal + triangle.C.Normal));
                Add(a, b);
                Add(b, c);
                Add(c, a);
            }

            Point Read(VolumeVertex vertex)
            {
                Assert.AreEqual(1f, vertex.Normal.Length(), 0.00001f);
                var point = Point.At(mesh, vertex.Position);
                if (vertices.TryGetValue(point, out var old))
                {
                    Assert.AreEqual(old.Normal, vertex.Normal);
                    Assert.AreEqual(old.Material, vertex.Material);
                    if ((old.Owners & (1 << i)) == 0)
                    {
                        sharedCount++;
                        vertices[point] = (old.Normal, old.Material, old.Owners | (1 << i));
                    }
                }
                else
                    vertices.Add(point, (vertex.Normal, vertex.Material, 1 << i));
                return point;
            }
        }
        Assert.IsGreaterThan(0, sharedCount);
        foreach (var (edge, state) in edges)
            Assert.AreEqual((2, 0), state, $"Unmatched or misoriented seam {edge}");

        void Add(Point a, Point b)
        {
            var direction = a.CompareTo(b) < 0 ? 1 : -1;
            var edge = direction > 0 ? (a, b) : (b, a);
            var prior = edges.GetValueOrDefault(edge);
            edges[edge] = (prior.Count + 1, prior.Balance + direction);
        }
    }

    private static string Hash(PlanetVolumeMesh mesh)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[26];
        foreach (var triangle in mesh.Triangles)
        {
            Add(triangle.A, bytes);
            Add(triangle.B, bytes);
            Add(triangle.C, bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());

        void Add(VolumeVertex vertex, Span<byte> buffer)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer, vertex.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(buffer[4..], vertex.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(buffer[8..], vertex.Position.Z);
            BinaryPrimitives.WriteSingleLittleEndian(buffer[12..], vertex.Normal.X);
            BinaryPrimitives.WriteSingleLittleEndian(buffer[16..], vertex.Normal.Y);
            BinaryPrimitives.WriteSingleLittleEndian(buffer[20..], vertex.Normal.Z);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[24..], vertex.Material);
            hash.AppendData(buffer);
        }
    }

    private readonly record struct Point(Int128 X, Int128 Y, Int128 Z) : IComparable<Point>
    {
        public static Point At(PlanetVolumeMesh mesh, Vector3 position) => new(
            (Int128)mesh.Origin.X * 131072 + checked((long)(position.X * 131072)),
            (Int128)mesh.Origin.Y * 131072 + checked((long)(position.Y * 131072)),
            (Int128)mesh.Origin.Z * 131072 + checked((long)(position.Z * 131072)));

        public int CompareTo(Point other)
        {
            var result = X.CompareTo(other.X);
            if (result != 0)
                return result;
            result = Y.CompareTo(other.Y);
            return result != 0 ? result : Z.CompareTo(other.Z);
        }
    }

    private sealed class Window : IDisposable
    {
        public Window(PlanetDescriptor descriptor, LatticeCoordinate shared, bool exactZeros = false)
        {
            Store = new PlanetBrickStore(new Source(descriptor, shared, exactZeros),
                64L * PlanetBrickStore.BrickPayloadBytes, 64);
            var (brick, _, _, _) = shared.Locate();
            var requests = new List<VolumeMeshRequest>();
            for (var z = 0; z < 4; z++)
            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                var key = new LatticeBrick(brick.X - 2 + x, brick.Y - 2 + y, brick.Z - 2 + z);
                Assert.IsTrue(Store.TryGenerate(key));
                requests.Add(new VolumeMeshRequest(key, (x < 2) == (shared.X >= 0) && (y < 2) == (shared.Y >= 0) && (z < 2) == (shared.Z >= 0) ? 1 : 2));
            }
            Requests = requests.ToArray();
        }

        public PlanetBrickStore Store { get; }
        public VolumeMeshRequest[] Requests { get; }

        public void Dispose()
        {
            foreach (var request in Requests)
                Store.Evict(request.Brick);
        }
    }

    private static PlanetDescriptor Descriptor(double radius) =>
        new(new Guid("00112233-4455-6677-8899-aabbccddeeff"), new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"),
            42, 1, 1, 1, new string('a', 64), radius);

    private sealed class Source(PlanetDescriptor descriptor, LatticeCoordinate center, bool exactZeros) : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => descriptor;

        public long SampleDensity(LatticeCoordinate p)
        {
            if (exactZeros)
            {
                var dx = p.X - center.X;
                var dy = p.Y - center.Y;
                var dz = p.Z - center.Z;
                return dx * dx + dy * dy + dz * dz - 576;
            }
            var x = (p.X - center.X) * 10 + 1;
            var y = (p.Y - center.Y) * 10 + 3;
            var z = (p.Z - center.Z) * 10 - 7;
            return x * x + y * y + z * z - 56169;
        }

        public ushort SampleMaterial(LatticeCoordinate cell) => (ushort)((cell.X & 1) == 0 ? 7 : 19);
    }
}
