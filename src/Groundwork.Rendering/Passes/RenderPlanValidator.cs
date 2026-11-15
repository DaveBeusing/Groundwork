using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Groundwork.Rendering.Passes;

/// <summary>Validates a fixed set of declarations without external state or execution.</summary>
public static partial class RenderPlanValidator
{
    /// <summary>Returns a plan only if declarations, dependencies and contents validate.</summary>
    public static bool TryValidate(
        in RenderPlanDescriptor descriptor,
        [NotNullWhen(true)] out ValidatedRenderPlan? plan,
        out RenderPlanFailure failure)
    {
        plan = null;
        var resources = descriptor.Resources.AsSpan();
        var passes = descriptor.Passes.AsSpan();
        failure = CheckLimits(resources, passes, out var accessCount, out var subresourceCount);
        if (failure.Error != RenderPlanError.None)
        {
            return false;
        }

        Span<ulong> ancestors = stackalloc ulong[passes.Length];
        Span<int> order = stackalloc int[passes.Length];
        failure = OrderPasses(passes, ancestors, order);
        if (failure.Error != RenderPlanError.None)
        {
            return false;
        }

        Span<int> offsets = stackalloc int[resources.Length];
        Span<Content> contents = stackalloc Content[subresourceCount];
        failure = InitializeResources(resources, offsets, contents);
        if (failure.Error != RenderPlanError.None)
        {
            return false;
        }

        var accesses = new PlannedAccess[accessCount];
        failure = ResolveAccesses(resources, passes, order, accesses);
        if (failure.Error != RenderPlanError.None)
        {
            return false;
        }

        failure = CheckConflicts(resources, passes, ancestors, accesses);
        if (failure.Error != RenderPlanError.None)
        {
            return false;
        }

        failure = CheckContents(resources, passes, offsets, contents, accesses);
        if (failure.Error != RenderPlanError.None)
        {
            return false;
        }

        var ordered = ImmutableArray.CreateBuilder<RenderPassDescriptor>(passes.Length);
        foreach (var index in order)
        {
            ordered.Add(passes[index]);
        }

        plan = new ValidatedRenderPlan(
            descriptor.Resources.IsDefault ? [] : descriptor.Resources, ordered.MoveToImmutable());
        return true;
    }

    private static RenderPlanFailure OrderPasses(
        ReadOnlySpan<RenderPassDescriptor> passes, Span<ulong> ancestors, Span<int> order)
    {
        for (var i = 0; i < passes.Length; i++)
        {
            if (passes[i].Id.Value == 0)
            {
                return new(RenderPlanError.InvalidPassId);
            }

            if (FindPass(passes[..i], passes[i].Id) >= 0)
            {
                return new(RenderPlanError.DuplicatePass, passes[i].Id);
            }
        }

        Span<ulong> parents = stackalloc ulong[passes.Length];
        for (var i = 0; i < passes.Length; i++)
        {
            var dependencyMask = 0UL;
            foreach (var dependency in passes[i].Dependencies.AsSpan())
            {
                var index = FindPass(passes, dependency);
                if (index < 0)
                {
                    return new(RenderPlanError.UnknownDependency, passes[i].Id, OtherPass: dependency);
                }

                var bit = 1UL << index;
                if ((dependencyMask & bit) != 0)
                {
                    return new(RenderPlanError.DuplicateDependency, passes[i].Id, OtherPass: dependency);
                }

                dependencyMask |= bit;
            }

            parents[i] = dependencyMask;
        }

        if (passes.IsEmpty)
        {
            return default;
        }

        // A mask bit represents each input pass index, including index 63.
        var remaining = ulong.MaxValue >> (64 - passes.Length);
        for (var position = 0; position < order.Length; position++)
        {
            var next = -1;
            for (var candidates = remaining; candidates != 0; candidates &= candidates - 1)
            {
                var i = BitOperations.TrailingZeroCount(candidates);
                if ((parents[i] & remaining) == 0 && (next < 0 || passes[i].Id.Value < passes[next].Id.Value))
                {
                    next = i;
                }
            }

            if (next < 0)
            {
                return new(RenderPlanError.Cycle);
            }

            order[position] = next;
            remaining &= ~(1UL << next);
            var reachable = parents[next];
            for (var pending = parents[next]; pending != 0; pending &= pending - 1)
            {
                reachable |= ancestors[BitOperations.TrailingZeroCount(pending)];
            }

            ancestors[next] = reachable;
        }

        return default;
    }

