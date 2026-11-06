using System.Numerics;
using MeshResult = Groundwork.Rendering.MeshReplacementSubmissionResult;

namespace Groundwork.Rendering.Tests;

[TestClass]
public sealed class RendererMeshRegionTests
{
    private static readonly RenderMesh EmptyMesh = RenderMesh.Create([], []);

    [TestMethod]
    public void RegionCopiesAllNineInstancesAndRejectsEmptyOversizedOrMissingInstances()
    {
        var instances = Enumerable.Range(0, RenderMeshRegion.MaximumInstanceCount).Select(Instance).ToArray();
        var expected = instances[0];
        var region = new RenderMeshRegion(instances);
        instances[0] = default;

        Assert.AreEqual(9, region.Instances.Length);
        Assert.AreEqual(expected, region.Instances[0]);
        Assert.AreEqual(Instance(8), region.Instances[^1]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RenderMeshRegion([]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RenderMeshRegion(new RenderMeshInstance[10]));
        Assert.ThrowsExactly<ArgumentException>(() => new RenderMeshRegion([default]));
    }

    [TestMethod]
    public void SupersededRegionCannotInstallAndLatestRegionRemovesLeavingInstancesAtomically()
    {
        var original = new RenderMeshRegion([Instance(0), Instance(1), Instance(2)]);
        var renderer = new FakeRegionRenderer(original);
        var meshes = renderer.Meshes;
        var older = new RenderMeshRegion([Instance(1), Instance(2), Instance(3)]);
        var latest = new RenderMeshRegion([Instance(2), Instance(4)]);

        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(2));
        Assert.AreEqual(MeshResult.InvalidRegion, meshes.TrySubmitMeshRegion(2, null));
        Assert.AreEqual(MeshResult.UndeclaredGeneration, meshes.TrySubmitMeshRegion(3, latest));
        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshRegion(2, older));
        Assert.AreEqual(MeshResult.DuplicateGeneration, meshes.TrySubmitMeshRegion(2, older));
        Assert.IsTrue(meshes.TryTake(out var superseded));

        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(3));
        Assert.AreEqual(MeshResult.StaleGeneration, meshes.TrySubmitMeshRegion(2, older));
        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshRegion(3, latest));
        Assert.IsFalse(meshes.TryTake(out _));
        Assert.IsFalse(renderer.TryInstall(superseded!));
        Assert.AreSame(original, renderer.Active);
        Assert.AreEqual(new MeshReplacementSnapshot(3, 1, 0), meshes.ReadMeshReplacement());
        meshes.ReleaseCandidate(superseded!, retry: true);

        Assert.IsTrue(meshes.TryTake(out var current));
        Assert.IsTrue(renderer.TryInstall(current!));
        CollectionAssert.AreEqual(new[] { Instance(2), Instance(4) }, renderer.Active.Instances.ToArray());
        Assert.AreSame(original, renderer.Retired);
        Assert.AreEqual(new MeshReplacementSnapshot(3, 3, 0), meshes.ReadMeshReplacement());
        renderer.PresentScene(3);
        Assert.AreEqual(new MeshReplacementSnapshot(3, 3, 3), meshes.ReadMeshReplacement());
    }

    [TestMethod]
    public void ClearOnlyDoesNotAdvanceRegionAndStopVetoesStagedGeometry()
    {
        var original = new RenderMeshRegion([Instance(0)]);
        var renderer = new FakeRegionRenderer(original);
        var meshes = renderer.Meshes;
        var next = new RenderMeshRegion([Instance(1)]);
        renderer.PresentScene(1);
        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(2));
        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshRegion(2, next));
        Assert.IsTrue(meshes.TryTake(out var current));
        Assert.IsTrue(renderer.TryInstall(current!));

        renderer.TakeFrame(new RenderFrame(2, Vector4.Zero));
        renderer.Frames.RecordPresented(2);
        Assert.AreEqual(new MeshReplacementSnapshot(2, 2, 1), meshes.ReadMeshReplacement());
        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(3));
        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshRegion(3, original));
        Assert.IsFalse(meshes.TryTake(out _));
        renderer.TakeSceneFrame(3);
        Assert.IsTrue(meshes.TryTake(out var staged));
        renderer.Frames.BeginStop();

        Assert.IsFalse(renderer.TryInstall(staged!));
        Assert.AreSame(next, renderer.Active);
        Assert.AreEqual(MeshResult.Closed, meshes.TrySubmitMeshRegion(3, original));
        Assert.AreEqual(MeshResult.Closed, meshes.TryDeclareMeshGeneration(4));
        renderer.Frames.MarkStopped();
        Assert.AreEqual(new MeshReplacementSnapshot(3, 0, 1), meshes.ReadMeshReplacement());
    }

    private static RenderMeshInstance Instance(int x) => new(EmptyMesh, new Vector3(x, 0, 0));

    private sealed class FakeRegionRenderer
    {
        private static readonly RenderScene Scene = new(
            Matrix4x4.Identity, Vector4.One, Matrix4x4.Identity, MathF.PI / 4, 0.1f, 100);

        private RenderMeshRegion _active;
        private ulong _frameSequence;

        public FakeRegionRenderer(RenderMeshRegion initial)
        {
            _active = initial;
            Frames.MarkInitialized();
            Assert.IsTrue(Frames.TrySetSurface(RenderSurfaceState.Drawable, 640, 480));
            Frames.MarkRunning();
            TakeSceneFrame(1);
        }

        public RendererFrameAdmission Frames { get; } = RendererFrameAdmission.CreateWithMeshRegionReplacement();
        public RendererFrameAdmission.MeshRegionAdmission Meshes => Frames.MeshRegionReplacement!;
        public RenderMeshRegion Active => _active;
        public RenderMeshRegion? Retired { get; private set; }

        public bool TryInstall(RendererFrameAdmission.MeshRegionAdmission.Candidate candidate)
        {
            var prepared = candidate.Region;
            if (!Meshes.TryInstall(candidate, ref _active, ref prepared, static () => true))
                return false;

            Retired = prepared;
            return true;
        }

        public void TakeSceneFrame(ulong sequence) => TakeFrame(new RenderFrame(sequence, Vector4.Zero, Scene));

        public void TakeFrame(RenderFrame frame)
        {
            Assert.AreEqual(RenderSubmissionResult.Accepted, Frames.TrySubmit(frame));
            Assert.IsTrue(Frames.TryTakeLatest(out var taken));
            _frameSequence = taken.Sequence;
        }

        public void PresentScene(ulong generation) => Meshes.RecordPresented(_frameSequence, generation);
    }
}
