namespace Groundwork.Rendering.Passes;

/// <summary>Portable admission limits applied before allocating validation scratch.</summary>
public static class RenderPlanLimits
{
    public const int Passes = 64;
    public const int Edges = 256;
    public const int Resources = 128;
    public const int Accesses = 512;
    public const int ImportedRanges = 256;

    /// <summary>Maximum number of subresource content records across all resources.</summary>
    public const int TrackedSubresources = 4096;

    /// <summary>Maximum sum of subresources visited by inputs, outputs and imported ranges.</summary>
    public const int RangeVisits = 65536;

    public const int TextureDimension = 16384;
    public const int ArrayLayers = 256;
    public const int BufferBytes = 1 << 30;
}

public enum RenderPlanError
{
    None,
    PassLimit,
    EdgeLimit,
    ResourceLimit,
    AccessLimit,
    ImportedRangeLimit,
    ScratchLimit,
    RangeWorkLimit,
    InvalidPassId,
    DuplicatePass,
    UnknownDependency,
    DuplicateDependency,
    Cycle,
    InvalidResourceId,
    DuplicateResource,
    AmbiguousResourceGeneration,
    AliasedResource,
    InvalidExtent,
    InvalidResource,
    InvalidImport,
    UnknownResource,
    StaleResourceGeneration,
    InvalidSubresourceRange,
    InvalidState,
    InvalidOutputAction,
    ConflictingAccess,
    MissingDependency,
    MissingProducer,
    UninitializedContent,
    UseAfterTerminal
}

/// <summary>First validation failure, with the relevant declarations where applicable.</summary>
public readonly record struct RenderPlanFailure(
    RenderPlanError Error,
    RenderPassId Pass = default,
    RenderResourceId Resource = default,
    RenderPassId OtherPass = default);
