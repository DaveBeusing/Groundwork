using System.Numerics;
using Groundwork.Voxels;

namespace Groundwork.Voxels.Tests;

[TestClass]
public sealed class PlanetVolumeMesherTests
{
    [TestMethod]
    public void EveryLodLayoutClosesAtTheThreePlaneJunction()
    {
        var descriptor = Descriptor(128);
        var store = new PlanetBrickStore(new Source(descriptor), 8L * PlanetBrickStore.BrickPayloadBytes, 8);
        var inputs = new VolumeMeshInput[8];
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var key = new LatticeBrick(i & 1, (i >> 1) & 1, (i >> 2) & 1);
                Assert.IsTrue(store.TryGenerate(key));
                var lease = store.TryAcquire(key);
                Assert.IsNotNull(lease);
                inputs[i] = new(lease, 1);
            }

            for (var mask = 0; mask < 256; mask++)
            {
                for (var i = 0; i < 8; i++)
                    inputs[i] = inputs[i] with { Stride = (mask & (1 << i)) == 0 ? 1 : 2 };
                var mesh = PlanetVolumeMesher.Build(inputs, 1, long.MaxValue);
                Assert.IsNotNull(mesh);
                Audit(mesh, mask);
            }
        }
        finally
        {
            foreach (var input in inputs)
                input.Brick?.Dispose();
        }
    }

    [TestMethod]
    public void AllTwelveChartsAndEightCornersHaveClosedMixedLodGeometry()
    {
        var points = new HashSet<LatticeCoordinate>();
        foreach (var (face, edge, otherFace, otherEdge) in PlanetCoordinateTests.Seams)
        {
            var descriptor = Descriptor(32 * Math.Sqrt(2));
            var a = descriptor.SampleAt(new PlanetPatchKey(face, 0, 0, 0), edge, 8);
            var b = descriptor.SampleAt(new PlanetPatchKey(otherFace, 0, 0, 0), otherEdge, 8);
            Assert.AreEqual(a, b);
            Assert.IsTrue(points.Add(a.Vertex));
            VerifyChartNeighborhood(descriptor, a.Vertex);
        }
        Assert.HasCount(12, points);

        var corners = new HashSet<LatticeCoordinate>();
        var cornerDescriptor = Descriptor(32 * Math.Sqrt(3));
        foreach (var face in Enum.GetValues<PlanetFace>())
        for (var u = 0; u <= 16; u += 16)
        for (var v = 0; v <= 16; v += 16)
            corners.Add(cornerDescriptor.SampleAt(new PlanetPatchKey(face, 0, 0, 0), u, v).Vertex);
        Assert.HasCount(8, corners);
        foreach (var corner in corners)
            VerifyChartNeighborhood(cornerDescriptor, corner);
    }

    [TestMethod]
    public void PayloadAdmissionAndInputOrderDoNotChangeCanonicalMeshBytes()
    {
        using var region = new Region(Descriptor(128), new LatticeCoordinate(32, 32, 32));
        var inputs = region.Inputs;
        for (var i = 0; i < inputs.Length; i++)
            inputs[i] = inputs[i] with { Stride = i == 0 ? 1 : 2 };
        var accepted = PlanetVolumeMesher.Build(inputs, 7, long.MaxValue);
        Assert.IsNotNull(accepted);
        var bytes = (long)accepted.Triangles.Length * PlanetVolumeMesher.TrianglePayloadBytes;
        Assert.IsNull(PlanetVolumeMesher.Build(inputs, 8, bytes - 1));
        Assert.HasCount(accepted.Triangles.Length, accepted.Triangles.ToArray());
        var exact = PlanetVolumeMesher.Build(inputs, 7, bytes);
        Assert.IsNotNull(exact);
        CollectionAssert.AreEqual(accepted.Triangles.ToArray(), exact.Triangles.ToArray());
        var copies = new VolumeTriangle[4][];
        Parallel.For(0, copies.Length, i =>
        {
            var reordered = (VolumeMeshInput[])inputs.Clone();
            new Random(i).Shuffle(reordered);
            var mesh = PlanetVolumeMesher.Build(reordered, 7, bytes + 1);
            Assert.IsNotNull(mesh);
            copies[i] = mesh.Triangles.ToArray();
        });
        foreach (var copy in copies)
            CollectionAssert.AreEqual(accepted.Triangles.ToArray(), copy);
        Assert.AreEqual((byte)254, accepted.LodMask);
        Assert.AreEqual(7UL, accepted.TerrainRevision);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlanetVolumeMesher.Build(inputs, 0, bytes));
        Assert.ThrowsExactly<ArgumentException>(() => PlanetVolumeMesher.Build(inputs.AsSpan(0, 7), 1, bytes));
    }

    [TestMethod]
    public void CavitiesOverhangsAndExactZeroFieldsAreClosedAndDeterministic()
    {
        foreach (var shape in new[] { 1, 2, 3, 4 })
        {
            using var region = new Region(Descriptor(128), new LatticeCoordinate(32, 32, 32), shape);
            for (var i = 0; i < region.Inputs.Length; i++)
                region.Inputs[i] = region.Inputs[i] with { Stride = i == 0 ? 1 : 2 };
            var mesh = PlanetVolumeMesher.Build(region.Inputs, 1, long.MaxValue);
            Assert.IsNotNull(mesh);
            Audit(mesh, shape);
        }
    }

    private static void VerifyChartNeighborhood(PlanetDescriptor descriptor, LatticeCoordinate shared)
    {
        using var region = new Region(descriptor, shared);
        // One fine octant creates three simultaneous transitions. A fine edge creates two.
        foreach (var mask in new[] { 254, 252, 240 })
        {
            for (var i = 0; i < region.Inputs.Length; i++)
                region.Inputs[i] = region.Inputs[i] with { Stride = (mask & (1 << i)) == 0 ? 1 : 2 };
            var mesh = PlanetVolumeMesher.Build(region.Inputs, 3, long.MaxValue);
            Assert.IsNotNull(mesh);
            Assert.AreEqual((byte)mask, mesh.LodMask);
            Assert.AreEqual(descriptor, mesh.Descriptor);
            Assert.IsGreaterThan(0, mesh.Triangles.Length);
            Audit(mesh, mask);
        }
    }

    private sealed class Region : IDisposable
    {
        public Region(PlanetDescriptor descriptor, LatticeCoordinate center, int shape = 0)
        {
            var store = new PlanetBrickStore(new Source(descriptor, center, shape),
                8L * PlanetBrickStore.BrickPayloadBytes, 8);
            var (brick, _, _, _) = center.Locate();
            try
            {
                for (var i = 0; i < 8; i++)
                {
                    var key = new LatticeBrick(brick.X - 1 + (i & 1),
                        brick.Y - 1 + ((i >> 1) & 1), brick.Z - 1 + ((i >> 2) & 1));
                    Assert.IsTrue(store.TryGenerate(key));
                    var lease = store.TryAcquire(key);
                    Assert.IsNotNull(lease);
                    Inputs[i] = new VolumeMeshInput(lease, 1);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public VolumeMeshInput[] Inputs { get; } = new VolumeMeshInput[8];

        public void Dispose()
        {
            foreach (var input in Inputs)
                input.Brick?.Dispose();
        }
    }

    private static void Audit(PlanetVolumeMesh mesh, int mask)
    {
        var attributes = new Dictionary<Vector3, VolumeVertex>();
        var edges = new Dictionary<(Vector3 A, Vector3 B), (int Count, int Balance)>();
        foreach (var triangle in mesh.Triangles)
        {
            var a = triangle.A.Position;
            var b = triangle.B.Position;
            var c = triangle.C.Position;
            var cross = Vector3.Cross(b - a, c - a);
            Assert.AreNotEqual(Vector3.Zero, cross);
            Assert.IsGreaterThan(0f, Vector3.Dot(cross, triangle.A.Normal + triangle.B.Normal + triangle.C.Normal));
            foreach (var vertex in new[] { triangle.A, triangle.B, triangle.C })
            {
                Assert.IsTrue(float.IsFinite(vertex.Normal.X) && float.IsFinite(vertex.Normal.Y) && float.IsFinite(vertex.Normal.Z));
                if (attributes.TryGetValue(vertex.Position, out var prior))
                {
                    Assert.AreEqual(prior.Material, vertex.Material);
                    Assert.AreEqual(prior.Normal, vertex.Normal);
                }
                else
                    attributes.Add(vertex.Position, vertex);
            }
            Add(a, b);
            Add(b, c);
            Add(c, a);
        }
        foreach (var (edge, count) in edges)
            Assert.AreEqual((2, 0), count, $"Mask {mask}, edge {edge}");

        void Add(Vector3 a, Vector3 b)
        {
            var sign = Compare(a, b) < 0 ? 1 : -1;
            var edge = sign > 0 ? (a, b) : (b, a);
            var old = edges.GetValueOrDefault(edge);
            edges[edge] = (old.Count + 1, old.Balance + sign);
        }
    }

    private static int Compare(Vector3 a, Vector3 b)
    {
        var result = a.X.CompareTo(b.X);
        if (result != 0) return result;
        result = a.Y.CompareTo(b.Y);
        return result != 0 ? result : a.Z.CompareTo(b.Z);
    }

    private static PlanetDescriptor Descriptor(double radius) =>
        new(new Guid("00112233-4455-6677-8899-aabbccddeeff"), new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"),
            42, 1, 1, 1, new string('a', 64), radius);

    private sealed class Source(PlanetDescriptor descriptor, LatticeCoordinate? center = null, int shape = 0) : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => descriptor;
        public long SampleDensity(LatticeCoordinate p)
        {
            var c = center ?? new LatticeCoordinate(32, 32, 32);
            var x = p.X - c.X;
            var y = p.Y - c.Y;
            var z = p.Z - c.Z;
            var sphere = 10 * ((x + 1) * (x + 1) + (y + 2) * (y + 2) + (z + 3) * (z + 3)) - 2997;
            return shape switch
            {
                1 => Math.Max(sphere, 700 - 10 * (x * x + y * y + z * z)),
                2 => Math.Min(Box(0, -4, 0, 4, 12, 4), Box(0, 8, 0, 18, 3, 11)),
                3 => x * x + y * y + z * z - 256,
                4 => sphere + ((x & 1) == 0 ? -50 : 50),
                _ => sphere
            };

            long Box(long cx, long cy, long cz, long ex, long ey, long ez) =>
                Math.Max(Math.Abs(x - cx) - ex, Math.Max(Math.Abs(y - cy) - ey, Math.Abs(z - cz) - ez));
        }        public ushort SampleMaterial(LatticeCoordinate cell) => (ushort)((cell.X & 1) == 0 ? 7 : 19);
    }
}
