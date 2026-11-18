using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Groundwork.Core;
using Groundwork.Core.Tests;
using Groundwork.Jobs;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [TestCategory("D3D12Q02IndependentGpuPressure")]
    [DataRow(VolumeWorkClass.Movement)]
    [DataRow(VolumeWorkClass.FirstEdit)]
    [DataRow(VolumeWorkClass.Recovery)]
    public async Task Q02NativeTextureQueueWaitOverlapsProtectedTerrainWorker(VolumeWorkClass path)
    {
        const int copyBytes = GpuDevice.MaxTextureCopyBytes;
        const ulong oldFrame = 2;
        const ulong maskFrame = 3;
        const ulong newFrame = 4;
        Assert.IsTrue(Environment.UserInteractive, "An interactive Windows desktop is required.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        using var run = new VolumeRun(editable: true, openFloor: true);
        using var gpuGate = new Q02NativeQueueGate();
        var runtime = run.Fixture.Runtime;
        var operationLimit = TimeSpan.FromSeconds(15);
        var context = new EngineModuleContext(run.Fixture.Clock,
            new RuntimeTimingLimits(operationLimit, operationLimit, operationLimit), runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions(
            $"Groundwork Q02 independent GPU pressure {path}", TerrainWidth, TerrainHeight, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        var textures = new D3D12TextureHandoff(new(1, 1, copyBytes + 4096, copyBytes + 4096, copyBytes + 4096,
            copyBytes + 65_536, copyBytes + 65_536, 1));
        var asset = CookPressureTexture(512, 1024, 1);
        var target = PressureTarget(1, asset);
        var credit = new D3D12TextureReservation(copyBytes + 256, copyBytes, copyBytes + 256, copyBytes, copyBytes);
        D3D12TextureTicket selected = default;
        GpuDevice? device = null;
        var hooks = new GpuRetirementTestHooks(BeforeVolumeUpload: gpu => device = gpu);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics with { EnableResourceSnapshots = true }, false, operationLimit, operationLimit, hooks)
        {
            BeforeTextureUploadForTests = (ticket, _) =>
            {
                Assert.AreEqual(selected, ticket);
                Assert.IsNotNull(device);
                gpuGate.Enqueue(device);
            },
        };
        renderer.AttachTextureHandoff(textures);

        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            AssertPlanetAdapter(renderer.ReadDiagnostics().Adapter, false);

            var originalView = run.Terrain.View;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(originalView, 1, Vector4.Zero));
            await AwaitStatus(renderer, s => s.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            using var old = run.Terrain.TryRead();
            Assert.IsNotNull(old);
            var original = old.Info;
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(original.Generation, oldFrame, VolumeCameraAt(originalView, 32, 4)));
            await AwaitStatus(renderer, s => s.LastPresentedSequence == oldFrame, token);
            var begin = await run.BeginHeldTick();
            Assert.IsTrue(run.Terrain.BeginTick(begin));

            // Select optional work while the predecessor is still drawable. The native queue wait is
            // ahead of the texture copy, independent of any terrain candidate or CPU scheduler ticket.
            var cut = new D3D12TextureView(run.Terrain.WorldIdentity, originalView, original.TerrainRevision, original.Generation);
            Assert.IsTrue(textures.Publish(cut, 1, [target]));
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryRequest(cut, target, credit, out selected));
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(selected, asset));
            D3D12ResourceSnapshot submitted = default;
            await AwaitStatus(renderer, _ => renderer.TryReadResourceDiagnostics(out submitted) &&
                submitted.TextureCopyFence != 0 && submitted.SubmittedFence == submitted.TextureCopyFence, token);
            var copySubmittedAt = Stopwatch.GetTimestamp();
            var copyFence = submitted.TextureCopyFence;
            AssertPendingCopy(submitted, copyFence);
            var prior = run.World.Publication.Committed.Read();

            VolumeBrushIntent? edit = null;
            if (path == VolumeWorkClass.FirstEdit)
            {
                edit = new(begin.Tick, 1, original.TerrainRevision, new(28, 28, -2), new(36, 36, 1), VolumeBrushOperation.Remove);
                Assert.AreEqual(VolumeAdmission.Accepted, run.Terrain.SealEditTick(begin.Tick, run.Descriptor, [edit.Value]));
            }
            Assert.IsTrue(originalView.Frame.TryRebase(new PlanetVector(32, 32, 0), out var rebased));
            var view = run.Terrain.ChangeView(rebased, originalView.RendererEpoch);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, maskFrame, Vector4.Zero));
            using var work = edit is { } brush
                ? run.StageReferenceEdit(brush, VolumeStreamingSelection.Select(run.Descriptor, new(0, 32, 0), new(-1, 0, 0)))
                : run.Stage(path, original.TerrainRevision, original.TerrainRevision, cut: false);
            var phase = run.World.Publication.Phases.TryOpen(begin, TickPhase.Base).Phase;
            Assert.IsNotNull(phase);
            var speculative = new JobHandlerId(1);
            var required = new JobHandlerId(2);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCpu = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long cpuStart = 0, cpuEnd = 0;
            VolumeStreamingWorkDetailedProfile profile = default;
            var scheduler = new JobScheduler(begin.World, new JobSchedulerLimits(2, 2), run.World.Publication.Execution,
                new CombinedNoInput(), run.World.Ownership, run.World.Reporter, run.Fixture.Clock,
                new Dictionary<JobHandlerId, JobHandler>
                {
                    [speculative] = async (_, cancellation) =>
                    {
                        started.TrySetResult();
                        await releaseCpu.Task.WaitAsync(cancellation);
                    },
                    [required] = (_, cancellation) =>
                    {
                        gpuGate.AssertHeld();
                        cpuStart = Stopwatch.GetTimestamp();
                        var completed = work.ExecuteDetailedProfiled(out profile, cancellation);
                        cpuEnd = Stopwatch.GetTimestamp();
                        gpuGate.AssertHeld();
                        Assert.IsTrue(completed);
                        return ValueTask.CompletedTask;
                    },
                }, new JobDispatchReservation(1, 1, [required]));
            try
            {
                using var running = Submit(speculative, 1);
                await scheduler.DispatchReadyAsync(phase, token);
                await started.Task.WaitAsync(token);
                using var queued = Submit(speculative, 2);
                Assert.AreEqual(JobAdmissionStatus.CapacityExceeded, scheduler.TrySubmit(Submission(speculative, 3), phase).Status);
                AssertLoad();
                using (var protectedWork = Submit(required, 4))
                {
                    await scheduler.DispatchReadyAsync(phase, token);
                    Assert.AreEqual(JobExecutionStatus.Succeeded, (await protectedWork.ExecutionCompletion.WaitAsync(token)).Status);
                }
                await WaitForWorkers(1);
                AssertLoad();
                var beforeRelease = renderer.Read();
                Assert.AreEqual(oldFrame, beforeRelease.LastPresentedSequence);
                Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var held));
                AssertPendingCopy(held, copyFence);
                Assert.AreEqual(copyBytes, (long)renderer.ReadTextureDiagnostics().UploadedBytes);
                Assert.AreSame(prior.Root, run.World.Publication.Committed.Read().Root);
                Assert.AreEqual(prior.Stamp, run.World.Publication.Committed.Read().Stamp);
                Assert.AreEqual(original, old.Info);
                Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
                var rebuiltBricks = profile.Bricks.ToArray().Count(x => !x.Reused);
                var rebuiltMeshes = profile.Meshes.ToArray().Count(x => !x.Reused);
                Assert.IsGreaterThan(0, rebuiltBricks);
                Assert.IsGreaterThan(0, rebuiltMeshes);

                var releasedAt = gpuGate.Open();
                Assert.IsTrue(gpuGate.QueuedAt <= copySubmittedAt && copySubmittedAt <= cpuStart &&
                    cpuStart < cpuEnd && cpuEnd <= releasedAt, "The complete protected Execute must lie inside the native hold.");
                D3D12ResourceSnapshot retired = default;
                await AwaitStatus(renderer, s => s.LastPresentedSequence == maskFrame &&
                    renderer.TryReadResourceDiagnostics(out retired) && retired.CompletedFence >= copyFence &&
                    retired.GetBalance(D3D12ResourceOwner.TextureStaging).Retained == 0, token);
                var retirementObservedAt = Stopwatch.GetTimestamp();
                gpuGate.Dispose();
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TryStageVolumeTerrain(work.Generation));
                await AwaitStatus(renderer, _ => renderer.IsVolumeTerrainReady(work.Generation), token);
                AssertLoad();

                run.World.Publication.Phases.Close(phase);
                await run.FinishHeldTick();
                var publishing = await run.BeginHeldTick();
                var candidates = await CoreRuntimeFixture.SealAsync(run.World, publishing.Tick, BarrierKind.CommitResults);
                var prepared = renderer.TryPrepareVolumePublication(work.Generation, candidates, [], out var status);
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, status);
                Assert.IsNotNull(prepared);
                var outcome = run.World.Publication.Finalization.TryFinalize(prepared);
                Assert.AreEqual(BarrierStatus.Committed, outcome.Status);
                CoreRuntimeFixture.PublishObserver(run.World, outcome.Stamp!.Value, (int)publishing.Tick);
                var published = (VolumeWorldRoot)run.World.Publication.Committed.Read().Root;
                Assert.AreEqual(work.Generation, published.Terrain.Generation);
                Assert.AreEqual(edit, published.CommittedEdit);
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                    renderer.TrySubmitVolumeTerrain(work.Generation, newFrame, VolumeCameraAt(view, 32, 4)));
                await AwaitStatus(renderer, s => s.LastPresentedSequence == newFrame, token);
                AssertLoad();
                Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
                var consuming = await run.BeginHeldTick();
                Assert.AreEqual(publishing.Tick + 1, consuming.Tick);
                Assert.IsTrue(run.Terrain.BeginTick(consuming));
                Assert.IsFalse(run.Terrain.Statistics.MovementHeld);
                AssertLoad();
                var receiptCount = 0;
                var openedCount = 0;
                while (run.Terrain.TryReadEvent(out var evidence))
                {
                    if (evidence.Generation != work.Generation || evidence.Frame != newFrame || evidence.View != view)
                        continue;
                    if (evidence.Kind == VolumeEventKind.Accepted)
                    {
                        Assert.AreEqual(outcome.Stamp, evidence.Stamp);
                        receiptCount++;
                    }
                    if (evidence.Kind == VolumeEventKind.Opened)
                    {
                        Assert.AreEqual(consuming, evidence.Stamp);
                        openedCount++;
                    }
                }
                Assert.AreEqual(1, receiptCount);
                Assert.AreEqual(1, openedCount);

                var after = run.World.Publication.Phases.TryOpen(consuming, TickPhase.Base).Phase;
                Assert.IsNotNull(after);
                try
                {
                    releaseCpu.TrySetResult();
                    Assert.AreEqual(JobExecutionStatus.Succeeded, (await running.ExecutionCompletion.WaitAsync(token)).Status);
                    await WaitForWorkers(0);
                    await scheduler.DispatchReadyAsync(after, token);
                    Assert.AreEqual(JobExecutionStatus.Succeeded, (await queued.ExecutionCompletion.WaitAsync(token)).Status);
                    await WaitForWorkers(0);
                    Assert.AreEqual(0, scheduler.ReadMetrics().Queued);
                }
                finally
                {
                    run.World.Publication.Phases.Close(after);
                }
                await run.FinishHeldTick();
                Assert.AreEqual(original, old.Info);
                old.Dispose();
                runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
                await renderer.StopAsync(token);
                await window.StopAsync(token);
                Assert.AreEqual(default, textures.ReadUsage());
                Assert.AreEqual(0L, renderer.ReadTextureDiagnostics().StagingBytes);
                Assert.IsTrue(renderer.TryReadResourceDiagnostics(out var final));
                foreach (var owner in Enum.GetValues<D3D12ResourceOwner>())
                {
                    var balance = final.GetBalance(owner);
                    Assert.AreEqual(0, balance.Live + balance.Retained, $"Outstanding native resources for {owner}.");
                    Assert.AreEqual(0UL, balance.LiveBytes + balance.RetainedBytes);
                }
                run.Terrain.Stop();
                var stopped = run.Terrain.Statistics;
                Assert.IsTrue(stopped.Used == default);
                Assert.AreEqual(0, stopped.Readers);
                Assert.AreEqual(0, stopped.Generations);
                var diagnostics = renderer.ReadDiagnostics();
                Assert.IsTrue(diagnostics.DebugLayerEnabled && diagnostics.DredEnabled);
                AssertCleanDiagnostics(diagnostics, diagnosticsRequested: true);

                Console.WriteLine("Q02_GPU " + JsonSerializer.Serialize(new
                {
                    path = path.ToString(), adapter = diagnostics.Adapter, frequency = Stopwatch.Frequency,
                    queueWait = gpuGate.QueuedAt, copySubmittedAt, cpuStart, cpuEnd, releasedAt, retirementObservedAt,
                    copyFence, completedBeforeWorker = submitted.CompletedFence, completedAfterWorker = held.CompletedFence,
                    completedAfterRelease = retired.CompletedFence, copyBytes, rebuiltBricks, rebuiltMeshes,
                    heldTextureBytes = held.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes,
                    heldStagingBytes = held.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes,
                    oldGeneration = original.Generation, newGeneration = published.Terrain.Generation,
                    acceptedTick = publishing.Tick, consumingTick = consuming.Tick, receiptCount, openedCount,
                    speculativeJob = running.Id.Value, queuedJob = queued.Id.Value,
                    workerMs = Stopwatch.GetElapsedTime(cpuStart, cpuEnd).TotalMilliseconds,
                }));

                void AssertLoad()
                {
                    var metrics = scheduler.ReadMetrics();
                    Assert.AreEqual(1, metrics.OccupiedWorkerSlots);
                    Assert.AreEqual(1, metrics.Queued);
                    Assert.AreEqual(JobExecutionStatus.Running, running.Observe().Execution);
                    Assert.AreEqual(JobExecutionStatus.Ready, queued.Observe().Execution);
                }
            }
            finally
            {
                try
                {
                    gpuGate.Open();
                }
                finally
                {
                    releaseCpu.TrySetResult();
                    run.World.Publication.Phases.Close(phase);
                    await scheduler.StopAsync(run.World.CreateBorrowedDrainDeadline());
                }
            }

            JobSubmission Submission(JobHandlerId handler, ulong ordinal) => new(
                new SourceKey(begin.World, begin.Tick, TickPhase.Base, 83, 0, ordinal), handler, JobInputKind.None,
                JobResultKind.Commandless, [], [], [], [], [], default);

            IJobExecutionHandle Submit(JobHandlerId handler, ulong ordinal)
            {
                var admission = scheduler.TrySubmit(Submission(handler, ordinal), phase);
                Assert.AreEqual(JobAdmissionStatus.Accepted, admission.Status);
                Assert.IsInstanceOfType<IJobExecutionHandle>(admission.Handle);
                return (IJobExecutionHandle)admission.Handle;
            }

            async Task WaitForWorkers(int count)
            {
                while (scheduler.ReadMetrics().OccupiedWorkerSlots != count)
                    await Task.Delay(1, token);
            }
        }
        finally
        {
            try
            {
                gpuGate.Open();
            }
            finally
            {
                runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            }
        }

        static void AssertPendingCopy(D3D12ResourceSnapshot snapshot, ulong fence)
        {
            Assert.AreEqual(fence, snapshot.TextureCopyFence);
            Assert.AreEqual(fence, snapshot.SubmittedFence);
            Assert.IsLessThan(fence, snapshot.CompletedFence);
            Assert.AreEqual(D3D12CustodyStatus.None, snapshot.Custody);
            Assert.IsFalse(snapshot.LedgerOverflowed || snapshot.LedgerHasUnknownSizes);
            Assert.IsGreaterThan(0UL, snapshot.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes);
            Assert.IsGreaterThan(0UL, snapshot.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes);
        }
    }

    private sealed unsafe class Q02NativeQueueGate : IDisposable
    {
        private readonly Lock _gate = new();
        private ID3D12Fence* _fence;
        private long _releasedAt;
        internal long QueuedAt { get; private set; }

        internal void Enqueue(GpuDevice device)
        {
            lock (_gate)
            {
                if (_releasedAt != 0)
                    return; // Fixture cleanup won the race with the upload callback.
                Assert.IsTrue(_fence == null, "The exact optional copy may only be held once.");
                ID3D12Fence* fence = null;
                GpuNative.Check(device.Device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE,
                    __uuidof<ID3D12Fence>(), (void**)&fence), "Create Q02 independent upload fence");
                try
                {
                    GpuNative.Check(device.Queue->Wait(fence, 1), "Queue Q02 independent upload wait");
                    _fence = fence;
                    QueuedAt = Stopwatch.GetTimestamp();
                }
                catch
                {
                    fence->Release();
                    throw;
                }
            }
        }

        internal void AssertHeld()
        {
            lock (_gate)
            {
                Assert.IsTrue(_fence != null);
                Assert.AreEqual(0UL, _fence->GetCompletedValue());
                Assert.AreEqual(0L, _releasedAt);
            }
        }

        internal long Open()
        {
            lock (_gate)
            {
                if (_releasedAt != 0)
                    return _releasedAt;
                var started = Stopwatch.GetTimestamp();
                if (_fence != null)
                    GpuNative.Check(_fence->Signal(1), "Release Q02 independent upload wait");
                return _releasedAt = started;
            }
        }

        public void Dispose()
        {
            try
            {
                Open();
            }
            finally
            {
                lock (_gate)
                {
                    if (_fence != null)
                    {
                        _fence->Release();
                        _fence = null;
                    }
                }
            }
        }
    }
}
