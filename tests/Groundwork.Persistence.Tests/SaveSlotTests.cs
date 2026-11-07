using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Groundwork.Voxels;

namespace Groundwork.Persistence.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class SaveSlotTests
{
    private const string FirstFile = "save.a";
    private const string SecondFile = "save.b";
    private const string CandidateFile = "save.pending";
    private const int DigestSize = 32;
    private static readonly Guid Lineage = new("82f61068-96e1-49e7-8fa7-563da90664d2");
    private static readonly SaveWorldRecipe Recipe = new(1, 1, 0x57505535, new RegionBounds(-4, 4, -2, 2));

    [TestMethod]
    public void AlternatesCommittedFilesAndIgnoresAPrivateCandidate()
    {
        using var files = new SlotDirectory();
        var slot = files.Open();
        var first = Checkpoint(1, 1);
        var second = Checkpoint(2, 2);
        var third = Checkpoint(3, 3);
        var candidate = Checkpoint(4, 4);

        files.Put(CandidateFile, candidate);
        Assert.AreEqual(SaveSlotStatus.Empty, slot.Load().Status);

        AssertCheckpoint(first, SaveSlotStatus.Committed, slot.Save(0, first));
        files.AssertFrame(FirstFile, first);
        Assert.IsFalse(File.Exists(files.PathOf(SecondFile)));

        AssertCheckpoint(second, SaveSlotStatus.Committed, slot.Save(1, second));
        files.AssertFrame(FirstFile, first);
        files.AssertFrame(SecondFile, second);

        AssertCheckpoint(third, SaveSlotStatus.Committed, slot.Save(2, third));
        files.AssertFrame(FirstFile, third);
        files.AssertFrame(SecondFile, second);

        files.Put(CandidateFile, candidate);
        AssertCheckpoint(third, SaveSlotStatus.Loaded, files.Open().Load());
        files.AssertFrame(CandidateFile, candidate);
    }

