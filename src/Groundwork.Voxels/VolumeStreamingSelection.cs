namespace Groundwork.Voxels;

/// <summary>A bounded 3D window and its collision-support halo. Requests contain canonical bricks.</summary>
public sealed class VolumeStreamingSelection
{
    private readonly VolumeMeshRequest[] _requests;
    private VolumeStreamingSelection(PlanetDescriptor descriptor, LatticeBrick minimum, int width,
        LatticeCoordinate camera, int supportCells, VolumeMeshRequest[] requests, bool requiresFineCoverage)
    {
        Descriptor = descriptor;
        Minimum = minimum;
        Width = width;
        Camera = camera;
        SupportCells = supportCells;
        _requests = requests;
        RequiresFineCoverage = requiresFineCoverage;
    }
    public PlanetDescriptor Descriptor { get; }
    public LatticeBrick Minimum { get; }
    public int Width { get; }
    public LatticeCoordinate Camera { get; }
    public int SupportCells { get; }
    public bool RequiresFineCoverage { get; }
    public ReadOnlySpan<VolumeMeshRequest> Requests => _requests;
    public int MeshCount => Width * Width * Width / 8;

    /// <summary>
    /// Selects a near window with a mandatory support halo. Distance and forward visibility refine
    /// additional bricks. The previous window is retained while its support halo remains covered.
    /// </summary>
    public static VolumeStreamingSelection Select(PlanetDescriptor descriptor, PlanetVector cameraPosition,
        PlanetVector forward, int width = 4, int supportCells = 8, VolumeStreamingSelection? previous = null) =>
        SelectCore(descriptor, cameraPosition, forward, width, supportCells, previous, null);

    /// <summary>Selects fine bricks for the complete swept capsule and optional edit stencil.</summary>
    public static VolumeStreamingSelection SelectCave(PlanetDescriptor descriptor, PlanetVector cameraPosition,
        PlanetVector forward, VolumeCapsule body, PlanetVector displacement, VolumeEditDependencies? edit = null,
        int width = 4, int supportCells = 8, VolumeStreamingSelection? previous = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var scale = descriptor.CellSizeMeters;
        if (!double.IsFinite(body.Radius) || body.Radius <= 0 || body.Radius / scale > 32)
            throw new ArgumentOutOfRangeException(nameof(body));

        var x = Axis(body.A.X, body.B.X, displacement.X);
        var y = Axis(body.A.Y, body.B.Y, displacement.Y);
        var z = Axis(body.A.Z, body.B.Z, displacement.Z);
        var lower = new LatticeCoordinate(x.Low, y.Low, z.Low).Locate().Brick;
        var upper = new LatticeCoordinate(x.High, y.High, z.High).Locate().Brick;
        if (edit is { } footprint)
        {
            if (footprint.Minimum.X > footprint.Maximum.X || footprint.Minimum.Y > footprint.Maximum.Y ||
                footprint.Minimum.Z > footprint.Maximum.Z)
                throw new ArgumentOutOfRangeException(nameof(edit));
            lower = new(Math.Min(lower.X, footprint.Minimum.X), Math.Min(lower.Y, footprint.Minimum.Y),
                Math.Min(lower.Z, footprint.Minimum.Z));
            upper = new(Math.Max(upper.X, footprint.Maximum.X), Math.Max(upper.Y, footprint.Maximum.Y),
                Math.Max(upper.Z, footprint.Maximum.Z));
        }
        return SelectCore(descriptor, cameraPosition, forward, width, supportCells, previous, new(lower, upper));

        (long Low, long High) Axis(double a, double b, double delta)
        {
            var low = Math.Floor((Math.Min(a, b) + Math.Min(0, delta) - body.Radius) / scale);
            var high = Math.Ceiling((Math.Max(a, b) + Math.Max(0, delta) + body.Radius) / scale);
            if (!double.IsFinite(low) || !double.IsFinite(high) ||
                low < -PlanetDensityField.MaximumCoordinate || high > PlanetDensityField.MaximumCoordinate)
                throw new ArgumentOutOfRangeException(nameof(body), "Swept capsule exceeds the density domain.");
            // Reserve both sides of an exactly touched brick face.
            return ((long)low - 1, (long)high);
        }
    }

