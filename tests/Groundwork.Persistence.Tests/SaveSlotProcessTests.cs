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
    public async Task InterruptedWritesRemainRecoverableFromFreshProcesses()
    {
        var mode = Environment.GetEnvironmentVariable(ChildMode);
        if (mode is not null)
        {
            RunChild(mode);
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
            var slot = OpenSlot(directory);

            await InterruptAndVerifyAsync(fixture.FullName, directory, 1, SaveWriteStage.BeforePublish, 0);
            Assert.AreEqual(SaveSlotStatus.Committed, slot.Save(0, Checkpoint(1)).Status);

            var first = File.ReadAllBytes(Path.Combine(directory, "save.a"));
            await InterruptAndVerifyAsync(fixture.FullName, directory, 2, SaveWriteStage.BeforePublish, 1);
            CollectionAssert.AreEqual(first, File.ReadAllBytes(Path.Combine(directory, "save.a")));
            Assert.AreEqual(SaveSlotStatus.Committed, slot.Save(1, Checkpoint(2)).Status);

            var second = File.ReadAllBytes(Path.Combine(directory, "save.b"));
            await InterruptAndVerifyAsync(fixture.FullName, directory, 3, SaveWriteStage.AfterPublish, 3);
            CollectionAssert.AreEqual(second, File.ReadAllBytes(Path.Combine(directory, "save.b")));
            Assert.AreEqual(SaveSlotStatus.AlreadyCommitted, slot.Save(2, Checkpoint(3)).Status);
            Assert.AreEqual(SaveSlotStatus.Conflict, slot.Save(1, Checkpoint(2)).Status);
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    private static async Task InterruptAndVerifyAsync(string fixture, string directory, ulong candidate,
        SaveWriteStage stage, ulong expected)
    {
        var run = Path.Combine(fixture, $"{candidate}-{stage}");
        Directory.CreateDirectory(run);
        var barrier = Path.Combine(run, "barrier");
        using (var child = StartChild(run, directory, "write", candidate, stage, barrier))
        {
            using var watchdog = new CancellationTokenSource(ChildTimeout);
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            Process? writer = null;
            try
            {
                while (!File.Exists(barrier))
                {
                    if (child.HasExited)
                        throw new AssertFailedException($"Writer exited before {stage}.{Environment.NewLine}{await output}{Environment.NewLine}{await errors}");
                    await Task.Delay(20, watchdog.Token);
                }

                var writerId = int.Parse(File.ReadAllText(barrier), CultureInfo.InvariantCulture);
                writer = Process.GetProcessById(writerId);
                // Open the handle before termination so a recycled PID cannot affect the wait.
                _ = writer.SafeHandle;
                var other = OpenSlot(directory);
                Assert.AreEqual(SaveSlotStatus.Busy, other.Load().Status);
                Assert.AreEqual(SaveSlotStatus.Busy, other.Save(candidate - 1, Checkpoint(candidate)).Status);
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
        using var reader = StartChild(readRun, directory, "read", expected, stage, barrier);
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
        SaveWriteStage stage, string barrier)
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
        start.ArgumentList.Add($"/TestCaseFilter:FullyQualifiedName={typeof(SaveSlotProcessTests).FullName}.{nameof(InterruptedWritesRemainRecoverableFromFreshProcesses)}");
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

    private static void RunChild(string mode)
    {
        var slot = OpenSlot(ChildArgument(ChildDirectory));
        var generation = ulong.Parse(ChildArgument(ChildGeneration), CultureInfo.InvariantCulture);
        if (mode == "read")
        {
            var result = slot.Load();
            Assert.AreEqual(generation == 0 ? SaveSlotStatus.Empty : SaveSlotStatus.Loaded, result.Status);
            if (generation > 0)
            {
                var checkpoint = result.Checkpoint ?? throw new AssertFailedException("The committed checkpoint was missing.");
                CollectionAssert.AreEqual(SaveCodec.Encode(Checkpoint(generation), EmptyGenerator.Instance),
                    SaveCodec.Encode(checkpoint, EmptyGenerator.Instance));
            }
            return;
        }

        Assert.AreEqual("write", mode);
        var selected = Enum.Parse<SaveWriteStage>(ChildArgument(ChildStage));
        var barrier = ChildArgument(ChildBarrier);
        var resultOfWrite = slot.Save(generation - 1, Checkpoint(generation), reached =>
        {
            if (reached != selected)
                return;
            File.WriteAllText(barrier + ".pending", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            File.Move(barrier + ".pending", barrier);
            using var parked = new ManualResetEventSlim();
            if (!parked.Wait(ChildTimeout))
                throw new TimeoutException("Parent did not terminate the writer at the selected stage.");
        });
        Assert.Fail($"Writer returned {resultOfWrite.Status} before it could be interrupted at {selected}.");
    }

    private static string ChildArgument(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new AssertFailedException($"Missing child argument {name}.");

    private static SaveSlot OpenSlot(string directory) =>
        new(directory, static (id, version) => id == 1 && version == 1 ? EmptyGenerator.Instance : null);

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
