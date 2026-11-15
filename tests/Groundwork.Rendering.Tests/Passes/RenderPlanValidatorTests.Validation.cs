using System.Collections.Immutable;
using Groundwork.Rendering.Passes;

namespace Groundwork.Rendering.Tests.Passes;

public sealed partial class RenderPlanValidatorTests
{
    [TestMethod]
    public void CyclesInvalidPassesAndUndeclaredEdgesAreRejected()
    {
        Reject(RenderPlanError.Cycle, new([], [Pass(1, [new(2)]), Pass(2, [new(3)]), Pass(3, [new(1)])]));
        Reject(RenderPlanError.Cycle, new([], [Pass(1, [new(1)])]));
        var unknown = Reject(RenderPlanError.UnknownDependency, new([], [Pass(1, [new(2)])]));
        Assert.AreEqual(new RenderPassId(2), unknown.OtherPass);
        Reject(RenderPlanError.DuplicateDependency, new([], [Pass(1), Pass(2, [new(1), new(1)])]));
        Reject(RenderPlanError.DuplicatePass, new([], [Pass(1), Pass(1)]));
        Reject(RenderPlanError.InvalidPassId, new([], [Pass(0)]));
    }

    [TestMethod]
    public void ResourceIdentitiesRejectAliasesAndAmbiguousGenerations()
    {
        var color = Color();
        Reject(RenderPlanError.DuplicateResource, new([color, color], []));
        Reject(RenderPlanError.AmbiguousResourceGeneration,
            new([color, color with { Id = color.Id with { Generation = color.Id.Generation + 1 } }], []));
        Reject(RenderPlanError.AliasedResource, new([color, Color(3) with { Allocation = color.Allocation }], []));
        Reject(RenderPlanError.InvalidResourceId, new([color with { Allocation = default }], []));

        var stale = Input(color) with { Resource = color.Id with { Generation = color.Id.Generation - 1 } };
        var failure = Reject(RenderPlanError.StaleResourceGeneration, new([color], [Pass(1, inputs: [stale])]));
        Assert.AreEqual(stale.Resource, failure.Resource);
        Reject(RenderPlanError.UnknownResource, new([color], [Pass(1, inputs: [Input(Color(3))])]));
        Reject(RenderPlanError.InvalidResourceId, new([color], [Pass(1, inputs: [Input(color) with { Resource = default }])]));
    }

    [TestMethod]
    public void RequiredStatesAndOutputActionsMustMatchTheirUseAndFormat()
    {
        var color = Color();
        var invalidInputs = new[]
        {
            RenderResourceState.Common, RenderResourceState.ColorAttachment, RenderResourceState.DepthRead,
            RenderResourceState.CopyDestination, RenderResourceState.Present, (RenderResourceState)int.MaxValue
        };
        foreach (var invalid in invalidInputs)
        {
            Reject(RenderPlanError.InvalidState, new([color], [Pass(1, inputs: [Input(color, invalid)])]));
        }

        Reject(RenderPlanError.InvalidState, new([color], [Pass(1, outputs: [Output(color, RenderResourceState.ShaderRead)])]));
        Reject(RenderPlanError.InvalidState, new([color], [Pass(1, outputs: [Output(color, RenderResourceState.DepthWrite)])]));
        Reject(RenderPlanError.InvalidOutputAction, new([color], [Pass(1, outputs: [Output(color) with { Store = default }])]));

        var staging = Buffer(1, RenderResourceOrigin.Produced, RenderResourceUsage.CopyDestination);
        Reject(RenderPlanError.InvalidOutputAction,
            new([staging], [Pass(1, outputs: [Output(staging, RenderResourceState.CopyDestination)])]));

        var backBuffer = BackBuffer();
        Reject(RenderPlanError.InvalidState,
            new([backBuffer with { ImportedRanges = [new(Whole, RenderResourceState.DepthWrite, true)] }], []));
        Reject(RenderPlanError.InvalidResource, new([color with { Format = RenderResourceFormat.Bc1Unorm }], []));
        Reject(RenderPlanError.InvalidResource, new([color with { Admission = default }], []));
        Reject(RenderPlanError.InvalidImport, new([color with { ImportedRanges = backBuffer.ImportedRanges }], []));
    }

