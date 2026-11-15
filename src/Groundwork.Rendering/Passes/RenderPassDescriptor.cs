using System.Collections.Immutable;

namespace Groundwork.Rendering.Passes;

/// <summary>Nonzero stable pass identifier. Lower IDs break ties among ready passes.</summary>
public readonly record struct RenderPassId(uint Value);

/// <summary>A read or terminal consumption of an initialized range in the required state.</summary>
public readonly record struct RenderPassInput(
    RenderResourceId Resource,
    RenderSubresourceRange Range,
    RenderResourceState State);

public enum RenderLoadAction
{
    /// <summary>Preserve existing initialized contents as a read and write of the range.</summary>
    Load = 1,

    /// <summary>Initialize the full attachment range with a clear before writing.</summary>
    Clear,

    /// <summary>Discard prior contents before writing. A full write can define new contents.</summary>
    Discard
}

public enum RenderStoreAction
{
    /// <summary>Retain the resulting contents, which remain undefined after a partial discard/write.</summary>
    Store = 1,

    /// <summary>Invalidate the range for subsequent reads or loads.</summary>
    Discard
}

public enum RenderWriteCoverage
{
    /// <summary>The write alone cannot establish initialization of every covered subresource.</summary>
    Partial = 1,

    /// <summary>The writer guarantees that every covered subresource is completely initialized.</summary>
    Complete
}

/// <summary>A write of the covered range. Load also reads existing contents.</summary>
public readonly record struct RenderPassOutput(
    RenderResourceId Resource,
    RenderSubresourceRange Range,
    RenderResourceState State,
    RenderLoadAction Load,
    RenderStoreAction Store,
    RenderWriteCoverage Coverage);

/// <summary>
/// A pass with ordered input and output lists. Dependencies name passes that must precede
/// this pass, including transitive dependencies for overlapping conflicting accesses.
/// A default immutable array is treated as empty.
/// </summary>
public readonly record struct RenderPassDescriptor(
    RenderPassId Id,
    ImmutableArray<RenderPassId> Dependencies,
    ImmutableArray<RenderPassInput> Inputs,
    ImmutableArray<RenderPassOutput> Outputs);

/// <summary>One branch of portable pass declarations ready for validation.</summary>
public readonly record struct RenderPlanDescriptor(
    ImmutableArray<RenderResourceDescriptor> Resources,
    ImmutableArray<RenderPassDescriptor> Passes);

/// <summary>A reusable portable topology with its validated deterministic pass order.</summary>
/// <remarks>The render owner must still validate live resource bindings before recording.</remarks>
public sealed class ValidatedRenderPlan
{
    internal ValidatedRenderPlan(
        ImmutableArray<RenderResourceDescriptor> resources, ImmutableArray<RenderPassDescriptor> passes)
    {
        Resources = resources;
        Passes = passes;
    }

    public ImmutableArray<RenderResourceDescriptor> Resources { get; }
    public ImmutableArray<RenderPassDescriptor> Passes { get; }
}
