namespace Groundwork.Voxels;

/// <summary>Version 2 planet terrain with bounded local cavities and deterministic rock and ore.</summary>
public sealed class PlanetGeologyField : IPlanetBrickSource
{
    public const uint GeneratorVersion = 2;
    private const int CellEdge = 64;
    private const int HalfEdge = CellEdge / 2;
    private const int Unit = PlanetDensityField.UnitsPerCell;
    private const int Fade = 4 * Unit;
    private const int ChamberRadius = 9;
    private const int PassageRadius = 5;
    private const ulong StrataStream = 0x5354524154410002UL;
    private const ulong OreStream = 0x4F52450000000002UL;
    private const ulong PortalStream = 0x504F5254414C0002UL;
    private const ulong ShaftStream = 0x5348414654000002UL;
    private static readonly LatticeCoordinate Hub = new(HalfEdge, HalfEdge, HalfEdge);
    private readonly PlanetDensityField _landform;
    private readonly PlanetGeologyRecipe _recipe;
    private readonly long _portalDepthMargin;

    public PlanetGeologyField(PlanetDescriptor descriptor, PlanetGeologyRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(recipe);
        if (descriptor.GeneratorVersion != GeneratorVersion ||
            descriptor.TerrainSchemaVersion != PlanetDensityField.TerrainSchemaVersion ||
            descriptor.ParameterSha256 != recipe.ParameterSha256)
            throw new ArgumentException("The descriptor does not match the V2 geology recipe.", nameof(descriptor));

        Descriptor = descriptor;
        _recipe = recipe;
        _portalDepthMargin = PortalDepthMargin(recipe.Landform);
        _landform = new PlanetDensityField(new PlanetDescriptor(descriptor.WorldId, descriptor.PlanetId,
            descriptor.Seed, PlanetDensityField.GeneratorVersion, descriptor.TerrainSchemaVersion,
            descriptor.MaterialCatalogVersion, recipe.Landform.ParameterSha256, descriptor.RadiusMeters,
            descriptor.CellSizeMeters), recipe.Landform);
    }

    public PlanetDescriptor Descriptor { get; }
    internal ushort OreMaterial => _recipe.Ore;
    internal ushort OreHostMaterial => _recipe.MiddleRock;

    public long SampleDensity(LatticeCoordinate vertex) => Carve(vertex, _landform.SampleDensity(vertex));

    public ushort SampleMaterial(LatticeCoordinate cell)
    {
        var baseline = _landform.SampleDensity(cell);
        var shift = PlanetDensityField.RoundDivide(
            (long)_landform.Noise(cell, _recipe.StrataNoiseCells, StrataStream) * _recipe.StrataReliefCells * Unit, 32768);
        var depth = -baseline + shift;
        if (depth < (long)_recipe.ShallowBandCells * Unit)
            return _recipe.ShallowRock;
        if (depth >= (long)_recipe.DeepBandCells * Unit)
            return _recipe.DeepRock;

        return _landform.Noise(cell, _recipe.OreNoiseCells, OreStream) >= _recipe.OreThreshold &&
            Carve(cell, baseline) < 0 ? _recipe.Ore : _recipe.MiddleRock;
    }

    private long Carve(LatticeCoordinate vertex, long baseline)
    {
        var depth = -baseline;
        var remainingDepth = (long)_recipe.MaximumCaveDepth * Unit - depth;
        if (remainingDepth <= 0 || depth <= -Fade)
            return baseline;

        var (x, localX) = PlanetDensityField.Grid(vertex.X, CellEdge);
        var (y, localY) = PlanetDensityField.Grid(vertex.Y, CellEdge);
        var (z, localZ) = PlanetDensityField.Grid(vertex.Z, CellEdge);
        var key = new LatticeCoordinate(x, y, z);
        var local = new LatticeCoordinate(localX, localY, localZ);

        var interior = Math.Min(remainingDepth, depth - (long)_recipe.MinimumCaveDepth * Unit);
        var density = baseline;
        if (interior > 0)
        {
            density = Blend(baseline, DistanceSquared(local, Hub, Hub), ChamberRadius, interior);
            for (var axis = 0; _recipe.PortalChance > 0 && axis < 3; axis++)
            {
                density = Math.Max(density, PortalDensity(key, local, axis, positive: false, baseline, interior));
                density = Math.Max(density, PortalDensity(key, local, axis, positive: true, baseline, interior));
            }
        }

        if (_recipe.ShaftChance > 0 && (Hash(key, ShaftStream) >> 48) < (ulong)_recipe.ShaftChance)
        {
            var axis = RadialAxis(key);
            var lateral = (axis + 1) % 3;
            var end = WithAxis(Hub, axis, Axis(key, axis) < 0 ? 16 : 48);
            end = WithAxis(end, lateral, HalfEdge + (Axis(key, lateral) < 0 ? -20 : 20));
            density = Math.Max(density, BlendDistance(baseline, RampDistance(local, end),
                PassageRadius, Math.Min(remainingDepth, depth + Fade)));
        }
        return density;
    }

