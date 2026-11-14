using System.Numerics;
using System.Runtime.InteropServices;

namespace Groundwork.Rendering;

public readonly record struct PropCellKey(long X, long Y, long Z)
{
    public int CompareTo(PropCellKey other)
    {
        var x = X.CompareTo(other.X);
        if (x != 0)
            return x;
        var y = Y.CompareTo(other.Y);
        return y != 0 ? y : Z.CompareTo(other.Z);
    }
}

/// <summary>Exact identity, independent of origin, residency and LOD.</summary>
public readonly record struct PropInstanceId(ulong Seed, uint Recipe, PropCellKey Cell, uint Ordinal);

public readonly record struct PropPoint(double X, double Y, double Z)
{
    internal bool IsValid => Math.Abs(X) <= 1e12 && Math.Abs(Y) <= 1e12 && Math.Abs(Z) <= 1e12;

    public PropPoint RelativeTo(PropPoint origin) => new(X - origin.X, Y - origin.Y, Z - origin.Z);

    internal double LengthSquared => X * X + Y * Y + Z * Z;
}

public readonly record struct PropPublication(Guid World, ulong RendererEpoch, ulong TerrainRevision, ulong CoverageGeneration)
{
    internal bool IsValid => World != Guid.Empty && RendererEpoch != 0 && TerrainRevision != 0 && CoverageGeneration != 0;
}

public readonly record struct PropCellRequest(PropCellKey Cell, PropPublication Publication, ulong Generation);

/// <summary>Model radius encloses all mesh LODs about the instance origin.</summary>
public readonly record struct PropModel(uint Model, uint Material, float Radius, float Range, float NearEnter, float NearExit)
{
    internal bool IsValid => Model != 0 && float.IsFinite(Radius) && Radius >= 0 &&
        float.IsFinite(Range) && Range > 0 && NearEnter >= 0 &&
        NearEnter <= NearExit && NearExit <= Range;
}

public readonly record struct PropPlacement(uint Ordinal, int ModelIndex, Vector3 Translation, Quaternion Orientation, float Scale, uint Tint);

/// <summary>32-byte immutable GPU payload, using four signed normalized 16-bit quaternion components.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct PropInstanceData(Vector3 Translation, ulong Rotation, float Scale, uint Tint, uint CellSlot)
{
    public const int ByteSize = 32;

    internal static PropInstanceData Pack(in PropPlacement placement, uint slot)
    {
        var q = Quaternion.Normalize(placement.Orientation);
        // q and -q describe the same rotation. Choose a single representation.
        if (q.W < 0 || (q.W == 0 && (q.Z < 0 || (q.Z == 0 && (q.Y < 0 || (q.Y == 0 && q.X < 0))))))
            q = -q;
        static ulong Part(float value) => unchecked((ushort)(short)MathF.Round(value * 32767));
        return new(placement.Translation, Part(q.X) | (Part(q.Y) << 16) |
            (Part(q.Z) << 32) | (Part(q.W) << 48), placement.Scale, placement.Tint, slot);
    }

    public Quaternion DecodeRotation()
    {
        static float Part(ulong bits) => unchecked((short)bits) / 32767f;
        return Quaternion.Normalize(new Quaternion(Part(Rotation), Part(Rotation >> 16), Part(Rotation >> 32), Part(Rotation >> 48)));
    }
}

public enum PropCellResult { Accepted, Pending, MissingCoverage, Stale, Quota, Invalid }

public readonly record struct PropCellLimits(int Cells, int PendingCells, int PendingInstances, int ResidentInstances, int Groups);

public readonly record struct PropCellUsage(int Cells, int PendingCells, int PendingInstances, int ResidentInstances);

/// <summary>Inward-facing unit plane in camera-relative metres. Positive distance is inside.</summary>
public readonly record struct PropFrustumPlane(Vector3 Normal, double Offset)
{
    internal bool IsValid => Math.Abs(Normal.LengthSquared() - 1) <= 0.0001f && double.IsFinite(Offset);
    internal double Evaluate(PropPoint point) => point.X * Normal.X + point.Y * Normal.Y + point.Z * Normal.Z + Offset;
}

public readonly record struct VisibleProp(PropInstanceId Id, uint Model, uint Material, byte Lod, uint CellSlot, int InstanceIndex)

{
    internal int CompareTo(VisibleProp other)
    {
        var model = Model.CompareTo(other.Model);
        if (model != 0)
            return model;
        var material = Material.CompareTo(other.Material);
        if (material != 0)
            return material;
        var lod = Lod.CompareTo(other.Lod);
        if (lod != 0)
            return lod;
        var cell = Id.Cell.CompareTo(other.Id.Cell);
        return cell != 0 ? cell : Id.Ordinal.CompareTo(other.Id.Ordinal);
    }
}
