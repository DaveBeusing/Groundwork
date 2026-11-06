namespace Groundwork.Voxels;

/// <summary>The state of a world cell in a stable collision view.</summary>
public enum VoxelOccupancy
{
    Unavailable,
    Air,
    Solid
}

/// <summary>Supplies occupancy for signed, integer world cell coordinates.</summary>
public interface IVoxelOccupancy
{
    VoxelOccupancy GetCell(int x, int y, int z);
}

/// <summary>The horizontal center and bottom of the body, in units of 1/1,024 voxel.</summary>
public readonly record struct VoxelPose(int X, int Y, int Z);

/// <summary>Movement state on the fixed simulation tick, with velocity in units per tick.</summary>
public readonly record struct VoxelMotionState(VoxelPose Pose, int VerticalVelocity = 0);

public readonly record struct VoxelMotionResult(VoxelMotionState State, bool Grounded, bool StreamingBlocked);

public readonly record struct VoxelSweepResult(VoxelPose Pose, bool Grounded, bool StreamingBlocked);

/// <summary>Resolves static voxel collision for the WP05 upright box.</summary>
public static class VoxelCollision
{
    public const int UnitsPerVoxel = 1_024;
    public const int HalfWidth = 256;
    public const int Height = 1_792;
    public const int MaximumSweep = 8 * UnitsPerVoxel;
    public const int GravityPerTick = 8;
    public const int MaximumFallSpeed = 256;

    /// <summary>Checks availability of the body and any cells immediately under its feet.</summary>
    public static bool HasCoverage(IVoxelOccupancy occupancy, VoxelPose pose)
    {
        ArgumentNullException.ThrowIfNull(occupancy);
        ValidateRange(pose, 0, 0, 0);
        return (InspectPose(occupancy, pose) & PoseContents.Unavailable) == 0;
    }

    internal static bool IsPublishablePose(IVoxelOccupancy occupancy, VoxelPose pose)
    {
        ValidateRange(pose, 0, 0, 0);
        return (InspectPose(occupancy, pose) & (PoseContents.Unavailable | PoseContents.Penetrating)) == 0;
    }

    internal static void ValidatePose(VoxelPose pose) => ValidateRange(pose, 0, 0, 0);

    /// <summary>Applies gravity and resolves one 60 Hz base tick against a single occupancy view.</summary>
    public static VoxelMotionResult Step(IVoxelOccupancy occupancy, VoxelMotionState state, int deltaX, int deltaZ)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(state.VerticalVelocity, -MaximumFallSpeed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(state.VerticalVelocity, MaximumSweep);
        var velocity = Math.Max(state.VerticalVelocity - GravityPerTick, -MaximumFallSpeed);
        var sweep = Sweep(occupancy, state.Pose, deltaX, velocity, deltaZ, out var verticalBlocked);
        if (verticalBlocked || sweep.Grounded || sweep.Pose.Y - state.Pose.Y != velocity)
            velocity = 0;

        return new(new(sweep.Pose, velocity), sweep.Grounded, sweep.StreamingBlocked);
    }

    /// <summary>Sweeps the entire displacement in X, Z, then Y order.</summary>
    public static VoxelSweepResult Sweep(IVoxelOccupancy occupancy, VoxelPose pose, int deltaX, int deltaY, int deltaZ) =>
        Sweep(occupancy, pose, deltaX, deltaY, deltaZ, out _);

    private static VoxelSweepResult Sweep(IVoxelOccupancy occupancy, VoxelPose pose, int deltaX, int deltaY, int deltaZ,
        out bool verticalBlocked)
    {
        ArgumentNullException.ThrowIfNull(occupancy);
        ValidateRange(pose, deltaX, deltaY, deltaZ);
        var initial = InspectPose(occupancy, pose);
        if ((initial & PoseContents.Penetrating) != 0)
            throw new ArgumentException("The initial body overlaps a solid cell.", nameof(pose));

        if ((initial & PoseContents.Unavailable) != 0)
        {
            verticalBlocked = true;
            return new(pose, Grounded: false, StreamingBlocked: true);
        }

        var x = SweepAxis(occupancy, pose, Axis.X, deltaX);
        pose = pose with { X = pose.X + x.Distance };
        var z = SweepAxis(occupancy, pose, Axis.Z, deltaZ);
        pose = pose with { Z = pose.Z + z.Distance };
        var y = SweepAxis(occupancy, pose, Axis.Y, deltaY);
        pose = pose with { Y = pose.Y + y.Distance };

        var final = InspectPose(occupancy, pose);
        verticalBlocked = y.StreamingBlocked || (final & PoseContents.Unavailable) != 0;
        var grounded = deltaY <= 0 && !verticalBlocked && (final & PoseContents.Supported) != 0;
        return new(pose, grounded, x.StreamingBlocked || z.StreamingBlocked || verticalBlocked);
    }

