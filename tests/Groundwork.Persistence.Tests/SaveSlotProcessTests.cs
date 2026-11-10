using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using Groundwork.Voxels;

namespace Groundwork.Persistence.Tests;

[TestClass]
public sealed class SaveSlotProcessTests
{
    private const string ChildMode = "GROUNDWORK_SAVE_SLOT_CHILD";
    private const string ChildDirectory = "GROUNDWORK_SAVE_SLOT_DIRECTORY";
    private const string ChildGeneration = "GROUNDWORK_SAVE_SLOT_GENERATION";
    private const string ChildStage = "GROUNDWORK_SAVE_SLOT_STAGE";
    private const string ChildBarrier = "GROUNDWORK_SAVE_SLOT_BARRIER";
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(45);
    private static readonly Guid Lineage = new("f4850f28-1b5e-4e4c-8848-6f22cfbcb097");
    private static readonly SaveWorldRecipe Recipe = new(1, 1, 17, new RegionBounds(-2, 2, -2, 2));

    [TestMethod]
    public Task InterruptedWritesRemainRecoverableFromFreshProcesses() => VerifyInterruptionsAsync(planetary: false);

    [TestMethod]
    public Task InterruptedPlanetWritesRemainRecoverableFromFreshProcesses() => VerifyInterruptionsAsync(planetary: true);

