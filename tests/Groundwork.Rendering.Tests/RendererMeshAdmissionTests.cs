using System.Numerics;
using MeshResult = Groundwork.Rendering.MeshReplacementSubmissionResult;

namespace Groundwork.Rendering.Tests;

[TestClass]
public sealed class RendererMeshAdmissionTests
{
    private static readonly RenderScene Scene = new(
        Matrix4x4.Identity, Vector4.One, Matrix4x4.Identity, MathF.PI / 4, 0.1f, 100);

    [TestMethod]
    public void StartupAndInvalidSubmissionsPreserveInitialGeometry()
    {
        Assert.IsNull(new RendererFrameAdmission().MeshReplacement);
        var queue = RendererFrameAdmission.CreateWithMeshReplacement();
        var meshes = queue.MeshReplacement!;
        var pair = CreatePair();

        Assert.AreEqual(new MeshReplacementSnapshot(1, 0, 0), meshes.ReadMeshReplacement());
        Assert.AreEqual(MeshResult.NotRunning, meshes.TryDeclareMeshGeneration(2));
        Assert.AreEqual(MeshResult.NotRunning, meshes.TrySubmitMeshPair(1, pair));
        queue.MarkInitialized();
        queue.MarkRunning();

        Assert.AreEqual(MeshResult.InvalidGeneration, meshes.TryDeclareMeshGeneration(0));
        Assert.AreEqual(MeshResult.InvalidGeneration, meshes.TrySubmitMeshPair(0, pair));
        Assert.AreEqual(MeshResult.InvalidPair, meshes.TrySubmitMeshPair(1, null));
        Assert.AreEqual(MeshResult.DuplicateGeneration, meshes.TrySubmitMeshPair(1, pair));
        Assert.AreEqual(MeshResult.UndeclaredGeneration, meshes.TrySubmitMeshPair(2, pair));
        Assert.AreEqual(new MeshReplacementSnapshot(1, 1, 0), meshes.ReadMeshReplacement());
    }