    private static AxisSweep SweepAxis(IVoxelOccupancy occupancy, VoxelPose pose, Axis axis, int distance)
    {
        if (distance == 0)
            return default;

        var (min, max, uMin, uMax, vMin, vMax) = axis switch
        {
            Axis.X => (pose.X - HalfWidth, pose.X + HalfWidth, pose.Y, pose.Y + Height, pose.Z - HalfWidth, pose.Z + HalfWidth),
            Axis.Z => (pose.Z - HalfWidth, pose.Z + HalfWidth, pose.X - HalfWidth, pose.X + HalfWidth, pose.Y, pose.Y + Height),
            _ => (pose.Y, pose.Y + Height, pose.X - HalfWidth, pose.X + HalfWidth, pose.Z - HalfWidth, pose.Z + HalfWidth)
        };

        var sign = Math.Sign(distance);
        var leading = sign > 0 ? max : min;
        var endpoint = leading + distance;
        var first = sign > 0 ? CeilingCell(leading) : FloorCell(leading) - 1;
        var last = sign > 0 ? FloorCell(endpoint) : CeilingCell(endpoint) - 1;
        var uStart = FloorCell(uMin);
        var vStart = FloorCell(vMin);
        var uEnd = FloorCell(uMax - 1);
        var vEnd = FloorCell(vMax - 1);

        // At a floor plane, horizontal motion also needs coverage directly
        // below the foot. A solid cell there allows tangential movement.
        var checkSupport = pose.Y % UnitsPerVoxel == 0;
        var supportOnU = checkSupport && axis == Axis.X;
        var supportOnV = checkSupport && axis == Axis.Z;
        if (supportOnU)
            uStart--;
        if (supportOnV)
            vStart--;

        // A sweep visits at most nine planes, two cells across the body and
        // three vertically (including support), within the 96-cell budget.
        for (var cell = first; sign > 0 ? cell <= last : cell >= last; cell += sign)
        {
            var blocked = false;
            var unavailable = false;
            for (var u = uStart; u <= uEnd; u++)
            for (var v = vStart; v <= vEnd; v++)
            {
                var value = axis switch
                {
                    Axis.X => occupancy.GetCell(cell, u, v),
                    Axis.Z => occupancy.GetCell(u, v, cell),
                    _ => occupancy.GetCell(u, cell, v)
                };
                var supportOnly = (supportOnU && u == uStart) || (supportOnV && v == vStart);
                unavailable |= value is not (VoxelOccupancy.Air or VoxelOccupancy.Solid);
                blocked |= value == VoxelOccupancy.Solid && !supportOnly;
            }

            if (!blocked && !unavailable)
                continue;

            var plane = (long)(sign > 0 ? cell : cell + 1) * UnitsPerVoxel;
            var clamped = (int)(plane - leading);
            // Merely touching unknown space at a side or ceiling is safe.
            var streamingBlocked = unavailable && (clamped != distance || (axis == Axis.Y && sign < 0));
            return new(clamped, streamingBlocked);
        }

        return new(distance, StreamingBlocked: false);
    }

    private static PoseContents InspectPose(IVoxelOccupancy occupancy, VoxelPose pose)
    {
        var contents = PoseContents.Clear;
        var minimumY = FloorCell(pose.Y);
        var firstY = pose.Y % UnitsPerVoxel == 0 ? minimumY - 1 : minimumY;
        var maxX = FloorCell(pose.X + HalfWidth - 1);
        var maxY = FloorCell(pose.Y + Height - 1);
        var maxZ = FloorCell(pose.Z + HalfWidth - 1);
        for (var x = FloorCell(pose.X - HalfWidth); x <= maxX; x++)
        for (var y = firstY; y <= maxY; y++)
        for (var z = FloorCell(pose.Z - HalfWidth); z <= maxZ; z++)
        {
            contents |= occupancy.GetCell(x, y, z) switch
            {
                VoxelOccupancy.Air => PoseContents.Clear,
                VoxelOccupancy.Solid => y < minimumY ? PoseContents.Supported : PoseContents.Penetrating,
                _ => PoseContents.Unavailable
            };
        }

        return contents;
    }

    private static void ValidateRange(VoxelPose pose, int deltaX, int deltaY, int deltaZ)
    {
        if (deltaX is < -MaximumSweep or > MaximumSweep)
            throw new ArgumentOutOfRangeException(nameof(deltaX));
        if (deltaY is < -MaximumSweep or > MaximumSweep)
            throw new ArgumentOutOfRangeException(nameof(deltaY));
        if (deltaZ is < -MaximumSweep or > MaximumSweep)
            throw new ArgumentOutOfRangeException(nameof(deltaZ));

        if (!Fits(pose.X, deltaX, HalfWidth, HalfWidth) || !Fits(pose.Y, deltaY, 0, Height) ||
            !Fits(pose.Z, deltaZ, HalfWidth, HalfWidth))
            throw new ArgumentOutOfRangeException(nameof(pose), "The swept box exceeds fixed-point coordinate limits.");
    }

    private static bool Fits(int position, int delta, int below, int above) =>
        (long)position + Math.Min(0, delta) - below >= int.MinValue &&
        (long)position + Math.Max(0, delta) + above <= int.MaxValue;

    private static int FloorCell(int position) => position / UnitsPerVoxel - (position % UnitsPerVoxel < 0 ? 1 : 0);

    private static int CeilingCell(int position) => position / UnitsPerVoxel + (position % UnitsPerVoxel > 0 ? 1 : 0);

    private enum Axis { X, Z, Y }

    private readonly record struct AxisSweep(int Distance, bool StreamingBlocked);

    [Flags]
    private enum PoseContents { Clear = 0, Unavailable = 1, Penetrating = 2, Supported = 4 }
}
