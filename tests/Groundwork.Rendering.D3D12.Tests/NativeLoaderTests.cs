using System.Diagnostics;

namespace Groundwork.Rendering.D3D12.Tests;

[TestClass]
public sealed class NativeLoaderTests
{
    private const string ChildMarker = "GROUNDWORK_D3D12_LOADER_CHILD";
    private static readonly string[] BootstrapLibraries = ["d3d12.dll", "dxgi.dll"];

    [TestMethod]
    public async Task BootstrapUsesSystem32WithColocatedLookalikes()
    {
        if (Environment.GetEnvironmentVariable(ChildMarker) == "1")
        {
            using var device = new GpuDevice(warp: true);
            using var process = Process.GetCurrentProcess();
            foreach (var library in BootstrapLibraries)
            {
                Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, library)));
                var modules = process.Modules.Cast<ProcessModule>()
                    .Where(module => library.Equals(module.ModuleName, StringComparison.OrdinalIgnoreCase)).ToArray();
                Assert.HasCount(1, modules);
                Assert.IsTrue(Path.Combine(Environment.SystemDirectory, library)
                    .Equals(modules[0].FileName, StringComparison.OrdinalIgnoreCase), modules[0].FileName);
            }
            return;
        }

        var fixture = Directory.CreateTempSubdirectory("groundwork-native-loader-");
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                File.Copy(file, Path.Combine(fixture.FullName, Path.GetFileName(file)));
            }
            foreach (var library in BootstrapLibraries)
            {
                // Valid inbox PE with unrelated exports. Loading it cannot satisfy our imports.
                File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(fixture.FullName, library));
            }

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = fixture.FullName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment[ChildMarker] = "1";
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(Path.Combine(fixture.FullName, typeof(NativeLoaderTests).Assembly.GetName().Name + ".dll"));
            start.ArgumentList.Add("/Platform:x64");
            start.ArgumentList.Add($"/TestCaseFilter:FullyQualifiedName={typeof(NativeLoaderTests).FullName}.{nameof(BootstrapUsesSystem32WithColocatedLookalikes)}");
            start.ArgumentList.Add("/Logger:trx;LogFileName=loader.trx");
            start.ArgumentList.Add($"/ResultsDirectory:{fixture.FullName}");

            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await child.WaitForExitAsync(watchdog.Token); }
            catch (OperationCanceledException)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                throw;
            }
            Assert.AreEqual(0, child.ExitCode, $"{await output}\n{await errors}");
            var report = System.Xml.Linq.XDocument.Load(Path.Combine(fixture.FullName, "loader.trx"));
            var counters = report.Descendants().Single(element => element.Name.LocalName == "Counters");
            Assert.AreEqual("1", (string?)counters.Attribute("passed"));
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }
}
