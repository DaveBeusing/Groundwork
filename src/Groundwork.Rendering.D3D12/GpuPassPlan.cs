using System.Collections.Immutable;
using Groundwork.Rendering.Passes;

namespace Groundwork.Rendering.D3D12;

internal enum ShadowPassKind : byte { Disabled, Draw, Cached }

internal readonly record struct GpuPassShape(
    int Width, int Height, int GeometryBytes, bool Scene, bool Sky, bool Props, bool FollowOn,
    ShadowPassKind Near, ShadowPassKind Far, bool Readback, bool Present);

/// <summary>Portable declarations for the existing recorder. Native bindings stay with the queue owner.</summary>
internal static class GpuPassPlan
{
    internal static readonly RenderPassId Clear = new(1);
    internal static readonly RenderPassId NearShadow = new(2);
    internal static readonly RenderPassId FarShadow = new(3);
    internal static readonly RenderPassId Sky = new(4);
    internal static readonly RenderPassId Terrain = new(5);
    internal static readonly RenderPassId Props = new(6);
    internal static readonly RenderPassId FollowOn = new(7);
    internal static readonly RenderPassId Copy = new(8);
    internal static readonly RenderPassId Present = new(9);
    internal static readonly RenderPassId Readback = new(10);
    private static readonly RenderSubresourceRange Whole = new(0, 1);
    private static readonly RenderResourceId Color = new(1, 1);
    private static readonly RenderResourceId Depth = new(2, 1);
    private static readonly RenderResourceId NearMap = new(3, 1);
    private static readonly RenderResourceId FarMap = new(4, 1);
    private static readonly RenderResourceId Geometry = new(5, 1);
    private static readonly RenderResourceId PackedReadback = new(6, 1);

