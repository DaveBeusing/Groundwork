using System.Collections.Immutable;
using Groundwork.Rendering.Passes;

namespace Groundwork.Rendering.Tests.Passes;

[TestClass]
public sealed partial class RenderPlanValidatorTests
{
    private static readonly RenderSubresourceRange Whole = new(0, 1);
    private static readonly RenderExtent FrameExtent = new(RenderExtentClass.FrameTexture, 640, 480);

    [TestMethod]
    public void ShadowTerrainPresentFollowsDependenciesWithStableTies()
    {
        var backBuffer = BackBuffer();
        var shadow = Shadow();
        var depth = Color(3) with
        {
            Format = RenderResourceFormat.Depth32Float, Usage = RenderResourceUsage.DepthAttachment
        };
        var shadowPass = Pass(50, outputs: [Output(shadow, RenderResourceState.DepthWrite)]);
        var clear = Pass(25, outputs: [Output(backBuffer), Output(depth, RenderResourceState.DepthWrite)]);
        var terrain = Pass(10, [shadowPass.Id, clear.Id], [Input(shadow)],
            [Output(backBuffer, load: RenderLoadAction.Load),
             Output(depth, RenderResourceState.DepthWrite, RenderLoadAction.Load)]);
        var present = Present(backBuffer, terrain.Id);
        var descriptor = new RenderPlanDescriptor([backBuffer, shadow, depth], [terrain, present, shadowPass, clear]);

        var validated = Validate(descriptor);
        AssertOrder(validated, 25, 50, 10, 1);
        AssertOrder(Validate(descriptor with { Passes = [present, clear, shadowPass, terrain] }), 25, 50, 10, 1);
        Assert.AreEqual(terrain, validated.Passes[2]);
        Assert.AreEqual(backBuffer, validated.Resources[0]);
    }

    [TestMethod]
    public void SkyAndTerrainVariantsKeepExplicitAttachmentOrder()
    {
        var backBuffer = BackBuffer();
        var geometry = Buffer(2, RenderResourceOrigin.Imported, RenderResourceUsage.VertexBuffer,
            new RenderImportedRange(Whole, RenderResourceState.VertexBuffer, true));
        var clear = Pass(100, outputs: [Output(backBuffer)]);
        var sky = Pass(80, [clear.Id], outputs: [Output(backBuffer, load: RenderLoadAction.Load)]);
        var terrain = Pass(40, [sky.Id], [Input(geometry, RenderResourceState.VertexBuffer)],
            [Output(backBuffer, load: RenderLoadAction.Load)]);
        var present = Present(backBuffer, terrain.Id);

        AssertOrder(Validate(new([backBuffer, geometry], [terrain, present, clear, sky])), 100, 80, 40, 1);
        AssertOrder(Validate(new([backBuffer, geometry], [present, clear, terrain with { Dependencies = [clear.Id] }])),
            100, 40, 1);
        AssertOrder(Validate(new([backBuffer], [Present(backBuffer, sky.Id), sky, clear])), 100, 80, 1);
    }

    [TestMethod]
    public void ClearOnlyInitializesUndefinedBackBuffer()
    {
        var backBuffer = BackBuffer();
        var clear = Pass(20, outputs: [Output(backBuffer)]);
        var present = Present(backBuffer, clear.Id);

        AssertOrder(Validate(new([backBuffer], [present, clear])), 20, 1);
        var missingClear = clear with { Outputs = [Output(backBuffer, load: RenderLoadAction.Load)] };
        var failure = Reject(RenderPlanError.UninitializedContent, new([backBuffer], [missingClear, present]));
        Assert.AreEqual(clear.Id, failure.Pass);
        Assert.AreEqual(backBuffer.Id, failure.Resource);
    }

    [TestMethod]
    public void OptionalHdrPostprocessingAndReadbackUseDeclaredTerminalStates()
    {
        var hdr = Color(3) with
        {
            Format = RenderResourceFormat.Rgba16Float, Admission = RenderResourceAdmission.OptionalVisual
        };
        var backBuffer = BackBuffer();
        var scene = Pass(40, outputs: [Output(hdr)]);
        var toneMap = Pass(20, [scene.Id], [Input(hdr)], [Output(backBuffer,
            load: RenderLoadAction.Discard, coverage: RenderWriteCoverage.Complete)]);
        AssertOrder(Validate(new([backBuffer, hdr], [Present(backBuffer, toneMap.Id), toneMap, scene])), 40, 20, 1);

        var readback = Buffer(4, RenderResourceOrigin.Produced,
            RenderResourceUsage.CopyDestination | RenderResourceUsage.Readback);
        var copy = Pass(10, [scene.Id], [Input(hdr, RenderResourceState.CopySource)],
            [Output(readback, RenderResourceState.CopyDestination,
                RenderLoadAction.Discard, coverage: RenderWriteCoverage.Complete)]);
        var consume = Pass(1, [copy.Id], [Input(readback, RenderResourceState.Readback)]);
        AssertOrder(Validate(new([hdr, readback], [consume, copy, scene])), 40, 10, 1);
    }

