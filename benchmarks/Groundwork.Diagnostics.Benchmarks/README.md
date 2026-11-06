# Diagnostics benchmark

Run from the repository root with the pinned SDK and a Release build. The benchmark records OS, runtime, CPU model when available, logical CPU count, process architecture, GC configuration, stopwatch frequency, sampling settings, and each raw sample. An optional label can identify the tested revision.

```powershell
$project = 'benchmarks/Groundwork.Diagnostics.Benchmarks/Groundwork.Diagnostics.Benchmarks.csproj'
dotnet restore $project --configfile NuGet.Config --locked-mode
dotnet run --project $project -c Release --no-restore -- --label baseline --save artifacts/diagnostics-baseline.json
dotnet run --project $project -c Release --no-restore -- --label candidate --compare artifacts/diagnostics-baseline.json --save artifacts/diagnostics-candidate.json
```

Use `--help` to see sample controls. By default, each scenario runs 20,000 warmup operations and then five samples of 100,000 operations. Run baseline and candidate with the same configuration on the same machine. The comparison matches scenario name and workload and reports time percentage and absolute allocated bytes per operation. Different environments or sample settings are flagged. Positive deltas represent increases.

The empty-loop control measures the delegate loop used by each scenario. Counter and duration scenarios exercise the disabled and enabled metrics APIs. The optional round trip measures recording, acquiring and releasing one numeric event. The saturation scenario measures rejection with 15 queued events and one held lease, then verifies that every dropped attempt was counted. Measurement runs without a sink, and the report is saved after sampling. The results describe single-producer median elapsed time and producer-thread allocation.
