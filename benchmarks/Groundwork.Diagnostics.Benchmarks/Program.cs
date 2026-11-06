using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using Groundwork.Core;
using Groundwork.Diagnostics;

return DiagnosticBenchmark.Run(args);

internal static class DiagnosticBenchmark
{
    private const int Schema = 1;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const string Method = "Sequential producer on one thread. Stopwatch elapsed time and current-thread allocated bytes, divided by operations per sample. Median across samples.";

    internal static int Run(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("Usage: [--iterations N] [--warmup N] [--samples N] [--label TEXT] [--save FILE] [--compare FILE]");
            return 0;
        }

        try
        {
            var options = Options.Parse(args);
            var baseline = options.Compare is null ? null : Load(options.Compare);
            var report = new Report(Schema, EnvironmentInfo.Read(options.Label), Method, options.Warmup, options.Iterations,
                options.Samples, MeasureAll(options));
            Print(report);
            if (baseline is not null)
                Compare(report, baseline);
            if (options.Save is not null)
                File.WriteAllText(options.Save, JsonSerializer.Serialize(report, Json));
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static Scenario[] MeasureAll(Options options)
    {
        var disabled = DiagnosticMetrics.Disabled;
        var enabled = new DiagnosticMetrics();
        var frequency = TimeProvider.System.TimestampFrequency;
        var scenarios = new List<Scenario>
        {
            Measure("empty-loop", "Empty delegate, included as a measurement control.", options, static () => { }),
            Measure("disabled-counter", "One counter call with diagnostics disabled.", options,
                () => disabled.TryRecord(DiagnosticMetric.CompletedTicks)),
            Measure("enabled-counter", "One counter increment with diagnostics enabled.", options,
                () => enabled.TryRecord(DiagnosticMetric.CompletedTicks)),
            Measure("enabled-duration", "One premeasured tick duration converted and recorded.", options,
                () => enabled.TryRecordElapsed(DiagnosticMetric.TickWorkNanoseconds, frequency / 1000, frequency)),
        };

        var calls = (long)options.Warmup + (long)options.Iterations * options.Samples;
        if (disabled.Read(DiagnosticMetric.CompletedTicks).Total != 0 ||
            enabled.Read(DiagnosticMetric.CompletedTicks).Total != calls)
            throw new InvalidOperationException("Metric observations did not match the benchmark workload.");

        scenarios.AddRange(MeasureIngress(options));
        return [.. scenarios];
    }

    private static Scenario[] MeasureIngress(Options options)
    {
        const int slots = 16;
        var quota = new ResourceQuota(1, 16,
            new JobQuota(8, 4, 16, 16, 4, 8, 16, 65536, 65536, 65536),
            new DiagnosticQuota(slots, 16384, 12, 32768, 4, 4, 64, 1024));
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, quota));
        using var reservation = budget.TryReserveRuntime(quota) ?? throw new InvalidOperationException("Benchmark reservation unavailable.");
        var duration = TimeSpan.FromSeconds(1);
        var runtime = RuntimeSafetyAuthority.Create(TimeProvider.System, new RuntimeTimingLimits(duration, duration, duration),
            reservation, slots);
        Scenario roundTrip;
        Scenario saturated;
        ShutdownSettlement settlement;
        try
        {
            roundTrip = Measure("optional-roundtrip", "One numeric optional event recorded and drained on one thread, without a sink.",
                options, () =>
                {
                    if (ReportOptional(runtime.Reporter) != FaultReportStatus.Recorded || !runtime.Diagnostics.TryRead(out _))
                        throw new InvalidOperationException("Optional report was not recorded and drained.");
                });

            for (int index = 0; index < slots; index++)
            {
                if (ReportOptional(runtime.Reporter) != FaultReportStatus.Recorded)
                    throw new InvalidOperationException("Optional queue saturated before its configured capacity.");
            }

            saturated = Measure("optional-saturated", "One numeric optional report rejected by an already-full 16-slot queue.",
                options, () =>
                {
                    if (ReportOptional(runtime.Reporter) != FaultReportStatus.Coalesced)
                        throw new InvalidOperationException("An optional report unexpectedly entered the saturated queue.");
                });
            var expectedDrops = (ulong)options.Warmup + (ulong)options.Iterations * (ulong)options.Samples;
            if (runtime.State.Read().DeliveryLossCount != expectedDrops)
                throw new InvalidOperationException("Optional delivery loss did not match the saturated workload.");
        }
        finally
        {
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            settlement = runtime.SettleStop();
        }

