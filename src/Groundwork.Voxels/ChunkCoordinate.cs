using System.Numerics;

namespace Groundwork.Voxels;

/// <summary>The signed grid coordinate of a chunk.</summary>
public readonly record struct ChunkCoordinate(int X, int Y, int Z)
{
    public const int Minimum = -1024;
    public const int Maximum = 1023;

    public bool IsValid => X is >= Minimum and <= Maximum &&
        Y is >= Minimum and <= Maximum && Z is >= Minimum and <= Maximum;

    public Vector3 GetOrigin()
    {
        if (!IsValid)
        {
            throw new ArgumentException("The chunk coordinate is outside the supported grid.");
        }

        return new(X * ChunkSnapshot.Size, Y * ChunkSnapshot.Size, Z * ChunkSnapshot.Size);
    }
}
