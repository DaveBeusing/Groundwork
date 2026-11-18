using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Groundwork.Voxels;

/// <summary>A versioned, immutable baseline sampler. Calls may run concurrently and must return stable values.</summary>
public interface IPlanetBrickSource
{
    PlanetDescriptor Descriptor { get; }
    long SampleDensity(LatticeCoordinate vertex);
    ushort SampleMaterial(LatticeCoordinate cell);
}

public enum BrickAvailability
{
    Ungenerated,
    Generating,
    Known,
    Unavailable,
}

/// <summary>
/// Sparse baseline storage scoped to a single planet descriptor. Quotas cover logical array payload
/// (including in-flight and retained bricks) and the number of live reader leases.
/// </summary>
public sealed class PlanetBrickStore
{
    public const int CellEdge = PlanetDescriptor.BrickEdge;
    public const int VertexEdge = CellEdge + 1;
    public const int VertexCount = VertexEdge * VertexEdge * VertexEdge;
    public const int CellCount = CellEdge * CellEdge * CellEdge;
    public const int BrickPayloadBytes = checked(VertexCount * sizeof(long) + CellCount * sizeof(ushort));

    private readonly object _gate = new();
    private readonly Dictionary<LatticeBrick, Entry> _entries = new();
    private readonly IPlanetBrickSource _source;
    private readonly long _payloadLimit;
    private readonly int _leaseLimit;
    private int _leases;
    private long _chargedBytes;

    public PlanetBrickStore(IPlanetBrickSource source, long payloadByteLimit, int leaseLimit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadByteLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(leaseLimit);

        Descriptor = source.Descriptor ?? throw new ArgumentException("The source requires a descriptor.", nameof(source));
        if (Descriptor.TerrainSchemaVersion != PlanetDensityField.TerrainSchemaVersion)
            throw new ArgumentException("The brick store does not support the source terrain schema.", nameof(source));

        _source = source;
        _payloadLimit = payloadByteLimit;
        _leaseLimit = leaseLimit;
    }

    public PlanetDescriptor Descriptor { get; }

    public long ChargedPayloadBytes
    {
        get
        {
            lock (_gate)
                return _chargedBytes;
        }
    }

    public BrickAvailability GetAvailability(LatticeBrick key)
    {
        lock (_gate)
            return _entries.TryGetValue(key, out var entry) ? entry.Availability : BrickAvailability.Ungenerated;
    }

    /// <summary>
    /// Generates a brick outside the store lock. Returns false under quota pressure or while the key
    /// is generating or retiring. Source failures and cancellation release the reservation.
    /// </summary>
    public bool TryGenerate(LatticeBrick key, CancellationToken cancellationToken = default) =>
        TryGenerateCore(key, false, out _, cancellationToken);

    internal bool TryGenerateProfiled(LatticeBrick key, out VolumeBrickGenerationTiming timing,
        CancellationToken cancellationToken = default) =>
        TryGenerateCore(key, true, out timing, cancellationToken);

    // The caller proves equivalent sampling inputs and holds a charged reader on the donor generation.
    // Each store retains its own entry, leases and full payload charge.
    internal bool TryImport(LatticeBrick key, PlanetBrickStore donor)
    {
        long[] density;
        ushort[] materials;
        lock (donor._gate)
        {
            if (!donor._entries.TryGetValue(key, out var source) || source.Availability != BrickAvailability.Known ||
                source.Density is not { } sourceDensity || source.Materials is not { } sourceMaterials)
                return false;
            density = sourceDensity;
            materials = sourceMaterials;
        }

        lock (_gate)
        {
            if (_entries.ContainsKey(key) || _payloadLimit - _chargedBytes < BrickPayloadBytes)
                return false;
            ValidateSharedVertices(key, density);
            _entries.Add(key, new Entry(key)
            {
                Density = density,
                Materials = materials,
                Availability = BrickAvailability.Known
            });
            _chargedBytes += BrickPayloadBytes;
            return true;
        }
    }

