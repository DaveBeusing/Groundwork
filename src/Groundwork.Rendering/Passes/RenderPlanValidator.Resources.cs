using System.Numerics;

namespace Groundwork.Rendering.Passes;

public static partial class RenderPlanValidator
{
    private const RenderResourceUsage Copy = RenderResourceUsage.CopySource | RenderResourceUsage.CopyDestination;
    private const RenderResourceUsage TextureRead = Copy | RenderResourceUsage.ShaderRead;
    private const RenderResourceUsage Buffer = TextureRead | RenderResourceUsage.VertexBuffer |
        RenderResourceUsage.IndexBuffer | RenderResourceUsage.ConstantBuffer | RenderResourceUsage.Readback;

    private static RenderPlanFailure CheckLimits(
        ReadOnlySpan<RenderResourceDescriptor> resources, ReadOnlySpan<RenderPassDescriptor> passes,
        out int accessCount, out int subresourceCount)
    {
        accessCount = 0;
        subresourceCount = 0;
        if (passes.Length > RenderPlanLimits.Passes)
        {
            return new(RenderPlanError.PassLimit);
        }

        if (resources.Length > RenderPlanLimits.Resources)
        {
            return new(RenderPlanError.ResourceLimit);
        }

        var edgeCount = 0;
        foreach (var pass in passes)
        {
            if (!TryAdd(ref edgeCount, pass.Dependencies.AsSpan().Length, RenderPlanLimits.Edges))
            {
                return new(RenderPlanError.EdgeLimit, pass.Id);
            }

            if (!TryAdd(ref accessCount, pass.Inputs.AsSpan().Length, RenderPlanLimits.Accesses) ||
                !TryAdd(ref accessCount, pass.Outputs.AsSpan().Length, RenderPlanLimits.Accesses))
            {
                return new(RenderPlanError.AccessLimit, pass.Id);
            }
        }

        var importRangeCount = 0;
        foreach (var resource in resources)
        {
            if (!TryAdd(ref importRangeCount, resource.ImportedRanges.AsSpan().Length, RenderPlanLimits.ImportedRanges))
            {
                return new(RenderPlanError.ImportedRangeLimit, Resource: resource.Id);
            }

            if (!IsValidExtent(resource))
            {
                return new(RenderPlanError.InvalidExtent, Resource: resource.Id);
            }

            if (!TryAdd(ref subresourceCount, resource.MipLevels * resource.ArrayLayers,
                RenderPlanLimits.TrackedSubresources))
            {
                return new(RenderPlanError.ScratchLimit, Resource: resource.Id);
            }
        }

        var visits = 0;
        foreach (var resource in resources)
        {
            foreach (var imported in resource.ImportedRanges.AsSpan())
            {
                var failure = CountVisits(ref visits, imported.Range, default, resource.Id);
                if (failure.Error != RenderPlanError.None)
                {
                    return failure;
                }
            }
        }

        foreach (var pass in passes)
        {
            foreach (var input in pass.Inputs.AsSpan())
            {
                var failure = CountVisits(ref visits, input.Range, pass.Id, input.Resource);
                if (failure.Error != RenderPlanError.None)
                {
                    return failure;
                }
            }

            foreach (var output in pass.Outputs.AsSpan())
            {
                var failure = CountVisits(ref visits, output.Range, pass.Id, output.Resource);
                if (failure.Error != RenderPlanError.None)
                {
                    return failure;
                }
            }
        }

        return default;
    }

    private static RenderPlanFailure CountVisits(
        ref int visits, RenderSubresourceRange range, RenderPassId pass, RenderResourceId resource)
    {
        if (range.First < 0 || range.Count <= 0 || range.Count > RenderPlanLimits.TrackedSubresources)
        {
            return new(RenderPlanError.InvalidSubresourceRange, pass, resource);
        }

        return TryAdd(ref visits, range.Count, RenderPlanLimits.RangeVisits)
            ? default
            : new(RenderPlanError.RangeWorkLimit, pass, resource);
    }

    private static bool TryAdd(ref int total, int count, int limit)
    {
        if (count > limit - total)
        {
            return false;
        }

        total += count;
        return true;
    }

    private static RenderPlanFailure InitializeResources(
        ReadOnlySpan<RenderResourceDescriptor> resources, Span<int> offsets, Span<Content> contents)
    {
        contents.Clear();
        var offset = 0;
        RenderExtent frameExtent = default;
        for (var i = 0; i < resources.Length; i++)
        {
            var resource = resources[i];
            if (resource.Id.Slot == 0 || resource.Id.Generation == 0 || resource.Allocation.Value == 0)
            {
                return new(RenderPlanError.InvalidResourceId, Resource: resource.Id);
            }

            var identityError = CheckResourceIdentity(resources[..i], resource);
            if (identityError != RenderPlanError.None)
            {
                return new(identityError, Resource: resource.Id);
            }

            if (resource.Extent.Class == RenderExtentClass.FrameTexture)
            {
                if (frameExtent != default && resource.Extent != frameExtent)
                {
                    return new(RenderPlanError.InvalidExtent, Resource: resource.Id);
                }

                frameExtent = resource.Extent;
            }

            if (!IsValidResource(resource))
            {
                return new(RenderPlanError.InvalidResource, Resource: resource.Id);
            }

            var rangeCount = resource.MipLevels * resource.ArrayLayers;
            if (resource.Origin == RenderResourceOrigin.Imported)
            {
                var error = InitializeImport(resource, contents.Slice(offset, rangeCount));
                if (error != RenderPlanError.None)
                {
                    return new(error, Resource: resource.Id);
                }
            }
            else if (!resource.ImportedRanges.IsDefaultOrEmpty)
            {
                return new(RenderPlanError.InvalidImport, Resource: resource.Id);
            }

            offsets[i] = offset;
            offset += rangeCount;
        }

        return default;
    }