    [TestMethod]
    public void ExtentsAndTextureLayoutsMustFitTheDeclaredExtentClass()
    {
        var color = Color();
        Reject(RenderPlanError.InvalidExtent, new([color with { Extent = FrameExtent with { Width = 0 } }], []));
        Reject(RenderPlanError.InvalidExtent, new([color with { Extent = FrameExtent with { Height = int.MaxValue } }], []));
        Reject(RenderPlanError.InvalidExtent, new([color with { MipLevels = 11 }], []));
        Reject(RenderPlanError.InvalidExtent, new([color with { ArrayLayers = RenderPlanLimits.ArrayLayers + 1 }], []));
        Reject(RenderPlanError.InvalidExtent, new([color, Color(3) with { Extent = FrameExtent with { Width = 800 } }], []));
        Reject(RenderPlanError.InvalidResource, new([BackBuffer() with { ArrayLayers = 2 }], []));

        var buffer = Buffer(1, RenderResourceOrigin.Produced, RenderResourceUsage.CopyDestination);
        Reject(RenderPlanError.InvalidExtent, new([buffer with { Extent = buffer.Extent with { Height = 2 } }], []));
        Reject(RenderPlanError.InvalidExtent, new([buffer with { Extent = buffer.Extent with { Width = int.MaxValue } }], []));
    }