    internal static bool TryCreate(in GpuPassShape shape, out ValidatedRenderPlan? plan)
    {
        plan = null;
        if (shape.Width is < 1 or > RenderPlanLimits.TextureDimension ||
            shape.Height is < 1 or > RenderPlanLimits.TextureDimension ||
            shape.GeometryBytes is < 0 or > RenderPlanLimits.BufferBytes ||
            !Enum.IsDefined(shape.Near) || !Enum.IsDefined(shape.Far) ||
            !shape.Scene && (shape.Props || shape.FollowOn || shape.GeometryBytes != 0 ||
                shape.Near != ShadowPassKind.Disabled || shape.Far != ShadowPassKind.Disabled) ||
            !shape.Readback && !shape.Present ||
            shape.Readback && (long)shape.Width * shape.Height * (sizeof(uint) + sizeof(float)) > RenderPlanLimits.BufferBytes)
            return false;

        var resources = ImmutableArray.CreateBuilder<RenderResourceDescriptor>(6);
        var passes = ImmutableArray.CreateBuilder<RenderPassDescriptor>(10);
        RenderPassId previous = default;
        var extent = new RenderExtent(RenderExtentClass.FrameTexture, shape.Width, shape.Height);
        var colorUsage = RenderResourceUsage.ColorAttachment |
            (shape.Present ? RenderResourceUsage.Present : RenderResourceUsage.None) |
            (shape.Readback ? RenderResourceUsage.CopySource : RenderResourceUsage.None);
        resources.Add(Import(Color, RenderResourceFormat.Rgba8Unorm, extent, colorUsage,
            shape.Present ? RenderResourceState.Present : RenderResourceState.Common, initialized: false));
        resources.Add(Import(Depth, RenderResourceFormat.Depth32Float, extent,
            RenderResourceUsage.DepthAttachment | (shape.Readback ? RenderResourceUsage.CopySource : RenderResourceUsage.None),
            RenderResourceState.DepthWrite, initialized: false));
        if (shape.GeometryBytes > 0)
        {
            // Packed terrain vertex/index views share one native GENERIC_READ allocation.
            resources.Add(Import(Geometry, RenderResourceFormat.Buffer,
                new(RenderExtentClass.Buffer, shape.GeometryBytes, 1), RenderResourceUsage.VertexBuffer,
                RenderResourceState.VertexBuffer, initialized: true));
        }
        var geometry = shape.GeometryBytes == 0
            ? ImmutableArray<RenderPassInput>.Empty : [Input(Geometry, RenderResourceState.VertexBuffer)];
        Add(Clear, [], Attachments(RenderLoadAction.Clear));
        var near = shape.Near;
        var far = shape.Far;
        Shadow(NearShadow, NearMap, near);
        Shadow(FarShadow, FarMap, far);
        if (shape.Sky)
            Add(Sky, [], [Output(Color, RenderResourceState.ColorAttachment, RenderLoadAction.Load)]);
        if (shape.Scene)
        {
            var inputs = geometry.ToBuilder();
            if (near != ShadowPassKind.Disabled)
                inputs.Add(Input(NearMap, RenderResourceState.ShaderRead));
            if (far != ShadowPassKind.Disabled)
                inputs.Add(Input(FarMap, RenderResourceState.ShaderRead));
            Add(Terrain, inputs.ToImmutable(), Attachments(RenderLoadAction.Load));
            // Prop storage is prepared and validated by its bounded native arena under the stream gate.
            if (shape.Props)
                Add(Props, [], Attachments(RenderLoadAction.Load));
            if (shape.FollowOn)
                Add(FollowOn, geometry, Attachments(RenderLoadAction.Load));
        }
        if (shape.Readback)
        {
            // One logical packed color/depth footprint, excluding the unread native row/plane padding.
            var bytes = checked(shape.Width * shape.Height * (sizeof(uint) + sizeof(float)));
            resources.Add(Import(PackedReadback, RenderResourceFormat.Buffer, new(RenderExtentClass.Buffer, bytes, 1),
                RenderResourceUsage.CopyDestination | RenderResourceUsage.Readback,
                RenderResourceState.CopyDestination, initialized: false));
            Add(Copy, [Input(Color, RenderResourceState.CopySource), Input(Depth, RenderResourceState.CopySource)],
                [Output(PackedReadback, RenderResourceState.CopyDestination, RenderLoadAction.Discard,
                    RenderWriteCoverage.Complete)]);
        }
        if (shape.Present)
            Add(Present, [Input(Color, RenderResourceState.Present)], []);
        if (shape.Readback)
            Add(Readback, [Input(PackedReadback, RenderResourceState.Readback)], []);
        return RenderPlanValidator.TryValidate(new(resources.ToImmutable(), passes.ToImmutable()), out plan, out _);

        void Add(RenderPassId id, ImmutableArray<RenderPassInput> inputs, ImmutableArray<RenderPassOutput> outputs)
        {
            passes.Add(new(id, previous == default ? [] : [previous], inputs, outputs));
            previous = id;
        }

        void Shadow(RenderPassId id, RenderResourceId resource, ShadowPassKind kind)
        {
            if (kind == ShadowPassKind.Disabled)
                return;
            resources.Add(Import(resource, RenderResourceFormat.Depth32Float, new(RenderExtentClass.FixedTexture, 1024, 1024),
                RenderResourceUsage.DepthAttachment | RenderResourceUsage.ShaderRead,
                RenderResourceState.ShaderRead, initialized: kind == ShadowPassKind.Cached));
            if (kind == ShadowPassKind.Draw)
                Add(id, geometry, [Output(resource, RenderResourceState.DepthWrite, RenderLoadAction.Clear)]);
        }
    }

    private static RenderResourceDescriptor Import(RenderResourceId id, RenderResourceFormat format, RenderExtent extent,
        RenderResourceUsage usage, RenderResourceState state, bool initialized) =>
        new(id, new(id.Slot), RenderResourceOrigin.Imported, format, extent, 1, 1, usage, RenderResourceAdmission.Required,
            [new(Whole, state, initialized)]);

    private static RenderPassInput Input(RenderResourceId id, RenderResourceState state) => new(id, Whole, state);

    private static RenderPassOutput Output(RenderResourceId id, RenderResourceState state, RenderLoadAction load,
        RenderWriteCoverage coverage = RenderWriteCoverage.Partial) =>
        new(id, Whole, state, load, RenderStoreAction.Store, coverage);

    private static ImmutableArray<RenderPassOutput> Attachments(RenderLoadAction load) =>
        [Output(Color, RenderResourceState.ColorAttachment, load), Output(Depth, RenderResourceState.DepthWrite, load)];
}
