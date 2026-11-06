using System.Numerics;

namespace Groundwork.Rendering;

/// <summary>A mesh placed by a translation in scene space.</summary>
public readonly record struct RenderMeshInstance
{
    public RenderMeshInstance(RenderMesh mesh, Vector3 translation)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!RenderMesh.IsFinite(translation))
        {
            throw new ArgumentException("Mesh translation must be finite.", nameof(translation));
        }

        Mesh = mesh;
        Translation = translation;
    }

    public RenderMesh Mesh { get; }
    public Vector3 Translation { get; }
}

/// <summary>The two complete mesh instances for a single scene handoff.</summary>
public sealed class RenderMeshPair
{
    public RenderMeshPair(RenderMeshInstance first, RenderMeshInstance second)
    {
        if (first.Mesh is null)
        {
            throw new ArgumentException("A mesh is required for the first instance.", nameof(first));
        }

        if (second.Mesh is null)
        {
            throw new ArgumentException("A mesh is required for the second instance.", nameof(second));
        }

        First = first;
        Second = second;
    }

    public RenderMeshInstance First { get; }
    public RenderMeshInstance Second { get; }
}