    private static async Task VerifyInterruptionsAsync(bool planetary)
    {
        var mode = Environment.GetEnvironmentVariable(ChildMode);
        if (mode is not null)
        {
            RunChild(mode, planetary);
            return;
        }

        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("This test qualifies the Windows NTFS backend.");

        var fixture = Directory.CreateTempSubdirectory("groundwork-save-crash-");
        try
        {
            var volume = new DriveInfo(Path.GetPathRoot(fixture.FullName)!);
            Assert.AreEqual("NTFS", volume.DriveFormat, "The crash fixture must exercise local NTFS.");
            var directory = Path.Combine(fixture.FullName, "slot");
            await InterruptAndVerifyAsync(fixture.FullName, directory, 1, SaveWriteStage.BeforePublish, 0, planetary);
            Assert.AreEqual(SaveSlotStatus.Committed, Save(directory, 1, planetary));

            var first = File.ReadAllBytes(Path.Combine(directory, "save.a"));
            await InterruptAndVerifyAsync(fixture.FullName, directory, 2, SaveWriteStage.BeforePublish, 1, planetary);
            CollectionAssert.AreEqual(first, File.ReadAllBytes(Path.Combine(directory, "save.a")));
            Assert.AreEqual(SaveSlotStatus.Committed, Save(directory, 2, planetary));

            var second = File.ReadAllBytes(Path.Combine(directory, "save.b"));
            await InterruptAndVerifyAsync(fixture.FullName, directory, 3, SaveWriteStage.AfterPublish, 3, planetary);
            CollectionAssert.AreEqual(second, File.ReadAllBytes(Path.Combine(directory, "save.b")));
            Assert.AreEqual(SaveSlotStatus.AlreadyCommitted, Save(directory, 3, planetary));
            Assert.AreEqual(SaveSlotStatus.Conflict, Save(directory, 2, planetary));
            if (planetary)
            {
                var invalid = PlanetCheckpointFor(4);
                var unsupportedPose = new PlanetCheckpoint(invalid.Identity, invalid.Recipe,
                    invalid.Pose with { Position = new(65, 0, 0) }, invalid.Edits);
                var planetSlot = OpenPlanetSlot(directory);
                Assert.AreEqual(SaveSlotStatus.InvalidCheckpoint, planetSlot.Save(3, unsupportedPose).Status);
                CollectionAssert.AreEqual(PlanetCheckpointCodec.Encode(PlanetCheckpointFor(3)),
                    File.ReadAllBytes(Path.Combine(directory, "save.a")));

                var older = PlanetCheckpointFor(2);
                var foreignRecipe = new PlanetCheckpoint(older.Identity, older.Recipe with { GeneratorVersion = 2 },
                    older.Pose, older.Edits);
                var foreignBytes = PlanetCheckpointCodec.Encode(foreignRecipe);
                var olderPath = Path.Combine(directory, "save.b");
                File.WriteAllBytes(olderPath, foreignBytes);
                Assert.AreEqual(SaveSlotStatus.Conflict, planetSlot.Load().Status);
                Assert.AreEqual(SaveSlotStatus.Conflict, planetSlot.Save(3, invalid).Status);
                CollectionAssert.AreEqual(foreignBytes, File.ReadAllBytes(olderPath));
            }
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    private static async Task InterruptAndVerifyAsync(string fixture, string directory, ulong candidate,
        SaveWriteStage stage, ulong expected, bool planetary)
    {
        var run = Path.Combine(fixture, $"{candidate}-{stage}");
        Directory.CreateDirectory(run);
        var barrier = Path.Combine(run, "barrier");
        using (var child = StartChild(run, directory, "write", candidate, stage, barrier, planetary))
        {
            using var watchdog = new CancellationTokenSource(ChildTimeout);
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            Process? writer = null;
            try
            {
                string? writerText = null;
                while (writerText is null)
                {
                    if (child.HasExited)
                        throw new AssertFailedException($"Writer exited before {stage}.{Environment.NewLine}{await output}{Environment.NewLine}{await errors}");
                    if (File.Exists(barrier))
                    {
                        try
                        {
                            writerText = File.ReadAllText(barrier);
                        }
                        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                        {
                            // The rename may expose the marker before its Windows handle is released.
                        }
                    }
                    if (writerText is null)
                        await Task.Delay(20, watchdog.Token);
                }

                var writerId = int.Parse(writerText, CultureInfo.InvariantCulture);
                writer = Process.GetProcessById(writerId);
                // Open the handle before termination so a recycled PID cannot affect the wait.
                _ = writer.SafeHandle;
                Assert.AreEqual(SaveSlotStatus.Busy, Load(directory, planetary).Status);
                Assert.AreEqual(SaveSlotStatus.Busy, Save(directory, candidate, planetary));
                writer.Kill();
                await writer.WaitForExitAsync(watchdog.Token);
                await child.WaitForExitAsync(watchdog.Token);
            }
            finally
            {
                using (writer)
                {
                    if (writer is not null)
                        await StopChildAsync(writer);
                    await StopChildAsync(child);
                }
            }
        }

        var readRun = Path.Combine(run, "read");
        Directory.CreateDirectory(readRun);
        using var reader = StartChild(readRun, directory, "read", expected, stage, barrier, planetary);
        var readOutput = reader.StandardOutput.ReadToEndAsync();
        var readErrors = reader.StandardError.ReadToEndAsync();
        using var readWatchdog = new CancellationTokenSource(ChildTimeout);
        try
        {
            await reader.WaitForExitAsync(readWatchdog.Token);
            Assert.AreEqual(0, reader.ExitCode, $"{await readOutput}{Environment.NewLine}{await readErrors}");
            var report = XDocument.Load(Path.Combine(readRun, "child.trx"));
            var counters = report.Descendants().Single(element => element.Name.LocalName == "Counters");
            Assert.AreEqual("1", (string?)counters.Attribute("passed"));
        }
        finally
        {
            await StopChildAsync(reader);
        }
    }

    private static Process StartChild(string run, string directory, string mode, ulong generation,
        SaveWriteStage stage, string barrier, bool planetary)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = run,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment[ChildMode] = mode;
        start.Environment[ChildDirectory] = directory;
        start.Environment[ChildGeneration] = generation.ToString(CultureInfo.InvariantCulture);
        start.Environment[ChildStage] = stage.ToString();
        start.Environment[ChildBarrier] = barrier;
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(SaveSlotProcessTests).Assembly.Location);
        var testName = planetary ? nameof(InterruptedPlanetWritesRemainRecoverableFromFreshProcesses) :
            nameof(InterruptedWritesRemainRecoverableFromFreshProcesses);
        start.ArgumentList.Add($"/TestCaseFilter:FullyQualifiedName={typeof(SaveSlotProcessTests).FullName}.{testName}");
        start.ArgumentList.Add("/Logger:trx;LogFileName=child.trx");
        start.ArgumentList.Add($"/ResultsDirectory:{run}");
        return Process.Start(start) ?? throw new AssertFailedException("Could not start the save-slot child.");
    }

    private static async Task StopChildAsync(Process child)
    {
        if (!child.HasExited)
            child.Kill(entireProcessTree: true);
        using var cleanup = new CancellationTokenSource(ChildTimeout);
        await child.WaitForExitAsync(cleanup.Token);
    }