    private long PortalDensity(LatticeCoordinate key, LatticeCoordinate local, int axis, bool positive,
        long baseline, long depthWeight)
    {
        var owner = positive ? key : WithAxis(key, axis, Axis(key, axis) - 1);
        var hash = Hash(owner, PortalStream + (ulong)axis);
        if ((hash >> 48) >= (ulong)_recipe.PortalChance)
            return baseline;

        // Both sides use the same two lateral offsets and a normal stub. All bends and
        // their support lie strictly inside the cell, so the stubs alone reach the face.
        var first = (axis + 1) % 3;
        var second = (axis + 2) % 3;
        var face = WithAxis(Hub, axis, positive ? CellEdge : 0);
        face = WithAxis(face, first, HalfEdge + (long)(hash % 17) - 8);
        face = WithAxis(face, second, HalfEdge + (long)((hash >> 16) % 17) - 8);
        var approach = WithAxis(face, axis, positive ? 48 : 16);
        var turn = WithAxis(approach, second, HalfEdge);
        var spine = WithAxis(turn, first, HalfEdge);
        var distance = Math.Min(DistanceSquared(local, face, approach), DistanceSquared(local, approach, turn));
        distance = Math.Min(distance, DistanceSquared(local, turn, spine));
        distance = Math.Min(distance, DistanceSquared(local, spine, Hub));
        if (distance >= (PassageRadius + Fade / Unit) * (PassageRadius + Fade / Unit))
            return baseline;

        // Both halves make the same admission decision before carving their paths.
        var centre = new LatticeCoordinate(key.X * CellEdge + face.X, key.Y * CellEdge + face.Y,
            key.Z * CellEdge + face.Z);
        var depth = -_landform.SampleDensity(centre);
        if (depth < (long)_recipe.MinimumCaveDepth * Unit + _portalDepthMargin ||
            depth > (long)_recipe.MaximumCaveDepth * Unit - _portalDepthMargin)
            return baseline;
        return Blend(baseline, distance, PassageRadius, depthWeight);
    }

    private static long PortalDepthMargin(PlanetLandformRecipe recipe)
    {
        // Bound the base field throughout a four-cell box around the common face point.
        // Its radial displacement is below seven cells. Trilinear noise changes by at
        // most its corner range times Manhattan displacement / scale, plus rounding.
        return Fade + 7 * Unit + 1 +
            ScaledChange((long)recipe.HighElevation - recipe.LowElevation, recipe.ContinentScaleCells, 65535) +
            ScaledChange(recipe.Mountains.Amplitude, recipe.Mountains.ScaleCells, 32768) +
            ScaledChange(recipe.Valleys.Amplitude, recipe.Valleys.ScaleCells, 32768) +
            ScaledChange(recipe.Detail.Amplitude, recipe.Detail.ScaleCells, 32768);

        static long ScaledChange(long amplitude, int scale, int divisor)
        {
            var noiseChange = Math.Min(65535, (65535L * 12 + scale - 1) / scale + 3);
            return amplitude == 0 ? 0 : (amplitude * noiseChange + divisor - 1) / divisor + 1;
        }
    }

    private static long Blend(long baseline, long squared, int radius, long depthWeight)
    {
        var outerRadius = radius + Fade / Unit;
        if (squared >= outerRadius * outerRadius)
            return baseline;

        var distance = PlanetDensityField.NearestSquareRoot((UInt128)squared << 16);
        return BlendDistance(baseline, distance, radius, depthWeight);
    }

    private static long BlendDistance(long baseline, long distance, int radius, long depthWeight)
    {
        var outer = radius * Unit + Fade;
        if (distance >= outer)
            return baseline;

        var cavity = radius * Unit - distance;
        var weight = Math.Min(Fade, Math.Min(depthWeight, outer - distance));
        return baseline + PlanetDensityField.RoundDivide(Math.Max(0, cavity - baseline) * weight, Fade);
    }

    private static long RampDistance(LatticeCoordinate local, LatticeCoordinate end)
    {
        var x = local.X - Hub.X;
        var y = local.Y - Hub.Y;
        var z = local.Z - Hub.Z;
        var dx = end.X - Hub.X;
        var dy = end.Y - Hub.Y;
        var dz = end.Z - Hub.Z;
        var lengthSquared = dx * dx + dy * dy + dz * dz;
        var projection = Math.Clamp(x * dx + y * dy + z * dz, 0, lengthSquared);
        var px = x * lengthSquared - dx * projection;
        var py = y * lengthSquared - dy * projection;
        var pz = z * lengthSquared - dz * projection;
        var squared = (UInt128)(px * px + py * py + pz * pz);
        return PlanetDensityField.NearestSquareRoot((squared << 16) / (ulong)(lengthSquared * lengthSquared));
    }

    private ulong Hash(LatticeCoordinate key, ulong stream)
    {
        var hash = PlanetDensityField.Mix(Descriptor.Seed ^ stream);
        hash = PlanetDensityField.Mix(hash ^ unchecked((ulong)key.X));
        hash = PlanetDensityField.Mix(hash ^ unchecked((ulong)key.Y));
        return PlanetDensityField.Mix(hash ^ unchecked((ulong)key.Z));
    }

    private static int RadialAxis(LatticeCoordinate key)
    {
        var x = Math.Abs(checked(key.X * CellEdge + HalfEdge));
        var y = Math.Abs(checked(key.Y * CellEdge + HalfEdge));
        var z = Math.Abs(checked(key.Z * CellEdge + HalfEdge));
        return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
    }

    private static long DistanceSquared(LatticeCoordinate point, LatticeCoordinate a, LatticeCoordinate b)
    {
        static long Delta(long point, long a, long b) => point - Math.Clamp(point, Math.Min(a, b), Math.Max(a, b));
        var x = Delta(point.X, a.X, b.X);
        var y = Delta(point.Y, a.Y, b.Y);
        var z = Delta(point.Z, a.Z, b.Z);
        return x * x + y * y + z * z;
    }

    private static long Axis(LatticeCoordinate point, int axis) => axis switch
    {
        0 => point.X,
        1 => point.Y,
        _ => point.Z,
    };

    private static LatticeCoordinate WithAxis(LatticeCoordinate point, int axis, long value) => axis switch
    {
        0 => point with { X = value },
        1 => point with { Y = value },
        _ => point with { Z = value },
    };
}
