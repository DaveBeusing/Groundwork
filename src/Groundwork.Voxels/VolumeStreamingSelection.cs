namespace Groundwork.Voxels;

/// <summary>A bounded 3D window and its collision-support halo. Requests contain canonical bricks.</summary>
public sealed class VolumeStreamingSelection
{
    private readonly VolumeMeshRequest[] _requests;
    private VolumeStreamingSelection(PlanetDescriptor descriptor, LatticeBrick minimum, int width,
        LatticeCoordinate camera, int supportCells, VolumeMeshRequest[] requests)
    {
        Descriptor = descriptor;
        Minimum = minimum;
        Width = width;
        Camera = camera;
        SupportCells = supportCells;
        _requests = requests;
    }
    public PlanetDescriptor Descriptor { get; }
    public LatticeBrick Minimum { get; }
    public int Width { get; }
    public LatticeCoordinate Camera { get; }
    public int SupportCells { get; }
    public ReadOnlySpan<VolumeMeshRequest> Requests => _requests;
    public int MeshCount => Width * Width * Width / 8;

    /// <summary>
    /// Selects a near window with a mandatory support halo. Distance and forward visibility refine
    /// additional bricks. The previous window is retained while its support halo remains covered.
    /// </summary>
    public static VolumeStreamingSelection Select(PlanetDescriptor descriptor, PlanetVector cameraPosition,
        PlanetVector forward, int width = 4, int supportCells = 8, VolumeStreamingSelection? previous = null)
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
            var fine = support || facing && distance <= 48 || retained && distance <= 64;
            requests[index++] = new VolumeMeshRequest(key, fine ? 1 : 2);
        }
        return new VolumeStreamingSelection(descriptor, minimum, width, camera, supportCells, requests);
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
