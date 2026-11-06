using System.Numerics;

namespace Groundwork.Rendering.Tests;

[TestClass]
public sealed class RendererFrameAdmissionTests
{
    private static readonly RenderScene Scene = new(
        Matrix4x4.CreateTranslation(1, 2, 3), Vector4.One,
        Matrix4x4.CreateLookAt(new Vector3(1, 2, 3), Vector3.Zero, Vector3.UnitY),
        MathF.PI / 4, 0.1f, 100);

    [TestMethod]
    public void SubmissionAndPresentationTrackDistinctSequences()
    {
        var queue = Start();
        var frame = new RenderFrame(1, Vector4.One);
        Assert.AreEqual(RenderSubmissionResult.Accepted, queue.TrySubmit(frame));
        frame = new RenderFrame(2, Vector4.Zero, Scene);
        Assert.AreEqual(RenderSubmissionResult.Replaced, queue.TrySubmit(frame));
        frame = frame with { ClearColor = Vector4.One };
        Assert.AreEqual(RenderSubmissionResult.StaleSequence, queue.TrySubmit(frame));
        Assert.AreEqual(RenderSubmissionResult.InvalidFrame, queue.TrySubmit(frame with { ClearColor = new Vector4(float.NaN) }));

        Assert.IsTrue(queue.TryTakeLatest(out var latest));
        Assert.AreEqual(new RenderFrame(2, Vector4.Zero, Scene), latest);
        Assert.IsFalse(queue.TryTakeLatest(out _));
        Assert.AreEqual(2UL, queue.Read().LastAcceptedSequence);
        Assert.AreEqual(0UL, queue.Read().LastPresentedSequence);
        Assert.AreEqual(1UL, queue.Read().ReplacedFrames);

        queue.RecordSkipped();
        Assert.ThrowsExactly<InvalidOperationException>(() => queue.RecordPresented(1));
        queue.RecordPresented(2);
        queue.RecordPresented(2); // Redrawing the same frame does not change the sequence.
        Assert.AreEqual(2UL, queue.Read().LastPresentedSequence);
        Assert.AreEqual(1UL, queue.Read().SkippedFrames);
    }

    [TestMethod]
    public void InvalidSceneValuesCannotAdvanceSequenceOrReplacePendingFrame()
    {
        var queue = Start();
        var first = new RenderFrame(1, Vector4.Zero, Scene);
        Assert.AreEqual(RenderSubmissionResult.Accepted, queue.TrySubmit(first));

        var invalidWorld = Matrix4x4.Identity;
        invalidWorld.M34 = 1;
        var badScenes = new[]
        {
            Scene with { World = invalidWorld },
            Scene with { World = Matrix4x4.CreateTranslation(float.NaN, 0, 0) },
            Scene with { View = Matrix4x4.CreateScale(2) },
            Scene with { View = Matrix4x4.CreateScale(-1, 1, 1) },
            Scene with { Tint = new Vector4(-0.1f) },
            Scene with { VerticalFieldOfView = MathF.PI },
            Scene with { VerticalFieldOfView = 0 },
            Scene with { NearPlane = 0 },
            Scene with { NearPlane = Scene.FarPlane },
            Scene with { FarPlane = float.PositiveInfinity },
        };
        foreach (var invalid in badScenes)
        {
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame, queue.TrySubmit(new RenderFrame(2, Vector4.Zero, invalid)));
        }

