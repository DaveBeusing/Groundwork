using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Groundwork.Voxels;

public enum VolumeBrushOperation { Add = 1, Remove }

/// <summary>Inclusive canonical lattice vertices. Material writes cover their incident cells.</summary>
public readonly record struct VolumeBrushIntent(ulong Tick, ulong Id, ulong ExpectedRevision, LatticeCoordinate Minimum,
    LatticeCoordinate Maximum, VolumeBrushOperation Operation, ushort Material = 0);

/// <summary>Natural deposits consumed by one terrain publication.</summary>
public readonly record struct VolumeOreDepletion(VolumeBrushIntent Edit, ushort Material, int Deposits);

/// <summary>Host state that stages the dependent inventory change at the terrain commit cut.</summary>
public interface IVolumeOreInventoryState : Groundwork.Core.IWorldStateRoot
{
    /// <summary>Version of the inventory scope affected by depletion. Ordinary writes advance it.</summary>
    ulong OreInventoryRevision { get; }

    /// <summary>Returns a new immutable state or null on conflict or capacity failure. Must be bounded and side-effect free.</summary>
    Groundwork.Core.IWorldStateRoot? StageOreDepletion(in VolumeOreDepletion depletion);
}

public enum VolumeEditStatus { Pending, Building, Committed, Rejected, Stale }

/// <summary>A conservative brick dependency box, including normal and 2:1 transition stencils.</summary>
public readonly record struct VolumeEditDependencies(LatticeBrick Minimum, LatticeBrick Maximum);

internal sealed class VolumeEditOverlay : IPlanetBrickSource
{
    // A row is 32 bytes in both the in-memory payload and the canonical encoding.
    private const long Depleted = 1L << 16;
    private const long Placed = 1L << 17;
    private const long MaterialMask = ushort.MaxValue;
    private readonly record struct Row(LatticeCoordinate Key, long Value);
    private readonly IPlanetBrickSource _baseline;
    private readonly Row[] _density;
    private readonly Row[] _materials;
    private readonly int _densityCount;
    private readonly int _materialCount;

    internal VolumeEditOverlay(IPlanetBrickSource baseline)
        : this(baseline, [], 0, [], 0) { }

    private VolumeEditOverlay(IPlanetBrickSource baseline, Row[] density, int densityCount, Row[] materials, int materialCount, Action? check = null)
    {
        _baseline = baseline;
        _density = density;
        _densityCount = densityCount;
        _materials = materials;
        _materialCount = materialCount;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> row = stackalloc byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(row, densityCount);
        BinaryPrimitives.WriteInt32LittleEndian(row[4..], materialCount);
        hash.AppendData(row[..8]);
        foreach (var entry in density.AsSpan(0, densityCount))
        {
            check?.Invoke();
            EncodeRow(entry, row);
            hash.AppendData(row);
        }
        foreach (var entry in materials.AsSpan(0, materialCount))
        {
            check?.Invoke();
            EncodeRow(entry, row);
            hash.AppendData(row);
        }
        Digest = hash.GetHashAndReset();
    }

    internal static VolumeEditOverlay Restore(IPlanetBrickSource baseline, int limit, ReadOnlySpan<byte> bytes,
        Func<ushort, bool> supportsMaterial)
    {
        if (bytes.Length < 8)
            throw new InvalidDataException("Missing overlay counts.");
        var densities = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var materials = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        if (densities < 0 || materials < 0 || (long)densities + materials > limit ||
            bytes.Length != 8 + 32 * (densities + materials))
            throw new InvalidDataException("Invalid overlay bounds.");
        var density = RestoreRows(baseline, bytes[8..], densities, null);
        var material = RestoreRows(baseline, bytes[(8 + 32 * densities)..], materials, supportsMaterial);
        return new VolumeEditOverlay(baseline, density, densities, material, materials);
    }