    [TestMethod]
    [DataRow(-1, 1)]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(0, 2)]
    [DataRow(int.MaxValue, 2)]
    [DataRow(0, int.MaxValue)]
    public void InputOutputAndImportRangesMustBeNonemptyAndInBounds(int first, int count)
    {
        var color = Color();
        var range = new RenderSubresourceRange(first, count);
        Reject(RenderPlanError.InvalidSubresourceRange,
            new([color], [Pass(1, inputs: [Input(color) with { Range = range }])]));
        Reject(RenderPlanError.InvalidSubresourceRange,
            new([color], [Pass(1, outputs: [Output(color) with { Range = range }])]));
        var backBuffer = BackBuffer() with { ImportedRanges = [new(range, RenderResourceState.Present, false)] };
        Reject(RenderPlanError.InvalidSubresourceRange, new([backBuffer], []));
    }

    [TestMethod]
    public void PassLimitIncludesDependencyBitForTheLastInputPass()
    {
        var passes = Enumerable.Range(1, RenderPlanLimits.Passes).Select(i => Pass((uint)i)).ToImmutableArray();
        passes = passes.SetItem(0, Pass(1, [passes[^1].Id]));
        var plan = Validate(new([], passes));
        Assert.HasCount(RenderPlanLimits.Passes, plan.Passes);
        Assert.AreEqual(passes[^1].Id, plan.Passes[^2].Id);
        Assert.AreEqual(passes[0].Id, plan.Passes[^1].Id);
        Reject(RenderPlanError.PassLimit, new([], passes.Add(Pass((uint)RenderPlanLimits.Passes + 1))));
    }

    [TestMethod]
    public void EdgeLimitCountsEdgesAcrossPasses()
    {
        var roots = Enumerable.Range(1, 16).Select(i => Pass((uint)i)).ToImmutableArray();
        var dependencies = roots.Select(pass => pass.Id).ToImmutableArray();
        var children = Enumerable.Range(17, RenderPlanLimits.Edges / roots.Length)
            .Select(i => Pass((uint)i, dependencies));
        var passes = roots.AddRange(children);
        Assert.AreEqual(RenderPlanLimits.Edges, passes.Sum(pass => pass.Dependencies.AsSpan().Length));
        Validate(new([], passes));
        Reject(RenderPlanError.EdgeLimit, new([], passes.Add(Pass((uint)passes.Length + 1, [roots[0].Id]))));
    }

    [TestMethod]
    public void ResourceAccessAndImportRangeLimitsHaveInclusiveBoundaries()
    {
        var resources = Enumerable.Range(1, RenderPlanLimits.Resources).Select(i => Color((ulong)i)).ToImmutableArray();
        Validate(new(resources, []));
        Reject(RenderPlanError.ResourceLimit, new(resources.Add(Color((ulong)RenderPlanLimits.Resources + 1)), []));

        var color = Color() with
        {
            Origin = RenderResourceOrigin.Imported,
            ImportedRanges = [new(Whole, RenderResourceState.ShaderRead, true)]
        };
        var reads = Enumerable.Repeat(Input(color), RenderPlanLimits.Accesses).ToImmutableArray();
        Validate(new([color], [Pass(1, inputs: reads)]));
        Reject(RenderPlanError.AccessLimit, new([color], [Pass(1, inputs: reads), Pass(2, inputs: [Input(color)])]));

        var ranges = Enumerable.Range(0, RenderPlanLimits.ImportedRanges)
            .Select(i => new RenderImportedRange(new(i, 1), RenderResourceState.ShaderRead, true)).ToImmutableArray();
        var layered = color with { ArrayLayers = ranges.Length, ImportedRanges = ranges };
        Validate(new([layered], []));
        Reject(RenderPlanError.ImportedRangeLimit, new([color, layered with { Id = new(3, 1), Allocation = new(3) }], []));
    }

    [TestMethod]
    public void TotalSubresourceScratchAndRangeWorkHaveInclusiveBoundaries()
    {
        const int mips = 15;
        var layers = RenderPlanLimits.ArrayLayers;
        var largeExtent = new RenderExtent(RenderExtentClass.FixedTexture,
            RenderPlanLimits.TextureDimension, RenderPlanLimits.TextureDimension);
        var resources = ImmutableArray.Create(
            Color(2) with { Extent = largeExtent, ArrayLayers = layers, MipLevels = mips },
            Color(3) with { Extent = largeExtent, ArrayLayers = layers });
        Assert.AreEqual(RenderPlanLimits.TrackedSubresources, (mips + 1) * layers);
        Validate(new(resources, []));
        Reject(RenderPlanError.ScratchLimit, new(resources.Add(Color(4)), []));

        resources = resources.Select(resource => resource with
        {
            Origin = RenderResourceOrigin.Imported,
            ImportedRanges = [new(new(0, resource.ArrayLayers * resource.MipLevels), RenderResourceState.ShaderRead, true)]
        }).ToImmutableArray();
        var fullCoverage = resources.Select(resource =>
            new RenderPassInput(resource.Id, resource.ImportedRanges[0].Range, RenderResourceState.ShaderRead)).ToArray();
        var repetitions = RenderPlanLimits.RangeVisits / RenderPlanLimits.TrackedSubresources - 1;
        var reads = Enumerable.Range(0, repetitions).SelectMany(_ => fullCoverage).ToImmutableArray();
        Assert.AreEqual(RenderPlanLimits.RangeVisits,
            reads.Sum(read => read.Range.Count) + RenderPlanLimits.TrackedSubresources);
        Validate(new(resources, [Pass(1, inputs: reads)]));
        Reject(RenderPlanError.RangeWorkLimit, new(resources, [Pass(1, inputs: reads.Add(fullCoverage[0]))]));
    }

    [TestMethod]
    public void RejectedVariantLeavesPriorValidatedPlanAndInputDescriptorsIntact()
    {
        var backBuffer = BackBuffer();
        var clear = Pass(4, outputs: [Output(backBuffer)]);
        var present = Present(backBuffer, clear.Id);
        var descriptor = new RenderPlanDescriptor([backBuffer], [present, clear]);
        var validated = Validate(descriptor);

        var invalid = descriptor with { Passes = [present with { Dependencies = [] }, clear] };
        Reject(RenderPlanError.MissingDependency, invalid);
        Assert.AreEqual(present, descriptor.Passes[0]);
        Assert.AreEqual(clear, descriptor.Passes[1]);
        Assert.AreEqual(backBuffer, validated.Resources[0]);
        AssertOrder(validated, 4, 1);
        AssertOrder(Validate(descriptor), 4, 1);
    }
}
