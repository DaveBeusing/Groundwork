using System.Numerics;

namespace Groundwork.Rendering.Materials;

/// <summary>Maps an existing nonzero canonical voxel material ID to its visual descriptor.</summary>
public readonly record struct MaterialPaletteEntry(ushort MaterialId, PbrMaterialDescriptor Descriptor);

/// <summary>An immutable visual snapshot indexed by canonical voxel material ID.</summary>
public sealed class MaterialPalette : IEquatable<MaterialPalette>
{
    private readonly MaterialPaletteEntry[] _entries;

    public MaterialPalette(ulong visualRevision, ReadOnlySpan<MaterialPaletteEntry> entries)
    {
        ArgumentOutOfRangeException.ThrowIfZero(visualRevision);
        if (entries.Length > ushort.MaxValue)
            throw new ArgumentException("The palette exceeds the number of nonzero material IDs.", nameof(entries));

        _entries = entries.ToArray();
        Array.Sort(_entries, static (left, right) => left.MaterialId.CompareTo(right.MaterialId));

        ushort previousId = 0;
        foreach (var entry in _entries)
        {
            if (entry.MaterialId == 0)
                throw new ArgumentException("Material ID zero is reserved.", nameof(entries));
            if (entry.MaterialId == previousId)
                throw new ArgumentException($"Material ID {entry.MaterialId} appears more than once.", nameof(entries));
            if (entry.Descriptor is null)
                throw new ArgumentException($"Material ID {entry.MaterialId} requires a descriptor.", nameof(entries));

            previousId = entry.MaterialId;
        }

        VisualRevision = visualRevision;
    }

    /// <summary>A visible, untextured magenta material for absent nonzero IDs.</summary>
    public static PbrMaterialDescriptor MissingMaterial { get; } = new(
        new Vector3(1f, 0f, 1f), metallic: 0f, roughness: 1f, ambientOcclusion: 1f,
        emissive: new Vector3(0.25f, 0f, 0.25f), normalStrength: 0f);

    /// <summary>A positive presentation revision scoped to this palette's sequence of replacements.</summary>
    public ulong VisualRevision { get; }

    /// <summary>The entries in ascending canonical material ID order.</summary>
    public ReadOnlySpan<MaterialPaletteEntry> Entries => _entries;

    /// <summary>Resolves a nonzero canonical ID, using the missing material when the palette has no entry.</summary>
    public PbrMaterialDescriptor Resolve(ushort materialId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(materialId);

        var start = 0;
        var end = _entries.Length - 1;
        while (start <= end)
        {
            var middle = start + (end - start) / 2;
            var entry = _entries[middle];
            if (entry.MaterialId == materialId)
                return entry.Descriptor;
            if (entry.MaterialId < materialId)
                start = middle + 1;
            else
                end = middle - 1;
        }

        return MissingMaterial;
    }

    /// <summary>Accepts a newer snapshot or retains this snapshot when an identical revision is replayed.</summary>
    /// <exception cref="ArgumentException">The candidate is stale or reuses this revision with different entries.</exception>
    public MaterialPalette AcceptReplacement(MaterialPalette candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.VisualRevision < VisualRevision)
            throw new ArgumentException("The candidate palette revision is stale.", nameof(candidate));
        if (candidate.VisualRevision > VisualRevision)
            return candidate;
        if (!Equals(candidate))
            throw new ArgumentException("The candidate palette conflicts with the current visual revision.", nameof(candidate));

        return this;
    }

    public bool Equals(MaterialPalette? other) => ReferenceEquals(this, other) ||
        (other is not null && VisualRevision == other.VisualRevision && Entries.SequenceEqual(other.Entries));

    public override bool Equals(object? obj) => Equals(obj as MaterialPalette);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(VisualRevision);
        foreach (var entry in _entries)
            hash.Add(entry);
        return hash.ToHashCode();
    }

    public static bool operator ==(MaterialPalette? left, MaterialPalette? right) => Equals(left, right);

    public static bool operator !=(MaterialPalette? left, MaterialPalette? right) => !Equals(left, right);
}
