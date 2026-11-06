namespace Groundwork.Rendering;

/// <summary>The outcome of declaring or submitting a mesh generation.</summary>
public enum MeshReplacementSubmissionResult
{
    Accepted = 1,
    InvalidGeneration,
    InvalidPair,
    StaleGeneration,
    UndeclaredGeneration,
    DuplicateGeneration,
    NotRunning,
    Closed,
    InvalidRegion
}

/// <summary>One coherent observation of the mesh replacement lifecycle.</summary>
public readonly record struct MeshReplacementSnapshot(
    ulong DesiredGeneration,
    ulong InstalledGeneration,
    ulong LastPresentedGeneration);

/// <summary>An optional capability for renderers that can replace a complete mesh pair.</summary>
public interface IRenderMeshReplacement
{
    /// <summary>Advances desired intent before its mesh build completes.</summary>
    MeshReplacementSubmissionResult TryDeclareMeshGeneration(ulong generation);

    /// <summary>Queues the immutable pair for the current desired generation without waiting for the GPU.</summary>
    MeshReplacementSubmissionResult TrySubmitMeshPair(ulong generation, RenderMeshPair? pair);

    /// <summary>Reads desired, installed and last successfully presented mesh generations.</summary>
    MeshReplacementSnapshot ReadMeshReplacement();
}

/// <summary>An optional capability for renderers that replace a complete mesh region.</summary>
public interface IRenderMeshRegionReplacement
{
    /// <summary>Advances desired intent before its mesh build completes.</summary>
    MeshReplacementSubmissionResult TryDeclareMeshGeneration(ulong generation);

    /// <summary>Queues the immutable region for the current desired generation without waiting for the GPU.</summary>
    MeshReplacementSubmissionResult TrySubmitMeshRegion(ulong generation, RenderMeshRegion? region);

    /// <summary>Reads desired, installed and last successfully presented mesh generations.</summary>
    MeshReplacementSnapshot ReadMeshReplacement();
}
