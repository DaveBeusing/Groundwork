using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public Task ProtectedEditedGenerationPresentsBetweenOptionalVisualQuanta(bool warp) =>
        RunVisualEditPressure(warp);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    public async Task Tex03MixedHealthyQueueLatency()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("GROUNDWORK_TEX03_MIXED_SAMPLE"), out var sample) || sample < 1)
            Assert.Inconclusive("Run benchmarks/Measure-Tex03MixedLatency.ps1 to collect mixed-workload observations.");
        var adapter = Environment.GetEnvironmentVariable("GROUNDWORK_TEX03_MIXED_ADAPTER");
        var diagnostics = Environment.GetEnvironmentVariable("GROUNDWORK_TEX03_MIXED_DIAGNOSTICS");
        Assert.IsTrue(adapter is "Warp" or "Hardware");
        Assert.IsTrue(diagnostics is "Enabled" or "Disabled");
        await RunVisualEditPressure(adapter == "Warp", sample, diagnostics == "Enabled");
    }

    private static async Task RunVisualEditPressure(bool warp, int sample = 0, bool diagnosticsRequested = true)
    {
        const int copyLimit = GpuDevice.MaxTextureCopyBytes;
        const int propCount = 32_768;
        const ulong maskSequence = 5;
        const ulong editSequence = 6;
        var measure = sample != 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        using var run = new VolumeRun(editable: true, openFloor: true);
        using var resumeFrame = new ManualResetEventSlim();
        using var resumeCopy = new ManualResetEventSlim();
        var frameHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copyIssued = new TaskCompletionSource<GpuTextureStatistics>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEditPresent = new TaskCompletionSource<(ulong Generation, D3D12ResourceSnapshot Resources, D3D12PropStatistics Props)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = run.Fixture.Runtime;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        var context = new EngineModuleContext(run.Fixture.Clock, timing, runtime.Reporter, runtime.StopControl);
        await using var window = new WindowsHostModule(new WindowsHostOptions($"Groundwork edit during visual pressure {Guid.NewGuid():N}",
            TerrainWidth, TerrainHeight, 32), runtime.UiWake, runtime.RuntimeOwnership);
        var textureLimits = new D3D12TextureLimits(4, 4, copyLimit + 4096, copyLimit + 4096, copyLimit + 4096,
            copyLimit + 4 * 65_536, copyLimit + 4 * 65_536, 4);
        var textures = new D3D12TextureHandoff(textureLimits);
        var mesh = RenderMesh.Create([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], [0, 1, 2]);
        var props = new D3D12PropCells(new(2, 2, propCount, 2 * propCount, 2), propCount, 42, 1,
            [new(new(1, 1, 2, 40, 20, 24), mesh, mesh)]);
        PropCellKey[] cells = [default, new(1, 0, 0)];
        var instances = new PropPlacement[propCount];
        for (var i = 0; i < instances.Length; i++)
            instances[i] = new((uint)i, 0, Vector3.Zero, Quaternion.Identity, 0.001f, 0xffff00ff);
        instances[0] = instances[0] with { Scale = 1 };
        var coarseAsset = CookPressureTexture(4, 4, 2);
        var maximumAsset = CookPressureTexture(512, 1024, 1);
        var smallCredit = new D3D12TextureReservation(256, 256, 256, 65_536, 65_536);
        var maximumCredit = new D3D12TextureReservation(copyLimit + 256, copyLimit, copyLimit + 256, copyLimit, copyLimit);
        var policy = new D3D12TextureMipPolicy(0, 1, 10, 30);
        var first = PressureTarget(1, coarseAsset);
        var largest = PressureTarget(2, maximumAsset);
        var next = PressureTarget(3, coarseAsset);
        var maximumTicket = default(D3D12TextureTicket);
        var waitingProp = default(PropCellRequest);
        var isMaximumCopy = false;
        var optionalAttempts = 0;
        var terrainUploads = 0;
        long copyStartedAt = 0, copyExecutedAt = 0, copyCompletionObservedAt = 0;
        long producerArmedAt = 0, protectedRequestAt = 0, maskPlanObservedAt = 0;
        long nativeTerrainUploadAt = 0, publicationObservedAt = 0;
        long maskAdmissionObservedAt = 0, terrainWorkAdmissionObservedAt = 0, rendererStageAdmissionObservedAt = 0;
        long movementOpenedAt = 0;
        var optionalAttemptsAtRequest = 0;
        var optionalAttemptsAtMaskAdmission = 0;
        var appliedWidth = 0;
        var appliedHeight = 0;
        HostWindowSnapshot windowAtSetup = default, windowAtRetirement = default;
        var warmupDrawPrepared = false;
        var textureFirstUseReady = false;
        var textureRefinementReady = false;
        PropCellResult waitingPropAtCopy = default;
        ulong maximumCopyFence = 0;
        D3D12TextureUsage textureUsageAtCopy = default;
        PropCellUsage propsAtCopy = default;
        VolumeStreamingStatistics terrainAtCopy = default;
        var view = run.Terrain.View;
        var camera = VolumeCameraAt(view, 32, 4);
        Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 4, out var pressureFrame));
        Assert.IsTrue(pressureFrame.Scene.HasValue);
        var scene = pressureFrame.Scene.Value;
        VolumePublicationInfo initial = default;
        D3D12Renderer? active = null;
        var hooks = new GpuRetirementTestHooks(
            SignalFailure: (gpu, fence) =>
            {
                if (!isMaximumCopy || !gpu.HasTextureCopy)
                    return null;
                isMaximumCopy = false;
                if (measure)
                {
                    copyExecutedAt = Stopwatch.GetTimestamp();
                    Volatile.Write(ref maximumCopyFence, fence);
                }
                else
                    Assert.IsTrue(gpu.HasReadyPropUploads(scene, initial));
                copyIssued.TrySetResult(gpu.TextureStatistics);
                if (!measure)
                    resumeCopy.Wait(token);
                return null;
            },
            CompletedValue: measure ? (_, completed) =>
            {
                var pending = Volatile.Read(ref maximumCopyFence);
                if (pending != 0 && completed != ulong.MaxValue && completed >= pending &&
                    Volatile.Read(ref copyCompletionObservedAt) == 0)
                    Interlocked.CompareExchange(ref copyCompletionObservedAt, Stopwatch.GetTimestamp(), 0);
                return completed;
            } : null,
            PresentFailure: measure ? _ =>
            {
                // Park only the warm-up frame, after prop recording and before Present.
                if (warmupDrawPrepared && frameHeld.TrySetResult())
                    resumeFrame.Wait(token);
                warmupDrawPrepared = false;
                return null;
            } : null,
            BeforeVolumeUpload: _ =>
            {
                if (Interlocked.Increment(ref terrainUploads) == 2 && measure)
                    nativeTerrainUploadAt = Stopwatch.GetTimestamp();
            },
            RejectPreparedPlan: measure ? sequence =>
            {
                if (sequence == 3)
                    warmupDrawPrepared = true;
                if (sequence == maskSequence)
                    Interlocked.CompareExchange(ref maskPlanObservedAt, Stopwatch.GetTimestamp(), 0);
                return false;
            } : null);
        var diagnosticsOptions = diagnosticsRequested ? QualificationDiagnostics : default;
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            diagnosticsOptions with { EnableResourceSnapshots = true }, warp, TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(15), hooks)
        {
            BeforeTextureUploadForTests = (ticket, wake) =>
            {
                Interlocked.Increment(ref optionalAttempts);
                isMaximumCopy = ticket == maximumTicket;
                if (isMaximumCopy && measure)
                {
                    textureFirstUseReady = textures.HasReady(Cut(initial), firstUse: true);
                    textureRefinementReady = textures.HasReady(Cut(initial), firstUse: false);
                    textureUsageAtCopy = textures.ReadUsage();
                    lock (props.Gate)
                    {
                        waitingPropAtCopy = props.Admission.ReadStagedLength(waitingProp, out _, out _);
                        propsAtCopy = props.Admission.Usage;
                    }
                    terrainAtCopy = run.Terrain.Statistics;
                    copyStartedAt = Stopwatch.GetTimestamp();
                }
            },
            VolumePresentReadbackForTests = measure ? null : (sequence, generation, _) =>
            {
                if (sequence == 3 && frameHeld.TrySetResult())
                    resumeFrame.Wait(token);
                if (sequence == editSequence)
                {
                    Assert.IsTrue(active!.TryReadResourceDiagnostics(out var resources));
                    firstEditPresent.TrySetResult((generation, resources, props.ReadStatistics()));
                }
            },
        };
        active = renderer;
        renderer.AttachTextureHandoff(textures);
        renderer.AttachPropCells(props);
        Task<VolumeViewIdentity>? edit = null;

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
            var cut = Cut(initial);
            props.Publish(new(cut.World, cut.View.RendererEpoch, cut.TerrainRevision, cut.CoverageGeneration), cells);
            StageProp(cells[0]);
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(initial.Generation, 2, camera));
            await AwaitStatus(renderer, state => state.LastPresentedSequence == 2, token);

            Assert.IsTrue(textures.Publish(cut, 1, [first, largest, next]));
            Assert.AreEqual(D3D12TextureResult.Accepted,
                textures.TryRequestTier(cut, first, policy, 100, smallCredit, smallCredit, out var coarse));
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(coarse, coarseAsset));
            await AwaitStatus(renderer,
                _ => textures.TryReadResidency(cut, first, out var resident) && resident.ResidentMip == 1, token);
            var uploadedBefore = renderer.ReadTextureDiagnostics().UploadedBytes;
            var terrainBefore = run.Terrain.Statistics.Peak;
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(initial.Generation, 3, camera));
            await AwaitStatus(renderer, _ => frameHeld.Task.IsCompleted, token);
            Assert.AreEqual(propCount, props.Admission.Usage.ResidentInstances);
            Assert.AreEqual(1, Volatile.Read(ref terrainUploads));
            while (run.Terrain.TryReadEvent(out _))
            {
            }

            waitingProp = StageProp(cells[1]);
            Assert.AreEqual(PropCellResult.Quota, props.TryRequest(cells[0], propCount, out _));
            Assert.AreEqual(D3D12TextureResult.Accepted,
                textures.TryRequestTier(cut, first, policy, 0, smallCredit, smallCredit, out var refine));
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(refine, coarseAsset));
            maximumTicket = StageTexture(cut, largest, maximumAsset, maximumCredit);
            StageTexture(cut, next, coarseAsset, smallCredit);
            Assert.AreEqual(textureLimits.Descriptors, textures.ReadUsage().Descriptors);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(initial.Generation, 4, camera));
            if (measure)
            {
                windowAtSetup = window.Read();
                producerArmedAt = Stopwatch.GetTimestamp();
                edit = StartHealthyEdit();
            }
            resumeFrame.Set();
            await AwaitStatus(renderer, _ => copyIssued.Task.IsCompleted, token);
            var inFlight = await copyIssued.Task.WaitAsync(token);
            Assert.AreEqual(copyLimit, inFlight.StagingBytes);
            Assert.AreEqual(uploadedBefore + copyLimit, inFlight.UploadedBytes);
            if (measure)
            {
                Assert.IsTrue(textureFirstUseReady, "The maximum copy must compete with another staged first-use texture.");
                Assert.IsTrue(textureRefinementReady, "The maximum copy must compete with a staged refinement.");
                Assert.AreEqual(PropCellResult.Accepted, waitingPropAtCopy,
                    "A WP16 prop upload must still be staged and eligible when the maximum copy is selected.");
                Assert.AreEqual(propCount, propsAtCopy.ResidentInstances);
                Assert.AreEqual(propCount, propsAtCopy.PendingInstances);
                Assert.AreEqual(terrainBefore, terrainAtCopy.Peak);
            }
            else
            {
                Assert.IsTrue(textures.HasReady(cut, firstUse: true));
                Assert.IsTrue(textures.HasReady(cut, firstUse: false));
                lock (props.Gate)
                    Assert.AreEqual(PropCellResult.Accepted, props.Admission.ReadStagedLength(waitingProp, out _, out _));
                Assert.AreEqual(2, Volatile.Read(ref optionalAttempts));
                Assert.AreEqual(terrainBefore, run.Terrain.Statistics.Peak);
                Assert.IsFalse(run.Terrain.TryReadEvent(out _));

                // Admit the edit while the largest copy awaits its deliberately held signal.
                edit = Task.Run(() => run.CutFloor(renderer, token, maskSequence: maskSequence), token);
                await AwaitStatus(renderer, state => state.LastAcceptedSequence == maskSequence &&
                    run.Terrain.Statistics.Used.QueueSlots == 1, token);
                Assert.AreEqual(3UL, renderer.Read().LastPresentedSequence);
                Assert.AreEqual(1, Volatile.Read(ref terrainUploads));
                Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
                Assert.IsFalse(edit.IsCompleted);
                resumeCopy.Set();
            }
            Assert.IsNotNull(edit);
            var editedView = await edit.WaitAsync(token);
            using var edited = run.Terrain.TryRead();
            Assert.IsNotNull(edited);
            Assert.AreEqual(2UL, edited.Info.TerrainRevision);
            Assert.AreNotEqual(initial.Generation, edited.Info.Generation);
            var committed = (VolumeWorldRoot)run.World.Publication.Committed.Read().Root;
            Assert.AreEqual(edited.Info, committed.Terrain);
            Assert.IsNotNull(committed.CommittedEdit);
            Assert.AreEqual(initial.Minimum, edited.Info.Minimum);
            Assert.AreEqual(initial.Width, edited.Info.Width);
            Assert.AreEqual(2, Volatile.Read(ref terrainUploads));
            if (!measure)
                Assert.AreEqual(2, Volatile.Read(ref optionalAttempts));
            var editedCut = Cut(edited.Info);
            Assert.IsTrue(textures.Publish(editedCut, 1, [first]));
            props.Publish(new(editedCut.World, editedCut.View.RendererEpoch, editedCut.TerrainRevision,
                editedCut.CoverageGeneration), cells, new HashSet<PropCellKey>(cells));
            Assert.IsFalse(props.TryCancel(waitingProp));
            StageProp(cells[0]);
            if (!measure)
                StageTexture(editedCut, first, coarseAsset, smallCredit);
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame,
                renderer.TrySubmitVolumeCamera(initial.Generation, editSequence, camera));
            var firstContentRequestAt = measure ? Stopwatch.GetTimestamp() : 0;
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(edited.Info.Generation, editSequence, VolumeCameraAt(editedView, 32, 4)));

            if (measure)
            {
                await AwaitStatus(renderer, state =>
                {
                    if (state.LastPresentedSequence != editSequence || !renderer.TryReadResourceDiagnostics(out var snapshot) ||
                        snapshot.FrameSequence != editSequence || snapshot.PresentedSequence != editSequence ||
                        snapshot.RetiredPresentSequence != editSequence)
                        return false;
                    appliedWidth = state.AppliedWidth;
                    appliedHeight = state.AppliedHeight;
                    windowAtRetirement = window.Read();
                    firstEditPresent.TrySetResult((edited.Info.Generation, snapshot, props.ReadStatistics()));
                    return true;
                }, token);
            }
            else
                await AwaitStatus(renderer, _ => firstEditPresent.Task.IsCompleted, token);
            var presented = await firstEditPresent.Task.WaitAsync(token);
            Assert.AreEqual(edited.Info.Generation, presented.Generation);
            Assert.AreEqual(editSequence, presented.Resources.PresentedSequence);
            Assert.AreEqual(editSequence, presented.Resources.RetiredPresentSequence);
            Assert.IsFalse(presented.Resources.LedgerOverflowed);
            Assert.AreEqual(0, presented.Resources.GetBalance(D3D12ResourceOwner.Texture).Live);
            Assert.AreEqual(0, presented.Resources.GetBalance(D3D12ResourceOwner.Texture).Retained);
            Assert.AreEqual(0, presented.Resources.GetBalance(D3D12ResourceOwner.TextureStaging).Live);
            Assert.IsGreaterThan(0, presented.Props.PayloadBytes);
            Assert.IsLessThanOrEqualTo(D3D12PropCells.MaximumUploadBytes, presented.Props.UploadBytes);
            var acceptedEditPresents = 0;
            while (run.Terrain.TryReadEvent(out var evidence))
            {
                if (evidence.Kind != VolumeEventKind.Accepted || evidence.Generation != edited.Info.Generation)
                    continue;
                Assert.AreEqual(editSequence, evidence.Frame);
                Assert.AreEqual(edited.Info.View, evidence.View);
                acceptedEditPresents++;
            }
            Assert.AreEqual(1, acceptedEditPresents);
            if (measure)
                StageTexture(editedCut, first, coarseAsset, smallCredit);
            await run.OpenMovement(measure ? () => movementOpenedAt = Stopwatch.GetTimestamp() : null);
            Assert.IsFalse(run.Terrain.Statistics.MovementHeld);
            await AwaitStatus(renderer,
                _ => textures.TryReadResidency(editedCut, first, out var installed) && installed.ResidentMip == 0, token);
            Assert.AreEqual(2, Volatile.Read(ref terrainUploads));
            var peak = run.Terrain.Statistics.Peak;
            Assert.AreEqual(1, peak.QueueSlots);
            Assert.AreEqual(1, peak.DensityJobs);
            Assert.AreEqual(1, peak.MeshJobs);
            Assert.IsLessThanOrEqualTo(2 * VolumeRun.NativeBytes, peak.NativeBytes);
            var texturePeak = textures.ReadPeakUsage();
            Assert.IsLessThanOrEqualTo(textureLimits.Descriptors, texturePeak.Descriptors);
            Assert.IsLessThanOrEqualTo(textureLimits.StagingBytes, texturePeak.StagingBytes);
            Assert.IsLessThanOrEqualTo(textureLimits.NativeBytes, texturePeak.NativeBytes);
            await renderer.StopAsync(token);
            Assert.AreEqual(default, textures.ReadUsage());
            Assert.AreEqual(0L, renderer.ReadTextureDiagnostics().ResidentAndPendingBytes);
            Assert.AreEqual(0L, renderer.ReadTextureDiagnostics().StagingBytes);
            var diagnostics = renderer.ReadDiagnostics();
            if (measure)
                AssertAdapter(diagnostics.Adapter, warp);
            else
                AssertPlanetAdapter(diagnostics.Adapter, warp);
            AssertCleanDiagnostics(diagnostics, diagnosticsRequested);
            if (measure)
            {
                var resources = presented.Resources;
                Assert.IsFalse(resources.LedgerHasUnknownSizes);
                Assert.AreEqual(D3D12CustodyStatus.None, resources.Custody);
                Assert.IsGreaterThan(0L, producerArmedAt);
                Assert.IsGreaterThanOrEqualTo(producerArmedAt, copyStartedAt);
                Assert.IsGreaterThanOrEqualTo(copyStartedAt, copyExecutedAt);
                Assert.IsGreaterThanOrEqualTo(copyExecutedAt, copyCompletionObservedAt);
                Assert.IsGreaterThanOrEqualTo(copyExecutedAt, protectedRequestAt);
                Assert.IsGreaterThanOrEqualTo(protectedRequestAt, maskAdmissionObservedAt);
                Assert.IsGreaterThanOrEqualTo(maskAdmissionObservedAt, terrainWorkAdmissionObservedAt);
                Assert.IsGreaterThanOrEqualTo(terrainWorkAdmissionObservedAt, rendererStageAdmissionObservedAt);
                Assert.IsGreaterThanOrEqualTo(protectedRequestAt, maskPlanObservedAt);
                Assert.IsGreaterThanOrEqualTo(protectedRequestAt, nativeTerrainUploadAt);
                Assert.IsGreaterThanOrEqualTo(maskPlanObservedAt, publicationObservedAt);
                Assert.IsGreaterThanOrEqualTo(nativeTerrainUploadAt, publicationObservedAt);
                Assert.IsGreaterThanOrEqualTo(rendererStageAdmissionObservedAt, publicationObservedAt);
                Assert.IsGreaterThanOrEqualTo(publicationObservedAt, firstContentRequestAt);
                Assert.IsGreaterThanOrEqualTo(firstContentRequestAt, resources.DrawTimestamp);
                Assert.IsGreaterThanOrEqualTo(resources.DrawTimestamp, resources.PresentTimestamp);
                Assert.IsGreaterThanOrEqualTo(resources.PresentTimestamp, resources.RetiredPresentTimestamp);
                Assert.IsGreaterThanOrEqualTo(resources.RetiredPresentTimestamp, movementOpenedAt);
                Assert.IsGreaterThan(maximumCopyFence, resources.DrawFence);
                Assert.IsGreaterThan(resources.DrawFence, resources.PresentFence);
                Assert.AreEqual(resources.PresentFence, resources.RetiredPresentFence);
                Assert.IsGreaterThanOrEqualTo(resources.RetiredPresentFence, resources.CompletedFence);
                Assert.IsGreaterThan(0, appliedWidth);
                Assert.IsGreaterThan(0, appliedHeight);
                Assert.AreEqual(windowAtSetup.Revision, windowAtRetirement.Revision,
                    "The window changed before the first edited frame retired.");
                Assert.AreEqual(windowAtSetup.ClientWidth, appliedWidth);
                Assert.AreEqual(windowAtSetup.ClientHeight, appliedHeight);
                Console.WriteLine("TEX03-MIXED " + JsonSerializer.Serialize(new
                {
                    schema = 1,
                    fixture = "tex03-wp11-edited-volume-wp16-props-v1",
                    sample,
                    adapter = diagnostics.Adapter,
                    diag = new { requested = diagnosticsRequested, diagnostics.DebugLayerEnabled, diagnostics.DredEnabled,
                        resourceSnapshots = true, presentReadback = false },
                    qpcFrequency = Stopwatch.Frequency,
                    width = appliedWidth,
                    height = appliedHeight,
                    windowRevision = windowAtRetirement.Revision,
                    injectedHoldDuringMeasurement = false,
                    world = cut.World,
                    rendererEpoch = cut.View.RendererEpoch,
                    initial.Generation,
                    initial.TerrainRevision,
                    editedGeneration = edited.Info.Generation,
                    editedTerrainRevision = edited.Info.TerrainRevision,
                    editedPlan = edited.Info.PlanIdentity,
                    maskSequence,
                    firstContentSequence = editSequence,
                    acceptedEditPresents,
                    maximumCopy = new { assetId = maximumAsset.AssetId, payloadBytes = inFlight.UploadedBytes - uploadedBefore,
                        fence = maximumCopyFence, actual = inFlight, readyFirstUse = true, readyRefinement = true,
                        textureUsageAtCopy, propsAtCopy, terrainAtCopy = new
                        {
                            used = ResourceVector(terrainAtCopy.Used), peak = ResourceVector(terrainAtCopy.Peak),
                            terrainAtCopy.Generations, terrainAtCopy.Readers, terrainAtCopy.MovementHeld,
                        },
                        optionalAttemptsAtRequest, optionalAttemptsAtMaskAdmission },
                    firstRetiredPresent = new { resources.FrameSequence, resources.DrawFence, resources.PresentedSequence,
                        resources.PresentFence, resources.RetiredPresentSequence, resources.RetiredPresentFence,
                        resources.CompletedFence, presented.Props, allocations = Balances(resources) },
                    qpc = new { producerArmedAt, copyStartedAt, copyExecutedAt, copyCompletionObservedAt, protectedRequestAt,
                        maskAdmissionObservedAt, terrainWorkAdmissionObservedAt, rendererStageAdmissionObservedAt,
                        maskPlanObservedAt, nativeTerrainUploadAt, publicationObservedAt, firstContentRequestAt,
                        resources.DrawTimestamp, resources.PresentTimestamp, resources.RetiredPresentTimestamp, movementOpenedAt },
                    peaks = new { terrain = ResourceVector(peak), texture = texturePeak },
                }, JsonSerializerOptions.Web));
            }
        }
        finally
        {
            resumeFrame.Set();
            resumeCopy.Set();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            if (edit is { IsCompleted: false })
            {
                await timeout.CancelAsync();
                try { await edit; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }

        D3D12TextureView Cut(VolumePublicationInfo info) =>
            new(run.Terrain.WorldIdentity, info.View, info.TerrainRevision, info.Generation);

        async Task<VolumeViewIdentity> StartHealthyEdit()
        {
            await copyIssued.Task.WaitAsync(token);
            protectedRequestAt = Stopwatch.GetTimestamp();
            optionalAttemptsAtRequest = Volatile.Read(ref optionalAttempts);
            var editedView = await run.CutFloor(renderer, token, maskSequence: maskSequence,
                observeAdmission: ObserveAdmission);
            publicationObservedAt = Stopwatch.GetTimestamp();
            return editedView;
        }

        void ObserveAdmission(VolumeRun.EditAdmission admission)
        {
            var observedAt = Stopwatch.GetTimestamp();
            switch (admission)
            {
                case VolumeRun.EditAdmission.Mask:
                    maskAdmissionObservedAt = observedAt;
                    optionalAttemptsAtMaskAdmission = Volatile.Read(ref optionalAttempts);
                    break;
                case VolumeRun.EditAdmission.TerrainWork:
                    terrainWorkAdmissionObservedAt = observedAt;
                    break;
                case VolumeRun.EditAdmission.RendererStage:
                    rendererStageAdmissionObservedAt = observedAt;
                    break;
            }
        }

        static object[] Balances(D3D12ResourceSnapshot snapshot) =>
            Enum.GetValues<D3D12ResourceOwner>().Select(owner =>
                new { owner = owner.ToString(), balance = snapshot.GetBalance(owner) }).ToArray();

        static object ResourceVector(VolumeResources resources) => new
        {
            resources.CpuBytes, resources.NativeBytes, resources.UploadBytes, resources.RetirementBytes,
            resources.DensityJobs, resources.MeshJobs, resources.QueueSlots, resources.LeaseSlots, resources.RetirementSlots,
        };

        D3D12TextureTicket StageTexture(D3D12TextureView cut, D3D12TextureTarget target, CookedTexture asset,
            D3D12TextureReservation credit)
        {
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryRequest(cut, target, credit, out var ticket));
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(ticket, asset));
            return ticket;
        }

        PropCellRequest StageProp(PropCellKey cell)
        {
            Assert.AreEqual(PropCellResult.Accepted, props.TryRequest(cell, propCount, out var request));
            Assert.AreEqual(PropCellResult.Accepted, props.TryStage(request, new(32, 32, 3), instances));
            return request;
        }
    }

    private static D3D12TextureTarget PressureTarget(ushort material, CookedTexture texture) =>
        new(new(material, MaterialTextureSemantic.BaseColor), 1, new(MaterialTextureSemantic.BaseColor, texture.AssetId));

    private static CookedTexture CookPressureTexture(ushort width, ushort height, int mips)
    {
        var pixels = checked(width * height * 4);
        var source = new byte[18 + pixels];
        source[2] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(12), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(14), height);
        source[16] = 32;
        source[17] = 0x28;
        source.AsSpan(18).Fill(255);
        var decodedBytes = mips == 2 ? checked(pixels + pixels / 4) : pixels;
        Assert.AreEqual(TextureCookError.None, TextureCooker.CookTga32(source, new(TextureColorSpace.Srgb, TextureSemantic.Color, mips),
            new(source.Length, decodedBytes, decodedBytes + 256, 1024, 2), out var texture));
        Assert.IsNotNull(texture);
        return texture;
    }
}
