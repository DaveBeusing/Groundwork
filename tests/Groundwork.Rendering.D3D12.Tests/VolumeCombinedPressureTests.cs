using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Groundwork.Core;
using Groundwork.Core.Tests;
using Groundwork.Jobs;
using Groundwork.Platform.Windows;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task VolumeUploadRemainsBlockedWhileProtectedTerrainWorkRunsOnFullCpuQueue(bool warp)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        using var run = new VolumeRun();
        using var releaseUpload = new ManualResetEventSlim();
        var uploadEntered = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockNextUpload = 0;
        var hooks = new GpuRetirementTestHooks(BeforeVolumeUpload: gpu =>
        {
            if (Interlocked.Exchange(ref blockNextUpload, 0) == 0)
                return;
            uploadEntered.TrySetResult(gpu);
            if (!releaseUpload.Wait(TimeSpan.FromSeconds(12), token))
                throw new TimeoutException("Q01 did not release the volume upload.");
        });
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions("Groundwork Q01 combined pressure", 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1, QualificationDiagnostics,
            warp, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), hooks);
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            var diagnostics = renderer.ReadDiagnostics();
            AssertPlanetAdapter(diagnostics.Adapter, warp);
            Assert.IsTrue(diagnostics.DebugLayerEnabled);
            Assert.IsTrue(diagnostics.DredEnabled);

            var original = run.Terrain.View;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(original, 1, Vector4.Zero));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 1, token);
            await run.Publish(1, cut: false, renderer);
            var initial = ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Terrain.Generation;
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(initial, 2, VolumeCameraAt(original, 32, 4)));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 2, token);
            await run.OpenMovement();
            using var retained = run.Terrain.TryRead();
            Assert.IsNotNull(retained);

            Assert.IsTrue(original.Frame.TryRebase(new PlanetVector(32, 0, 0), out var frame));
            var view = run.Terrain.ChangeView(frame, original.RendererEpoch);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, 3, Vector4.Zero));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 3, token);
            using var replacement = run.Stage(VolumeWorkClass.Movement, 1, 2, cut: true);
            using var recovery = run.Stage(VolumeWorkClass.Recovery, 1, 1, cut: false);
            Assert.IsTrue(replacement.Execute(token));

            var begin = await run.BeginHeldTick();
            var phase = run.World.Publication.Phases.TryOpen(begin, TickPhase.Base).Phase;
            Assert.IsNotNull(phase);
            var releaseCpu = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cpuEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var speculative = new JobHandlerId(1);
            var protectedHandler = new JobHandlerId(2);
            var scheduler = new JobScheduler(begin.World, new JobSchedulerLimits(2, 2), run.World.Publication.Execution,
                new CombinedNoInput(), run.World.Ownership, run.World.Reporter, run.Fixture.Clock,
                new Dictionary<JobHandlerId, JobHandler>
                {
                    [speculative] = async (_, cancellation) =>
                    {
                        cpuEntered.TrySetResult();
                        await releaseCpu.Task.WaitAsync(cancellation);
                    },
                    [protectedHandler] = (_, cancellation) =>
                    {
                        Assert.IsTrue(recovery.Execute(cancellation));
                        return ValueTask.CompletedTask;
                    }
                }, new JobDispatchReservation(1, 1, [protectedHandler]));
            GpuDevice device;
            long started;
            double protectedMs;
            double blockedMs;
            double uploadMs;
            VolumeStreamingStatistics pressure;
            long heldNative;
            long readyNative;
            try
            {
                using var running = Submit(speculative, 1);
                await scheduler.DispatchReadyAsync(phase, token);
                await cpuEntered.Task.WaitAsync(token);
                using var queued = Submit(speculative, 2);
                Assert.AreEqual(JobAdmissionStatus.CapacityExceeded, scheduler.TrySubmit(Submission(speculative, 3), phase).Status);

                Volatile.Write(ref blockNextUpload, 1);
                started = Stopwatch.GetTimestamp();
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TryStageVolumeTerrain(replacement.Generation));
                device = await uploadEntered.Task.WaitAsync(token);
                Assert.IsFalse(renderer.IsVolumeTerrainReady(replacement.Generation));
                Assert.AreEqual(MeshReplacementSubmissionResult.Backpressure, renderer.TryStageVolumeTerrain(recovery.Generation));

                var protectedStart = Stopwatch.GetTimestamp();
                using var completed = Submit(protectedHandler, 4);
                await scheduler.DispatchReadyAsync(phase, token);
                Assert.AreEqual(JobExecutionStatus.Succeeded, (await completed.ExecutionCompletion.WaitAsync(token)).Status);
                protectedMs = Stopwatch.GetElapsedTime(protectedStart).TotalMilliseconds;

                Assert.IsFalse(running.ExecutionCompletion.IsCompleted);
                Assert.IsFalse(queued.ExecutionCompletion.IsCompleted);
                Assert.AreEqual(JobAdmissionStatus.CapacityExceeded, scheduler.TrySubmit(Submission(speculative, 5), phase).Status);
                Assert.IsFalse(renderer.IsVolumeTerrainReady(replacement.Generation));
                pressure = run.Terrain.Statistics;
                Assert.IsTrue(pressure.MovementHeld);
                Assert.AreEqual(1UL, retained.Info.TerrainRevision);
                heldNative = device.LiveMeshBytes;
                Assert.IsGreaterThan(0L, heldNative);
                Assert.AreEqual(initial, ((VolumeWorldRoot)run.World.Publication.Committed.Read().Root).Terrain.Generation);
                blockedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                var releaseStart = Stopwatch.GetTimestamp();
                releaseUpload.Set();
                await AwaitStatus(renderer, _ => renderer.IsVolumeTerrainReady(replacement.Generation), token);
                uploadMs = Stopwatch.GetElapsedTime(releaseStart).TotalMilliseconds;
                readyNative = device.LiveMeshBytes;
                Assert.IsGreaterThan(heldNative, readyNative);
                Assert.AreEqual(2, device.LiveMeshBuffers);
                Assert.IsFalse(running.ExecutionCompletion.IsCompleted);
                Assert.IsFalse(queued.ExecutionCompletion.IsCompleted);
                releaseCpu.TrySetResult();
                Assert.AreEqual(JobExecutionStatus.Succeeded, (await running.ExecutionCompletion.WaitAsync(token)).Status);
                await scheduler.DispatchReadyAsync(phase, token);
                Assert.AreEqual(JobExecutionStatus.Succeeded, (await queued.ExecutionCompletion.WaitAsync(token)).Status);
            }
            finally
            {
                releaseUpload.Set();
                releaseCpu.TrySetResult();
                run.World.Publication.Phases.Close(phase);
                await scheduler.StopAsync(run.World.CreateBorrowedDrainDeadline());
            }

            var candidates = await CoreRuntimeFixture.SealAsync(run.World, begin.Tick, BarrierKind.CommitResults);
            var prepared = renderer.TryPrepareVolumePublication(replacement.Generation, candidates, [], out var status);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, status);
            Assert.IsNotNull(prepared);
            var outcome = run.World.Publication.Finalization.TryFinalize(prepared);
            Assert.AreEqual(BarrierStatus.Committed, outcome.Status);
            CoreRuntimeFixture.PublishObserver(run.World, outcome.Stamp!.Value, (int)begin.Tick);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(replacement.Generation, 4, VolumeCameraAt(view, 32, 4)));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 4, token);
            await run.OpenMovement();
            Assert.IsFalse(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual(1UL, retained.Info.TerrainRevision);
            retained.Dispose();
            recovery.Dispose();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            await renderer.StopAsync(token);
            Assert.AreEqual(0, run.Terrain.Statistics.Readers);
            Assert.AreEqual(0L, device.LiveMeshBytes);

            using var process = Process.GetCurrentProcess();
            Console.WriteLine("Q01 " + JsonSerializer.Serialize(new
            {
                route = warp ? "combined-warp" : "combined-hardware",
                phase = "protected-build-during-upload-block",
                elapsedMs = protectedMs,
                stageThroughBlockMs = blockedMs,
                releaseThroughNativeReadyMs = uploadMs,
                pressure.Used.CpuBytes,
                terrainQueueSlots = pressure.Used.QueueSlots,
                blockedSpeculativeWorkers = 1,
                queuedSpeculativeJobs = 1,
                pressure.Readers,
                pressure.Generations,
                heldNative,
                readyNative,
                settledNative = device.LiveMeshBytes,
                process.PeakWorkingSet64,
            }));

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
        }
        finally
        {
            releaseUpload.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        }
    }

    private sealed class CombinedNoInput : IJobInputSource
    {
        public JobInputCapture TryCapture(in JobInputRequest request) =>
            throw new AssertFailedException("The commandless Q01 jobs do not capture input.");
    }
}
