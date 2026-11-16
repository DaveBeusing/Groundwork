using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Groundwork.Voxels;

/// <summary>Successful work timing, cumulative brick timing and detached output fingerprints.</summary>
/// <remarks>Evidence collection starts at <see cref="VolumeStreamingWorkProfile.MeshBuilt"/> and ends at
/// <see cref="EvidenceFinished"/>. Fingerprints compare observed outputs and do not authorize reuse.</remarks>
public readonly record struct VolumeStreamingWorkDetailedProfile(VolumeStreamingWorkProfile Timing,
    VolumeBrickGenerationTiming BrickTiming, long EvidenceFinished, ReadOnlyMemory<VolumeBrickWorkFingerprint> Bricks,
    ReadOnlyMemory<VolumeMeshWorkFingerprint> Meshes);

/// <summary>Sampled brick payload, requested mesh-plan stride and elapsed Stopwatch ticks to generate the brick.</summary>
public readonly record struct VolumeBrickWorkFingerprint(LatticeBrick Key, int RequestedStride,
    string DensitySha256, string MaterialSha256, long ElapsedTicks);

/// <summary>Output fingerprint for one mesh region. Geometry includes ordered triangles and their transition flags.</summary>
/// <remarks>Elapsed ticks cover the mesh build without shared plan creation or fingerprinting.</remarks>
public readonly record struct VolumeMeshWorkFingerprint(LatticeCoordinate Origin, byte LodMask, int TriangleCount,
    string GeometrySha256, long ElapsedTicks)
{
    private const int VertexBytes = 6 * sizeof(float) + sizeof(ushort);
    private const int TriangleBytes = 3 * VertexBytes + 1;

    internal static VolumeMeshWorkFingerprint Capture(PlanetVolumeMesh mesh, long elapsedTicks, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[32 * TriangleBytes];
        var used = 0;
        foreach (var triangle in mesh.Triangles)
        {
            var encoded = buffer.Slice(used, TriangleBytes);
            WriteVertex(encoded, triangle.A);
            WriteVertex(encoded[VertexBytes..], triangle.B);
            WriteVertex(encoded[(2 * VertexBytes)..], triangle.C);
            encoded[^1] = triangle.IsTransition ? (byte)1 : (byte)0;
            used += TriangleBytes;
            if (used == buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer);
                used = 0;
            }
        }
        hash.AppendData(buffer[..used]);
        return new(mesh.Origin, mesh.LodMask, mesh.Triangles.Length, Convert.ToHexStringLower(hash.GetHashAndReset()), elapsedTicks);
    }

    private static void WriteVertex(Span<byte> bytes, VolumeVertex vertex)
    {
        WriteVector(bytes, vertex.Position);
        WriteVector(bytes[(3 * sizeof(float))..], vertex.Normal);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[(6 * sizeof(float))..], vertex.Material);
    }

    private static void WriteVector(Span<byte> bytes, Vector3 vector)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, vector.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[sizeof(float)..], vector.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes[(2 * sizeof(float))..], vector.Z);
    }
}