    [TestMethod]
    public void CachedShadowRequiresOnlyTheInitializedRangesThatAreRead()
    {
        var cache = Shadow() with
        {
            MipLevels = 3,
            Origin = RenderResourceOrigin.Imported,
            Usage = Shadow().Usage | RenderResourceUsage.CopyDestination,
            ImportedRanges =
            [
                new(new(2, 1), RenderResourceState.ShaderRead, true),
                new(new(0, 1), RenderResourceState.ShaderRead, true),
                new(new(1, 1), RenderResourceState.CopyDestination, false)
            ]
        };
        var sky = Pass(20, inputs: [new(cache.Id, new(2, 1), RenderResourceState.ShaderRead)]);
        var terrain = Pass(10, inputs: [Input(cache)]);
        var join = Pass(1, [terrain.Id, sky.Id]);
        AssertOrder(Validate(new([cache], [join, sky, terrain])), 10, 20, 1);

        var tooBroad = terrain with { Inputs = [new(cache.Id, new(0, 3), RenderResourceState.ShaderRead)] };
        Reject(RenderPlanError.UninitializedContent, new([cache], [tooBroad]));
        Reject(RenderPlanError.InvalidImport, new([cache with { ImportedRanges = [cache.ImportedRanges[0]] }], [sky]));
        Reject(RenderPlanError.InvalidImport,
            new([cache with { ImportedRanges = [.. cache.ImportedRanges, cache.ImportedRanges[0]] }], [sky]));
    }

    [TestMethod]
    public void DisjointMipAndLayerWritesCanProceedWithoutDependency()
    {
        var color = Color() with { MipLevels = 2, ArrayLayers = 2 };
        var writeMips = Pass(30, outputs: [Output(color) with { Range = new(0, 2) }]);
        var writeLayer = Pass(20, outputs: [Output(color) with { Range = new(2, 2) }]);
        var combine = Pass(1, [writeMips.Id, writeLayer.Id], [new(color.Id, new(0, 4), RenderResourceState.ShaderRead)]);
        AssertOrder(Validate(new([color], [combine, writeMips, writeLayer])), 20, 30, 1);

        var overlap = writeLayer with { Outputs = [Output(color) with { Range = new(1, 2) }] };
        Reject(RenderPlanError.MissingDependency, new([color], [overlap, writeMips]));
    }

    [TestMethod]
    public void OrderedSameResourceReadsAllowDifferentRequiredStates()
    {
        var color = Color() with
        {
            Origin = RenderResourceOrigin.Imported,
            ImportedRanges = [new(Whole, RenderResourceState.Common, true)]
        };
        var sample = Pass(10, inputs: [Input(color), Input(color)]);
        var copy = Pass(1, [sample.Id], [Input(color, RenderResourceState.CopySource)]);
        AssertOrder(Validate(new([color], [copy, sample])), 10, 1);

        Reject(RenderPlanError.MissingDependency, new([color], [copy with { Dependencies = [] }, sample]));
        Reject(RenderPlanError.ConflictingAccess,
            new([color], [sample with { Inputs = [Input(color), Input(color, RenderResourceState.CopySource)] }]));
    }

    [TestMethod]
    public void MissingUnorderedAndAfterReadProducersAreRejected()
    {
        var color = Color();
        var read = Pass(1, inputs: [Input(color)]);
        var write = Pass(2, outputs: [Output(color)]);
        Reject(RenderPlanError.MissingProducer, new([color], [read]));
        Reject(RenderPlanError.MissingDependency, new([color], [read, write]));
        Reject(RenderPlanError.MissingProducer, new([color], [read, write with { Dependencies = [read.Id] }]));
        Reject(RenderPlanError.MissingProducer,
            new([color], [write with { Outputs = [Output(color, load: RenderLoadAction.Load)] }]));
    }

    [TestMethod]
    [DataRow(RenderLoadAction.Clear, RenderStoreAction.Discard, RenderWriteCoverage.Complete)]
    [DataRow(RenderLoadAction.Discard, RenderStoreAction.Store, RenderWriteCoverage.Partial)]
    public void DiscardAndPartialOverwriteCannotBeRead(
        RenderLoadAction load, RenderStoreAction store, RenderWriteCoverage coverage)
    {
        var color = Color() with
        {
            Origin = RenderResourceOrigin.Imported,
            ImportedRanges = [new(Whole, RenderResourceState.ColorAttachment, true)]
        };
        var invalidate = Pass(10, outputs: [Output(color, load: load, store: store, coverage: coverage)]);
        var read = Pass(1, [invalidate.Id], [Input(color)]);
        Reject(RenderPlanError.UninitializedContent, new([color], [read, invalidate]));

        var clear = Pass(20, [invalidate.Id], outputs: [Output(color)]);
        AssertOrder(Validate(new([color], [invalidate, clear, read with { Dependencies = [clear.Id] }])), 10, 20, 1);
    }