    private static int FindPass(ReadOnlySpan<RenderPassDescriptor> passes, RenderPassId id)
    {
        for (var i = 0; i < passes.Length; i++)
        {
            if (passes[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static RenderPlanFailure ResolveAccesses(
        ReadOnlySpan<RenderResourceDescriptor> resources, ReadOnlySpan<RenderPassDescriptor> passes,
        ReadOnlySpan<int> order, Span<PlannedAccess> accesses)
    {
        var count = 0;
        foreach (var index in order)
        {
            var pass = passes[index];
            foreach (var input in pass.Inputs.AsSpan())
            {
                var failure = ResolveAccess(resources, pass.Id, input.Resource, input.Range, out var resource);
                if (failure.Error != RenderPlanError.None)
                {
                    return failure;
                }

                if (!SupportsState(resources[resource], input.State) ||
                    input.State is RenderResourceState.Common or RenderResourceState.ColorAttachment or
                        RenderResourceState.DepthWrite or RenderResourceState.CopyDestination)
                {
                    return new(RenderPlanError.InvalidState, pass.Id, input.Resource);
                }

                accesses[count++] = new(index, resource, input.Range, input.State);
            }

            foreach (var output in pass.Outputs.AsSpan())
            {
                var failure = ResolveAccess(resources, pass.Id, output.Resource, output.Range, out var resource);
                if (failure.Error != RenderPlanError.None)
                {
                    return failure;
                }

                if (!SupportsState(resources[resource], output.State) ||
                    output.State is not (RenderResourceState.ColorAttachment or RenderResourceState.DepthWrite or
                        RenderResourceState.CopyDestination))
                {
                    return new(RenderPlanError.InvalidState, pass.Id, output.Resource);
                }

                if (!Enum.IsDefined(output.Load) || !Enum.IsDefined(output.Store) || !Enum.IsDefined(output.Coverage) ||
                    (output.Load == RenderLoadAction.Clear && output.State == RenderResourceState.CopyDestination))
                {
                    return new(RenderPlanError.InvalidOutputAction, pass.Id, output.Resource);
                }

                accesses[count++] = new(index, resource, output.Range, output.State,
                    output.Load, output.Store, output.Coverage);
            }
        }

        return default;
    }

    private static RenderPlanFailure ResolveAccess(
        ReadOnlySpan<RenderResourceDescriptor> resources, RenderPassId pass, RenderResourceId id,
        RenderSubresourceRange range, out int resourceIndex)
    {
        resourceIndex = -1;
        if (id.Slot == 0 || id.Generation == 0)
        {
            return new(RenderPlanError.InvalidResourceId, pass, id);
        }

        for (var i = 0; i < resources.Length; i++)
        {
            if (resources[i].Id.Slot != id.Slot)
            {
                continue;
            }

            if (resources[i].Id.Generation != id.Generation)
            {
                return new(RenderPlanError.StaleResourceGeneration, pass, id);
            }

            if (!IsValidRange(range, resources[i].MipLevels * resources[i].ArrayLayers))
            {
                return new(RenderPlanError.InvalidSubresourceRange, pass, id);
            }

            resourceIndex = i;
            return default;
        }

        return new(RenderPlanError.UnknownResource, pass, id);
    }

    private static RenderPlanFailure CheckConflicts(
        ReadOnlySpan<RenderResourceDescriptor> resources, ReadOnlySpan<RenderPassDescriptor> passes,
        ReadOnlySpan<ulong> ancestors, ReadOnlySpan<PlannedAccess> accesses)
    {
        // The deterministic order is also a linear extension of the declared dependencies.
        // Stable-ID order alone is insufficient evidence that two incompatible accesses can run.
        for (var i = 0; i < accesses.Length; i++)
        {
            var access = accesses[i];
            foreach (var previous in accesses[..i])
            {
                if (previous.Resource != access.Resource || !Overlaps(previous.Range, access.Range) ||
                    (!previous.Writes && !access.Writes &&
                     !previous.IsTerminal && !access.IsTerminal && previous.State == access.State))
                {
                    continue;
                }

                var pass = passes[access.Pass].Id;
                var resource = resources[access.Resource].Id;
                if (previous.Pass == access.Pass)
                {
                    return new(RenderPlanError.ConflictingAccess, pass, resource);
                }

                var otherPass = passes[previous.Pass].Id;
                if ((ancestors[access.Pass] & (1UL << previous.Pass)) == 0)
                {
                    return new(RenderPlanError.MissingDependency, pass, resource, otherPass);
                }

                if (previous.IsTerminal)
                {
                    return new(RenderPlanError.UseAfterTerminal, pass, resource, otherPass);
                }
            }
        }

        return default;
    }

    private static RenderPlanFailure CheckContents(
        ReadOnlySpan<RenderResourceDescriptor> resources, ReadOnlySpan<RenderPassDescriptor> passes,
        ReadOnlySpan<int> offsets, Span<Content> contents, ReadOnlySpan<PlannedAccess> accesses)
    {
        foreach (var access in accesses)
        {
            var range = contents.Slice(offsets[access.Resource] + access.Range.First, access.Range.Count);
            if (!access.Writes || access.Load == RenderLoadAction.Load)
            {
                foreach (var content in range)
                {
                    if (content == Content.Initialized)
                    {
                        continue;
                    }

                    var error = content == Content.Unproduced
                        ? RenderPlanError.MissingProducer
                        : RenderPlanError.UninitializedContent;
                    return new(error, passes[access.Pass].Id, resources[access.Resource].Id);
                }
            }

            if (access.Writes)
            {
                var initialized = access.Store == RenderStoreAction.Store &&
                    (access.Load is RenderLoadAction.Load or RenderLoadAction.Clear ||
                     access.Coverage == RenderWriteCoverage.Complete);
                range.Fill(initialized ? Content.Initialized : Content.Uninitialized);
            }
        }

        return default;
    }

    private static bool Overlaps(RenderSubresourceRange left, RenderSubresourceRange right) =>
        left.First < right.First + right.Count && right.First < left.First + left.Count;

    private enum Content : byte
    {
        Unproduced,
        Uninitialized,
        Initialized
    }

    private readonly record struct PlannedAccess(
        int Pass, int Resource, RenderSubresourceRange Range, RenderResourceState State,
        RenderLoadAction Load = default, RenderStoreAction Store = default, RenderWriteCoverage Coverage = default)
    {
        public bool Writes => Load != default;
        public bool IsTerminal => State is RenderResourceState.Present or RenderResourceState.Readback;
    }
}