    private bool TryGenerateCore(LatticeBrick key, bool measure, out VolumeBrickGenerationTiming timing,
        CancellationToken cancellationToken)
    {
        timing = default;
        var started = measure ? Stopwatch.GetTimestamp() : 0;
        cancellationToken.ThrowIfCancellationRequested();
        var origin = key.Vertex(0, 0, 0);
        var upper = key.Vertex(CellEdge, CellEdge, CellEdge);
        if (origin.X < -PlanetDensityField.MaximumCoordinate || origin.Y < -PlanetDensityField.MaximumCoordinate ||
            origin.Z < -PlanetDensityField.MaximumCoordinate || upper.X > PlanetDensityField.MaximumCoordinate ||
            upper.Y > PlanetDensityField.MaximumCoordinate || upper.Z > PlanetDensityField.MaximumCoordinate)
            throw new ArgumentOutOfRangeException(nameof(key), "Brick vertices must fit the supported density domain.");

        Entry entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
                return existing.Availability == BrickAvailability.Known;
            if (_payloadLimit - _chargedBytes < BrickPayloadBytes)
                return false;

            entry = new Entry(key);
            _entries.Add(key, entry);
            _chargedBytes += BrickPayloadBytes;
        }

        var published = false;
        try
        {
            var reserved = measure ? Stopwatch.GetTimestamp() : 0;
            cancellationToken.ThrowIfCancellationRequested();
            var density = new long[VertexCount];
            var materials = new ushort[CellCount];
            var allocated = measure ? Stopwatch.GetTimestamp() : 0;
            long densityTicks = 0, materialTicks = 0;
            if (measure || _source is PlanetDensityField || _source is VolumeEditOverlay { SupportsRowSampling: true })
            {
                (densityTicks, materialTicks) = SampleRows(origin, density, materials, measure, cancellationToken);
            }
            else
            {
                for (var z = 0; z < VertexEdge; z++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var y = 0; y < VertexEdge; y++)
                    for (var x = 0; x < VertexEdge; x++)
                    {
                        var coordinate = new LatticeCoordinate(origin.X + x, origin.Y + y, origin.Z + z);
                        density[Index(x, y, z, VertexEdge)] = _source.SampleDensity(coordinate);
                        if (x < CellEdge && y < CellEdge && z < CellEdge)
                        {
                            var material = _source.SampleMaterial(coordinate);
                            if (material == 0)
                                throw new InvalidOperationException("A generated cell must have a nonzero material identifier.");
                            materials[Index(x, y, z, CellEdge)] = material;
                        }
                    }
                }
            }