    private static Row[] RestoreRows(IPlanetBrickSource baseline, ReadOnlySpan<byte> bytes, int count,
        Func<ushort, bool>? supportsMaterial)
    {
        var rows = new Row[count];
        for (var i = 0; i < count; i++)
        {
            var row = bytes[(32 * i)..];
            var key = new LatticeCoordinate(BinaryPrimitives.ReadInt64LittleEndian(row),
                BinaryPrimitives.ReadInt64LittleEndian(row[8..]), BinaryPrimitives.ReadInt64LittleEndian(row[16..]));
            var value = BinaryPrimitives.ReadInt64LittleEndian(row[24..]);
            if (!InRange(key.X) || !InRange(key.Y) || !InRange(key.Z) ||
                i > 0 && Compare(rows[i - 1].Key, key) >= 0)
                throw new InvalidDataException("Invalid overlay sample key.");
            if (supportsMaterial is null ? value is not (-128 or 128) || value == baseline.SampleDensity(key) :
                !ValidMaterial(baseline, key, value, supportsMaterial))
                throw new InvalidDataException("Invalid overlay sample value.");
            rows[i] = new Row(key, value);
        }
        return rows;

        static bool InRange(long value) =>
            value >= -PlanetDensityField.MaximumCoordinate && value <= PlanetDensityField.MaximumCoordinate;
    }

    private static bool ValidMaterial(IPlanetBrickSource baseline, LatticeCoordinate key, long value, Func<ushort, bool> supports)
    {
        var material = (ushort)(value & MaterialMask);
        var flags = value & ~MaterialMask;
        if (material == 0 || !supports(material))
            return false;
        var natural = baseline.SampleMaterial(key);
        if (flags == 0)
            return baseline is not PlanetGeologyField && material != natural;
        return baseline is PlanetGeologyField geology && (flags & ~(Depleted | Placed)) == 0 &&
            ((flags & Depleted) == 0 || natural == geology.OreMaterial) &&
            ((flags & Placed) != 0 || material == geology.OreHostMaterial);
    }

    public PlanetDescriptor Descriptor => _baseline.Descriptor;
    internal byte[] Digest { get; }
    internal int Count => _densityCount + _materialCount;

    public long SampleDensity(LatticeCoordinate vertex) =>
        TryRead(_density, _densityCount, vertex, out var density) ? density : _baseline.SampleDensity(vertex);

    public ushort SampleMaterial(LatticeCoordinate cell) =>
        TryRead(_materials, _materialCount, cell, out var material) ? (ushort)(material & MaterialMask) : _baseline.SampleMaterial(cell);

    internal static long ReservedBytes(int limit) => 64L * limit;

    internal VolumeEditOverlay? Apply(VolumeBrushIntent brush, int limit, Action check, out VolumeOreDepletion depletion)
    {
        depletion = default;
        var deposits = 0;
        var geology = _baseline as PlanetGeologyField;
        var density = new Row[limit];
        var materials = new Row[limit];
        var min = brush.Minimum;
        var max = brush.Maximum;
        var densityCount = Merge(_density, _densityCount, density, limit, min, max, density: true);
        if (densityCount < 0)
            return null;
        // Include all cells incident to modified vertices so their material is resolved at the same cut.
        var materialMin = new LatticeCoordinate(min.X - 1, min.Y - 1, min.Z - 1);
        var materialCount = Merge(_materials, _materialCount, materials, limit - densityCount, materialMin, max, density: false);
        if (materialCount < 0)
            return null;
        if (deposits > 0)
            depletion = new(brush, geology!.OreMaterial, deposits);
        return new VolumeEditOverlay(_baseline, density, densityCount, materials, materialCount, check);

        long EditMaterial(LatticeCoordinate key, long baseline, long previous)
        {
            if (geology is null)
                return brush.Operation == VolumeBrushOperation.Add ? brush.Material : baseline;

            var spent = previous & Depleted;
            if (brush.Operation == VolumeBrushOperation.Add)
                return brush.Material | spent | Placed;
            if ((previous & Placed) == 0 && (spent != 0 || baseline != geology.OreMaterial))
                return previous;

            // A cell depletes only on the transition from any solid corner to all empty.
            var hadSolid = false;
            for (var z = 0; z <= 1; z++)
            for (var y = 0; y <= 1; y++)
            for (var x = 0; x <= 1; x++)
            {
                var corner = new LatticeCoordinate(key.X + x, key.Y + y, key.Z + z);
                var prior = SampleDensity(corner);
                hadSolid |= prior < 0;
                if (prior < 0 && (corner.X < min.X || corner.X > max.X ||
                    corner.Y < min.Y || corner.Y > max.Y || corner.Z < min.Z || corner.Z > max.Z))
                    return previous;
            }
            if (!hadSolid)
                return previous;
            if (baseline != geology.OreMaterial)
                return baseline;
            if ((previous & (Placed | Depleted)) == 0)
                deposits++;
            return geology.OreHostMaterial | Depleted;
        }

        int Merge(Row[] previous, int previousCount, Row[] destination, int budget,
            LatticeCoordinate lower, LatticeCoordinate upper, bool density)
        {
            var old = 0;
            var written = 0;
            for (var x = lower.X; x <= upper.X; x++)
            for (var y = lower.Y; y <= upper.Y; y++)
            for (var z = lower.Z; z <= upper.Z; z++)
            {
                check();
                var key = new LatticeCoordinate(x, y, z);
                while (old < previousCount && Compare(previous[old].Key, key) < 0)
                {
                    check();
                    if (written == budget)
                        return -1;
                    destination[written++] = previous[old++];
                }

                var baseline = density ? _baseline.SampleDensity(key) : _baseline.SampleMaterial(key);
                var value = baseline;
                if (old < previousCount && previous[old].Key == key)
                    value = previous[old++].Value;
                if (density)
                {
                    // Stamp the selected vertices at least half a cell into the intended solid/empty sign.
                    const int strength = PlanetDensityField.UnitsPerCell / 2;
                    value = brush.Operation == VolumeBrushOperation.Add ? Math.Min(value, -strength) : Math.Max(value, strength);
                }
                else
                    value = EditMaterial(key, baseline, value);
                if (value == baseline)
                    continue;
                if (written == budget)
                    return -1;
                destination[written++] = new Row(key, value);
            }
            if (previousCount - old > budget - written)
                return -1;
            previous.AsSpan(old, previousCount - old).CopyTo(destination.AsSpan(written));
            return written + previousCount - old;
        }
    }