    [TestMethod]
    public void OverlappingWritesAndFeedbackRequireADeclaredOrderOrAnAttachmentLoad()
    {
        var color = Color();
        var write = Pass(1, outputs: [Output(color)]);
        var writeAgain = Pass(2, outputs: [Output(color)]);
        var failure = Reject(RenderPlanError.MissingDependency, new([color], [write, writeAgain]));
        Assert.AreEqual(writeAgain.Id, failure.Pass);
        Assert.AreEqual(write.Id, failure.OtherPass);

        Reject(RenderPlanError.ConflictingAccess, new([color], [write with { Outputs = [Output(color), Output(color)] }]));
        Reject(RenderPlanError.ConflictingAccess, new([color], [write with { Inputs = [Input(color)] }]));
        AssertOrder(Validate(new([color], [writeAgain with { Dependencies = [write.Id] }, write])), 1, 2);
    }

    [TestMethod]
    public void TerminalConsumptionCannotBeFollowedByAnotherAccess()
    {
        var backBuffer = BackBuffer();
        var clear = Pass(30, outputs: [Output(backBuffer)]);
        var present = Present(backBuffer, clear.Id);
        var tooLate = Pass(5, [present.Id], outputs: [Output(backBuffer)]);
        Reject(RenderPlanError.UseAfterTerminal, new([backBuffer], [tooLate, present, clear]));
        Reject(RenderPlanError.MissingDependency,
            new([backBuffer], [present with { Dependencies = [] }, clear]));
        Reject(RenderPlanError.UninitializedContent, new([backBuffer], [present with { Dependencies = [] }]));
    }

    private static RenderResourceDescriptor Color(ulong slot = 2) =>
        new(new(slot, 7), new(slot), RenderResourceOrigin.Produced, RenderResourceFormat.Rgba8Unorm, FrameExtent, 1, 1,
            RenderResourceUsage.ColorAttachment | RenderResourceUsage.ShaderRead | RenderResourceUsage.CopySource,
            RenderResourceAdmission.Required, []);

    private static RenderResourceDescriptor BackBuffer() =>
        Color(1) with
        {
            Origin = RenderResourceOrigin.Imported,
            Usage = RenderResourceUsage.ColorAttachment | RenderResourceUsage.Present,
            ImportedRanges = [new(Whole, RenderResourceState.Present, false)]
        };

    private static RenderResourceDescriptor Shadow() =>
        Color() with
        {
            Format = RenderResourceFormat.Depth32Float, Extent = new(RenderExtentClass.FixedTexture, 1024, 1024),
            Usage = RenderResourceUsage.DepthAttachment | RenderResourceUsage.ShaderRead
        };

    private static RenderResourceDescriptor Buffer(
        ulong slot, RenderResourceOrigin origin, RenderResourceUsage usage, params RenderImportedRange[] imports) =>
        new(new(slot, 1), new(slot), origin, RenderResourceFormat.Buffer, new(RenderExtentClass.Buffer, 4096, 1),
            1, 1, usage, RenderResourceAdmission.Required, [.. imports]);

    private static RenderPassDescriptor Pass(uint id, ImmutableArray<RenderPassId> dependencies = default,
        ImmutableArray<RenderPassInput> inputs = default, ImmutableArray<RenderPassOutput> outputs = default) =>
        new(new(id), dependencies, inputs, outputs);

    private static RenderPassDescriptor Present(RenderResourceDescriptor resource, RenderPassId dependency) =>
        Pass(1, [dependency], [Input(resource, RenderResourceState.Present)]);

    private static RenderPassInput Input(
        RenderResourceDescriptor resource, RenderResourceState state = RenderResourceState.ShaderRead) =>
        new(resource.Id, Whole, state);

    private static RenderPassOutput Output(
        RenderResourceDescriptor resource, RenderResourceState state = RenderResourceState.ColorAttachment,
        RenderLoadAction load = RenderLoadAction.Clear, RenderStoreAction store = RenderStoreAction.Store,
        RenderWriteCoverage coverage = RenderWriteCoverage.Partial) =>
        new(resource.Id, Whole, state, load, store, coverage);

    private static ValidatedRenderPlan Validate(RenderPlanDescriptor descriptor)
    {
        Assert.IsTrue(RenderPlanValidator.TryValidate(descriptor, out var plan, out var failure), failure.ToString());
        Assert.AreEqual(default(RenderPlanFailure), failure);
        return plan;
    }

    private static RenderPlanFailure Reject(RenderPlanError error, RenderPlanDescriptor descriptor)
    {
        Assert.IsFalse(RenderPlanValidator.TryValidate(descriptor, out var plan, out var failure));
        Assert.IsNull(plan);
        Assert.AreEqual(error, failure.Error, failure.ToString());
        return failure;
    }

    private static void AssertOrder(ValidatedRenderPlan plan, params uint[] expected) =>
        CollectionAssert.AreEqual(expected, plan.Passes.Select(pass => pass.Id.Value).ToArray());
}