        Assert.AreEqual(RenderSubmissionResult.InvalidFrame, queue.TrySubmit(default));
        Assert.AreEqual(1UL, queue.Read().LastAcceptedSequence);
        Assert.AreEqual(0UL, queue.Read().ReplacedFrames);
        Assert.IsTrue(queue.TryTakeLatest(out var actual));
        Assert.AreEqual(first, actual);
    }

    [TestMethod]
    public void SuspensionKeepsOnlyTheNewestPendingFrameAndResumeAppliesNewExtent()
    {
        var queue = Start();
        Assert.ThrowsExactly<ArgumentException>(() => queue.TrySetSurface(RenderSurfaceState.Drawable, 0, 0));
        Assert.ThrowsExactly<ArgumentException>(() => queue.TrySetSurface(RenderSurfaceState.Drawable, 640, 0));
        Assert.IsTrue(queue.TrySetSurface(RenderSurfaceState.Suspended, 640, 480));
        Assert.AreEqual(RenderSubmissionResult.Accepted, queue.TrySubmit(new RenderFrame(1, Vector4.Zero)));
        Assert.AreEqual(RenderSubmissionResult.Replaced, queue.TrySubmit(new RenderFrame(2, Vector4.Zero)));
        Assert.IsFalse(queue.TryTakeLatest(out _));
        Assert.IsTrue(queue.TrySetSurface(RenderSurfaceState.Drawable, 800, 600));
        Assert.IsTrue(queue.TryTakeLatest(out var frame));
        Assert.AreEqual(2UL, frame.Sequence);
        var snapshot = queue.Read();
        Assert.AreEqual(RenderSurfaceState.Drawable, snapshot.Surface);
        Assert.AreEqual((800, 600), (snapshot.AppliedWidth, snapshot.AppliedHeight));
    }

    [TestMethod]
    public void TerminalAdmissionClosesEvenAfterPartialStartup()
    {
        var queue = new RendererFrameAdmission();
        var frame = new RenderFrame(1, Vector4.Zero);
        Assert.AreEqual(RenderSubmissionResult.NotRunning, queue.TrySubmit(frame));
        Assert.ThrowsExactly<InvalidOperationException>(queue.MarkRunning);
        queue.MarkInitialized();
        Assert.AreEqual(RenderSubmissionResult.NotRunning, queue.TrySubmit(frame));
        queue.BeginStop();
        Assert.ThrowsExactly<InvalidOperationException>(queue.MarkRunning);
        Assert.AreEqual(RenderSubmissionResult.Closed, queue.TrySubmit(frame));
        Assert.IsFalse(queue.TrySetSurface(RenderSurfaceState.Drawable, 640, 480));
        queue.MarkStopped();
        queue.BeginStop();
        queue.MarkStopped();
        Assert.AreEqual(RendererLifecycle.Stopped, queue.Read().Lifecycle);
    }

    [TestMethod]
    public void FirstFaultAndIncompleteCleanupRemainVisibleAfterLateSettlement()
    {
        var queue = Start();
        var primary = new RenderFaultSummary(1, -1, -2, 1);
        var cleanup = new RenderFaultSummary(2, -3, null, 1);
        Assert.AreEqual(RenderSubmissionResult.Accepted, queue.TrySubmit(new RenderFrame(1, Vector4.Zero)));
        Assert.IsTrue(queue.TryTakeLatest(out _));
        Assert.AreEqual(RenderSubmissionResult.Accepted, queue.TrySubmit(new RenderFrame(2, Vector4.Zero)));

        queue.RecordTerminalFault(primary);
        queue.RecordTerminalFault(primary with { ErrorCode = -10 });
        queue.RecordCleanupFault(cleanup, incomplete: true);
        queue.RecordCleanupFault(cleanup with { ErrorCode = -20 }, incomplete: false);
        Assert.AreEqual(RenderSurfaceState.Unavailable, queue.Read().Surface);
        Assert.AreEqual(640, queue.Read().AppliedWidth);
        Assert.IsFalse(queue.TryTakeLatest(out _));
        Assert.AreEqual(RenderSubmissionResult.Closed, queue.TrySubmit(new RenderFrame(3, Vector4.Zero)));
        queue.RecordPresented(1); // An already-admitted call may return after closure.
        queue.MarkStopped();

        var snapshot = queue.Read();
        Assert.AreEqual(RendererLifecycle.Stopped, snapshot.Lifecycle);
        Assert.AreEqual(RenderSurfaceState.Unavailable, snapshot.Surface);
        Assert.AreEqual(2UL, snapshot.LastAcceptedSequence);
        Assert.AreEqual(1UL, snapshot.LastPresentedSequence);
        Assert.AreEqual(primary, snapshot.FirstFault);
        Assert.AreEqual(cleanup, snapshot.FirstCleanupFault);
        Assert.IsTrue(snapshot.IncompleteCleanup);
    }

    [TestMethod]
    public void MaximumSequenceCannotWrapOrRepeat()
    {
        var queue = Start();
        var frame = new RenderFrame(ulong.MaxValue, Vector4.Zero);
        Assert.AreEqual(RenderSubmissionResult.Accepted, queue.TrySubmit(frame));
        Assert.AreEqual(RenderSubmissionResult.StaleSequence, queue.TrySubmit(frame));
        Assert.AreEqual(RenderSubmissionResult.InvalidFrame, queue.TrySubmit(frame with { Sequence = 0 }));
        Assert.AreEqual(RenderSubmissionResult.StaleSequence, queue.TrySubmit(frame with { Sequence = 1 }));
        Assert.AreEqual(ulong.MaxValue, queue.Read().LastAcceptedSequence);
    }

    [TestMethod]
    public void ConcurrentSubmissionsKeepTheHighestSequence()
    {
        var queue = Start();
        Parallel.For(1, 65, sequence => queue.TrySubmit(new RenderFrame((ulong)sequence, Vector4.Zero)));
        Assert.AreEqual(64UL, queue.Read().LastAcceptedSequence);
        Assert.IsTrue(queue.TryTakeLatest(out var latest));
        Assert.AreEqual(64UL, latest.Sequence);
        Assert.IsFalse(queue.TryTakeLatest(out _));

        Parallel.Invoke(queue.BeginStop, () => queue.TrySubmit(new RenderFrame(65, Vector4.Zero)));
        Assert.AreEqual(RendererLifecycle.Stopping, queue.Read().Lifecycle);
        Assert.IsFalse(queue.TryTakeLatest(out _));
        Assert.AreEqual(RenderSubmissionResult.Closed, queue.TrySubmit(new RenderFrame(66, Vector4.Zero)));
    }

    private static RendererFrameAdmission Start()
    {
        var queue = new RendererFrameAdmission();
        queue.MarkInitialized();
        Assert.IsTrue(queue.TrySetSurface(RenderSurfaceState.Drawable, 640, 480));
        queue.MarkRunning();
        return queue;
    }
}
