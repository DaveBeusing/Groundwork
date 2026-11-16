namespace Groundwork.Voxels;

/// <summary>Ordered <see cref="System.Diagnostics.Stopwatch"/> timestamps for one successful terrain build.</summary>
/// <param name="Started">Start of the worker, after claiming the job.</param>
/// <param name="OverlayPrepared">The edit overlay and the refined brick requests are ready.</param>
/// <param name="BricksGenerated">The requested bricks are available in the build's private store.</param>
/// <param name="PlanCreated">The mesh plan has acquired its brick leases.</param>
/// <param name="MeshBuilt">The meshes and the private terrain artifacts are ready.</param>
/// <param name="Completed">The coordinator has accepted the private build result.</param>
public readonly record struct VolumeStreamingWorkProfile(
    long Started, long OverlayPrepared, long BricksGenerated, long PlanCreated, long MeshBuilt, long Completed);