    [TestMethod]
    [DataRow(nameof(SaveWriteStage.BeforeWrite))]
    [DataRow(nameof(SaveWriteStage.PartialHeader))]
    [DataRow(nameof(SaveWriteStage.PartialBody))]
    [DataRow(nameof(SaveWriteStage.PartialFooter))]
    [DataRow(nameof(SaveWriteStage.BeforeFlush))]
    [DataRow(nameof(SaveWriteStage.AfterFlush))]
    [DataRow(nameof(SaveWriteStage.BeforePublish))]
    public void WriteFailureBeforePublicationKeepsBothCommittedGenerations(string stageName)
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1);
        var second = Checkpoint(2, 2);
        var third = Checkpoint(3, 3);
        files.Put(FirstFile, first);
        files.Put(SecondFile, second);
        var failure = Enum.Parse<SaveWriteStage>(stageName);
        var reached = false;

        var result = files.Open().Save(2, third, stage =>
        {
            if (stage == failure)
            {
                reached = true;
                throw new IOException("Injected interruption before publication.");
            }
        });

        Assert.IsTrue(reached);
        Assert.AreEqual(SaveSlotStatus.StorageError, result.Status);
        files.AssertFrame(FirstFile, first);
        files.AssertFrame(SecondFile, second);
        AssertCheckpoint(second, SaveSlotStatus.Loaded, files.Open().Load());
    }

    [TestMethod]
    [DataRow(nameof(SaveWriteStage.AfterPublish))]
    [DataRow(nameof(SaveWriteStage.BeforeAcknowledge))]
    public void WriteFailureAfterPublicationResolvesTheCommitAndRetryIsIdempotent(string stageName)
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1);
        var second = Checkpoint(2, 2);
        files.Put(FirstFile, first);
        var failure = Enum.Parse<SaveWriteStage>(stageName);
        var reached = false;

        var result = files.Open().Save(1, second, stage =>
        {
            if (stage == failure)
            {
                reached = true;
                throw new IOException("Injected loss of acknowledgement.");
            }
        });

        Assert.IsTrue(reached);
        AssertCheckpoint(second, SaveSlotStatus.Committed, result);
        AssertCheckpoint(second, SaveSlotStatus.AlreadyCommitted, files.Open().Save(1, second));
        files.AssertFrame(FirstFile, first);
        files.AssertFrame(SecondFile, second);
    }

    [TestMethod]
    public void UnreadableFileAfterPublicationReportsAnIndeterminateOutcome()
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1);
        var second = Checkpoint(2, 2);
        files.Put(FirstFile, first);
        FileStream? blocked = null;
        try
        {
            var result = files.Open().Save(1, second, stage =>
            {
                if (stage == SaveWriteStage.AfterPublish)
                {
                    blocked = File.Open(files.PathOf(SecondFile), FileMode.Open, FileAccess.Read, FileShare.None);
                    throw new IOException("Injected loss of acknowledgement with blocked read access.");
                }
            });

            Assert.IsNotNull(blocked);
            Assert.AreEqual(SaveSlotStatus.Indeterminate, result.Status);
        }
        finally
        {
            blocked?.Dispose();
        }

        AssertCheckpoint(second, SaveSlotStatus.Loaded, files.Open().Load());
        AssertCheckpoint(second, SaveSlotStatus.AlreadyCommitted, files.Open().Save(1, second));
    }

    [TestMethod]
    public void CancellationCanAbandonStagingAndLetsAnAdmittedPublicationSettle()
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1);
        var second = Checkpoint(2, 2);
        files.Put(FirstFile, first);
        var slot = files.Open();

        using (var early = new CancellationTokenSource())
        {
            var result = slot.Save(1, second, stage =>
            {
                if (stage == SaveWriteStage.BeforePublish)
                    early.Cancel();
            }, early.Token);
            Assert.IsTrue(early.IsCancellationRequested);
            Assert.AreEqual(SaveSlotStatus.Canceled, result.Status);
        }

        files.AssertFrame(FirstFile, first);
        Assert.IsFalse(File.Exists(files.PathOf(SecondFile)));

        using var late = new CancellationTokenSource();
        var committed = slot.Save(1, second, stage =>
        {
            if (stage == SaveWriteStage.AfterPublish)
                late.Cancel();
        }, late.Token);
        Assert.IsTrue(late.IsCancellationRequested);
        AssertCheckpoint(second, SaveSlotStatus.Committed, committed);
        files.AssertFrame(SecondFile, second);
    }

    [TestMethod]
    public void CorruptNewestFallsBackWithEvidenceAndTheNextSaveRepairsOnlyTheInactiveFile()
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1);
        var second = Checkpoint(2, 2);
        files.Put(FirstFile, first);
        var damaged = Frame(second);
        damaged[^1] ^= 1;
        File.WriteAllBytes(files.PathOf(SecondFile), damaged);
        var slot = files.Open();

        var recovered = slot.Load();
        AssertCheckpoint(first, SaveSlotStatus.Loaded, recovered);
        var evidence = recovered.Recovery ?? throw new AssertFailedException("Expected recovery evidence.");
        Assert.AreEqual(SecondFile, evidence.FileName);
        CollectionAssert.AreEqual(damaged, File.ReadAllBytes(files.PathOf(SecondFile)));

        var replacement = Checkpoint(2, 3);
        AssertCheckpoint(replacement, SaveSlotStatus.Committed, slot.Save(1, replacement));
        files.AssertFrame(FirstFile, first);
        files.AssertFrame(SecondFile, replacement);
    }

    [TestMethod]
    public void MalformedApplicationPayloadCanRecoverAnOlderCheckpoint()
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1, marker: 1);
        var second = Checkpoint(2, 2, marker: 2);
        files.Put(FirstFile, first);
        files.Put(SecondFile, second);
        var slot = files.Open(checkpoint => checkpoint.GamePayload[0] == 2
            ? SaveGameValidation.Malformed : SaveGameValidation.Valid);

        var recovered = slot.Load();

        AssertCheckpoint(first, SaveSlotStatus.Loaded, recovered);
        var evidence = recovered.Recovery ?? throw new AssertFailedException("Expected recovery evidence.");
        Assert.AreEqual(SecondFile, evidence.FileName);
        files.AssertFrame(SecondFile, second);
    }

    [TestMethod]
    public void BothCorruptFilesBlockLoadAndSaveWithoutReplacingEvidence()
    {
        using var files = new SlotDirectory();
        byte[] first = [1, 2, 3];
        byte[] second = [4, 5, 6];
        File.WriteAllBytes(files.PathOf(FirstFile), first);
        File.WriteAllBytes(files.PathOf(SecondFile), second);
        var slot = files.Open();

        Assert.AreEqual(SaveSlotStatus.Corrupt, slot.Load().Status);
        Assert.AreEqual(SaveSlotStatus.Corrupt, slot.Save(0, Checkpoint(1, 1)).Status);
        CollectionAssert.AreEqual(first, File.ReadAllBytes(files.PathOf(FirstFile)));
        CollectionAssert.AreEqual(second, File.ReadAllBytes(files.PathOf(SecondFile)));
        Assert.IsFalse(File.Exists(files.PathOf(CandidateFile)));
    }

    [TestMethod]
    [DataRow("framing")]
    [DataRow("schema")]
    [DataRow("generator")]
    [DataRow("game")]
    public void IncompatibleNewestBlocksFallbackAndSubsequentWrites(string incompatibility)
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1, marker: 1);
        var second = Checkpoint(2, 2, marker: 2);
        files.Put(FirstFile, first);
        var incompatible = Frame(second);
        switch (incompatibility)
        {
            case "framing":
                BinaryPrimitives.WriteUInt16LittleEndian(incompatible.AsSpan(8), 2);
                break;
            case "schema":
                BinaryPrimitives.WriteUInt16LittleEndian(incompatible.AsSpan(10), 2);
                break;
            case "generator":
                BinaryPrimitives.WriteUInt32LittleEndian(incompatible.AsSpan(60), 2);
                break;
        }
        SHA256.HashData(incompatible.AsSpan(0, incompatible.Length - DigestSize), incompatible.AsSpan(incompatible.Length - DigestSize));
        File.WriteAllBytes(files.PathOf(SecondFile), incompatible);
        var slot = files.Open(checkpoint => incompatibility == "game" && checkpoint.GamePayload[0] == 2
            ? SaveGameValidation.Unsupported : SaveGameValidation.Valid);

        Assert.AreEqual(SaveSlotStatus.Unsupported, slot.Load().Status);
        Assert.AreEqual(SaveSlotStatus.Unsupported, slot.Save(1, Checkpoint(2, 3)).Status);
        files.AssertFrame(FirstFile, first);
        CollectionAssert.AreEqual(incompatible, File.ReadAllBytes(files.PathOf(SecondFile)));
        Assert.IsFalse(File.Exists(files.PathOf(CandidateFile)));
    }

    [TestMethod]
    public void FlatSlotPreservesARealPlanetaryCheckpoint()
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1);
        files.Put(FirstFile, first);
        var identity = Checkpoint(2, 2).Identity;
        var planetary = PlanetCheckpointCodec.Encode(new PlanetCheckpoint(identity, new(1, 1, 0x57505538),
            new(new(64, 0, 0), PlanetFlightOrientation.Identity), [new(new(512, 0, 0), 120)]));
        File.WriteAllBytes(files.PathOf(SecondFile), planetary);
        Assert.AreEqual(SaveReadStatus.UnsupportedSchema, SaveCodec.Decode(planetary, static (_, _) => null).Status);
        var slot = files.Open();
        Assert.AreEqual(SaveSlotStatus.Unsupported, slot.Load().Status);
        Assert.AreEqual(SaveSlotStatus.Unsupported, slot.Save(1, Checkpoint(2, 3)).Status);
        files.AssertFrame(FirstFile, first);
        CollectionAssert.AreEqual(planetary, File.ReadAllBytes(files.PathOf(SecondFile)));
        Assert.IsFalse(File.Exists(files.PathOf(CandidateFile)));
    }

    [TestMethod]
    [DataRow("lineage")]
    [DataRow("generation")]
    [DataRow("recipe")]
    public void InconsistentIntactFramesArePreservedAsConflicts(string conflict)
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1);
        var second = conflict switch
        {
            "lineage" => Checkpoint(2, 2, lineage: new Guid("ccfb98aa-03aa-4d1d-879b-82a6c909961d")),
            "generation" => Checkpoint(1, 2),
            _ => Checkpoint(2, 2, recipe: Recipe with { Seed = 1234 }),
        };
        files.Put(FirstFile, first);
        files.Put(SecondFile, second);
        var slot = files.Open();

        Assert.AreEqual(SaveSlotStatus.Conflict, slot.Load().Status);
        Assert.AreEqual(SaveSlotStatus.Conflict, slot.Save(1, Checkpoint(2, 3)).Status);
        files.AssertFrame(FirstFile, first);
        files.AssertFrame(SecondFile, second);
        Assert.IsFalse(File.Exists(files.PathOf(CandidateFile)));
    }

    [TestMethod]
    public void AConflictingOlderRecipeIsDetectedEvenWhenItsGeneratorIsUnavailable()
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1, recipe: Recipe with { GeneratorVersion = 2 });
        var second = Checkpoint(2, 2);
        files.Put(FirstFile, first);
        files.Put(SecondFile, second);
        var slot = files.Open();

        Assert.AreEqual(SaveSlotStatus.Conflict, slot.Load().Status);
        Assert.AreEqual(SaveSlotStatus.Conflict, slot.Save(2, Checkpoint(3, 3)).Status);
        files.AssertFrame(FirstFile, first);
        files.AssertFrame(SecondFile, second);
    }

    [TestMethod]
    public void IdenticalTiedFramesSelectTheFirstSlotAndReplaceTheSecond()
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1);
        var second = Checkpoint(2, 2);
        files.Put(FirstFile, first);
        files.Put(SecondFile, first);
        var slot = files.Open();

        AssertCheckpoint(first, SaveSlotStatus.Loaded, slot.Load());
        AssertCheckpoint(second, SaveSlotStatus.Committed, slot.Save(1, second));
        files.AssertFrame(FirstFile, first);
        files.AssertFrame(SecondFile, second);
    }

    [TestMethod]
    public void StaleWritersAndRetainedRequestIdReuseCannotChangeCommittedBytes()
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1);
        var second = Checkpoint(2, 2);
        files.Put(FirstFile, first);
        files.Put(SecondFile, second);
        var slot = files.Open();

        Assert.AreEqual(SaveSlotStatus.Conflict, slot.Save(1, Checkpoint(2, 3)).Status);
        Assert.AreEqual(SaveSlotStatus.Conflict, slot.Save(0, first).Status);
        Assert.AreEqual(SaveSlotStatus.Conflict, slot.Save(1, Checkpoint(2, 2, marker: 9)).Status);
        Assert.AreEqual(SaveSlotStatus.Conflict, slot.Save(2, Checkpoint(3, 1, marker: 9)).Status);
        Assert.AreEqual(SaveSlotStatus.Conflict, slot.Save(2, Checkpoint(3, 3, recipe: Recipe with { Seed = 1234 })).Status);
        files.AssertFrame(FirstFile, first);
        files.AssertFrame(SecondFile, second);
        Assert.IsFalse(File.Exists(files.PathOf(CandidateFile)));
    }

    [TestMethod]
    public void InaccessibleSiblingPreventsFallbackAndReplacement()
    {
        using var files = new SlotDirectory();
        var first = Checkpoint(1, 1);
        var second = Checkpoint(2, 2);
        files.Put(FirstFile, first);
        files.Put(SecondFile, second);

        using (File.Open(files.PathOf(SecondFile), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.AreEqual(SaveSlotStatus.StorageError, files.Open().Load().Status);
            Assert.AreEqual(SaveSlotStatus.StorageError, files.Open().Save(1, Checkpoint(2, 3)).Status);
        }

        files.AssertFrame(FirstFile, first);
        files.AssertFrame(SecondFile, second);
        Assert.IsFalse(File.Exists(files.PathOf(CandidateFile)));
    }

    private static SaveCheckpoint Checkpoint(ulong generation, int request, byte marker = 0,
        Guid? lineage = null, SaveWorldRecipe? recipe = null) =>
        new(new SaveFrameIdentity(generation, lineage ?? Lineage, new Guid(request, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            recipe ?? Recipe, [new TerrainChunkDelta(new ChunkCoordinate(-1, 0, 0), [new(1087, 0)])], 1, [marker]);

    private static byte[] Frame(SaveCheckpoint checkpoint) => SaveCodec.Encode(checkpoint, FloorGenerator.Instance);

    private static void AssertCheckpoint(SaveCheckpoint expected, SaveSlotStatus status, SaveSlotResult actual)
    {
        Assert.AreEqual(status, actual.Status);
        Assert.AreEqual<SaveFrameIdentity?>(expected.Identity, actual.Identity);
        var checkpoint = actual.Checkpoint ?? throw new AssertFailedException("Expected a decoded checkpoint.");
        CollectionAssert.AreEqual(Frame(expected), Frame(checkpoint));
    }

    private sealed class SlotDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("groundwork-save-slot-");

        internal string PathOf(string file) => Path.Combine(_directory.FullName, file);

        internal SaveSlot Open(Func<SaveCheckpoint, SaveGameValidation>? validateGame = null) =>
            new(_directory.FullName, static (id, version) => id == 1 && version == 1 ? FloorGenerator.Instance : null, validateGame);

        internal void Put(string file, SaveCheckpoint checkpoint) => File.WriteAllBytes(PathOf(file), Frame(checkpoint));

        internal void AssertFrame(string file, SaveCheckpoint checkpoint) =>
            CollectionAssert.AreEqual(Frame(checkpoint), File.ReadAllBytes(PathOf(file)));

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
