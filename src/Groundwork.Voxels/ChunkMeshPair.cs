namespace Groundwork.Voxels;

/// <summary>Two complete meshes from the same captured neighbor pair, ordered low to high on their shared axis.</summary>
public sealed class ChunkMeshPair
{
    private readonly ChunkSnapshot _lowSnapshot;
    private readonly ChunkSnapshot _highSnapshot;

    internal ChunkMeshPair(ChunkSnapshot lowSnapshot, ChunkSnapshot highSnapshot, ChunkMesh low, ChunkMesh high)
    {
        _lowSnapshot = lowSnapshot;
        _highSnapshot = highSnapshot;
        Low = low;
        High = high;
    }

    public ChunkMesh Low { get; }

    public ChunkMesh High { get; }

    /// <summary>Checks both captured snapshots against the scene's current pair before handoff.</summary>
    public bool Matches(ChunkSnapshot? first, ChunkSnapshot? second) =>
        (ReferenceEquals(first, _lowSnapshot) && ReferenceEquals(second, _highSnapshot)) ||
        (ReferenceEquals(first, _highSnapshot) && ReferenceEquals(second, _lowSnapshot));
}
