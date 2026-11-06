namespace Groundwork.Rendering;

/// <summary>The complete, ordered mesh instances for one bounded region handoff.</summary>
public sealed class RenderMeshRegion
{
    public const int MaximumInstanceCount = 9;

    private readonly RenderMeshInstance[] _instances;

    public RenderMeshRegion(ReadOnlySpan<RenderMeshInstance> instances)
    {
        if (instances.Length is < 1 or > MaximumInstanceCount)
            throw new ArgumentOutOfRangeException(nameof(instances), $"A region requires one to {MaximumInstanceCount} mesh instances.");

        _instances = instances.ToArray();
        foreach (var instance in _instances)
        {
            if (instance.Mesh is null)
                throw new ArgumentException("Every instance must contain a mesh.", nameof(instances));
        }
    }

    public ReadOnlySpan<RenderMeshInstance> Instances => _instances;
}