    private static RenderPlanError CheckResourceIdentity(
        ReadOnlySpan<RenderResourceDescriptor> previousResources, RenderResourceDescriptor resource)
    {
        foreach (var previous in previousResources)
        {
            if (previous.Id.Slot == resource.Id.Slot)
            {
                return previous.Id.Generation == resource.Id.Generation
                    ? RenderPlanError.DuplicateResource
                    : RenderPlanError.AmbiguousResourceGeneration;
            }

            if (previous.Allocation == resource.Allocation)
            {
                return RenderPlanError.AliasedResource;
            }
        }

        return RenderPlanError.None;
    }

    private static RenderPlanError InitializeImport(RenderResourceDescriptor resource, Span<Content> contents)
    {
        var covered = 0;
        foreach (var imported in resource.ImportedRanges.AsSpan())
        {
            if (!IsValidRange(imported.Range, contents.Length))
            {
                return RenderPlanError.InvalidSubresourceRange;
            }

            if (imported.State != RenderResourceState.Common && !SupportsState(resource, imported.State))
            {
                return RenderPlanError.InvalidState;
            }

            var range = contents.Slice(imported.Range.First, imported.Range.Count);
            if (range.ContainsAnyExcept(Content.Unproduced))
            {
                return RenderPlanError.InvalidImport;
            }

            range.Fill(imported.Initialized ? Content.Initialized : Content.Uninitialized);
            covered += range.Length;
        }

        return covered == contents.Length ? RenderPlanError.None : RenderPlanError.InvalidImport;
    }

    private static bool IsValidExtent(RenderResourceDescriptor resource)
    {
        var extent = resource.Extent;
        if (extent.Class == RenderExtentClass.Buffer)
        {
            return extent.Width is > 0 and <= RenderPlanLimits.BufferBytes && extent.Height == 1 &&
                resource.MipLevels == 1 && resource.ArrayLayers == 1;
        }

        return extent.Class is RenderExtentClass.FixedTexture or RenderExtentClass.FrameTexture &&
            extent.Width is > 0 and <= RenderPlanLimits.TextureDimension &&
            extent.Height is > 0 and <= RenderPlanLimits.TextureDimension &&
            resource.ArrayLayers is > 0 and <= RenderPlanLimits.ArrayLayers &&
            resource.MipLevels > 0 && resource.MipLevels <= BitOperations.Log2((uint)Math.Max(extent.Width, extent.Height)) + 1;
    }

    private static bool IsValidResource(RenderResourceDescriptor resource)
    {
        var allowed = resource.Format switch
        {
            RenderResourceFormat.Buffer => Buffer,
            RenderResourceFormat.Rgba8Unorm or RenderResourceFormat.Bgra8Unorm =>
                TextureRead | RenderResourceUsage.ColorAttachment | RenderResourceUsage.Present,
            RenderResourceFormat.Rgba8UnormSrgb or RenderResourceFormat.Rgba16Float =>
                TextureRead | RenderResourceUsage.ColorAttachment,
            RenderResourceFormat.Depth32Float => TextureRead | RenderResourceUsage.DepthAttachment,
            RenderResourceFormat.Bc1Unorm or RenderResourceFormat.Bc1UnormSrgb => TextureRead,
            _ => RenderResourceUsage.None
        };

        return resource.Origin is RenderResourceOrigin.Imported or RenderResourceOrigin.Produced &&
            resource.Admission is RenderResourceAdmission.Required or RenderResourceAdmission.OptionalVisual &&
            (resource.Extent.Class == RenderExtentClass.Buffer) == (resource.Format == RenderResourceFormat.Buffer) &&
            resource.Usage != RenderResourceUsage.None && (resource.Usage & ~allowed) == 0 &&
            ((resource.Usage & RenderResourceUsage.Present) == 0 ||
             (resource.Origin == RenderResourceOrigin.Imported && resource.Extent.Class == RenderExtentClass.FrameTexture &&
              resource.MipLevels == 1 && resource.ArrayLayers == 1)) &&
            ((resource.Usage & RenderResourceUsage.Readback) == 0 ||
             resource.Usage == (RenderResourceUsage.CopyDestination | RenderResourceUsage.Readback));
    }

    private static bool SupportsState(RenderResourceDescriptor resource, RenderResourceState state)
    {
        var usage = state switch
        {
            RenderResourceState.ShaderRead => RenderResourceUsage.ShaderRead,
            RenderResourceState.ColorAttachment => RenderResourceUsage.ColorAttachment,
            RenderResourceState.DepthRead or RenderResourceState.DepthWrite => RenderResourceUsage.DepthAttachment,
            RenderResourceState.CopySource => RenderResourceUsage.CopySource,
            RenderResourceState.CopyDestination => RenderResourceUsage.CopyDestination,
            RenderResourceState.VertexBuffer => RenderResourceUsage.VertexBuffer,
            RenderResourceState.IndexBuffer => RenderResourceUsage.IndexBuffer,
            RenderResourceState.ConstantBuffer => RenderResourceUsage.ConstantBuffer,
            RenderResourceState.Present => RenderResourceUsage.Present,
            RenderResourceState.Readback => RenderResourceUsage.Readback,
            _ => RenderResourceUsage.None
        };

        return (resource.Usage & usage) != 0;
    }

    private static bool IsValidRange(RenderSubresourceRange range, int count) =>
        range.First >= 0 && range.Count > 0 && range.Count <= count - range.First;
}