        if (settlement != ShutdownSettlement.Released)
            throw new InvalidOperationException("Benchmark resources were not released.");
        return [roundTrip, saturated];
    }

    private static FaultReportStatus ReportOptional(IFaultReporter reporter)
    {
        var observation = new FaultObservation(FaultClass.DiagnosticOnly, FaultSeverity.Information, FaultSubsystem.Host,
            FaultOrigin.Primary, 1, 0, default, [], []);
        return reporter.TryReport(in observation);
    }

    private static Scenario Measure(string name, string workload, Options options, Action operation)
    {
        Invoke(operation, options.Warmup);
        var samples = new Sample[options.Samples];
        for (int sample = 0; sample < samples.Length; sample++)
        {
            var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            Invoke(operation, options.Iterations);
            var elapsed = Stopwatch.GetElapsedTime(start);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            samples[sample] = new Sample(elapsed.TotalNanoseconds / options.Iterations, (double)allocated / options.Iterations);
        }

        return new(name, workload, Median(samples.Select(sample => sample.NanosecondsPerOperation)),
            Median(samples.Select(sample => sample.BytesPerOperation)), samples);
    }

    private static void Invoke(Action operation, int count)
    {
        for (int index = 0; index < count; index++)
            operation();
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return (ordered[ordered.Length / 2] + ordered[(ordered.Length - 1) / 2]) / 2;
    }

    private static Report Load(string path)
    {
        var report = JsonSerializer.Deserialize<Report>(File.ReadAllText(path), Json);
        if (report is not { SchemaVersion: Schema, Environment: not null, Scenarios.Length: > 0 } ||
            report.Scenarios.Any(scenario => scenario is null || string.IsNullOrWhiteSpace(scenario.Name) ||
                !double.IsFinite(scenario.MedianNanosecondsPerOperation) || scenario.MedianNanosecondsPerOperation <= 0 ||
                !double.IsFinite(scenario.MedianBytesPerOperation) || scenario.MedianBytesPerOperation < 0) ||
            report.Scenarios.Select(scenario => scenario.Name).Distinct(StringComparer.Ordinal).Count() != report.Scenarios.Length)
            throw new ArgumentException("The baseline is not a supported diagnostics benchmark report.", nameof(path));
        return report;
    }

    private static void Print(Report report)
    {
        var environment = report.Environment;
        Console.WriteLine($"{environment.Configuration}, {environment.Runtime}, {environment.OperatingSystem}, {environment.ProcessArchitecture}");
        Console.WriteLine(FormattableString.Invariant($"{environment.Processor}, {environment.LogicalProcessors} logical processors, server GC {environment.ServerGc}, {environment.StopwatchFrequency} stopwatch Hz"));
        Console.WriteLine(FormattableString.Invariant($"{environment.GcAvailableMemoryBytes} bytes available to the GC"));
        if (!string.IsNullOrEmpty(environment.Label))
            Console.WriteLine($"Label {environment.Label}");
        Console.WriteLine(FormattableString.Invariant($"{report.Samples} samples, {report.Iterations} operations per sample, {report.Warmup} warmup operations per scenario."));
        Console.WriteLine(report.Method);
        foreach (var scenario in report.Scenarios)
        {
            Console.WriteLine(FormattableString.Invariant($"{scenario.Name,-24} {scenario.MedianNanosecondsPerOperation,10:F2} ns/op  {scenario.MedianBytesPerOperation,9:F3} B/op"));
            Console.WriteLine($"  {scenario.Workload}");
        }
    }

    private static void Compare(Report current, Report baseline)
    {
        Console.WriteLine("Baseline comparison (current minus baseline)");
        if (current.Environment with { Label = null } != baseline.Environment with { Label = null } ||
            current.Method != baseline.Method || current.Samples != baseline.Samples ||
            current.Warmup != baseline.Warmup || current.Iterations != baseline.Iterations)
            Console.WriteLine("Environment or sampling configuration differs. Treat deltas as descriptive.");

        var previous = baseline.Scenarios.ToDictionary(scenario => scenario.Name, StringComparer.Ordinal);
        foreach (var scenario in current.Scenarios)
        {
            if (!previous.TryGetValue(scenario.Name, out var prior) || prior.Workload != scenario.Workload)
            {
                Console.WriteLine($"{scenario.Name,-24} no matching baseline workload");
                continue;
            }

            var elapsed = (scenario.MedianNanosecondsPerOperation / prior.MedianNanosecondsPerOperation - 1) * 100;
            var bytes = scenario.MedianBytesPerOperation - prior.MedianBytesPerOperation;
            Console.WriteLine(FormattableString.Invariant($"{scenario.Name,-24} {elapsed,10:+0.00;-0.00;0.00}% time  {bytes,9:+0.000;-0.000;0.000} B/op"));
        }
    }

    private sealed record Options(int Warmup, int Iterations, int Samples, string? Label, string? Save, string? Compare)
    {
        internal static Options Parse(string[] args)
        {
            var options = new Options(20_000, 100_000, 5, null, null, null);
            for (int index = 0; index < args.Length; index += 2)
            {
                var option = args[index];
                if (index + 1 == args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                    throw new ArgumentException($"Missing value for {option}.", nameof(args));
                var value = args[index + 1];
                options = option switch
                {
                    "--warmup" => options with { Warmup = ReadCount(option, value, 10_000_000) },
                    "--iterations" => options with { Iterations = ReadCount(option, value, 10_000_000) },
                    "--samples" => options with { Samples = ReadCount(option, value, 100) },
                    "--label" => options with { Label = value },
                    "--save" => options with { Save = value },
                    "--compare" => options with { Compare = value },
                    _ => throw new ArgumentException($"Unknown option {option}. Use --help to see supported options.", nameof(args)),
                };
            }

            return options;
        }

        private static int ReadCount(string option, string value, int maximum)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0 || count > maximum)
                throw new ArgumentException(FormattableString.Invariant($"{option} requires an integer between 1 and {maximum}."), nameof(value));
            return count;
        }
    }

    private sealed record EnvironmentInfo(string OperatingSystem, string Runtime, Architecture ProcessArchitecture,
        string Processor, int LogicalProcessors, long GcAvailableMemoryBytes, bool ServerGc, long StopwatchFrequency,
        string Configuration, string? Label)
    {
        internal static EnvironmentInfo Read(string? label) => new(RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture,
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, GCSettings.IsServerGC,
            Stopwatch.Frequency,
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            label);
    }

    private sealed record Report(int SchemaVersion, EnvironmentInfo Environment, string Method, int Warmup, int Iterations,
        int Samples, Scenario[] Scenarios);
    private sealed record Scenario(string Name, string Workload, double MedianNanosecondsPerOperation,
        double MedianBytesPerOperation, Sample[] Samples);
    private sealed record Sample(double NanosecondsPerOperation, double BytesPerOperation);
}
