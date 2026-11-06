# WP01 test wiring

This specification proposes concrete wiring for the design accepted in [WP01 runtime API review](wp01-runtime-api-review.md) at `75d0847760fc70a6387969e1d14b8899dceff0c1`. It describes the wiring to add with the first substantive Core tests. The B00 owner coordinates the solution and project guard, L4 owns Windows CI, and each runtime lane owns its tests. Review this specification under ADR-0007 before applying it.

## Projects and dependency boundary

Use `Groundwork.slnx` with the existing `/src/` folder and a new `/tests/` folder. A test project is added with its first substantive test. Each lane owns one matching test project, using the shipping module name followed by `.Tests` for its directory, project filename and default assembly name. The B00 owner coordinates the shared solution additions in integration order and checks test discovery against each rebased candidate.

| Owner | Test project directory | Target framework | Direct project reference |
| --- | --- | --- | --- |
| Core contract work | `tests/Groundwork.Core.Tests/` | `net10.0` | `src/Groundwork.Core/` |
| L1 | `tests/Groundwork.Platform.Windows.Tests/` | `net10.0-windows` | `src/Groundwork.Platform.Windows/` |
| L2 | `tests/Groundwork.Simulation.Tests/` | `net10.0` | `src/Groundwork.Simulation/` |
| L3A | `tests/Groundwork.Ecs.Tests/` | `net10.0` | `src/Groundwork.Ecs/` |
| L3B | `tests/Groundwork.Jobs.Tests/` | `net10.0` | `src/Groundwork.Jobs/` |
| L5 | `tests/Groundwork.Diagnostics.Tests/` | `net10.0` | `src/Groundwork.Diagnostics/` |

The composed recovery regression in [PR #28](https://github.com/DaveBeusing/Groundwork/pull/28)
adds ECS, Jobs and Diagnostics references to the Simulation test project. The
gate allowlists that exact test-only dependency set. The six shipping modules
follow the original dependency rules below. See the [WP01 handover](../wp01-handover.md)
for the executed matrix.

The project shape below uses `Groundwork.Core.Tests` as the example. `net10.0` is inherited from `Directory.Build.props`. L1 sets `<TargetFramework>net10.0-windows</TargetFramework>` in its test project to match the tested assembly. All projects inherit C# 14, nullable analysis, recommended .NET analyzers, warnings as errors, deterministic builds, `artifacts/` outputs and lock-file generation. Explicitly opt out of assembly-level test parallelization at first using `[assembly: DoNotParallelize]`. Concurrency and race tests start their own controlled workers. MSTest runs through the SDK's default VSTest driver and the TRX logger.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsTestProject>true</IsTestProject>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MSTest" Version="[4.0.2]" />
    <ProjectReference Include="../../src/Groundwork.Core/Groundwork.Core.csproj" />
    <Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" />
  </ItemGroup>
</Project>
```

Use the repository `NuGet.Config` and its mapped `nuget.org` source. The package pin is from the installed .NET SDK 10.0.403 template. Apply the repository's NuGet audit to the test dependency graph. Any future dependency change is a test-infrastructure change with its own generated lock diff.

The structural portion of `scripts/Verify-Bootstrap.ps1` checks the solution paths under `/src/` against the six shipping projects. Each path under `/tests/` must match the approved test roster and reference set. Shipping projects must never reference test projects or MSTest. The gate rejects unexpected solution entries and test projects on disk that were omitted from the solution. The verified package graph remains the five runtime assemblies pointing only to Core.

## Lock files and the Windows CI gate

Generate each project's `packages.lock.json` with the SDK, including the existing shipping projects for the locked solution restore. Commit the generated files next to their `.csproj` files. Run the first command only when generating or deliberately updating locks. Run the second and third commands in CI and when verifying the committed candidate.

```powershell
dotnet restore Groundwork.slnx --configfile NuGet.Config --force-evaluate -p:RestoreLockedMode=false
dotnet restore Groundwork.slnx --configfile NuGet.Config --locked-mode
dotnet build Groundwork.slnx -c Release --no-restore
```

Before the locked restore, verify that each expected lock file exists and is tracked. Fail on any changed or newly generated lock file after restore. This makes a missing lock an explicit failure even if an SDK can generate it. For clean-candidate verification, create a fresh package cache and set `NUGET_PACKAGES` to its directory for the entire job. Restore and build the same commit later passed to the test step. Keep the existing package and isolated consumer steps after the test step.

After the first test project is added, replace the solution-wide test command in `windows-ci.yml` with a per-project test step. Enumerate the verified `/tests/` entries from the solution. Every integrated test project must execute at least one selected, passing test. A missing test project on the owning lane PR is a failed lane gate. The test step uses a new results root per run and a separate directory for each project. The command below assumes `$project` is one verified project path and `$results` is its new empty results directory.

```powershell
dotnet test $project -c Release --no-build --no-restore `
  --filter 'TestCategory!=WindowsDesktopSmoke' `
  --logger 'trx;LogFileName=results.trx' --results-directory $results
if ($LASTEXITCODE -ne 0) { throw "Tests failed for $project" }
$reportPath = Join-Path $results 'results.trx'
if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    throw "No test report for $project"
}
[xml]$report = Get-Content -LiteralPath $reportPath -Raw
$counters = $report.TestRun.ResultSummary.Counters
if ([int]$counters.executed -lt 1 -or [int]$counters.passed -lt 1 -or [int]$counters.failed -ne 0) {
    throw "No passing executed test, or a failed test, for $project"
}
```

Record the commit, per-project counts and TRX evidence. Publish TRX from successful and failed runs under a separate test-evidence artifact. Module test suites cover their issue's positive, negative and race/failure scenarios. Timing uses synthetic time. Race tests use explicit synchronization and bounded timeouts. The performance baseline is a separate recorded run with an exact commit and machine details.

A temporary two-case probe with .NET SDK 10.0.403, the repository build properties and NuGet configuration, and exact MSTest `[4.0.2]` generated its lock, restored in locked mode, and built in Release with zero warnings. The normal and desktop-category filters each executed one passing sample. A filter matching no cases returned exit code zero and a TRX with zero executed and passed, which the gate above rejects. The samples exercised runner configuration and category selection only.

## Interactive Windows lifecycle smoke

L1 adds a non-parallel case in `Groundwork.Platform.Windows.Tests` with `[TestCategory("WindowsDesktopSmoke")]`. Run it on the same candidate as the normal Windows tests, on an unlocked interactive Windows desktop where the harness can verify focus and resizing. The harness owns its window and uses the production UI thread/message pump. It applies a finite watchdog, exercises open, resize, focus loss and reacquisition, minimize and restore, and then requests close. Verify focus neutralizes input, close signals terminal intent, cleanup returns within its budget, and all owned native handles are released. Use the same locked restore and Release build before the interactive run.

```powershell
$project = 'tests/Groundwork.Platform.Windows.Tests/Groundwork.Platform.Windows.Tests.csproj'
dotnet test $project -c Release --no-build --no-restore `
  --filter 'TestCategory=WindowsDesktopSmoke' `
  --logger 'trx;LogFileName=desktop-smoke.trx' --results-directory $results
```

Apply the same exit and positive-execution checks using `desktop-smoke.trx`. Record the commit, SDK, Windows version, session type, result and any native-resource observations. A headless CI run supplies the normal L1 tests. The separate interactive record supplies the real lifecycle evidence required before L1 acceptance and again for the G00 milestone.
