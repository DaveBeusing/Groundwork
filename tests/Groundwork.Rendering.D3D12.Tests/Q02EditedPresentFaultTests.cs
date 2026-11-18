using System.Diagnostics;
using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12Q02VisualCustody")]
    public async Task Q02WarpFailedFirstEditedPresentSignalRetainsCommittedEditUntilRemoval()
    {
        const ulong maskSequence = 3;
        const ulong editedSequence = 4;
        const int signalError = unchecked((int)0x80004005); // E_FAIL
        Assert.IsTrue(Environment.UserInteractive, "Run this test on an interactive Windows desktop.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var failSignal = new ManualResetEventSlim();
        using var permitRemoval = new ManualResetEventSlim();
        using var run = new VolumeRun(editable: true);
        var token = timeout.Token;
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions(
            $"Groundwork Q02 edited Present failure {Guid.NewGuid():N}", TerrainWidth, TerrainHeight, 32),
            runtime.UiWake, runtime.RuntimeOwnership);

        var textures = new D3D12TextureHandoff(new(1, 1, 4096, 4096, 4096, 128 * 1024, 128 * 1024, 1));
        var asset = CookPressureTexture(4, 4, 2);
        var textureTarget = PressureTarget(1, asset);
        var mesh = RenderMesh.Create([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], [0, 1, 2]);
        var props = new D3D12PropCells(new(1, 1, 1, 1, 2), 1, 42, 1,
            [new(new(1, 1, 2, 40, 20, 24), mesh, mesh)]);
        PropCellKey cell = default;

        var atSignal = new TaskCompletionSource<(ulong Fence, int PresentResult, long PresentAt)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var atSentinel = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executingFrame = 0UL;
        var presentFence = 0UL;
        var presentResult = 0;
        var presentAt = 0L;
        var failed = false;
        var removed = 0;
        var hooks = new GpuRetirementTestHooks(
            BeforeExecute: sequence => executingFrame = sequence,
            AfterNativePresent: (fence, result) =>
            {
                if (executingFrame != editedSequence || presentFence != 0)
                    return;
                presentFence = fence;
                presentResult = result;
                presentAt = Stopwatch.GetTimestamp();
            },
            SignalFailure: (_, fence) =>
            {
                if (fence != presentFence)
                    return null;
                // Present already owns queue work. Cleanup needs the removal path even if the wait times out.
                failed = true;
                atSignal.TrySetResult((fence, presentResult, presentAt));
                failSignal.Wait(token);
                return signalError;
            },
            CompletedValue: (gpu, completed) =>
            {
                if (!failed)
                    return completed;
                atSentinel.TrySetResult(gpu.RemovedReason);
                if (permitRemoval.IsSet && removed == 0)
                    removed = RemoveWarpRetirementDevice(gpu);
                return ulong.MaxValue;
            });
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics with { EnableResourceSnapshots = true }, true, TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(15), hooks);
        renderer.AttachTextureHandoff(textures);
        renderer.AttachPropCells(props);

        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            var view = run.Terrain.View;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 1, Vector4.Zero));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            using var old = run.Terrain.TryRead();
            Assert.IsNotNull(old);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(old.Info.Generation, 2, VolumeCameraAt(view, 32, 4)));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 2, token);

            // CutFloor consumes the original frame's receipt at its opening BeginTick.
            var editedView = await run.CutFloor(renderer, token, maskSequence: maskSequence);
            using var edited = run.Terrain.TryRead();
            Assert.IsNotNull(edited);
            var info = edited.Info;
            Assert.AreNotEqual(old.Info.Generation, info.Generation);
            Assert.AreEqual(old.Info.TerrainRevision + 1, info.TerrainRevision);
            var editedRoot = (VolumeWorldRoot)run.World.Publication.Committed.Read().Root;
            Assert.AreEqual(info, editedRoot.Terrain);
            Assert.IsNotNull(editedRoot.CommittedEdit);
            AssertEditedCollision(old, edited);

            var cut = new D3D12TextureView(run.Terrain.WorldIdentity, editedView, info.TerrainRevision, info.Generation);
            Assert.IsTrue(textures.Publish(cut, 1, [textureTarget]));
            var credit = new D3D12TextureReservation(256, 256, 256, 65_536, 65_536);
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryRequest(cut, textureTarget, credit, out var texture));
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(texture, asset));
            props.Publish(new(cut.World, editedView.RendererEpoch, info.TerrainRevision, info.Generation), [cell]);
            Assert.AreEqual(PropCellResult.Accepted, props.TryRequest(cell, 1, out var prop));
            Assert.AreEqual(PropCellResult.Accepted, props.TryStage(prop, new(32, 32, 3),
                [new(0, 0, Vector3.Zero, Quaternion.Identity, 1, 0xffff00ff)]));
            Assert.IsTrue(textures.HasReady(cut, firstUse: true));

            var submittedAt = Stopwatch.GetTimestamp();
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(info.Generation, editedSequence, VolumeCameraAt(editedView, 32, 4)));
            var signal = await atSignal.Task.WaitAsync(token);
            Assert.AreEqual(0, signal.PresentResult, "The first edited frame must reach a successful native DXGI Present.");
            Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var unassigned));
            AssertPresentCustody(unassigned, signal.Fence);
            Assert.IsTrue(submittedAt <= unassigned.DrawTimestamp && unassigned.DrawTimestamp <= signal.PresentAt);
            Assert.IsTrue(textures.HasReady(cut, firstUse: true));
            var readyProps = props.ReadStatistics();
            Assert.IsTrue(readyProps.PayloadBytes > 0 || readyProps.NativeBytes > 0);
            Assert.AreEqual(3, run.Terrain.Statistics.Readers, "The renderer must still hold its edited registration.");

            // Use a real current BeginTick before terminal stop can independently fence the runtime.
            var begin = await run.BeginHeldTick();
            Assert.AreEqual(begin, run.World.Publication.Committed.Read().Stamp);
            Assert.IsFalse(run.Terrain.BeginTick(begin));
            Assert.IsNull(run.Terrain.TryPlanMovement(new(new(32, 32, 4), new(1, 0, 0), default), 1, 0, out var status));
            Assert.AreEqual(VolumeQueryStatus.CoverageUnavailable, status);
            await run.FinishHeldTick();
            var committed = run.World.Publication.Committed.Read();
            Assert.AreSame(editedRoot, committed.Root);

            failSignal.Set();
            Assert.AreEqual(0, await atSentinel.Task.WaitAsync(token), "The sentinel precedes device removal.");
            D3D12ResourceSnapshot held;
            while (!renderer.TryReadResourceDiagnostics(out held) ||
                !held.Custody.HasFlag(D3D12CustodyStatus.FenceSentinelObserved) ||
                !runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop))
                await Task.Delay(10, token);
            AssertPresentCustody(held, signal.Fence);
            var beforeSentinel = unassigned.GetBalance(D3D12ResourceOwner.Terrain);
            var afterSentinel = held.GetBalance(D3D12ResourceOwner.Terrain);
            Assert.AreEqual(beforeSentinel.LiveBytes + beforeSentinel.RetainedBytes,
                afterSentinel.LiveBytes + afterSentinel.RetainedBytes);
            Assert.IsFalse(held.Custody.HasFlag(D3D12CustodyStatus.ConfirmedRemoval));
            Assert.IsNull(renderer.ReadDiagnostics().DeviceRemoval);
            Assert.AreEqual(new RenderFaultSummary((uint)RendererStage.Present, signalError, null, editedSequence),
                renderer.Read().FirstFault);
            Assert.AreEqual(3, run.Terrain.Statistics.Readers);
            Assert.AreEqual(2, run.Terrain.Statistics.Generations);
            AssertCommittedAndHeld();
            var registered = ReadEditedFrameEvents();
            Assert.AreEqual(1, registered);

            permitRemoval.Set();
            while (renderer.Read().Lifecycle != RendererLifecycle.Stopped)
                await Task.Delay(10, token);
            await renderer.StopAsync(token);
            await window.StopAsync(token);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, true);
            Assert.IsTrue(diagnostics.DebugLayerEnabled && diagnostics.DredEnabled);
            Assert.IsNotNull(diagnostics.DeviceRemoval);
            Assert.IsLessThan(0, removed);
            Assert.AreEqual(removed, diagnostics.DeviceRemoval.Reason);
            Assert.IsTrue(renderer.ReadTerminalGpuCustody().HasFlag(D3D12CustodyStatus.ConfirmedRemoval));
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var released));
            foreach (var owner in Enum.GetValues<D3D12ResourceOwner>())
            {
                var balance = released.GetBalance(owner);
                Assert.AreEqual(0, balance.Live + balance.Retained, $"Outstanding allocation for {owner}.");
                Assert.AreEqual(0UL, balance.LiveBytes + balance.RetainedBytes, $"Outstanding backing for {owner}.");
            }
            Assert.IsGreaterThan(0UL, released.GetBalance(D3D12ResourceOwner.Terrain).Released);
            Assert.AreEqual(default, textures.ReadUsage());
            Assert.AreEqual(0L, props.ReadStatistics().NativeBytes);
            Assert.AreEqual(2, run.Terrain.Statistics.Readers, "Only the test's two readers may remain after removal.");
            AssertEditedCollision(old, edited);
            AssertCommittedAndHeld();
            Assert.AreEqual(0, ReadEditedFrameEvents());

            old.Dispose();
            edited.Dispose();
            run.Terrain.Stop();
            var settled = run.Terrain.Statistics;
            Assert.AreEqual(0, settled.Readers);
            Assert.AreEqual(0, settled.Generations);
            Assert.IsTrue(settled.Used == default);
            var debug = diagnostics.DebugMessages;
            Assert.IsNotNull(debug);
            Assert.AreEqual(0, debug.LiveObjectReportResult);
            Assert.AreEqual(0, debug.LiveChildObjects);
            Console.WriteLine($"Q02 WARP edited Present: generation={info.Generation}, revision={info.TerrainRevision}, " +
                $"view={editedView.View}, epoch={editedView.RendererEpoch}, frame={editedSequence}, " +
                $"QPC frequency={Stopwatch.Frequency}, draw={unassigned.DrawTimestamp}, native Present={signal.PresentAt}, " +
                $"draw fence={unassigned.DrawFence}, unassigned Present fence={signal.Fence}, signal=0x{signalError:X8}, " +
                $"removal=0x{removed:X8}, registrations={registered}, receipts=0, opened=0, " +
                $"terrain backing at sentinel={afterSentinel.LiveBytes + afterSentinel.RetainedBytes} bytes, " +
                $"prop payload={readyProps.PayloadBytes} bytes, prop backing={readyProps.NativeBytes} bytes, " +
                $"warnings={debug.Warnings}, errors={debug.Errors}, corruption={debug.Corruption}, " +
                $"discarded={debug.Discarded}, live child objects={debug.LiveChildObjects}.");

            void AssertCommittedAndHeld()
            {
                var current = run.World.Publication.Committed.Read();
                Assert.AreSame(editedRoot, current.Root);
                Assert.AreEqual(committed.Stamp, current.Stamp);
                Assert.AreEqual(info, ((VolumeWorldRoot)current.Root).Terrain);
                Assert.AreEqual(editedSequence, renderer.Read().LastAcceptedSequence);
                Assert.AreEqual(maskSequence, renderer.Read().LastPresentedSequence);
                Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            }

            int ReadEditedFrameEvents()
            {
                var registrations = 0;
                while (run.Terrain.TryReadEvent(out var evidence))
                {
                    if (evidence.Generation != info.Generation)
                        continue;
                    Assert.IsFalse(evidence.Kind is VolumeEventKind.Accepted or VolumeEventKind.Opened);
                    if (evidence.Kind != VolumeEventKind.Registered)
                        continue;
                    Assert.AreEqual(info.View, evidence.View);
                    Assert.AreEqual(editedSequence, evidence.Frame);
                    registrations++;
                }
                return registrations;
            }
        }
        finally
        {
            permitRemoval.Set();
            failSignal.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }

        static void AssertPresentCustody(D3D12ResourceSnapshot snapshot, ulong fence)
        {
            Assert.AreEqual(editedSequence, snapshot.FrameSequence);
            Assert.AreEqual(editedSequence, snapshot.UnassignedFrameSequence);
            Assert.AreEqual(fence, snapshot.UnassignedFence);
            Assert.IsGreaterThan(snapshot.DrawFence, fence);
            Assert.IsLessThan(fence, snapshot.CompletedFence);
            Assert.IsTrue(snapshot.Custody.HasFlag(D3D12CustodyStatus.UnassignedSubmission));
            Assert.IsFalse(snapshot.LedgerOverflowed || snapshot.LedgerHasUnknownSizes);
            Assert.IsGreaterThan(0, snapshot.GetBalance(D3D12ResourceOwner.Presentation).Retained);
            var terrain = snapshot.GetBalance(D3D12ResourceOwner.Terrain);
            Assert.IsGreaterThan(0UL, terrain.LiveBytes + terrain.RetainedBytes);
        }

        static void AssertEditedCollision(VolumeStreamingLease old, VolumeStreamingLease edited)
        {
            var before = new VolumeRayHit[4];
            var after = new VolumeRayHit[4];
            Assert.AreEqual(1, old.Raycast(new(32, 32, 4), new(32, 32, -8), before).Count);
            Assert.AreEqual(1, edited.Raycast(new(32, 32, 4), new(32, 32, -8), after).Count);
            Assert.IsLessThan(after[0].Distance, before[0].Distance);
        }
    }
}
