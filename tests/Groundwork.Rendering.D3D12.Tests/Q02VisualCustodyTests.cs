using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Rendering.Textures;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12Q02VisualCustody")]
    public async Task Q02WarpFailedMaximumTextureSignalKeepsProtectedEditUnpublishedUntilSafeSettlement()
    {
        const int signalError = unchecked((int)0x80004005); // E_FAIL
        const int copyLimit = GpuDevice.MaxTextureCopyBytes;
        const int propCount = 32_768;
        const ulong lastPresented = 3;
        const ulong editMask = 5;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var resumeWarmup = new ManualResetEventSlim();
        using var failCopySignal = new ManualResetEventSlim();
        using var permitRemoval = new ManualResetEventSlim();
        using var run = new VolumeRun(editable: true, openFloor: true);
        var token = timeout.Token;
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions(
            $"Groundwork Q02 optional texture failure {Guid.NewGuid():N}", TerrainWidth, TerrainHeight, 32),
            runtime.UiWake, runtime.RuntimeOwnership);

        var limits = new D3D12TextureLimits(4, 4, copyLimit + 4096, copyLimit + 4096, copyLimit + 4096,
            copyLimit + 4 * 65_536, copyLimit + 4 * 65_536, 4);
        var textures = new D3D12TextureHandoff(limits);
        var coarseAsset = CookPressureTexture(4, 4, 2);
        var maximumAsset = CookPressureTexture(512, 1024, 1);
        var coarseTarget = PressureTarget(1, coarseAsset);
        var maximumTarget = PressureTarget(2, maximumAsset);
        var waitingTarget = PressureTarget(3, coarseAsset);
        var smallCredit = new D3D12TextureReservation(256, 256, 256, 65_536, 65_536);
        var maximumCredit = new D3D12TextureReservation(copyLimit + 256, copyLimit, copyLimit + 256, copyLimit, copyLimit);
        var policy = new D3D12TextureMipPolicy(0, 1, 10, 30);

        var mesh = RenderMesh.Create([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], [0, 1, 2]);
        var props = new D3D12PropCells(new(2, 2, propCount, 2 * propCount, 2), propCount, 42, 1,
            [new(new(1, 1, 2, 40, 20, 24), mesh, mesh)]);
        PropCellKey[] cells = [default, new(1, 0, 0)];
        var instances = new PropPlacement[propCount];
        for (var i = 0; i < instances.Length; i++)
            instances[i] = new((uint)i, 0, Vector3.Zero, Quaternion.Identity, 0.001f, 0xffff00ff);
        instances[0] = instances[0] with { Scale = 1 };

        var view = run.Terrain.View;
        var camera = VolumeCameraAt(view, 32, 4);
        Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 4, out var frame));
        Assert.IsTrue(frame.Scene.HasValue);
        var scene = frame.Scene.Value;
        var warmupHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copyHeld = new TaskCompletionSource<(ulong Fence, GpuTextureStatistics Texture)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retirementReached = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        D3D12TextureTicket maximumTicket = default;
        VolumePublicationInfo initial = default;
        var maximumSelected = false;
        var signalFailed = false;
        var removalReason = 0;
        var optionalAttempts = 0;
        var terrainUploads = 0;
        var hooks = new GpuRetirementTestHooks(
            SignalFailure: (gpu, fence) =>
            {
                if (!maximumSelected || !gpu.HasTextureCopy)
                    return null;
                maximumSelected = false;
                Assert.IsTrue(gpu.HasReadyPropUploads(scene, initial));
                copyHeld.TrySetResult((fence, gpu.TextureStatistics));
                failCopySignal.Wait(token);
                signalFailed = true;
                return signalError;
            },
            CompletedValue: (gpu, completed) =>
            {
                if (!signalFailed)
                    return completed;
                if (!retirementReached.Task.IsCompleted)
                    retirementReached.TrySetResult(gpu.RemovedReason);
                if (permitRemoval.IsSet && removalReason == 0)
                    removalReason = RemoveWarpRetirementDevice(gpu);
                return ulong.MaxValue; // Only the separately confirmed removal permits disposal.
            },
            BeforeVolumeUpload: _ => Interlocked.Increment(ref terrainUploads));
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics with { EnableResourceSnapshots = true }, true, TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(15), hooks)
        {
            BeforeTextureUploadForTests = (ticket, _) =>
            {
                Interlocked.Increment(ref optionalAttempts);
                maximumSelected = ticket == maximumTicket;
            },
            VolumePresentReadbackForTests = (sequence, _, _) =>
            {
                if (sequence == lastPresented && warmupHeld.TrySetResult())
                    resumeWarmup.Wait(token);
            },
        };
        renderer.AttachTextureHandoff(textures);
        renderer.AttachPropCells(props);

        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 1, Vector4.Zero));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            using var original = run.Terrain.TryRead();
            Assert.IsNotNull(original);
            initial = original.Info;
            var cut = new D3D12TextureView(run.Terrain.WorldIdentity, initial.View, initial.TerrainRevision, initial.Generation);
            props.Publish(new(cut.World, cut.View.RendererEpoch, cut.TerrainRevision, cut.CoverageGeneration), cells);
            StageProp(cells[0]);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(initial.Generation, 2, camera));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 2, token);
            Assert.IsTrue(textures.Publish(cut, 1, [coarseTarget, maximumTarget, waitingTarget]));
            Assert.AreEqual(D3D12TextureResult.Accepted,
                textures.TryRequestTier(cut, coarseTarget, policy, 100, smallCredit, smallCredit, out var coarse));
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(coarse, coarseAsset));
            await AwaitStatus(renderer,
                _ => textures.TryReadResidency(cut, coarseTarget, out var installed) && installed.ResidentMip == 1, token);
            Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var baseline));
            var uploadedBefore = renderer.ReadTextureDiagnostics().UploadedBytes;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(initial.Generation, lastPresented, camera));
            await warmupHeld.Task.WaitAsync(token);
            while (run.Terrain.TryReadEvent(out _))
            {
            }

            var waitingProp = StageProp(cells[1]);
            Assert.AreEqual(PropCellResult.Quota, props.TryRequest(cells[0], propCount, out _));
            Assert.AreEqual(D3D12TextureResult.Accepted,
                textures.TryRequestTier(cut, coarseTarget, policy, 0, smallCredit, smallCredit, out var refinement));
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(refinement, coarseAsset));
            maximumTicket = StageTexture(maximumTarget, maximumAsset, maximumCredit);
            StageTexture(waitingTarget, coarseAsset, smallCredit);
            Assert.AreEqual(limits.Descriptors, textures.ReadUsage().Descriptors);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(initial.Generation, 4, camera));
            resumeWarmup.Set();

            var copy = await copyHeld.Task.WaitAsync(token);
            Assert.AreEqual(copyLimit, copy.Texture.StagingBytes);
            Assert.AreEqual(uploadedBefore + copyLimit, copy.Texture.UploadedBytes);
            Assert.IsTrue(textures.HasReady(cut, firstUse: true));
            Assert.IsTrue(textures.HasReady(cut, firstUse: false));
            Assert.IsTrue(textures.TryReadResidency(cut, coarseTarget, out var resident) && resident.ResidentMip == 1);
            Assert.IsFalse(textures.TryGetResident(cut, maximumTarget.Slot, out _));
            lock (props.Gate)
            {
                Assert.AreEqual(propCount, props.Admission.Usage.ResidentInstances);
                Assert.AreEqual(PropCellResult.Accepted, props.Admission.ReadStagedLength(waitingProp, out _, out _));
            }
            Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var unassigned));
            AssertRetainedCopy(unassigned);
            Assert.AreEqual(D3D12CustodyStatus.UnassignedSubmission, unassigned.Custody);

            var begin = await run.BeginHeldTick();
            Assert.IsTrue(run.Terrain.BeginTick(begin));
            var brush = new VolumeBrushIntent(begin.Tick, 1, initial.TerrainRevision,
                new(28, 28, -2), new(36, 36, 1), VolumeBrushOperation.Remove);
            Assert.AreEqual(VolumeAdmission.Accepted, run.Terrain.SealEditTick(begin.Tick, run.Descriptor, [brush]));
            Assert.IsTrue(view.Frame.TryRebase(new PlanetVector(32, 32, 0), out var frameOrigin));
            var editedView = run.Terrain.ChangeView(frameOrigin, view.RendererEpoch);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(editedView, editMask, Vector4.Zero));
            // Match the editable default VolumeRun mesh and overlay reservation.
            const int meshBytes = 8 * 1024 * 1024;
            const int overlayBudget = 64 * 1024 + 4096;
            var selection = VolumeStreamingSelection.Select(run.Descriptor, new(0, 32, 0), new(-1, 0, 0));
            var resources = new VolumeResources(VolumeStreamingCoordinator.RequiredCpuBytes(selection, meshBytes) + overlayBudget,
                VolumeRun.NativeBytes, VolumeRun.NativeBytes, VolumeRun.NativeBytes, 1, 1, 1, 64, 1);
            Assert.AreEqual(VolumeAdmission.Accepted,
                run.Terrain.TryStageEdit(begin.Tick, brush.Id, selection, meshBytes, resources, out var edit));
            Assert.IsNotNull(edit);
            using var pendingEdit = edit;
            Assert.AreEqual(1, run.Terrain.Statistics.Used.QueueSlots);
            Assert.IsFalse(pendingEdit.IsCancellationRequested);
            Assert.AreEqual(editMask, renderer.Read().LastAcceptedSequence);
            Assert.AreEqual(lastPresented, renderer.Read().LastPresentedSequence);
            var prior = run.World.Publication.Committed.Read();
            Assert.AreEqual(initial, ((VolumeWorldRoot)prior.Root).Terrain);

            failCopySignal.Set();
            Assert.AreEqual(0, await retirementReached.Task.WaitAsync(token), "The observed sentinel precedes actual device removal.");
            var heldCustody = D3D12CustodyStatus.UnassignedSubmission | D3D12CustodyStatus.FenceSentinelObserved;
            while (!renderer.ReadTerminalGpuCustody().HasFlag(D3D12CustodyStatus.FenceSentinelObserved))
                await Task.Delay(10, token);
            Assert.AreEqual(heldCustody, renderer.ReadTerminalGpuCustody());
            var failed = renderer.Read();
            Assert.AreEqual(new RenderFaultSummary((uint)RendererStage.TextureUpload, signalError, null, lastPresented), failed.FirstFault);
            Assert.AreEqual(RendererLifecycle.Stopping, failed.Lifecycle);
            Assert.IsNull(renderer.ReadDiagnostics().DeviceRemoval);
            Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var held));
            AssertRetainedCopy(held);
            Assert.IsTrue(runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop));
            Assert.IsFalse(textures.TryGetResident(cut, maximumTarget.Slot, out _));
            Assert.IsTrue(pendingEdit.IsCancellationRequested);
            pendingEdit.Dispose();
            Assert.AreEqual(0, run.Terrain.Statistics.Used.QueueSlots);
            AssertUnpublished(pendingEdit.Generation);

            permitRemoval.Set();
            while (renderer.Read().Lifecycle != RendererLifecycle.Stopped)
                await Task.Delay(10, token);
            await renderer.StopAsync(token);
            await window.StopAsync(token);
            Assert.AreEqual(heldCustody | D3D12CustodyStatus.ConfirmedRemoval, renderer.ReadTerminalGpuCustody());
            Assert.IsFalse(renderer.Read().IncompleteCleanup);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, true);
            Assert.IsTrue(diagnostics.DredEnabled);
            Assert.IsNotNull(diagnostics.DeviceRemoval);
            Assert.AreEqual(removalReason, diagnostics.DeviceRemoval.Reason);
            Assert.IsLessThan(0, removalReason);
            Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var released));
            Assert.AreEqual(baseline.TextureCopyFence, released.TextureCopyFence);
            Assert.AreEqual(copy.Fence, released.UnassignedFence);
            foreach (var owner in new[] { D3D12ResourceOwner.Texture, D3D12ResourceOwner.TextureStaging,
                D3D12ResourceOwner.Terrain, D3D12ResourceOwner.Props })
            {
                var balance = released.GetBalance(owner);
                Assert.AreEqual(0, balance.Live + balance.Retained);
                Assert.IsGreaterThan(0UL, balance.Released);
            }
            Assert.AreEqual(default, textures.ReadUsage());
            Assert.AreEqual(0L, props.ReadStatistics().NativeBytes);
            Assert.AreEqual(0L, renderer.ReadTextureDiagnostics().ResidentAndPendingBytes);
            Assert.AreEqual(0L, renderer.ReadTextureDiagnostics().StagingBytes);
            AssertUnpublished(pendingEdit.Generation);
            Console.WriteLine($"Q02 WARP optional copy {copy.Texture.StagingBytes} bytes, unassigned fence {copy.Fence}, " +
                $"protected generation {pendingEdit.Generation}, prior generation {initial.Generation}, " +
                $"signal 0x{signalError:X8}, confirmed removal 0x{removalReason:X8}.");

            void AssertRetainedCopy(D3D12ResourceSnapshot snapshot)
            {
                Assert.IsFalse(snapshot.LedgerOverflowed);
                Assert.IsFalse(snapshot.LedgerHasUnknownSizes);
                Assert.AreEqual(copy.Fence, snapshot.UnassignedFence);
                Assert.AreEqual(0UL, snapshot.UnassignedFrameSequence);
                Assert.AreEqual(baseline.TextureCopyFence, snapshot.TextureCopyFence);
                Assert.IsGreaterThan(snapshot.CompletedFence, copy.Fence);
                Assert.AreEqual(1, snapshot.GetBalance(D3D12ResourceOwner.Texture).Retained);
                Assert.AreEqual((ulong)copyLimit, snapshot.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes);
                Assert.AreEqual(1, snapshot.GetBalance(D3D12ResourceOwner.TextureStaging).Retained);
                Assert.AreEqual((ulong)copyLimit, snapshot.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes);
            }

            void AssertUnpublished(ulong protectedGeneration)
            {
                Assert.AreEqual(lastPresented, renderer.Read().LastPresentedSequence);
                Assert.AreEqual(2, Volatile.Read(ref optionalAttempts));
                Assert.AreEqual(1, Volatile.Read(ref terrainUploads));
                var committed = run.World.Publication.Committed.Read();
                Assert.AreSame(prior.Root, committed.Root);
                Assert.AreEqual(prior.Stamp, committed.Stamp);
                Assert.AreEqual(initial, ((VolumeWorldRoot)committed.Root).Terrain);
                Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
                while (run.Terrain.TryReadEvent(out var evidence))
                    Assert.IsFalse(evidence.Generation == protectedGeneration &&
                        evidence.Kind is VolumeEventKind.Commit or VolumeEventKind.Registered or VolumeEventKind.Accepted);
            }

            D3D12TextureTicket StageTexture(D3D12TextureTarget target, CookedTexture asset, D3D12TextureReservation credit)
            {
                Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryRequest(cut, target, credit, out var ticket));
                Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(ticket, asset));
                return ticket;
            }
        }
        finally
        {
            permitRemoval.Set();
            failCopySignal.Set();
            resumeWarmup.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }

        PropCellRequest StageProp(PropCellKey cell)
        {
            Assert.AreEqual(PropCellResult.Accepted, props.TryRequest(cell, propCount, out var request));
            Assert.AreEqual(PropCellResult.Accepted, props.TryStage(request, new(32, 32, 3), instances));
            return request;
        }
    }
}
