using System.Numerics;
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
    public async Task ProtectedEditedGenerationPresentsBetweenOptionalVisualQuanta(bool warp)
    {
        const int copyLimit = GpuDevice.MaxTextureCopyBytes;
        const int propCount = 32_768;
        const ulong maskSequence = 5;
        const ulong editSequence = 6;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        using var run = new VolumeRun(editable: true, openFloor: true);
        using var resumeFrame = new ManualResetEventSlim();
        using var resumeCopy = new ManualResetEventSlim();
        var frameHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copyHeld = new TaskCompletionSource<GpuTextureStatistics>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        var holdCopy = false;
        var optionalAttempts = 0;
        var terrainUploads = 0;
        var view = run.Terrain.View;
        var camera = VolumeCameraAt(view, 32, 4);
        Assert.IsTrue(D3D12Renderer.TryCreateVolumeFrame(camera, 4, out var pressureFrame));
        Assert.IsTrue(pressureFrame.Scene.HasValue);
        var scene = pressureFrame.Scene.Value;
        VolumePublicationInfo initial = default;
        D3D12Renderer? active = null;
        var hooks = new GpuRetirementTestHooks(
            SignalFailure: (gpu, _) =>
            {
                if (!holdCopy || !gpu.HasTextureCopy)
                    return null;
                holdCopy = false;
                Assert.IsTrue(gpu.HasReadyPropUploads(scene, initial));
                copyHeld.TrySetResult(gpu.TextureStatistics);
                resumeCopy.Wait(token);
                return null;
            },
            BeforeVolumeUpload: _ => Interlocked.Increment(ref terrainUploads));
        await using var renderer = new D3D12Renderer(window, runtime.RuntimeOwnership, run.Terrain, 1,
            QualificationDiagnostics with { EnableResourceSnapshots = true }, warp, TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(15), hooks)
        {
            BeforeTextureUploadForTests = (ticket, _) =>
            {
                Interlocked.Increment(ref optionalAttempts);
                holdCopy = ticket == maximumTicket;
            },
            VolumePresentReadbackForTests = (sequence, generation, _) =>
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

            var waitingProp = StageProp(cells[1]);
            Assert.AreEqual(PropCellResult.Quota, props.TryRequest(cells[0], propCount, out _));
            Assert.AreEqual(D3D12TextureResult.Accepted,
                textures.TryRequestTier(cut, first, policy, 0, smallCredit, smallCredit, out var refine));
            Assert.AreEqual(D3D12TextureResult.Accepted, textures.TryComplete(refine, coarseAsset));
            maximumTicket = StageTexture(cut, largest, maximumAsset, maximumCredit);
            StageTexture(cut, next, coarseAsset, smallCredit);
            Assert.AreEqual(textureLimits.Descriptors, textures.ReadUsage().Descriptors);
            Assert.AreEqual(RenderSubmissionResult.Accepted, renderer.TrySubmitVolumeCamera(initial.Generation, 4, camera));
            resumeFrame.Set();
            await AwaitStatus(renderer, _ => copyHeld.Task.IsCompleted, token);
            var inFlight = await copyHeld.Task.WaitAsync(token);
            Assert.AreEqual(copyLimit, inFlight.StagingBytes);
            Assert.AreEqual(uploadedBefore + copyLimit, inFlight.UploadedBytes);
            Assert.IsTrue(textures.HasReady(cut, firstUse: true));
            Assert.IsTrue(textures.HasReady(cut, firstUse: false));
            lock (props.Gate)
                Assert.AreEqual(PropCellResult.Accepted, props.Admission.ReadStagedLength(waitingProp, out _, out _));
            Assert.AreEqual(2, Volatile.Read(ref optionalAttempts));
            Assert.AreEqual(terrainBefore, run.Terrain.Statistics.Peak);
            Assert.IsFalse(run.Terrain.TryReadEvent(out _));

            // Accept the edit mask while the largest texture copy has executed and
            // its retirement signal is held. The protected CPU generation can
            // take the first-edit envelope before the native queue is released.
            edit = Task.Run(() => run.CutFloor(renderer, token, maskSequence: maskSequence), token);
            await AwaitStatus(renderer, state => state.LastAcceptedSequence == maskSequence &&
                run.Terrain.Statistics.Used.QueueSlots == 1, token);
            Assert.AreEqual(3UL, renderer.Read().LastPresentedSequence);
            Assert.AreEqual(1, Volatile.Read(ref terrainUploads));
            Assert.IsTrue(run.Terrain.Statistics.MovementHeld);
            Assert.IsFalse(edit.IsCompleted);
            resumeCopy.Set();
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
            Assert.AreEqual(2, Volatile.Read(ref optionalAttempts));
            var editedCut = Cut(edited.Info);
            Assert.IsTrue(textures.Publish(editedCut, 1, [first]));
            props.Publish(new(editedCut.World, editedCut.View.RendererEpoch, editedCut.TerrainRevision,
                editedCut.CoverageGeneration), cells, new HashSet<PropCellKey>(cells));
            Assert.IsFalse(props.TryCancel(waitingProp));
            StageProp(cells[0]);
            StageTexture(editedCut, first, coarseAsset, smallCredit);
            Assert.AreEqual(RenderSubmissionResult.InvalidFrame,
                renderer.TrySubmitVolumeCamera(initial.Generation, editSequence, camera));
            Assert.AreEqual(MeshReplacementSubmissionResult.Accepted,
                renderer.TrySubmitVolumeTerrain(edited.Info.Generation, editSequence, VolumeCameraAt(editedView, 32, 4)));

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
            await run.OpenMovement();
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
            AssertPlanetAdapter(diagnostics.Adapter, warp);
            AssertCleanDiagnostics(diagnostics, diagnosticsRequested: true);
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