    private static void RunChild(string mode, bool planetary)
    {
        var directory = ChildArgument(ChildDirectory);
        var generation = ulong.Parse(ChildArgument(ChildGeneration), CultureInfo.InvariantCulture);
        if (mode == "read")
        {
            var result = Load(directory, planetary);
            Assert.AreEqual(generation == 0 ? SaveSlotStatus.Empty : SaveSlotStatus.Loaded, result.Status);
            if (generation > 0)
            {
                var expected = planetary ? PlanetCheckpointCodec.Encode(PlanetCheckpointFor(generation)) :
                    SaveCodec.Encode(Checkpoint(generation), EmptyGenerator.Instance);
                CollectionAssert.AreEqual(expected, result.Frame);
            }
            return;
        }

        Assert.AreEqual("write", mode);
        var selected = Enum.Parse<SaveWriteStage>(ChildArgument(ChildStage));
        var barrier = ChildArgument(ChildBarrier);
        var resultOfWrite = Save(directory, generation, planetary, reached =>
        {
            if (reached != selected)
                return;
            File.WriteAllText(barrier + ".pending", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            File.Move(barrier + ".pending", barrier);
            using var parked = new ManualResetEventSlim();
            if (!parked.Wait(ChildTimeout))
                throw new TimeoutException("Parent did not terminate the writer at the selected stage.");
        });
        Assert.Fail($"Writer returned {resultOfWrite} before it could be interrupted at {selected}.");
    }

    private static string ChildArgument(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new AssertFailedException($"Missing child argument {name}.");

    private static SaveSlot OpenSlot(string directory) =>
        new(directory, static (id, version) => id == 1 && version == 1 ? EmptyGenerator.Instance : null);

    private static PlanetSaveSlot OpenPlanetSlot(string directory) =>
        new(directory, static (id, version) => id == 1 && version == 1, ValidatePlanetPose);

    private static (SaveSlotStatus Status, byte[]? Frame) Load(string directory, bool planetary)
    {
        if (planetary)
        {
            var result = OpenPlanetSlot(directory).Load();
            return (result.Status, result.Checkpoint is { } checkpoint ? PlanetCheckpointCodec.Encode(checkpoint) : null);
        }

        var flat = OpenSlot(directory).Load();
        return (flat.Status, flat.Checkpoint is { } saved ? SaveCodec.Encode(saved, EmptyGenerator.Instance) : null);
    }

    private static SaveSlotStatus Save(string directory, ulong generation, bool planetary, Action<SaveWriteStage>? observe = null)
    {
        if (planetary)
        {
            var slot = OpenPlanetSlot(directory);
            var checkpoint = PlanetCheckpointFor(generation);
            return (observe is null ? slot.Save(generation - 1, checkpoint) : slot.Save(generation - 1, checkpoint, observe)).Status;
        }

        var flat = OpenSlot(directory);
        var state = Checkpoint(generation);
        return (observe is null ? flat.Save(generation - 1, state) : flat.Save(generation - 1, state, observe)).Status;
    }

    private static PlanetCheckpoint PlanetCheckpointFor(ulong generation)
    {
        var delta = (short)(generation * 20);
        return new(new(generation, Lineage, new Guid((int)generation, 0, 0, new byte[8])), new(1, 1, 0x57505538),
            new(new(64 + delta / 1000d, 0, 0), PlanetFlightOrientation.Identity), [new(new(512, 0, 0), delta)]);
    }

    private static SaveGameValidation ValidatePlanetPose(PlanetCheckpoint checkpoint)
    {
        if (!PlanetTerrainSelection.TrySelect(checkpoint.Pose.Position, 64, null, out var plan) || plan is null)
            return SaveGameValidation.Malformed;
        var patches = plan.Tiles.ToArray().Where(tile => tile.SupportsCollision).Select(tile =>
            PlanetPatchMesh.Build(tile.Patch, 64, sample => checkpoint.GetHeightDelta(sample) / 1000d)).ToArray();
        var pose = checkpoint.Pose;
        var result = PlanetFlight.Step(new PlanetSurface(patches), new(pose.Position, default, pose.Orientation, Grounded: true),
            new(default, pose.Orientation));
        return result.Status == PlanetFlightStatus.Grounded ? SaveGameValidation.Valid : SaveGameValidation.Malformed;
    }

    private static SaveCheckpoint Checkpoint(ulong generation) =>
        new(new SaveFrameIdentity(generation, Lineage, new Guid((int)generation, 0, 0, new byte[8])),
            Recipe, [new TerrainChunkDelta(new ChunkCoordinate(-1, 0, 0), [new((ushort)generation, 1)])],
            1, [(byte)generation, 0x75]);

    private sealed class EmptyGenerator : ISaveTerrainGenerator
    {
        internal static EmptyGenerator Instance { get; } = new();

        public void Generate(SaveWorldRecipe recipe, ChunkCoordinate coordinate, Span<byte> cells) => cells.Clear();
    }
}