    [TestMethod]
    public void MaximumGenerationCannotWrapOrBeSubmittedTwice()
    {
        var (_, meshes) = Start();
        var pair = CreatePair();

        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(ulong.MaxValue));
        Assert.AreEqual(MeshResult.StaleGeneration, meshes.TryDeclareMeshGeneration(2));
        Assert.AreEqual(MeshResult.StaleGeneration, meshes.TrySubmitMeshPair(2, pair));
        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshPair(ulong.MaxValue, pair));
        Assert.AreEqual(MeshResult.DuplicateGeneration, meshes.TrySubmitMeshPair(ulong.MaxValue, pair));
        Assert.AreEqual(new MeshReplacementSnapshot(ulong.MaxValue, 1, 0), meshes.ReadMeshReplacement());
    }

    [TestMethod]
    public void NewIntentSupersedesPendingAndInFlightPairsWithoutTakingAnotherCandidate()
    {
        var (_, meshes) = Start();
        var active = CreatePair();
        var original = active;
        var prepared = CreatePair();
        var newest = CreatePair();

        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(2));
        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshPair(2, CreatePair()));
        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(3));
        Assert.IsFalse(meshes.TryTake(out _));

        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshPair(3, prepared));
        Assert.IsTrue(meshes.TryTake(out var older));
        Assert.AreEqual(3UL, older!.Generation);
        Assert.AreSame(prepared, older.Pair);
        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(4));
        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshPair(4, newest));
        Assert.IsFalse(meshes.TryTake(out _));
        Assert.IsFalse(meshes.TryInstall(older, ref active, ref prepared, static () => true));
        Assert.AreSame(original, active);
        Assert.AreSame(older.Pair, prepared);
        Assert.AreEqual(new MeshReplacementSnapshot(4, 1, 0), meshes.ReadMeshReplacement());
        meshes.ReleaseCandidate(older, retry: true);

        Assert.IsTrue(meshes.TryTake(out var latest));
        Assert.AreEqual(4UL, latest!.Generation);
        Assert.AreSame(newest, latest.Pair);
        Assert.IsTrue(meshes.TryInstall(latest, ref active, ref newest, static () => true));
        Assert.AreSame(latest.Pair, active);
        Assert.AreSame(original, newest);
        Assert.AreEqual(new MeshReplacementSnapshot(4, 4, 0), meshes.ReadMeshReplacement());
    }

    [TestMethod]
    public void GuardDenialAndSuspensionAllowRetryButStopClosesAdmission()
    {
        var (queue, meshes) = Start();
        var active = CreatePair();
        var original = active;
        var prepared = CreatePair();

        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(2));
        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshPair(2, prepared));
        Assert.IsTrue(meshes.TryTake(out var denied));
        Assert.IsFalse(meshes.TryInstall(denied!, ref active, ref prepared, static () => false));
        Assert.AreSame(original, active);
        Assert.AreSame(denied!.Pair, prepared);
        meshes.ReleaseCandidate(denied!, retry: true);
        Assert.AreEqual(MeshResult.DuplicateGeneration, meshes.TrySubmitMeshPair(2, prepared));

        Assert.IsTrue(meshes.TryTake(out var suspended));
        Assert.IsTrue(queue.TrySetSurface(RenderSurfaceState.Suspended, 640, 480));
        Assert.IsFalse(meshes.TryInstall(suspended!, ref active, ref prepared, static () => true));
        meshes.ReleaseCandidate(suspended!, retry: true);
        Assert.IsFalse(meshes.TryTake(out _));

        Assert.IsTrue(queue.TrySetSurface(RenderSurfaceState.Drawable, 640, 480));
        Assert.IsTrue(meshes.TryTake(out var stopping));
        Assert.AreSame(prepared, stopping!.Pair);
        queue.BeginStop();
        Assert.AreEqual(MeshResult.Closed, meshes.TryDeclareMeshGeneration(3));
        Assert.AreEqual(MeshResult.Closed, meshes.TrySubmitMeshPair(2, prepared));
        Assert.IsFalse(meshes.TryInstall(stopping, ref active, ref prepared, static () => true));
        meshes.ReleaseCandidate(stopping, retry: true);
        Assert.IsFalse(meshes.TryTake(out _));
        Assert.AreSame(original, active);
        Assert.AreEqual(new MeshReplacementSnapshot(2, 1, 0), meshes.ReadMeshReplacement());
    }

    [TestMethod]
    public void StopBeforeTakingCandidateClosesBothAdmissionPaths()
    {
        var (queue, meshes) = Start();
        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(2));
        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshPair(2, CreatePair()));
        Assert.AreEqual(RenderSubmissionResult.Accepted, queue.TrySubmit(new RenderFrame(2, Vector4.Zero, Scene)));

        queue.BeginStop();

        Assert.AreEqual(MeshResult.Closed, meshes.TryDeclareMeshGeneration(3));
        Assert.AreEqual(RenderSubmissionResult.Closed, queue.TrySubmit(new RenderFrame(3, Vector4.Zero, Scene)));
        Assert.IsFalse(meshes.TryTake(out _));
        Assert.IsFalse(queue.TryTakeLatest(out _));
        Assert.AreEqual(new MeshReplacementSnapshot(2, 1, 0), meshes.ReadMeshReplacement());
    }

    [TestMethod]
    public void GeometryRedrawReusesFrameSequenceAndClearOnlyDoesNotPresentGeometry()
    {
        var (queue, meshes) = Start();
        var active = CreatePair();
        var prepared = CreatePair();
        Assert.ThrowsExactly<InvalidOperationException>(() => queue.RecordPresented(1));
        meshes.RecordPresented(1, 1);
        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(2));
        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshPair(2, prepared));

        Assert.IsTrue(meshes.TryTake(out var redraw));
        Assert.IsTrue(meshes.TryInstall(redraw!, ref active, ref prepared, static () => true));
        Assert.AreEqual(new MeshReplacementSnapshot(2, 2, 1), meshes.ReadMeshReplacement());
        Assert.IsFalse(queue.TryTakeLatest(out _));
        meshes.RecordPresented(1, 2);
        Assert.AreEqual(1UL, queue.Read().LastAcceptedSequence);
        Assert.AreEqual(1UL, queue.Read().LastPresentedSequence);

        Assert.AreEqual(RenderSubmissionResult.Accepted, queue.TrySubmit(new RenderFrame(2, Vector4.Zero)));
        Assert.IsTrue(queue.TryTakeLatest(out _));
        queue.RecordPresented(2);
        Assert.AreEqual(MeshResult.Accepted, meshes.TryDeclareMeshGeneration(3));
        Assert.AreEqual(MeshResult.Accepted, meshes.TrySubmitMeshPair(3, CreatePair()));
        Assert.IsFalse(meshes.TryTake(out _));
        Assert.AreEqual(2UL, queue.Read().LastPresentedSequence);
        Assert.AreEqual(new MeshReplacementSnapshot(3, 2, 2), meshes.ReadMeshReplacement());

        queue.BeginStop();
        Assert.AreEqual(new MeshReplacementSnapshot(3, 2, 2), meshes.ReadMeshReplacement());
        queue.MarkStopped();
        Assert.AreEqual(new MeshReplacementSnapshot(3, 0, 2), meshes.ReadMeshReplacement());
    }

    private static (RendererFrameAdmission Queue, RendererFrameAdmission.MeshPairAdmission Meshes) Start()
    {
        var queue = RendererFrameAdmission.CreateWithMeshReplacement();
        queue.MarkInitialized();
        Assert.IsTrue(queue.TrySetSurface(RenderSurfaceState.Drawable, 640, 480));
        queue.MarkRunning();
        Assert.AreEqual(RenderSubmissionResult.Accepted, queue.TrySubmit(new RenderFrame(1, Vector4.Zero, Scene)));
        Assert.IsTrue(queue.TryTakeLatest(out _));
        return (queue, queue.MeshReplacement!);
    }

    private static RenderMeshPair CreatePair()
    {
        var instance = new RenderMeshInstance(RenderMesh.Create([], []), Vector3.Zero);
        return new RenderMeshPair(instance, instance);
    }
}
