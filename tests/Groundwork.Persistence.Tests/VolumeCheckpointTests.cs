using System.Diagnostics;
using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
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
            SaveWriteStage.AfterFlush, SaveWriteStage.BeforePublish, SaveWriteStage.AfterPublish, SaveWriteStage.BeforeAcknowledge })
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
            var loaded = fixture.Reopen().Load();
            AssertStatus(VolumeSlotStatus.Loaded, loaded);
            CollectionAssert.AreEqual(VolumeCheckpointCodec.Encode(Create(published ? 1UL : 0UL)),
                VolumeCheckpointCodec.Encode(loaded.Checkpoint!));
            AssertStatus(published ? VolumeSlotStatus.AlreadyCommitted : VolumeSlotStatus.Committed,
                fixture.Reopen().Save(Create(1)));
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
        return new(new(Slot, generation, nonce), Lineage, Planet, 21 + generation, 4, overlay, [game]);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("groundwork-volume-slot-");
        private readonly string _root;
        public Fixture()
        {
            _root = _temp.CreateSubdirectory("v2").FullName;
            Slot = VolumeCheckpointSlot.Reserve(_root, VolumeCheckpointTests.Slot, Lineage, Planet, Validate);
        }
        public string Root => _root;
        public VolumeCheckpointSlot Slot { get; }
        public string Member(string name) => Path.Combine(_root, VolumeCheckpointTests.Slot.ToString("N"), name);
        public VolumeCheckpointSlot Reopen() => new(_root, VolumeCheckpointTests.Slot, Lineage, Planet, Validate);
        public void Dispose() => _temp.Delete(recursive: true);
        private static SaveGameValidation Validate(VolumeCheckpoint _) => SaveGameValidation.Valid;
    }
}
