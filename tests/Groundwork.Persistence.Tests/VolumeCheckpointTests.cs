using System.Diagnostics;
using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Groundwork.Core;
using Groundwork.Core.Tests;
using Groundwork.Voxels;

namespace Groundwork.Persistence.Tests;

[TestClass]
public sealed class VolumeCheckpointTests
{
    private static readonly Guid Slot = new("c4997403-d04d-4404-8e95-bc092b9661c1");
    private static readonly Guid Lineage = new("7993563d-b0e5-477c-ae82-81f867ab8ca1");
    private static readonly PlanetDescriptor Planet = new(new Guid("7ab2e4df-06c5-4933-b82b-355ed45584bc"),
        new Guid("f5624df5-53da-4e55-b07d-b5554fc3dca6"), 17, 1, 1, 1, new string('a', 64), 100);

    [TestMethod]
    public void CodecPreservesEditsAndClassifiesTruncation()
    {
        var checkpoint = Create(0);
        var bytes = VolumeCheckpointCodec.Encode(checkpoint);
        Assert.AreEqual("46C065F9D12CCD5118BA046068F2A9E7B69595013EFE2627BD648ACA35DEAD50",
            Convert.ToHexString(SHA256.HashData(bytes)));
        var decoded = VolumeCheckpointCodec.Decode(bytes, Slot, Lineage, Planet);
        Assert.AreEqual(VolumeCheckpointStatus.Valid, decoded.Status);
        CollectionAssert.AreEqual(bytes, VolumeCheckpointCodec.Encode(decoded.Checkpoint!));
        Assert.AreEqual(VolumeCheckpointStatus.Incompatible,
            VolumeCheckpointCodec.Decode(bytes, Slot, Guid.NewGuid(), Planet).Status);
        for (var length = 0; length < bytes.Length; length++)
            Assert.AreEqual(length < VolumeCheckpointCodec.HeaderLength
                ? VolumeCheckpointStatus.Incompatible : VolumeCheckpointStatus.DamagedCompatible,
                VolumeCheckpointCodec.Decode(bytes.AsSpan(0, length), Slot, Lineage, Planet).Status);
        bytes[^1] ^= 1;
        Assert.AreEqual(VolumeCheckpointStatus.DamagedCompatible,
            VolumeCheckpointCodec.Decode(bytes, Slot, Lineage, Planet).Status);
    }