    internal VolumeMeshRequest[] Refine(VolumeStreamingSelection selection, Action check)
    {
        var requests = selection.Requests.ToArray();
        var minimum = selection.Minimum;
        var width = selection.Width;
        Mark(_density.AsSpan(0, _densityCount));
        Mark(_materials.AsSpan(0, _materialCount));
        return requests;

        void Mark(ReadOnlySpan<Row> rows)
        {
            foreach (var row in rows)
            {
                check();
                var key = row.Key;
                var low = new LatticeCoordinate(key.X - 4, key.Y - 4, key.Z - 4).Locate().Brick;
                var high = new LatticeCoordinate(key.X + 4, key.Y + 4, key.Z + 4).Locate().Brick;
                for (var z = Math.Max(0, low.Z - minimum.Z); z <= Math.Min(width - 1, high.Z - minimum.Z); z++)
                for (var y = Math.Max(0, low.Y - minimum.Y); y <= Math.Min(width - 1, high.Y - minimum.Y); y++)
                for (var x = Math.Max(0, low.X - minimum.X); x <= Math.Min(width - 1, high.X - minimum.X); x++)
                {
                    var index = (int)((z * width + y) * width + x);
                    requests[index] = requests[index] with { Stride = 1 };
                }
            }
        }
    }

    internal bool TryWrite(Span<byte> destination, out int written)
    {
        written = 0;
        var bytes = 8 + Count * 32;
        if (destination.Length < bytes)
            return false;
        BinaryPrimitives.WriteInt32LittleEndian(destination, _densityCount);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], _materialCount);
        var offset = 8;
        WriteRows(_density.AsSpan(0, _densityCount), destination, ref offset);
        WriteRows(_materials.AsSpan(0, _materialCount), destination, ref offset);
        written = offset;
        return true;
    }

    private static void WriteRows(ReadOnlySpan<Row> rows, Span<byte> destination, ref int offset)
    {
        foreach (var row in rows)
        {
            EncodeRow(row, destination[offset..]);
            offset += 32;
        }
    }

    private static void EncodeRow(Row row, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, row.Key.X);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], row.Key.Y);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], row.Key.Z);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], row.Value);
    }

    private static bool TryRead(Row[] rows, int count, LatticeCoordinate key, out long value)
    {
        var low = 0;
        var high = count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var order = Compare(rows[middle].Key, key);
            if (order == 0)
            {
                value = rows[middle].Value;
                return true;
            }
            if (order < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }
        value = 0;
        return false;
    }

    private static int Compare(LatticeCoordinate first, LatticeCoordinate second)
    {
        var x = first.X.CompareTo(second.X);
        if (x != 0)
            return x;
        var y = first.Y.CompareTo(second.Y);
        return y != 0 ? y : first.Z.CompareTo(second.Z);
    }
}