    private static VolumeStreamingSelection SelectCore(PlanetDescriptor descriptor, PlanetVector cameraPosition,
        PlanetVector forward, int width, int supportCells, VolumeStreamingSelection? previous, VolumeEditDependencies? interest)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (width < 4 || width > VolumeMeshPlan.MaximumBricksPerAxis || (width & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (supportCells < 1 || supportCells > 32)
            throw new ArgumentOutOfRangeException(nameof(supportCells));
        _ = PlanetCoordinates.Project(forward);
        var camera = descriptor.SampleAt(cameraPosition).Vertex;
        var (brick, _, _, _) = camera.Locate();
        var minimum = new LatticeBrick(checked(brick.X - width / 2), checked(brick.Y - width / 2), checked(brick.Z - width / 2));
        if (previous is not null && previous.Descriptor == descriptor && previous.Width == width &&
            Covers(previous.Minimum, width, camera, supportCells))
            minimum = previous.Minimum;
        if (interest is { } required)
        {
            var cameraLow = new LatticeCoordinate(checked(camera.X - supportCells), checked(camera.Y - supportCells),
                checked(camera.Z - supportCells)).Locate().Brick;
            var cameraHigh = new LatticeCoordinate(checked(camera.X + supportCells), checked(camera.Y + supportCells),
                checked(camera.Z + supportCells)).Locate().Brick;
            minimum = new(Fit(minimum.X, cameraLow.X, cameraHigh.X, required.Minimum.X, required.Maximum.X),
                Fit(minimum.Y, cameraLow.Y, cameraHigh.Y, required.Minimum.Y, required.Maximum.Y),
                Fit(minimum.Z, cameraLow.Z, cameraHigh.Z, required.Minimum.Z, required.Maximum.Z));
        }
        _ = minimum.Vertex(0, 0, 0);
        _ = new LatticeBrick(checked(minimum.X + width - 1), checked(minimum.Y + width - 1),
            checked(minimum.Z + width - 1)).Vertex(32, 32, 32);

        var lower = minimum.Vertex(0, 0, 0);
        var upper = new LatticeBrick(minimum.X + width - 1, minimum.Y + width - 1, minimum.Z + width - 1).Vertex(32, 32, 32);
        if (lower.X < -PlanetDensityField.MaximumCoordinate || lower.Y < -PlanetDensityField.MaximumCoordinate ||
            lower.Z < -PlanetDensityField.MaximumCoordinate || upper.X > PlanetDensityField.MaximumCoordinate ||
            upper.Y > PlanetDensityField.MaximumCoordinate || upper.Z > PlanetDensityField.MaximumCoordinate)
            throw new ArgumentOutOfRangeException(nameof(cameraPosition), "Selection exceeds the density domain.");

        var requests = new VolumeMeshRequest[checked(width * width * width)];
        var scale = Math.Max(Math.Abs(forward.X), Math.Max(Math.Abs(forward.Y), Math.Abs(forward.Z)));
        var fx = forward.X / scale;
        var fy = forward.Y / scale;
        var fz = forward.Z / scale;
        var length = Math.Sqrt(fx * fx + fy * fy + fz * fz);
        var index = 0;
        for (var z = 0; z < width; z++)
        for (var y = 0; y < width; y++)
        for (var x = 0; x < width; x++)
        {
            var key = new LatticeBrick(minimum.X + x, minimum.Y + y, minimum.Z + z);
            var origin = key.Vertex(0, 0, 0);
            var dx = (double)origin.X - camera.X + 16;
            var dy = (double)origin.Y - camera.Y + 16;
            var dz = (double)origin.Z - camera.Z + 16;
            var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var support = Math.Abs(dx) <= supportCells + 16 && Math.Abs(dy) <= supportCells + 16 && Math.Abs(dz) <= supportCells + 16;
            var facing = distance == 0 || (dx * fx + dy * fy + dz * fz) / (distance * length) >= 0.25;
            var retained = previous is not null && previous.Descriptor == descriptor && WasFine(previous, key);
            var protectedInterest = interest is { } bounds && key.X >= bounds.Minimum.X && key.X <= bounds.Maximum.X &&
                key.Y >= bounds.Minimum.Y && key.Y <= bounds.Maximum.Y && key.Z >= bounds.Minimum.Z && key.Z <= bounds.Maximum.Z;
            var fine = protectedInterest || support || facing && distance <= 48 || retained && distance <= 64;
            requests[index++] = new VolumeMeshRequest(key, fine ? 1 : 2);
        }
        return new VolumeStreamingSelection(descriptor, minimum, width, camera, supportCells, requests, interest.HasValue);

        long Fit(long preferred, long cameraLow, long cameraHigh, long interestLow, long interestHigh)
        {
            var low = Math.Min(cameraLow, interestLow);
            var high = Math.Max(cameraHigh, interestHigh);
            if ((Int128)high - low >= width)
                throw new ArgumentOutOfRangeException(nameof(interest), "Protected interest exceeds the bounded brick window.");
            return Math.Clamp(preferred, high - width + 1, low);
        }
    }

    internal bool CoversFine(VolumeEditDependencies footprint)
    {
        for (var z = footprint.Minimum.Z; z <= footprint.Maximum.Z; z++)
        for (var y = footprint.Minimum.Y; y <= footprint.Maximum.Y; y++)
        for (var x = footprint.Minimum.X; x <= footprint.Maximum.X; x++)
        {
            var index = (int)(((z - Minimum.Z) * Width + y - Minimum.Y) * Width + x - Minimum.X);
            if (_requests[index].Stride != 1)
                return false;
        }
        return true;
    }

    internal bool Contains(LatticeCoordinate point) => Covers(Minimum, Width, point, SupportCells);

    private static bool Covers(LatticeBrick minimum, int width, LatticeCoordinate point, int halo)
    {
        var origin = minimum.Vertex(0, 0, 0);
        var extent = width * 32;
        return (Int128)point.X - origin.X >= halo && (Int128)point.X - origin.X <= extent - halo &&
            (Int128)point.Y - origin.Y >= halo && (Int128)point.Y - origin.Y <= extent - halo &&
            (Int128)point.Z - origin.Z >= halo && (Int128)point.Z - origin.Z <= extent - halo;
    }

    private static bool WasFine(VolumeStreamingSelection previous, LatticeBrick key)
    {
        foreach (var request in previous._requests)
            if (request.Brick == key)
                return request.Stride == 1;
        return false;
    }
}
