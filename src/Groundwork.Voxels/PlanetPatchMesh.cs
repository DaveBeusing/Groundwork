using System.Numerics;

namespace Groundwork.Voxels;

/// <summary>The edges that must match a neighbor one level coarser than the patch.</summary>
[Flags]
public enum PlanetTransitionEdges
{
    None = 0,
    Bottom = 1,
    Right = 2,
    Top = 4,
    Left = 8
}

/// <summary>Immutable planet-centered geometry with canonical sample identities and outward triangles.</summary>
public sealed class PlanetPatchMesh
{
    private const int Intervals = PlanetCoordinates.IntervalsPerPatch;
    private const int Stride = Intervals + 1;
    private const PlanetTransitionEdges AllEdges = PlanetTransitionEdges.Bottom | PlanetTransitionEdges.Right |
        PlanetTransitionEdges.Top | PlanetTransitionEdges.Left;

    private readonly PlanetVector[] _positions;
    private readonly PlanetSampleKey[] _samples;
    private readonly uint[] _indices;

    private PlanetPatchMesh(PlanetPatchKey patch, PlanetTransitionEdges transitions,
        PlanetVector[] positions, PlanetSampleKey[] samples, uint[] indices)
    {
        Patch = patch;
        Transitions = transitions;
        _positions = positions;
        _samples = samples;
        _indices = indices;
    }

    public PlanetPatchKey Patch { get; }
    public PlanetTransitionEdges Transitions { get; }
    public ReadOnlySpan<PlanetVector> Positions => _positions;
    public ReadOnlySpan<PlanetSampleKey> Samples => _samples;
    public ReadOnlySpan<uint> Indices => _indices;

    /// <summary>
    /// Builds one patch with an optional 2:1 transition at each edge. Transition edges
    /// omit the odd fine samples, so their boundary segments match the coarse neighbor.
    /// </summary>
    /// <param name="patch">The validated cube chart patch to mesh.</param>
    /// <param name="radius">The finite positive reference radius in meters.</param>
    /// <param name="heightMeters">A deterministic height function in meters shared by all patches in the same surface recipe.</param>
    /// <param name="transitions">The edges selected by the balanced quadtree.</param>
    public static PlanetPatchMesh Build(PlanetPatchKey patch, double radius, Func<PlanetSampleKey, double> heightMeters,
        PlanetTransitionEdges transitions = PlanetTransitionEdges.None)
    {
        PlanetCoordinates.ValidatePatch(patch);
        ArgumentNullException.ThrowIfNull(heightMeters);
        if (!double.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if ((transitions & ~AllEdges) != 0 || (transitions != PlanetTransitionEdges.None && patch.Level == 0))
            throw new ArgumentOutOfRangeException(nameof(transitions));

        // Each edge collapse removes eight boundary vertices and one triangle per removed vertex.
        var removed = Intervals / 2 * BitOperations.PopCount((uint)transitions);
        var positions = new PlanetVector[Stride * Stride - removed];
        var samples = new PlanetSampleKey[positions.Length];
        var indices = new uint[(Intervals * Intervals * 2 - removed) * 3];
        Span<uint> vertexAt = stackalloc uint[Stride * Stride];

        var vertex = 0;
        for (var v = 0; v <= Intervals; v++)
        {
            for (var u = 0; u <= Intervals; u++)
            {
                var gridIndex = v * Stride + u;
                if (CollapseVertex(u, v, transitions) != gridIndex)
                    continue;

                var sample = PlanetCoordinates.Sample(patch, u, v);
                var radialDistance = radius + heightMeters(sample);
                if (!double.IsFinite(radialDistance) || radialDistance <= 0)
                    throw new ArgumentException("The height must produce a finite positive radial distance.", nameof(heightMeters));

                var direction = PlanetCoordinates.Direction(sample);
                positions[vertex] = new PlanetVector(radialDistance * direction.X, radialDistance * direction.Y,
                    radialDistance * direction.Z);
                samples[vertex] = sample;
                vertexAt[gridIndex] = (uint)vertex;
                vertex++;
            }
        }

        var index = 0;
        for (var v = 0; v < Intervals; v++)
        {
            for (var u = 0; u < Intervals; u++)
            {
                var bottomLeft = vertexAt[CollapseVertex(u, v, transitions)];
                var bottomRight = vertexAt[CollapseVertex(u + 1, v, transitions)];
                var topRight = vertexAt[CollapseVertex(u + 1, v + 1, transitions)];
                var topLeft = vertexAt[CollapseVertex(u, v + 1, transitions)];
                WriteTriangle(bottomLeft, bottomRight, topRight);
                WriteTriangle(bottomLeft, topRight, topLeft);
            }
        }

        return new PlanetPatchMesh(patch, transitions, positions, samples, indices);

        void WriteTriangle(uint first, uint second, uint third)
        {
            if (first == second || second == third || third == first)
                return;

            indices[index++] = first;
            indices[index++] = second;
            indices[index++] = third;
        }
    }

    // Contract each odd boundary vertex to the preceding even vertex in counterclockwise
    // edge order. Remapping both triangles of each cell handles joint corner topology.
    private static int CollapseVertex(int u, int v, PlanetTransitionEdges transitions)
    {
        if ((u & 1) != 0)
        {
            if (v == 0 && (transitions & PlanetTransitionEdges.Bottom) != 0)
                u--;
            else if (v == Intervals && (transitions & PlanetTransitionEdges.Top) != 0)
                u++;
        }
        else if ((v & 1) != 0)
        {
            if (u == Intervals && (transitions & PlanetTransitionEdges.Right) != 0)
                v--;
            else if (u == 0 && (transitions & PlanetTransitionEdges.Left) != 0)
                v++;
        }

        return v * Stride + u;
    }
}