    [TestMethod]
    public void OverlayRejectsOverflowAndDuplicateKeys()
    {
        var checkpoint = Create(0);
        var overflow = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(overflow, int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(overflow.AsSpan(4), int.MaxValue);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            new VolumeCheckpoint(checkpoint.Operation, Lineage, Planet, 1, 1, overflow, []));
        var duplicate = new byte[72];
        BinaryPrimitives.WriteInt32LittleEndian(duplicate, 2);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            new VolumeCheckpoint(checkpoint.Operation, Lineage, Planet, 1, 1, duplicate, []));
    }

    [TestMethod]
    public void BoundedPayloadFuzzCannotBypassCanonicalDecode()
    {
        var random = new Random(117087);
        var reference = VolumeCheckpointCodec.Encode(Create(0));
        for (var trial = 0; trial < 1_024; trial++)
        {
            var bytes = (byte[])reference.Clone();
            for (var mutation = 0; mutation < 4; mutation++)
                bytes[random.Next(VolumeCheckpointCodec.HeaderLength, bytes.Length - 32)] ^= (byte)random.Next(1, 256);
            SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32), bytes.AsSpan(bytes.Length - 32));
            var result = VolumeCheckpointCodec.Decode(bytes, Slot, Lineage, Planet);
            if (result.Status == VolumeCheckpointStatus.Valid)
                CollectionAssert.AreEqual(bytes, VolumeCheckpointCodec.Encode(result.Checkpoint!));
            else
                Assert.AreEqual(VolumeCheckpointStatus.DamagedCompatible, result.Status);
        }
    }
    [TestMethod]
    public void NtfsRotatesReceiptsAndPreservesDamagedOrForeignSibling()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires Windows NTFS.");
        using var fixture = new Fixture();
        var slot = fixture.Slot;
        var first = Create(0);
        var second = Create(1);
        Assert.AreEqual(VolumeSlotStatus.Empty, slot.Load().Status);
        AssertStatus(VolumeSlotStatus.Committed, slot.Save(first));
        AssertStatus(VolumeSlotStatus.Committed, slot.Save(second));
        AssertStatus(VolumeSlotStatus.Committed, slot.Save(Create(2)));
        AssertStatus(VolumeSlotStatus.Stale, slot.Save(first));
        AssertStatus(VolumeSlotStatus.AlreadyCommitted, slot.Save(second));
        AssertStatus(VolumeSlotStatus.Conflict, slot.Save(Create(1, game: 42)));
        var committedA = File.ReadAllBytes(fixture.Member("volume.a"));

        var damaged = (byte[])committedA.Clone();
        damaged[^1] ^= 1;
        File.WriteAllBytes(fixture.Member("volume.a"), damaged);
        var recovery = fixture.Reopen().Load();
        AssertStatus(VolumeSlotStatus.ReadOnlyRecovery, recovery);
        Assert.AreEqual(2UL, recovery.Checkpoint!.Generation);
        AssertStatus(VolumeSlotStatus.ReadOnlyRecovery, slot.Save(second));
        AssertStatus(VolumeSlotStatus.ReadOnlyRecovery, slot.Save(Create(1, game: 42)));
        CollectionAssert.AreEqual(damaged, File.ReadAllBytes(fixture.Member("volume.a")));
        File.WriteAllBytes(fixture.Member("volume.a"), committedA);
        File.WriteAllBytes(fixture.Member("volume.b"), [1, 2, 3]);
        AssertStatus(VolumeSlotStatus.Incompatible, slot.Load());
        AssertStatus(VolumeSlotStatus.Incompatible, slot.Save(Create(3)));
        CollectionAssert.AreEqual(committedA, File.ReadAllBytes(fixture.Member("volume.a")));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.Member("volume.b")));

    }

    [TestMethod]
    public void WriterLeaseRejectsSecondWriterAndCanceledStagePreservesCut()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires Windows NTFS.");
        using var fixture = new Fixture();
        var slot = fixture.Slot;
        AssertStatus(VolumeSlotStatus.Committed, slot.Save(Create(0)));
        using var cancellation = new CancellationTokenSource();
        var result = slot.Save(Create(1), stage =>
        {
            if (stage != SaveWriteStage.AfterFlush)
                return;
            AssertStatus(VolumeSlotStatus.Busy, fixture.Reopen().Save(Create(1)));
            cancellation.Cancel();
        }, cancellation.Token);
        AssertStatus(VolumeSlotStatus.Canceled, result);
        Assert.AreEqual(1UL, fixture.Reopen().Load().Checkpoint!.Generation);
        AssertStatus(VolumeSlotStatus.Committed, fixture.Reopen().Save(Create(1)));
    }

    [TestMethod]
    public async Task NtfsChildProcessInterruptionPreservesTheDurableCut()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires Windows NTFS.");
        const string childVariable = "GROUNDWORK_D02_CHILD";
        var childRoot = Environment.GetEnvironmentVariable(childVariable);
        if (childRoot is not null)
        {
            var stage = Enum.Parse<SaveWriteStage>(Environment.GetEnvironmentVariable("GROUNDWORK_D02_STAGE")!);
            var slot = new VolumeCheckpointSlot(childRoot, Slot, Lineage, Planet, static _ => SaveGameValidation.Valid);
            var result = slot.Save(Create(1), reached =>
            {
                if (reached != stage)
                    return;
                File.WriteAllText(Path.Combine(childRoot, "ready.tmp"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                File.Move(Path.Combine(childRoot, "ready.tmp"), Path.Combine(childRoot, "ready"));
                using var parked = new ManualResetEventSlim();
                parked.Wait(TimeSpan.FromSeconds(40));
            }, default);
            Assert.Fail($"Child returned {result.Status} before termination.");
        }

        foreach (var stage in new[] { SaveWriteStage.PartialBody, SaveWriteStage.BeforeFlush,
            SaveWriteStage.AfterFlush, SaveWriteStage.BeforePublish, SaveWriteStage.PartialIntent, SaveWriteStage.AfterIntent, SaveWriteStage.AfterPublish, SaveWriteStage.BeforeAcknowledge })
        {
            using var fixture = new Fixture();
            AssertStatus(VolumeSlotStatus.Committed, fixture.Slot.Save(Create(0)));
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            start.Environment[childVariable] = fixture.Root;
            start.Environment["GROUNDWORK_D02_STAGE"] = stage.ToString();
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(VolumeCheckpointTests).Assembly.Location);
            start.ArgumentList.Add($"/TestCaseFilter:FullyQualifiedName={typeof(VolumeCheckpointTests).FullName}.{nameof(NtfsChildProcessInterruptionPreservesTheDurableCut)}");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start writer.");
            var ready = Path.Combine(fixture.Root, "ready");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try
            {
                while (!File.Exists(ready) && !process.HasExited)
                    await Task.Delay(20, timeout.Token);
                Assert.IsTrue(File.Exists(ready), $"Writer failed to reach {stage}.");
                using var writer = Process.GetProcessById(int.Parse(File.ReadAllText(ready), CultureInfo.InvariantCulture));
                AssertStatus(VolumeSlotStatus.Busy, fixture.Reopen().Load());
                writer.Kill();
                await writer.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(timeout.Token);
            }

            var published = stage is SaveWriteStage.AfterPublish or SaveWriteStage.BeforeAcknowledge;
            var loaded = await WaitForInterruptedWriter(fixture.Reopen(), timeout.Token);
            AssertStatus(VolumeSlotStatus.Loaded, loaded);
            CollectionAssert.AreEqual(VolumeCheckpointCodec.Encode(Create(published ? 1UL : 0UL)),
                VolumeCheckpointCodec.Encode(loaded.Checkpoint!));
            AssertStatus(published ? VolumeSlotStatus.AlreadyCommitted : VolumeSlotStatus.Committed,
                fixture.Reopen().Save(Create(1)));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(fixture.Member("staging")));
        }
    }

    [TestMethod]
    public async Task NtfsIntentCloseWaitRetriesSharingViolationsAndKeepsOtherFailuresVisible()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires Windows NTFS.");
        using var fixture = new Fixture();
        AssertStatus(VolumeSlotStatus.Committed, fixture.Slot.Save(Create(0)));
        var slot = fixture.Reopen();
        var intent = fixture.Member("volume.intent");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<VolumeSlotResult> recovered;
        using (File.Open(intent, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = slot.Load();
            AssertStatus(VolumeSlotStatus.Indeterminate, blocked);
            Assert.AreEqual("volume.intent", blocked.Member);
            Assert.IsInstanceOfType<IOException>(blocked.Error);
            Assert.AreEqual(32, blocked.Error.HResult & 0xffff);

            using var canceled = new CancellationTokenSource();
            var pending = WaitForInterruptedWriter(slot, canceled.Token);
            Assert.IsFalse(pending.IsCompleted);
            canceled.Cancel();
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => pending);
            recovered = WaitForInterruptedWriter(slot, timeout.Token);
            Assert.IsFalse(recovered.IsCompleted);
        }
        var loaded = await recovered;
        AssertStatus(VolumeSlotStatus.Loaded, loaded);
        CollectionAssert.AreEqual(VolumeCheckpointCodec.Encode(Create(0)),
            VolumeCheckpointCodec.Encode(loaded.Checkpoint!));

        File.WriteAllBytes(intent, [1]);
        var malformed = await WaitForInterruptedWriter(slot, timeout.Token);
        AssertStatus(VolumeSlotStatus.Indeterminate, malformed);
        Assert.AreEqual("volume.intent", malformed.Member);
        Assert.IsNull(malformed.Error);
        CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(intent));
    }

    private static async Task<VolumeSlotResult> WaitForInterruptedWriter(VolumeCheckpointSlot slot, CancellationToken token)
    {
        // Windows can signal process exit before closing both the slot lease and the renamed intent handle.
        while (true)
        {
            var result = slot.Load();
            var intentLocked = result is { Status: VolumeSlotStatus.Indeterminate, Member: "volume.intent", Error: IOException error } &&
                (error.HResult & 0xffff) is 32 or 33;
            if (result.Status != VolumeSlotStatus.Busy && !intentLocked)
                return result;
            try
            {
                await Task.Delay(20, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw new TimeoutException($"NTFS writer handles did not close. Last result: {result.Status}, member: {result.Member}.",
                    result.Error);
            }
        }
    }

    [TestMethod]
    public void IdenticalReplacementBeforePublicationDoesNotAcquireWriteAuthority()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires Windows NTFS.");
        using var fixture = new Fixture();
        AssertStatus(VolumeSlotStatus.Committed, fixture.Slot.Save(Create(0)));
        AssertStatus(VolumeSlotStatus.Committed, fixture.Slot.Save(Create(1)));
        var target = fixture.Member("volume.a");
        var bytes = File.ReadAllBytes(target);
        AssertStatus(VolumeSlotStatus.Conflict, fixture.Slot.Save(Create(2), stage =>
        {
            if (stage != SaveWriteStage.BeforePublish)
                return;
            File.Move(target, target + ".old");
            File.WriteAllBytes(target, bytes);
        }, default));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));
    }
    [TestMethod]
    public void BothMembersAreClassifiedBeforeSelectionInEitherGenerationOrder()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires Windows NTFS.");
        foreach (var oddNewer in new[] { false, true })
        foreach (var a in Enum.GetValues<MemberCase>())
        foreach (var b in Enum.GetValues<MemberCase>())
        {
            using var fixture = new Fixture();
            var aPath = fixture.Member("volume.a");
            var bPath = fixture.Member("volume.b");
            var aBytes = Write(aPath, a, oddNewer ? 2UL : 0UL);
            var bBytes = Write(bPath, b, 1);
            using (var aLock = a == MemberCase.Access ? File.Open(aPath, FileMode.Open, FileAccess.Read, FileShare.None) : null)
            using (var bLock = b == MemberCase.Access ? File.Open(bPath, FileMode.Open, FileAccess.Read, FileShare.None) : null)
            {
                var expected = Expected(a, b, oddNewer);
                Assert.AreEqual(expected, fixture.Reopen().Load().Status, $"{oddNewer}: {a}/{b}");
                if (expected is not (VolumeSlotStatus.Empty or VolumeSlotStatus.Loaded))
                    Assert.AreEqual(expected, fixture.Reopen().Save(Create(3)).Status, $"{oddNewer}: {a}/{b}");
            }
            Verify(aPath, aBytes);
            Verify(bPath, bBytes);
        }

        static byte[]? Write(string path, MemberCase kind, ulong basis)
        {
            if (kind == MemberCase.Missing)
                return null;
            var frame = VolumeCheckpointCodec.Encode(Create(basis));
            switch (kind)
            {
                case MemberCase.Damaged:
                    frame[^1] ^= 1;
                    break;
                case MemberCase.Foreign:
                    frame = VolumeCheckpointCodec.Encode(new VolumeCheckpoint(Create(basis).Operation,
                        new Guid("2ea59529-dd9e-4d82-96e7-364430cfe420"), Planet, 21 + basis, 4, new byte[8], []));
                    break;
                case MemberCase.Short:
                    frame = frame[..20];
                    break;
                case MemberCase.Oversize:
                    frame = new byte[VolumeCheckpointCodec.MaximumBytes + 1];
                    break;
            }
            File.WriteAllBytes(path, frame);
            return frame;
        }

        static VolumeSlotStatus Expected(MemberCase a, MemberCase b, bool oddNewer)
        {
            if (a == MemberCase.Access || b == MemberCase.Access)
                return VolumeSlotStatus.StorageError;
            if (a >= MemberCase.Foreign || b >= MemberCase.Foreign)
                return VolumeSlotStatus.Incompatible;
            if (a == MemberCase.Damaged || b == MemberCase.Damaged)
                return a == MemberCase.Valid || b == MemberCase.Valid ? VolumeSlotStatus.ReadOnlyRecovery : VolumeSlotStatus.Corrupt;
            if (a == MemberCase.Missing && b == MemberCase.Missing)
                return VolumeSlotStatus.Empty;
            return a == MemberCase.Valid && (b == MemberCase.Valid || !oddNewer)
                ? VolumeSlotStatus.Loaded : VolumeSlotStatus.Incompatible;
        }

        static void Verify(string path, byte[]? bytes)
        {
            if (bytes is null)
                Assert.IsFalse(File.Exists(path));
            else
                CollectionAssert.AreEqual(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(path)));
        }
    }

    [TestMethod]
    public void PinnedAncestorCannotBeRenamedDuringPublication()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires Windows NTFS.");
        using var fixture = new Fixture();
        AssertStatus(VolumeSlotStatus.Committed, fixture.Slot.Save(Create(0)));
        AssertStatus(VolumeSlotStatus.Committed, fixture.Slot.Save(Create(1), stage =>
        {
            if (stage == SaveWriteStage.BeforePublish)
                Assert.ThrowsExactly<IOException>(() => Directory.Move(fixture.Root, fixture.Root + "-substituted"));
        }, default));
    }

    [TestMethod]
    public void JunctionInsertedAfterClassificationIsPreservedAndNeverFollowed()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires Windows NTFS.");
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var payload = Path.Combine(outside, "keep.txt");
        File.WriteAllText(payload, "untouched");
        var target = fixture.Member("volume.b");
        AssertStatus(VolumeSlotStatus.Committed, fixture.Slot.Save(Create(0)));
        try
        {
            AssertStatus(VolumeSlotStatus.Conflict, fixture.Slot.Save(Create(1), stage =>
            {
                if (stage != SaveWriteStage.AfterIntent)
                    return;
                using var junction = Process.Start(new ProcessStartInfo("cmd.exe",
                    string.Join(' ', "/d /c mklink /J", '"' + target + '"', '"' + outside + '"'))
                    { UseShellExecute = false, CreateNoWindow = true });
                Assert.IsNotNull(junction);
                Assert.IsTrue(junction.WaitForExit(5000));
                Assert.AreEqual(0, junction.ExitCode);
            }, default));
            AssertStatus(VolumeSlotStatus.Incompatible, fixture.Slot.Load());
            Assert.AreEqual("untouched", File.ReadAllText(payload));
            Assert.IsTrue(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(target))
                Directory.Delete(target);
        }
    }
    [TestMethod]
    public async Task WriterOwnsOneCutInOrderAndPreservesTheFinalizedCutAcrossStop()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires Windows NTFS.");
        var throwValidation = false;
        using var fixture = new Fixture(_ => throwValidation ? throw new InvalidOperationException("validation fault") : SaveGameValidation.Valid);
        AssertStatus(VolumeSlotStatus.Committed, fixture.Slot.Save(new VolumeCheckpoint(Create(0).Operation,
            Lineage, Planet, 21, 1, new byte[8], [])));
        using var runtime = new CoreRuntimeFixture(new CaptureClock());
        var world = runtime.OpenWorld(completedTick: 21);
        var source = new FixtureDensity();
        var selection = VolumeStreamingSelection.Select(Planet, default, new(-1, 0, 0));
        const int meshBytes = 8_000_000;
        const int maximumOverlay = 256;
        var credits = new VolumeResources(VolumeStreamingCoordinator.RequiredCpuBytes(selection, meshBytes) +
            maximumOverlay * 64 + 4096, 0, 0, 0, 1, 1, 1, 64, 1);
        var baseline = credits.Retained with { LeaseSlots = credits.Retained.LeaseSlots + 8 };
        var limits = new VolumeStreamingLimits(baseline + credits + credits + credits + credits,
            baseline, credits, credits, credits, credits, 12, 8, 256);
        using var terrain = new VolumeStreamingCoordinator(world, source, limits, maximumOverlay);
        Assert.IsTrue(terrain.ConfirmMask(terrain.View));
        Assert.AreEqual(VolumeAdmission.Accepted, terrain.TryStage(VolumeWorkClass.Movement, selection, 0, 1,
            meshBytes, credits, source, out var work));
        Assert.IsNotNull(work);

        await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 22, BarrierKind.BeginTick);
        using var firstReservation = terrain.TryReserveCheckpoint(22);
        Assert.IsNotNull(firstReservation);
        using (work)
        {
            Assert.IsTrue(work.Execute());
            var candidates = await CoreRuntimeFixture.SealAsync(world, 22, BarrierKind.CommitResults);
            var prepared = terrain.TryPreparePublication(work.Generation, candidates, []);
            Assert.IsNotNull(prepared);
            var committed = world.Publication.Finalization.TryFinalize(prepared);
            Assert.AreEqual(BarrierStatus.Committed, committed.Status);
            CoreRuntimeFixture.PublishObserver(world, committed.Stamp!.Value, 1);
        }
        using var older = firstReservation.TryTake();
        Assert.IsNotNull(older);

        await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 23, BarrierKind.BeginTick);
        using var secondReservation = terrain.TryReserveCheckpoint(23);
        Assert.IsNotNull(secondReservation);
        var second = await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 23, BarrierKind.CommitResults);
        CoreRuntimeFixture.PublishObserver(world, second, 2);

        await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 24, BarrierKind.BeginTick);
        using var thirdReservation = terrain.TryReserveCheckpoint(24);
        Assert.IsNull(thirdReservation, "An untaken cut still occupies the reservation.");
        terrain.Stop();
        using var newer = secondReservation.TryTake();
        Assert.IsNotNull(newer);
        Assert.AreEqual(23UL, newer.Value.Publication.Stamp.Tick);

        using var writer = fixture.Slot.OpenWriter(out var opened);
        AssertStatus(VolumeSlotStatus.Loaded, opened);
        Assert.IsNotNull(writer);
        using var secondWriter = fixture.Reopen().OpenWriter(out var busy);
        Assert.IsNull(secondWriter);
        AssertStatus(VolumeSlotStatus.Busy, busy);
        Assert.AreEqual(VolumeCaptureAdmission.Accepted, writer.TryAdmit(newer.Value, 1, (_, game) =>
        {
            AssertStatus(VolumeSlotStatus.Busy, writer.ExecutePending());
            game.Span[0] = 42;
            return 1;
        }));
        Assert.AreEqual(VolumeCaptureAdmission.Stale, writer.TryAdmit(older.Value, 1, static (_, _) => 0));
        var marker = fixture.Member("volume.intent");
        File.WriteAllBytes(marker, [1]);
        AssertStatus(VolumeSlotStatus.Indeterminate, writer.ExecutePending());
        throwValidation = true;
        Assert.ThrowsExactly<InvalidOperationException>(() => writer.ExecutePending());
        throwValidation = false;
        File.Delete(marker);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        AssertStatus(VolumeSlotStatus.Committed, writer.ExecutePending(cancellation.Token));
        var loaded = fixture.Reopen().Load();
        AssertStatus(VolumeSlotStatus.Loaded, loaded);
        Assert.AreEqual(23UL, loaded.Checkpoint!.Tick);
        CollectionAssert.AreEqual(new byte[] { 42 }, loaded.Checkpoint.Game.ToArray());
    }

    [TestMethod]
    public void ExplicitRecoveryCopiesTheCutToANewLineageAndCanRetry()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires Windows NTFS.");
        using var fixture = new Fixture();
        AssertStatus(VolumeSlotStatus.Committed, fixture.Slot.Save(Create(0)));
        AssertStatus(VolumeSlotStatus.Committed, fixture.Slot.Save(Create(1)));
        var damaged = File.ReadAllBytes(fixture.Member("volume.b"));
        damaged[^1] ^= 1;
        File.WriteAllBytes(fixture.Member("volume.b"), damaged);
        var recovered = fixture.Reopen().Load();
        AssertStatus(VolumeSlotStatus.ReadOnlyRecovery, recovered);
        var newLineage = Guid.NewGuid();
        var target = VolumeCheckpointSlot.Reserve(fixture.Root, Guid.NewGuid(), newLineage, Planet,
            static _ => SaveGameValidation.Valid);
        var nonce = Guid.NewGuid();
        var copied = target.CopyRecoveredCut(recovered, nonce);
        AssertStatus(VolumeSlotStatus.Committed, copied);
        Assert.AreEqual(newLineage, copied.Checkpoint!.Lineage);
        Assert.AreEqual(recovered.Checkpoint!.Player, copied.Checkpoint.Player);
        CollectionAssert.AreEqual(recovered.Checkpoint.Overlay.ToArray(), copied.Checkpoint.Overlay.ToArray());
        AssertStatus(VolumeSlotStatus.AlreadyCommitted, target.CopyRecoveredCut(recovered, nonce));
        AssertStatus(VolumeSlotStatus.Stale, target.CopyRecoveredCut(recovered, Guid.NewGuid()));
        CollectionAssert.AreEqual(damaged, File.ReadAllBytes(fixture.Member("volume.b")));
    }
    [TestMethod]
    public async Task DecodedDurableCheckpointRebuildsTheEditedCollisionAndRestoredPose()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires Windows NTFS.");
        using var fixture = new Fixture();
        var overlay = new byte[8 + 27 * 32];
        BinaryPrimitives.WriteInt32LittleEndian(overlay, 27);
        var offset = 8;
        for (var x = -1; x <= 1; x++)
        for (var y = -1; y <= 1; y++)
        for (var z = -1; z <= 1; z++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(overlay.AsSpan(offset), x);
            BinaryPrimitives.WriteInt64LittleEndian(overlay.AsSpan(offset + 8), y);
            BinaryPrimitives.WriteInt64LittleEndian(overlay.AsSpan(offset + 16), z);
            BinaryPrimitives.WriteInt64LittleEndian(overlay.AsSpan(offset + 24), 128);
            offset += 32;
        }
        var falling = new PlanetWalkState(new(0, 0, 12), new(1, 0, 0), new(0, 0, -2));
        var sample = Create(0);
        AssertStatus(VolumeSlotStatus.Committed, fixture.Slot.Save(new VolumeCheckpoint(sample.Operation,
            Lineage, Planet, 21, 4, overlay, [9], player: falling)));
        var durable = fixture.Reopen().Load().Checkpoint;
        Assert.IsNotNull(durable);

        using var runtime = new CoreRuntimeFixture(new CaptureClock());
        var world = runtime.OpenWorld(completedTick: durable.Tick);
        var source = new FixtureDensity();
        var selection = VolumeStreamingSelection.Select(Planet, default, new(-1, 0, 0));
        const int meshBytes = 8_000_000;
        const int maximumOverlay = 256;
        var credits = new VolumeResources(VolumeStreamingCoordinator.RequiredCpuBytes(selection, meshBytes) +
            maximumOverlay * 64 + 4096, 0, 0, 0, 1, 1, 1, 64, 1);
        var baseline = credits.Retained with
        {
            CpuBytes = credits.CpuBytes + 27 * 32,
            LeaseSlots = credits.LeaseSlots + 8,
        };
        var limits = new VolumeStreamingLimits(baseline + credits + credits + credits + credits,
            baseline, credits, credits, credits, credits, 12, 8, 256);
        var foreignPlanet = new PlanetDescriptor(Planet.WorldId, Planet.PlanetId, Planet.Seed + 1,
            Planet.GeneratorVersion, Planet.TerrainSchemaVersion, Planet.MaterialCatalogVersion,
            Planet.ParameterSha256, Planet.RadiusMeters, Planet.CellSizeMeters);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            durable.RestoreTerrain(world, new FixtureDensity(foreignPlanet), limits, maximumOverlay, static material => material == 3));
        using var terrain = durable.RestoreTerrain(world, source, limits, maximumOverlay, static material => material == 3);
        Assert.IsTrue(terrain.ConfirmMask(terrain.View));
        Assert.AreEqual(VolumeAdmission.Accepted, terrain.TryStage(VolumeWorkClass.Movement, selection, 0,
            durable.TerrainRevision, meshBytes, credits, source, out var work));
        Assert.IsNotNull(work);
        using (work)
        {
            Assert.IsTrue(work.Execute());
            await CoreRuntimeFixture.AdvanceEmptyBarrierAsync(world, 22, BarrierKind.BeginTick);
            var candidates = await CoreRuntimeFixture.SealAsync(world, 22, BarrierKind.CommitResults);
            var prepared = terrain.TryPreparePublication(work.Generation, candidates, []);
            Assert.IsNotNull(prepared);
            var committed = world.Publication.Finalization.TryFinalize(prepared);
            Assert.AreEqual(BarrierStatus.Committed, committed.Status);
            var root = (VolumeWorldRoot)world.Publication.Committed.Read().Root;
            Assert.AreEqual(falling, root.Player);
            Assert.AreEqual(4UL, root.Terrain.TerrainRevision);
        }
        Assert.IsTrue(terrain.Statistics.MovementHeld);
        using var lease = terrain.TryRead();
        Assert.IsNotNull(lease);
        Assert.AreEqual(2, lease.Raycast(new(0, 0, -5), new(0, 0, 5), new VolumeRayHit[8]).Count);
    }
    private sealed class CaptureClock : CoreRuntimeFixture.TestClock { }

    private sealed class FixtureDensity(PlanetDescriptor? descriptor = null) : IPlanetBrickSource
    {
        public PlanetDescriptor Descriptor => descriptor ?? Planet;
        public long SampleDensity(LatticeCoordinate vertex) => (vertex.Z - 8) * 256;
        public ushort SampleMaterial(LatticeCoordinate cell) => 3;
    }
    private enum MemberCase { Missing, Valid, Damaged, Access, Foreign, Short, Oversize }
    private static void AssertStatus(VolumeSlotStatus expected, VolumeSlotResult result) =>
        Assert.AreEqual(expected, result.Status, result.Error?.ToString() ?? result.Member);

    private static VolumeCheckpoint Create(ulong generation, byte game = 7)
    {
        var nonce = new Guid((int)generation + 1, 0, 0, new byte[8]);
        var overlay = new byte[72];
        BinaryPrimitives.WriteInt32LittleEndian(overlay, 1);
        BinaryPrimitives.WriteInt32LittleEndian(overlay.AsSpan(4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(overlay.AsSpan(8), 99);
        BinaryPrimitives.WriteInt64LittleEndian(overlay.AsSpan(32), -128);
        BinaryPrimitives.WriteInt64LittleEndian(overlay.AsSpan(40), 99);
        BinaryPrimitives.WriteInt64LittleEndian(overlay.AsSpan(64), 3);
        return new(new(Slot, generation, nonce), Lineage, Planet, 21 + generation, 4, overlay, [game], player: new(new(0, 0, 107), new(1, 0, 0), new(0, 0, -2)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("groundwork-volume-slot-");
        private readonly string _root;
        private readonly Func<VolumeCheckpoint, SaveGameValidation> _validate;
        public Fixture(Func<VolumeCheckpoint, SaveGameValidation>? validate = null)
        {
            _root = _temp.CreateSubdirectory("v2").FullName;
            _validate = validate ?? Validate;
            Slot = VolumeCheckpointSlot.Reserve(_root, VolumeCheckpointTests.Slot, Lineage, Planet, _validate);
        }
        public string Root => _root;
        public VolumeCheckpointSlot Slot { get; }
        public string Member(string name) => Path.Combine(_root, VolumeCheckpointTests.Slot.ToString("N"), name);
        public VolumeCheckpointSlot Reopen() => new(_root, VolumeCheckpointTests.Slot, Lineage, Planet, _validate);
        public void Dispose() => _temp.Delete(recursive: true);
        private static SaveGameValidation Validate(VolumeCheckpoint _) => SaveGameValidation.Valid;
    }
}
