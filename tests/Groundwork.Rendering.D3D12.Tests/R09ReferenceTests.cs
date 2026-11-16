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
    public async Task R09ReferencePressureRoute()
    {
        var adapter = Environment.GetEnvironmentVariable("GROUNDWORK_R09_ADAPTER");
        if (adapter is null)
            Assert.Inconclusive("Use benchmarks/Measure-Wp11R09Reference.ps1.");
        Assert.IsTrue(adapter is "Hardware" or "Warp");
        var warp = adapter == "Warp";
        var mode = Environment.GetEnvironmentVariable("GROUNDWORK_R09_DIAGNOSTICS");
        Assert.IsTrue(mode is "On" or "Off");
        var diagnostics = mode == "On";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        using var run = new VolumeRun(editable: true, openFloor: true, floorHeight: 16);
        if (diagnostics)
            run.Terrain.EnableEventTiming();
        using var releaseUpload = new ManualResetEventSlim();
        TaskCompletionSource<GpuDevice>? heldUpload = null;
        var hooks = new GpuRetirementTestHooks(BeforeVolumeUpload: gpu =>
        {
            var hold = Interlocked.Exchange(ref heldUpload, null);
            if (hold is null)
                return;
            hold.SetResult(gpu);
            if (!releaseUpload.Wait(TimeSpan.FromSeconds(25), token))
                throw new TimeoutException("The reference route did not release terrain upload.");
        });
        var runtime = run.Fixture.Runtime;
        var limit = TimeSpan.FromSeconds(30);
        var context = new EngineModuleContext(run.Fixture.Clock, new RuntimeTimingLimits(limit, limit, limit),
            runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions("Groundwork R09", 360, 280, 32),
            runtime.UiWake, runtime.RuntimeOwnership);
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics with { EnableResourceSnapshots = diagnostics }, warp, limit, limit, hooks);
        var frames = new List<object>();
        var stages = new List<object>();
        var events = new List<VolumeStreamingEvent>();
        var sequence = 0UL;
        var coverageFocus = 0d;
        var yaw = 0f;
        GpuDevice? device = null;
        var speculative = new JobHandlerId(1);
        var required = new JobHandlerId(2);
        Func<CancellationToken, ValueTask>? runSpeculative = null;
        Func<CancellationToken, ValueTask>? runRequired = null;
        var scheduler = new JobScheduler(run.World.Publication.Committed.Read().Stamp.World, new JobSchedulerLimits(2, 2),
            run.World.Publication.Execution, new CombinedNoInput(), run.World.Ownership, run.World.Reporter, run.Fixture.Clock,
            new Dictionary<JobHandlerId, JobHandler>
            {
                [speculative] = (_, cancellation) => runSpeculative!(cancellation),
                [required] = (_, cancellation) => runRequired!(cancellation),
            }, new JobDispatchReservation(1, 1, [required]));
        try
        {
            await window.InitializeAsync(context, token);
            await window.StartAsync(token);
            await renderer.InitializeAsync(context, token);
            await renderer.StartAsync(token);
            var native = renderer.ReadDiagnostics();
            AssertPlanetAdapter(native.Adapter, warp);
            Assert.IsTrue(native.DebugLayerEnabled);
            Assert.IsTrue(native.DredEnabled);
            await Mask(run.Terrain.View);
            await run.Publish(1, cut: false, renderer);
            await Draw("initial", Root().Terrain.Generation, run.Terrain.View, install: true);
            await run.OpenMovement();
            DrainEvents();

            var player = new PlanetWalkState(new(0, 0, 16), new(1, 0, 0), default);
            for (var i = 0; i < 24; i++)
            {
                var begin = await run.BeginHeldTick();
                player = Root().Player ?? player;
                var draft = run.Terrain.TryPlanMovement(player, i < 12 ? 1 : 0, i < 12 ? 0 : 1, out var status);
                Assert.IsNotNull(draft, $"Movement failed at tick {begin.Tick}: {status}.");
                var candidates = await CoreRuntimeFixture.SealAsync(run.World, begin.Tick, BarrierKind.CommitResults);
                var prepared = run.Terrain.TryPrepareMovement(draft, candidates, []);
                Assert.IsNotNull(prepared);
                var moved = run.World.Publication.Finalization.TryFinalize(prepared);
                Assert.AreEqual(BarrierStatus.Committed, moved.Status);
                CoreRuntimeFixture.PublishObserver(run.World, moved.Stamp!.Value, (int)begin.Tick);
                yaw = i * 0.025f;
                await Draw("walk-turn", Root().Terrain.Generation, run.Terrain.View, install: false);
                DrainEvents();
            }
            Assert.AreNotEqual(player.Position, new PlanetVector(0, 0, 16));

            await Pressure("coverage-transition", VolumeWorkClass.Movement, 32);
            await Pressure("first-edit", VolumeWorkClass.FirstEdit, 32);
            await Pressure("recovery", VolumeWorkClass.Recovery, 0);
            Assert.AreEqual(2UL, Root().Terrain.TerrainRevision);
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            await renderer.StopAsync(token);
            run.Terrain.Stop();
            var settled = run.Terrain.Statistics;
            Assert.AreEqual(0, settled.Readers);
            Assert.AreEqual(0, settled.Generations);
            Assert.AreEqual(default, settled.Used);
            Assert.IsNotNull(device);
            Assert.AreEqual(0L, device.LiveMeshBytes);

            using var process = Process.GetCurrentProcess();
            Console.WriteLine("R09 " + JsonSerializer.Serialize(new
            {
                adapter, mode, selectedAdapter = native.Adapter, native.DebugLayerEnabled, native.DredEnabled,
                seed = 42, radius = 128, generatorVersion = run.Descriptor.GeneratorVersion,
                qpcFrequency = Stopwatch.Frequency, frames, stages, process.PeakWorkingSet64,
                process.PrivateMemorySize64, settledNativeBytes = device.LiveMeshBytes,
                settledReaders = settled.Readers, settledGenerations = settled.Generations,
            }));
        }
        finally
        {
            releaseUpload.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            using var cleanup = new CancellationTokenSource(limit);
            await scheduler.StopAsync(run.World.CreateBorrowedDrainDeadline(), cleanup.Token);
        }

        VolumeWorldRoot Root() => (VolumeWorldRoot)run.World.Publication.Committed.Read().Root;

        static object ResourceCounts(VolumeResources value) => new
        {
            value.CpuBytes, value.NativeBytes, value.UploadBytes, value.RetirementBytes,
            value.DensityJobs, value.MeshJobs, value.QueueSlots, value.LeaseSlots, value.RetirementSlots,
        };

        void DrainEvents()
        {
            while (run.Terrain.TryReadEvent(out var record))
                events.Add(record);
        }

        async Task Mask(VolumeViewIdentity view)
        {
            var frame = ++sequence;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeMask(view, frame, Vector4.Zero));
            await AwaitStatus(renderer, value => value.LastPresentedSequence == frame, token);
        }

        async Task Draw(string phase, ulong generation, VolumeViewIdentity view, bool install)
        {
            var frame = ++sequence;
            var camera = VolumeCameraAt(view, coverageFocus, 24) with
            {
                Position = new PlanetVector(coverageFocus + (Root().Player?.Position.X ?? 0),
                    32 + (Root().Player?.Position.Y ?? 0), 24),
                Forward = Vector3.Normalize(new(MathF.Sin(yaw) * 0.25f, MathF.Cos(yaw) * 0.25f, -1)),
            };
            var start = Stopwatch.GetTimestamp();
            if (install)
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TrySubmitVolumeTerrain(generation, frame, camera));
            else
                Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(generation, frame, camera));
            await AwaitStatus(renderer, value => value.LastPresentedSequence == frame, token);
            var observed = Stopwatch.GetTimestamp();
            D3D12ResourceSnapshot snapshot = default;
            if (diagnostics)
                await AwaitStatus(renderer, _ => renderer.TryReadResourceDiagnostics(out snapshot) &&
                    snapshot.RetiredPresentSequence == frame, token);
            frames.Add(new
            {
                phase, frame, generation, epoch = view.RendererEpoch, view = view.View,
                start, observed, snapshot.DrawTimestamp, snapshot.DrawFence, snapshot.PresentTimestamp,
                snapshot.PresentFence, snapshot.RetiredPresentTimestamp, snapshot.RetiredPresentFence,
                terrainBacking = diagnostics ? snapshot.GetBalance(D3D12ResourceOwner.Terrain) : (D3D12ResourceBalance?)null,
            });
        }

        async Task WaitForWorkerSlots(int occupied)
        {
            // ExecutionCompletion is published before the scheduler releases its worker reservation.
            while (scheduler.ReadMetrics().OccupiedWorkerSlots != occupied)
                await Task.Delay(1, token);
        }

        async Task Pressure(string name, VolumeWorkClass kind, double focus)
        {
            DrainEvents();
            events.Clear();
            var previous = Root().Terrain;
            // The fixture authorizes this transition before any recovery preload or renderer view change.
            var transitionAuthorizedAt = Stopwatch.GetTimestamp();
            using var retained = run.Terrain.TryRead();
            Assert.IsNotNull(retained);
            var begin = await run.BeginHeldTick();
            VolumeBrushIntent? editIntent = null;
            if (kind == VolumeWorkClass.FirstEdit)
            {
                VolumeBrushIntent edit = new(begin.Tick, 1, 1, new(28, 28, 14), new(36, 36, 17), VolumeBrushOperation.Remove);
                Assert.AreEqual(VolumeAdmission.Accepted, run.Terrain.SealEditTick(begin.Tick, run.Descriptor, [edit]));
                editIntent = edit;
            }
            var oldView = run.Terrain.View;
            oldView.Frame.TryRebase(new(focus, 32, 0), out var origin);
            var view = run.Terrain.ChangeView(origin, oldView.RendererEpoch);
            var viewChangedAt = Stopwatch.GetTimestamp();
            await Mask(view);
            var selection = VolumeStreamingSelection.Select(run.Descriptor, new(focus, 32, 24), new(-1, 0, 0));
            using var work = kind == VolumeWorkClass.FirstEdit
                ? run.StageReferenceEdit(editIntent!.Value, selection)
                : run.Stage(kind, previous.TerrainRevision, previous.TerrainRevision, cut: false, selection: selection);
            var phase = run.World.Publication.Phases.TryOpen(begin, TickPhase.Base).Phase;
            Assert.IsNotNull(phase);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long workerQueuedAt = 0, workerStart = 0, workerEnd = 0, workerCompletionObservedAt = 0;
            long overlapWorkerStart = 0, overlapWorkerEnd = 0;
            VolumeStreamingWork? overlap = null;
            runSpeculative = async cancellation =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellation);
            };
            runRequired = cancellation =>
            {
                if (overlap is { } probe)
                {
                    overlapWorkerStart = Stopwatch.GetTimestamp();
                    Assert.IsTrue(probe.Execute(cancellation));
                    overlapWorkerEnd = Stopwatch.GetTimestamp();
                }
                else
                {
                    workerStart = Stopwatch.GetTimestamp();
                    Assert.IsTrue(work.Execute(cancellation));
                    workerEnd = Stopwatch.GetTimestamp();
                }
                return ValueTask.CompletedTask;
            };

            var blockedCpuBytes = 0L;
            var heldNative = 0L;
            var readyNative = 0L;
            long uploadBlockedAt = 0, releasedAt = 0, nativeReadyObservedAt = 0;
            var denialCount = 0;
            VolumeStreamingStatistics pressure = default;
            try
            {
                using var running = Submit(speculative, 1);
                await scheduler.DispatchReadyAsync(phase, token);
                await entered.Task.WaitAsync(token);
                using var queued = Submit(speculative, 2);
                Denied(3);
                using var job = Submit(required, 4);
                // This scheduler only dispatches in DispatchReadyAsync. The accepted job cannot start before this marker.
                workerQueuedAt = Stopwatch.GetTimestamp();
                await scheduler.DispatchReadyAsync(phase, token);
                Assert.AreEqual(JobExecutionStatus.Succeeded, (await job.ExecutionCompletion.WaitAsync(token)).Status);
                workerCompletionObservedAt = Stopwatch.GetTimestamp();
                Assert.IsTrue(transitionAuthorizedAt <= viewChangedAt && viewChangedAt <= workerQueuedAt &&
                    workerQueuedAt <= workerStart && workerStart <= workerEnd && workerEnd <= workerCompletionObservedAt);
                await WaitForWorkerSlots(1);

                releaseUpload.Reset();
                var upload = new TaskCompletionSource<GpuDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref heldUpload, upload);
                Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, renderer.TryStageVolumeTerrain(work.Generation));
                device = await upload.Task.WaitAsync(token);
                uploadBlockedAt = Stopwatch.GetTimestamp();
                Denied(5);
                var secondaryKind = kind == VolumeWorkClass.Recovery ? VolumeWorkClass.Movement : VolumeWorkClass.Recovery;
                using var pressureProbe = run.Stage(secondaryKind, previous.TerrainRevision, previous.TerrainRevision,
                    cut: false, selection: selection);
                overlap = pressureProbe;
                using var overlapJob = Submit(required, 6);
                await scheduler.DispatchReadyAsync(phase, token);
                Assert.AreEqual(JobExecutionStatus.Succeeded, (await overlapJob.ExecutionCompletion.WaitAsync(token)).Status);
                await WaitForWorkerSlots(1);
                Assert.IsFalse(renderer.IsVolumeTerrainReady(work.Generation));
                Assert.AreEqual(previous.Generation, Root().Terrain.Generation);
                Assert.AreEqual(previous, retained.Info);
                pressure = run.Terrain.Statistics;
                Assert.IsTrue(pressure.MovementHeld);
                blockedCpuBytes = pressure.Used.CpuBytes;
                heldNative = device.LiveMeshBytes;
                Assert.IsGreaterThan(0L, heldNative);
                Assert.IsFalse(running.ExecutionCompletion.IsCompleted);
                Assert.IsFalse(queued.ExecutionCompletion.IsCompleted);
                releasedAt = Stopwatch.GetTimestamp();
                releaseUpload.Set();
                await AwaitStatus(renderer, _ => renderer.IsVolumeTerrainReady(work.Generation), token);
                nativeReadyObservedAt = Stopwatch.GetTimestamp();
                Assert.IsTrue(workerCompletionObservedAt <= uploadBlockedAt &&
                    uploadBlockedAt <= overlapWorkerStart && overlapWorkerStart <= overlapWorkerEnd &&
                    overlapWorkerEnd <= releasedAt && releasedAt <= nativeReadyObservedAt);
                readyNative = device.LiveMeshBytes;
                Assert.IsGreaterThan(heldNative, readyNative);
                Denied(7);
                Assert.IsFalse(running.ExecutionCompletion.IsCompleted);
                Assert.IsFalse(queued.ExecutionCompletion.IsCompleted);
                release.TrySetResult();
                Assert.AreEqual(JobExecutionStatus.Succeeded, (await running.ExecutionCompletion.WaitAsync(token)).Status);
                await WaitForWorkerSlots(0);
                await scheduler.DispatchReadyAsync(phase, token);
                Assert.AreEqual(JobExecutionStatus.Succeeded, (await queued.ExecutionCompletion.WaitAsync(token)).Status);
                await WaitForWorkerSlots(0);
            }
            finally
            {
                releaseUpload.Set();
                release.TrySetResult();
                run.World.Publication.Phases.Close(phase);
            }

            if (kind == VolumeWorkClass.FirstEdit)
            {
                await run.FinishHeldTick();
                begin = await run.BeginHeldTick();
            }
            var candidates = await CoreRuntimeFixture.SealAsync(run.World, begin.Tick, BarrierKind.CommitResults);
            var prepared = renderer.TryPrepareVolumePublication(work.Generation, candidates, [], out var preparation);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted, preparation);
            Assert.IsNotNull(prepared);
            var outcome = run.World.Publication.Finalization.TryFinalize(prepared);
            Assert.AreEqual(BarrierStatus.Committed, outcome.Status);
            CoreRuntimeFixture.PublishObserver(run.World, outcome.Stamp!.Value, (int)begin.Tick);
            Assert.AreEqual(work.Generation, Root().Terrain.Generation);
            if (kind == VolumeWorkClass.FirstEdit)
            {
                Assert.AreEqual(editIntent, Root().CommittedEdit);
                Assert.AreEqual(previous.Minimum, Root().Terrain.Minimum);
            }
            else
                Assert.AreNotEqual(previous.Minimum, Root().Terrain.Minimum);
            coverageFocus = focus;
            await Draw(name, work.Generation, view, install: true);
            await run.OpenMovement();
            DrainEvents();
            var evidence = events.Where(e => e.Generation == work.Generation).ToArray();
            var ready = evidence.Single(e => e.Kind == VolumeEventKind.WorkReady);
            var receipt = evidence.Single(e => e.Kind == VolumeEventKind.Accepted && e.Frame == sequence);
            var consumed = evidence.Single(e => e.Kind == VolumeEventKind.Opened && e.Frame == sequence);
            Assert.AreEqual(view, receipt.View);
            Assert.AreEqual(view, consumed.View);
            Assert.AreEqual(BarrierKind.BeginTick, consumed.Stamp!.Value.Kind);
            Assert.AreEqual(receipt.Stamp!.Value.World, consumed.Stamp.Value.World);
            Assert.IsGreaterThan(receipt.Stamp.Value.Tick, consumed.Stamp.Value.Tick);
            if (diagnostics)
                Assert.IsTrue(workerStart <= ready.MonotonicTimestamp && ready.MonotonicTimestamp <= workerEnd &&
                    nativeReadyObservedAt <= receipt.MonotonicTimestamp && receipt.MonotonicTimestamp <= consumed.MonotonicTimestamp);
            else
                Assert.IsTrue(evidence.All(e => e.MonotonicTimestamp == 0));
            Assert.IsFalse(run.Terrain.Statistics.MovementHeld);
            Assert.AreEqual(previous, retained.Info);

            stages.Add(new
            {
                phase = name, generation = work.Generation, frame = sequence, epoch = view.RendererEpoch, view = view.View,
                previousGeneration = previous.Generation, revision = Root().Terrain.TerrainRevision,
                minimum = Root().Terrain.Minimum, focus, transitionAuthorizedAt, viewChangedAt,
                workerQueuedAt, workerStart, workerEnd, workerCompletionObservedAt,
                uploadBlockedAt, releasedAt, nativeReadyObservedAt, overlapWorkerStart, overlapWorkerEnd,
                world = receipt.Stamp.Value.World.Value,
                workReady = ready.MonotonicTimestamp, authorizedReceipt = receipt.MonotonicTimestamp,
                consumingBeginTick = consumed.MonotonicTimestamp, receiptTick = receipt.Stamp.Value.Tick,
                consumingTick = consumed.Stamp.Value.Tick, blockedCpuBytes, heldNative, readyNative,
                used = ResourceCounts(pressure.Used), peak = ResourceCounts(pressure.Peak),
                pressure.Readers, pressure.Generations,
                speculativeAttempts = 2 + denialCount, denied = denialCount, protectedJobs = 2, completedProtectedJobs = 2, publishingJobs = 1,
            });

            JobSubmission Submission(JobHandlerId handler, ulong ordinal) => new(
                new SourceKey(begin.World, begin.Tick, TickPhase.Base, 83, 0, (ulong)stages.Count * 8 + ordinal), handler, JobInputKind.None,
                JobResultKind.Commandless, [], [], [], [], [], default);

            IJobExecutionHandle Submit(JobHandlerId handler, ulong ordinal)
            {
                var admission = scheduler.TrySubmit(Submission(handler, ordinal), phase);
                Assert.AreEqual(JobAdmissionStatus.Accepted, admission.Status);
                return (IJobExecutionHandle)admission.Handle!;
            }

            void Denied(ulong ordinal)
            {
                Assert.AreEqual(JobAdmissionStatus.CapacityExceeded, scheduler.TrySubmit(Submission(speculative, ordinal), phase).Status);
                denialCount++;
            }
        }
    }
}