            var sampled = measure ? Stopwatch.GetTimestamp() : 0;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Availability != BrickAvailability.Generating)
                    return false;

                var seamStart = measure ? Stopwatch.GetTimestamp() : 0;
                ValidateSharedVertices(key, density);
                var seams = measure ? Stopwatch.GetTimestamp() - seamStart : 0;
                entry.Density = density;
                entry.Materials = materials;
                entry.Availability = BrickAvailability.Known;
                published = true;
                if (measure)
                {
                    var publish = Stopwatch.GetTimestamp() - sampled - seams;
                    timing = new(reserved - started, allocated - reserved, densityTicks, materialTicks, seams, publish);
                }
                return true;
            }
        }
        finally
        {
            if (!published)
            {
                lock (_gate)
                    ReleaseEntry(entry);
            }
        }
    }

    private (long DensityTicks, long MaterialTicks) SampleRows(LatticeCoordinate origin, long[] density,
        ushort[] materials, bool measure, CancellationToken cancellationToken)
    {
        long densityTicks = 0, materialTicks = 0;
        for (var z = 0; z < VertexEdge; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var y = 0; y < VertexEdge; y++)
            {
                // The source is immutable. Group each row by sample type to avoid a timer read for each value.
                var rowStart = measure ? Stopwatch.GetTimestamp() : 0;
                var row = new LatticeCoordinate(origin.X, origin.Y + y, origin.Z + z);
                SampleDensityRow(row, density.AsSpan(Index(0, y, z, VertexEdge), VertexEdge));
                var densityEnd = measure ? Stopwatch.GetTimestamp() : 0;
                densityTicks += densityEnd - rowStart;
                if (y == CellEdge || z == CellEdge)
                    continue;

                var materialRow = materials.AsSpan(Index(0, y, z, CellEdge), CellEdge);
                SampleMaterialRow(row, materialRow);
                foreach (var material in materialRow)
                {
                    if (material == 0)
                        throw new InvalidOperationException("A generated cell must have a nonzero material identifier.");
                }
                materialTicks += measure ? Stopwatch.GetTimestamp() - densityEnd : 0;
            }
        }
        return (densityTicks, materialTicks);
    }

    private void SampleDensityRow(LatticeCoordinate origin, Span<long> values)
    {
        if (_source is PlanetDensityField field)
        {
            field.SampleDensityRow(origin, values);
            return;
        }
        if (_source is VolumeEditOverlay overlay && overlay.TrySampleDensityRow(origin, values))
            return;
        for (var x = 0; x < values.Length; x++)
            values[x] = _source.SampleDensity(origin with { X = origin.X + x });
    }

    private void SampleMaterialRow(LatticeCoordinate origin, Span<ushort> values)
    {
        if (_source is PlanetDensityField field)
        {
            field.SampleMaterialRow(origin, values);
            return;
        }
        if (_source is VolumeEditOverlay overlay && overlay.TrySampleMaterialRow(origin, values))
            return;
        for (var x = 0; x < values.Length; x++)
            values[x] = _source.SampleMaterial(origin with { X = origin.X + x });
    }

    /// <summary>Hashes each immutable payload separately in native numeric byte order, little-endian on Windows x64.</summary>
    internal (string DensitySha256, string MaterialSha256) Fingerprint(LatticeBrick key)
    {
        long[] density;
        ushort[] materials;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Availability != BrickAvailability.Known ||
                entry.Density is not { } knownDensity || entry.Materials is not { } knownMaterials)
                throw new InvalidOperationException("Brick fingerprints require a published payload.");
            density = knownDensity;
            materials = knownMaterials;
        }

        return (Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(density.AsSpan()))),
            Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(materials.AsSpan()))));
    }

    /// <summary>Acquires a reader of a published brick if a lease slot is available.</summary>
    public BrickLease? TryAcquire(LatticeBrick key)
    {
        lock (_gate)
        {
            if (_leases == _leaseLimit || !_entries.TryGetValue(key, out var entry) ||
                entry.Availability != BrickAvailability.Known)
                return null;

            var lease = new BrickLease(this, entry);
            entry.Readers++;
            _leases++;
            return lease;
        }
    }

    /// <summary>Revokes new access. Generation or existing readers keep the reservation until they finish.</summary>
    public bool Evict(LatticeBrick key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Availability == BrickAvailability.Unavailable)
                return false;

            var release = entry.Availability == BrickAvailability.Known && entry.Readers == 0;
            entry.Availability = BrickAvailability.Unavailable;
            if (release)
                ReleaseEntry(entry);
            return true;
        }
    }

    private void ReleaseReader(Entry entry)
    {
        lock (_gate)
        {
            entry.Readers--;
            _leases--;
            if (entry.Readers == 0 && entry.Availability == BrickAvailability.Unavailable)
                ReleaseEntry(entry);
        }
    }

    private void ReleaseEntry(Entry entry)
    {
        _entries.Remove(entry.Key);
        _chargedBytes -= BrickPayloadBytes;
    }

    // Compare all intersecting faces, edges and corners against published payloads,
    // including evicted payloads still visible to readers. Concurrent generators publish serially.
    private void ValidateSharedVertices(LatticeBrick key, long[] density)
    {
        for (var dz = -1; dz <= 1; dz++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var neighbor = new LatticeBrick(key.X + dx, key.Y + dy, key.Z + dz);
            if (neighbor == key || !_entries.TryGetValue(neighbor, out var entry) || entry.Density is not { } other)
                continue;

            for (var z = Math.Max(0, dz * CellEdge); z <= Math.Min(CellEdge, (dz + 1) * CellEdge); z++)
            for (var y = Math.Max(0, dy * CellEdge); y <= Math.Min(CellEdge, (dy + 1) * CellEdge); y++)
            for (var x = Math.Max(0, dx * CellEdge); x <= Math.Min(CellEdge, (dx + 1) * CellEdge); x++)
            {
                var matching = Index(x - dx * CellEdge, y - dy * CellEdge, z - dz * CellEdge, VertexEdge);
                if (density[Index(x, y, z, VertexEdge)] != other[matching])
                    throw new InvalidOperationException("The source returned conflicting density at a shared vertex.");
            }
        }
    }

    private static int Index(int x, int y, int z, int edge) => (z * edge + y) * edge + x;

    private static int CheckedIndex(int x, int y, int z, int edge)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegative(z);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, edge);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, edge);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(z, edge);
        return Index(x, y, z, edge);
    }

    internal sealed class Entry(LatticeBrick key)
    {
        public LatticeBrick Key { get; } = key;
        public BrickAvailability Availability { get; set; } = BrickAvailability.Generating;
        public long[]? Density { get; set; }
        public ushort[]? Materials { get; set; }
        public int Readers { get; set; }
    }

    /// <summary>
    /// An immutable brick reader. Disposal revokes public access immediately; active plan read
    /// sessions defer returning its existing lease slot and allowing retirement until they finish.
    /// </summary>
    public sealed class BrickLease : IDisposable
    {
        private readonly object _gate = new();
        private readonly PlanetBrickStore _store;
        private Entry? _entry;
        private int _readSessions;
        private bool _disposed;

        internal BrickLease(PlanetBrickStore store, Entry entry)
        {
            _store = store;
            _entry = entry;
            Key = entry.Key;
        }

        public LatticeBrick Key { get; }
        public PlanetDescriptor Descriptor => _store.Descriptor;

        /// <summary>Reads a signed density vertex, including the shared upper boundary.</summary>
        public long ReadDensity(int x, int y, int z)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _entry!.Density![CheckedIndex(x, y, z, VertexEdge)];
            }
        }

        /// <summary>Reads a cell material. Its identifier is independent of density sign.</summary>
        public ushort ReadMaterial(int x, int y, int z)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _entry!.Materials![CheckedIndex(x, y, z, CellEdge)];
            }
        }

        // A plan pairs these calls for its lifetime. Its Enter/Exit guard keeps every
        // in-flight extraction inside that lifetime, including concurrent plan disposal.
        // Sessions retain this lease's existing charged store reader and quota slot;
        // they do not acquire another reader, copy payloads or keep uncharged arrays.
        internal void EnterReadSession()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _readSessions = checked(_readSessions + 1);
            }
        }

        internal void ExitReadSession()
        {
            lock (_gate)
            {
                if (_readSessions == 0)
                    throw new InvalidOperationException("The brick has no active read session.");
                _readSessions--;
                if (_disposed && _readSessions == 0)
                    ReleaseReader();
            }
        }

        // Only callers holding their own session and preventing its concurrent exit
        // may use these accessors. VolumeMeshPlan supplies that guard once per build,
        // rather than taking a monitor for each immutable density or material value.
        internal long ReadDensityInSession(int x, int y, int z)
        {
            EnsureReadSession();
            return _entry!.Density![CheckedIndex(x, y, z, VertexEdge)];
        }

        internal ushort ReadMaterialInSession(int x, int y, int z)
        {
            EnsureReadSession();
            return _entry!.Materials![CheckedIndex(x, y, z, CellEdge)];
        }

        private void EnsureReadSession()
        {
            if (Volatile.Read(ref _readSessions) == 0)
                throw new InvalidOperationException("A brick read requires an active read session.");
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (_readSessions == 0)
                    ReleaseReader();
            }
        }

        // Called under the lease gate only after public access is revoked and all
        // sessions finish. No accessor may retain a payload past this quota release.
        private void ReleaseReader()
        {
            var entry = _entry!;
            _entry = null;
            _store.ReleaseReader(entry);
        }
    }
}
