using System.Numerics;
using System.Text.Json;
using Groundwork.Core;
using Groundwork.Core.Tests;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12Q02NativeBaselineExhaustion")]
    public async Task Q02NativeFirstEditWaitsForR09BaselineCreditRelease()
    {
        var unit = new VolumeResources(50_388_576, 8_388_608, 8_388_608, 8_388_608, 1, 1, 1, 64, 1);
        var baseline = new VolumeResources(101_039_296, 16_777_216, 16_777_216, 16_777_216, 0, 0, 0, 144, 2);
        var total = new VolumeResources(302_593_600, 50_331_648, 50_331_648, 50_331_648, 4, 4, 4, 400, 6);
        var limits = new VolumeStreamingLimits(total, baseline, unit, unit, unit, unit, 12, 12, 256);
        const ulong editMask = 5;
        const ulong editFrame = 6;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        Assert.IsTrue(Environment.UserInteractive, "An interactive Windows desktop is required.");
        using var run = new VolumeRun(editable: true, openFloor: true, qualificationLimits: limits, qualificationOverlayRows: 8192);
        var runtime = run.Fixture.Runtime;
        var limit = TimeSpan.FromSeconds(15);
        var context = new EngineModuleContext(run.Fixture.Clock, new RuntimeTimingLimits(limit, limit, limit),
            runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions(
            "Groundwork Q02 FirstEdit baseline credits", TerrainWidth, TerrainHeight, 32), runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = D3D12Renderer.CreateWithVolumeTerrain(window, runtime.RuntimeOwnership, run.Terrain,
            1, QualificationDiagnostics with { EnableResourceSnapshots = true }, startupTimeout: limit, operationTimeout: limit);
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            AssertPlanetAdapter(renderer.ReadDiagnostics().Adapter, false);

            await PresentMask(run.Terrain.View, 1);
            await run.Publish(1, cut: false, renderer);
            using var predecessor = run.Terrain.TryRead();
            Assert.IsNotNull(predecessor);
            var predecessorInfo = predecessor.Info;
            await PresentTerrain(predecessorInfo, 2);

            var nextView = Rebase(new(16, 16, 0));
            await PresentMask(nextView, 3);
            await run.Publish(1, cut: false, renderer, sourceRevision: 1);
            using var visible = run.Terrain.TryRead();
            Assert.IsNotNull(visible);
            var visibleInfo = visible.Info;
            Assert.AreNotEqual(predecessorInfo.Generation, visibleInfo.Generation);
            await PresentTerrain(visibleInfo, 4);
            var sealing = await run.BeginHeldTick();
            Assert.IsTrue(run.Terrain.BeginTick(sealing));
            using var speculative = run.StagePrivateSpeculation(1);
            var edit = new VolumeBrushIntent(sealing.Tick, 1, 1, new(28, 28, -2), new(36, 36, 1), VolumeBrushOperation.Remove);
            Assert.AreEqual(VolumeAdmission.Accepted, run.Terrain.SealEditTick(sealing.Tick, run.Descriptor, [edit]));
            var editView = Rebase(new(32, 32, 0));
            await PresentMask(editView, editMask);
            var selection = VolumeStreamingSelection.Select(run.Descriptor, new(0, 32, 0), new(-1, 0, 0));
            using var work = run.StageReferenceEdit(edit, selection);
            Assert.IsFalse(speculative.IsCancellationRequested);
            Assert.IsTrue(work.Execute(token));
            var beforeStage = ReadTerrainBacking();
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TryStageVolumeTerrain(work.Generation));
            await AwaitStatus(renderer, _ => renderer.IsVolumeTerrainReady(work.Generation), token);
            var nativeReadyCount = 0;
            while (run.Terrain.TryReadEvent(out var evidence))
            {
                if (evidence.Kind != VolumeEventKind.NativeReady)
                    continue;
                Assert.AreEqual(work.Generation, evidence.Generation);
                Assert.AreEqual(edit, evidence.Edit);
                Assert.AreEqual(editView, evidence.View);
                Assert.AreEqual(sealing, evidence.Stamp);
                nativeReadyCount++;
            }
            Assert.AreEqual(1, nativeReadyCount);
            await run.FinishHeldTick();

            var deniedTick = await run.BeginHeldTick();
            Assert.AreEqual(sealing.Tick + 1, deniedTick.Tick);
            Assert.IsFalse(run.Terrain.BeginTick(deniedTick));
            var deniedCut = await CoreRuntimeFixture.SealAsync(run.World, deniedTick.Tick, BarrierKind.CommitResults);
            var original = run.World.Publication.Committed.Read();
            Assert.AreEqual(visibleInfo, ((VolumeWorldRoot)original.Root).Terrain);
            var before = run.Terrain.Statistics;
            var baselineUsed = unit.Retained + unit.Retained +
                new VolumeResources(0, 0, 0, 0, 0, 0, 0, before.Readers, 0);
            Assert.IsTrue(baselineUsed.Fits(baseline));
            Assert.IsFalse(unit.Retained.Fits(baseline - baselineUsed));
            Assert.IsTrue(before.Used == baselineUsed + unit.Retained + unit);
            Assert.AreEqual(4, before.Generations);
            var nativeBefore = ReadTerrainBacking();
            Assert.IsGreaterThan(beforeStage.Live + beforeStage.Retained, nativeBefore.Live + nativeBefore.Retained);
            var stageBacking = beforeStage.LiveBytes + beforeStage.RetainedBytes;
            var preparedBacking = nativeBefore.LiveBytes + nativeBefore.RetainedBytes;
            Assert.IsGreaterThan(stageBacking, preparedBacking);

            Assert.IsNull(renderer.TryPrepareVolumePublication(work.Generation, deniedCut, [], out var denied));
            Assert.AreEqual(MeshReplacementSubmissionResult.StaleGeneration, denied);
            Assert.IsTrue(run.Terrain.TryReadEvent(out var denial));
            Assert.AreEqual(VolumeEventKind.Backpressure, denial.Kind);
            Assert.AreEqual(0UL, denial.Generation);
            Assert.AreEqual(deniedTick, denial.Stamp);
            Assert.IsFalse(run.Terrain.TryReadEvent(out _));
            Assert.AreSame(original.Root, run.World.Publication.Committed.Read().Root);
            Assert.AreEqual(original.Stamp, run.World.Publication.Committed.Read().Stamp);
            Assert.IsTrue(before == run.Terrain.Statistics);
            Assert.IsTrue(run.Terrain.TryReadEdit(edit.Tick, edit.Id, out var editStatus, out var editStamp));
            Assert.AreEqual(VolumeEditStatus.Building, editStatus);
            Assert.IsNull(editStamp);
            Assert.AreEqual(editMask, renderer.Read().LastPresentedSequence);
            Assert.IsTrue(renderer.IsVolumeTerrainReady(work.Generation));
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual(predecessorInfo, predecessor.Info);
            Assert.AreEqual(nativeBefore, ReadTerrainBacking());
            var unchanged = run.World.Publication.Finalization.TryFinalize(CoreRuntimeFixture.Prepare(run.World, deniedCut, []));
            Assert.AreEqual(BarrierStatus.Unchanged, unchanged.Status);
            CoreRuntimeFixture.PublishObserver(run.World, unchanged.Stamp!.Value, (int)deniedTick.Tick);

            // The renderer's first frame has already retired. The remaining old reader owns the
            // predecessor's generation and credits until it is released.
            predecessor.Dispose();
            var released = run.Terrain.Statistics;
            Assert.AreEqual(before.Generations - 1, released.Generations);
            Assert.AreEqual(before.Readers - 1, released.Readers);
            Assert.IsTrue(before.Used - released.Used == (unit.Retained with { LeaseSlots = unit.LeaseSlots + 1 }));
            Assert.AreEqual(nativeBefore, ReadTerrainBacking());
            var retry = await run.BeginHeldTick();
            Assert.AreEqual(deniedTick.Tick + 1, retry.Tick);
            Assert.IsFalse(run.Terrain.BeginTick(retry));
            var retryCut = await CoreRuntimeFixture.SealAsync(run.World, retry.Tick, BarrierKind.CommitResults);
            var prepared = renderer.TryPrepareVolumePublication(work.Generation, retryCut, [], out var result);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, result);
            Assert.IsNotNull(prepared);
            var committed = run.World.Publication.Finalization.TryFinalize(prepared);
            Assert.AreEqual(BarrierStatus.Committed, committed.Status);
            CoreRuntimeFixture.PublishObserver(run.World, committed.Stamp!.Value, (int)retry.Tick);
            var newRoot = (VolumeWorldRoot)run.World.Publication.Committed.Read().Root;
            Assert.AreEqual(work.Generation, newRoot.Terrain.Generation);
            Assert.AreEqual(2UL, newRoot.Terrain.TerrainRevision);
            Assert.AreEqual(edit, newRoot.CommittedEdit);
            Assert.AreEqual(visibleInfo, visible.Info);
            await PresentTerrain(newRoot.Terrain, editFrame);
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            var consuming = await run.BeginHeldTick();
            Assert.AreEqual(retry.Tick + 1, consuming.Tick);
            Assert.IsTrue(run.Terrain.BeginTick(consuming));
            Assert.IsFalse(run.Terrain.Statistics.MovementHeld);
            var receipts = 0;
            var openings = 0;
            while (run.Terrain.TryReadEvent(out var evidence))
            {
                if (evidence.Generation != work.Generation || evidence.View != editView || evidence.Frame != editFrame)
                    continue;
                if (evidence.Kind == VolumeEventKind.Accepted)
                {
                    Assert.AreEqual(committed.Stamp, evidence.Stamp);
                    receipts++;
                }
                if (evidence.Kind == VolumeEventKind.Opened)
                {
                    Assert.AreEqual(consuming, evidence.Stamp);
                    openings++;
                }
            }
            Assert.AreEqual(1, receipts);
            Assert.AreEqual(1, openings);
            await run.FinishHeldTick();

            visible.Dispose();
            speculative.Dispose();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            await renderer.StopAsync(token);
            await window.StopAsync(token);
            run.Terrain.Stop();
            var stopped = run.Terrain.Statistics;
            Assert.AreEqual(0, stopped.Readers);
            Assert.AreEqual(0, stopped.Generations);
            Assert.IsTrue(stopped.Used == default && stopped.Peak.Fits(total));
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var final));
            Assert.IsFalse(final.LedgerOverflowed || final.LedgerHasUnknownSizes);
            Assert.AreEqual(D3D12CustodyStatus.None, final.Custody);
            foreach (var owner in Enum.GetValues<D3D12ResourceOwner>())
            {
                var balance = final.GetBalance(owner);
                Assert.AreEqual(0, balance.Live + balance.Retained, $"Outstanding native allocations for {owner}.");
                Assert.AreEqual(0UL, balance.LiveBytes + balance.RetainedBytes);
            }
            var diagnostics = renderer.ReadDiagnostics();
            Assert.IsTrue(diagnostics.DebugLayerEnabled && diagnostics.DredEnabled);
            AssertCleanDiagnostics(diagnostics, diagnosticsRequested: true);

            var returned = before.Used - released.Used;
            Console.WriteLine("Q02_BASELINE " + JsonSerializer.Serialize(new
            {
                adapter = diagnostics.Adapter, sealedTick = sealing.Tick, nativeReadyCount, deniedTick = deniedTick.Tick,
                retryTick = retry.Tick, consumedTick = consuming.Tick, receipts, openings,
                predecessor = predecessorInfo.Generation, visible = visibleInfo.Generation, published = newRoot.Terrain.Generation,
                preStageTerrainBytes = stageBacking, addedTerrainBytes = preparedBacking - stageBacking,
                deniedNativeLiveBytes = nativeBefore.LiveBytes, deniedNativeRetainedBytes = nativeBefore.RetainedBytes,
                returned.CpuBytes, returned.NativeBytes, returned.UploadBytes, returned.RetirementBytes,
                returned.LeaseSlots, returned.RetirementSlots,
            }));
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }

        VolumeViewIdentity Rebase(PlanetVector origin)
        {
            var view = run.Terrain.View;
            Assert.IsTrue(view.Frame.TryRebase(origin, out var frame));
            return run.Terrain.ChangeView(frame, view.RendererEpoch);
        }

        async Task PresentMask(VolumeViewIdentity view, ulong sequence)
        {
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, sequence, Vector4.Zero));
            await WaitForRetiredPresent(sequence);
        }

        async Task PresentTerrain(VolumePublicationInfo info, ulong sequence)
        {
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(info.Generation, sequence, VolumeCameraAt(info.View, 32, 4)));
            await WaitForRetiredPresent(sequence);
        }

        Task<RendererSnapshot> WaitForRetiredPresent(ulong sequence) =>
            AwaitStatus(renderer, state => state.LastPresentedSequence == sequence &&
                renderer.TryReadResourceDiagnostics(out var snapshot) && snapshot.RetiredPresentSequence == sequence, token);

        D3D12ResourceBalance ReadTerrainBacking()
        {
            D3D12ResourceSnapshot snapshot = default;
            Assert.IsTrue(SpinWait.SpinUntil(() => renderer.TryReadResourceDiagnostics(out snapshot), TimeSpan.FromSeconds(1)));
            Assert.IsFalse(snapshot.LedgerOverflowed || snapshot.LedgerHasUnknownSizes);
            Assert.AreEqual(D3D12CustodyStatus.None, snapshot.Custody);
            return snapshot.GetBalance(D3D12ResourceOwner.Terrain);
        }
    }
}
