using System.Collections.Immutable;

namespace Groundwork.Rendering.Passes;

/// <summary>Identifies one generation of a logical resource slot. Both values must be nonzero.</summary>
public readonly record struct RenderResourceId(ulong Slot, ulong Generation);

/// <summary>
/// Opaque allocation identity supplied by the plan author. Distinct resource declarations must
/// have distinct nonzero allocation identities, even when their declared lifetimes do not overlap.
/// </summary>
public readonly record struct RenderAllocationId(ulong Value);

public enum RenderResourceOrigin
{
    Imported = 1,
    Produced
}

/// <summary>
/// Admission class for selecting a plan branch. Every resource declared by a selected
/// branch must validate, including optional visual resources.
/// </summary>
public enum RenderResourceAdmission
{
    Required = 1,
    OptionalVisual
}

public enum RenderResourceFormat
{
    Buffer = 1,
    Rgba8Unorm,
    Rgba8UnormSrgb,
    Bgra8Unorm,
    Rgba16Float,
    Depth32Float,
    Bc1Unorm,
    Bc1UnormSrgb
}

public enum RenderExtentClass
{
    FixedTexture = 1,
    FrameTexture,
    Buffer
}

/// <summary>
/// Base extent in pixels for a texture, or width in bytes and height one for a buffer.
/// Frame textures declare the same base extent throughout the plan. Mip extents halve each
/// dimension with a floor of one.
/// </summary>
public readonly record struct RenderExtent(RenderExtentClass Class, int Width, int Height);

[Flags]
public enum RenderResourceUsage
{
    None = 0,
    ShaderRead = 1 << 0,
    ColorAttachment = 1 << 1,
    DepthAttachment = 1 << 2,
    CopySource = 1 << 3,
    CopyDestination = 1 << 4,
    VertexBuffer = 1 << 5,
    IndexBuffer = 1 << 6,
    ConstantBuffer = 1 << 7,
    Present = 1 << 8,
    Readback = 1 << 9
}

/// <summary>
/// Exclusive portable state for a subresource. Common is an import state. Present and Readback
/// as pass inputs declare terminal consumption of the covered subresources.
/// </summary>
public enum RenderResourceState
{
    Common = 1,
    ShaderRead,
    ColorAttachment,
    DepthWrite,
    DepthRead,
    CopySource,
    CopyDestination,
    VertexBuffer,
    IndexBuffer,
    ConstantBuffer,
    Present,
    Readback
}

/// <summary>
/// Nonempty contiguous range of subresources. The mip index advances first within each array
/// layer, starting at zero. A buffer has one subresource and ranges cover the whole buffer.
/// </summary>
public readonly record struct RenderSubresourceRange(int First, int Count);

/// <summary>One part of an import's expected initial state and initialization coverage.</summary>
public readonly record struct RenderImportedRange(
    RenderSubresourceRange Range,
    RenderResourceState State,
    bool Initialized);

/// <summary>
/// Portable resource shape and ownership for one plan. Imported ranges partition the full
/// resource and declare its state even where contents are undefined. Produced resources begin
/// with undefined contents and use an empty import list.
/// </summary>
public readonly record struct RenderResourceDescriptor(
    RenderResourceId Id,
    RenderAllocationId Allocation,
    RenderResourceOrigin Origin,
    RenderResourceFormat Format,
    RenderExtent Extent,
    int MipLevels,
    int ArrayLayers,
    RenderResourceUsage Usage,
    RenderResourceAdmission Admission,
    ImmutableArray<RenderImportedRange> ImportedRanges);
