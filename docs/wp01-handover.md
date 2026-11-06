# WP01 foundation handover

Groundwork and Worlds Yet Unseen (WYU) were verified from independent clean
Windows checkouts on 2026-11-06. This handover is the evidence for
[WP01-G00](https://github.com/DaveBeusing/Groundwork/issues/9). Owner sign-off
requires the reviewed handover PR commit under [ADR-0007](adr/ADR-0007-solo-review-governance.md).

## Baselines

| Baseline | Exact source | Evidence |
| --- | --- | --- |
| Groundwork `master` | `b0c3a94c97fb8d65481c91b0545c0c9e53d6500c` | [Windows build, tests and packaging](https://github.com/DaveBeusing/Groundwork/actions/runs/42139767186) |
| WYU `master` | `1874c30306160b23fb2087237847ac52258c2259` | [Windows package consumer and runtime smoke](https://github.com/DaveBeusing/wyu/actions/runs/42141285912) |
| Groundwork packages pinned by WYU | `8da185b176961e365dc32261d8c80a672a01b7b9` | [Immutable archive `0.1.0-dev.42137008200.1`](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42137008200.1-immutable) |

The commits between the archived producer and the verified Groundwork source
change only tests, the test dependency gate and documentation. The WYU build
uses the six immutable archive packages with exact references, the committed
NuGet lock and an isolated local feed. The newer Groundwork CI packages were
verified separately under their own version, `0.1.0-dev.42139767186.1`.

| Verified artifact | SHA-256 |
| --- | --- |
| WYU pin build manifest | `7981A019E4D8C4706E66D0C00B5E3E9150ABBA5DE77439DFC13C73FEC9AF2DB3` |
| WYU pin archive record | `293DEA0BA15F0895A7C37D8FD5D0B9D3B36234874839A72EE0AA42F4018D01A3` |
| Groundwork `master` package artifact ZIP | `6C2C0A5813B5320D796A2B416A85C505E664144058FB4517045FC9002809EE53` |
| [Groundwork `master` build manifest](wp01-evidence/groundwork-master-manifest.json) | `89B62E089C50D4042B352FD122632E621564B43BCC94BD91410FBBAAB77427B2` |
| [Local clean-build manifest](wp01-evidence/groundwork-local-manifest.json) | `224C8A607B331F5E86E199103A340C3BED6CCB8EF2D47D900EC2DDE28E86301C` |

GitHub reported the WYU archive as immutable. All nine downloaded assets
matched the release sizes and digests. Both producer manifests bind their
six package identities, sizes and SHA-256 hashes to the exact source and
build. Keep the immutable archive available while WYU pins it.

The local pack rehearsal produced six packages at
`0.1.0-dev.g00.0f812a0295874a7395beb9f71b7afc30` from the verified Groundwork
checkout. The local manifest records the package hashes. Integrity, locked
restore and the isolated consumer passed against those bytes.

## Windows verification

Windows 11 Enterprise x64 build `10.0.26300`, Intel Core Ultra 9 285HX
(24 cores and 24 logical processors), 63.46 GiB RAM, .NET SDK `10.0.403` and
runtime `10.0.14`. Native tests ran on an interactive desktop. The repositories
used separate clones, and the engine and WYU restores used fresh package
caches. Both Release builds completed with zero warnings or errors.

| Groundwork suite | Passed | Coverage sampled for G00 |
| --- | ---: | --- |
| Core | 35 | Barrier authority and evidence fencing, observer publication, deadline and ownership settlement |
| Diagnostics | 33 | Bounded delivery, emergency observation, supervisor decisions, sink failures and cleanup |
| ECS | 14 | Entity lifecycle and queries, atomic/stale batch rejection, borrow drain and observer lease release |
| Jobs | 25 | Dependency gating, cancellation during capture, bounded admission, deadline and custody |
| Simulation | 20 | Synthetic 60/20 Hz, bounded catch-up and backlog, pause, cancellation and composed recovery |
| Windows | 21 | Host lifecycle, input translation and bounded input buffering |
| Windows interactive desktop | 5 | Real HWND lifecycle, resize/focus/minimize, modal and idle wakes, bounded close |

The composed recovery cases verified that the last observer remains visible
while paused, that a fresh Core-authorized decision resumes without replay,
and that an active-tick pause fences the new observer and stops the runtime.
The Jobs stop-during-capture regression checked cancellation and lease release
without spurious fault evidence. The shipping graph gate found six engine
assemblies, with the five runtime modules depending only on Core. A shipping
source scan found zero WYU namespace or repository references. WYU has six
exact package references and zero project references.

The separate WYU clone passed headless startup and shutdown, controlled-fault
shutdown, and native-window startup and shutdown from the verified archive.
A separate default launch opened its own visible HWND, handled `WM_CLOSE`,
destroyed the HWND and returned exit code 0. The package gate rejected
incompatible, missing and tampered packages. Groundwork's integrity matrix
also rejected altered or missing manifests, wrong commit or version, and
changed bytes at an existing package coordinate. The isolated NuGet consumer
checked stale lock and missing feed rejection.

An intentional Core test failure on a disposable local branch caused the
same verification script used by Windows CI to fail. The failed test and its
injected marker were present in TRX.

## Reproduce the matrix

Use Windows, PowerShell 7, Git and a stable .NET 10 SDK matching both
`global.json` files. Use GitHub CLI for the public artifact downloads. Run
the following from a new Groundwork clone checked out at the baseline above.
Choose an unlocked desktop for the interactive test filter.

```powershell
$env:NUGET_PACKAGES = Join-Path ([IO.Path]::GetTempPath()) ('gw-cache-' + [guid]::NewGuid().ToString('N'))
$results = Join-Path (Get-Location) 'artifacts/g00-results'
./scripts/Verify-Bootstrap.ps1 -TestResultsDirectory $results
dotnet test tests/Groundwork.Platform.Windows.Tests/Groundwork.Platform.Windows.Tests.csproj `
  -c Release --no-build --no-restore --filter 'TestCategory=WindowsDesktopSmoke' `
  --logger 'trx;LogFileName=windows-desktop.trx' --results-directory $results
```

Follow the [package workflow](../.github/workflows/windows-ci.yml) to build
and verify all six packages. For a local build, use a fresh `0.1.0-dev.g00.<guid>`
coordinate, generate a new manifest and run `eng/Test-PackageIntegrity.ps1`
and `eng/Test-IsolatedNuGetConsumer.ps1` against it.

For the game, use a new WYU clone at the verified commit and follow the
[pinned archive and smoke instructions](https://github.com/DaveBeusing/wyu/blob/1874c30306160b23fb2087237847ac52258c2259/docs/build-bootstrap.md).
Add `-WindowSmoke` on the desktop. For the normal close case, run the built
assembly without arguments, close its window and check exit code 0.

## Performance and diagnostics baseline

The following Release measurements used the Groundwork source and hardware
listed above. These samples describe the fixture workloads.

| Scenario | Measured result |
| --- | --- |
| Simulation scheduler | 2,323.5 ns/tick and 0.000 B/tick over 100,000 synthetic ticks after 20,000 warmup ticks |
| Diagnostics | 0.74 ns/op empty control, 5.32 disabled counter, 69.25 enabled counter, 290.29 enabled duration, 151.07 optional round trip, 86.43 saturated optional report. Median producer allocation 0.000 B/op in all scenarios. |
| ECS storage | 234,352 create/add, 1,410,845 point lookups, 3,070,292 query rows and 318,785 deletes per second. Live root charged 463,104 bytes for 1,024 entities. |
| Jobs | 32 no-op jobs at 29.85 µs queue, 28.14 µs worker and 26.09 µs dispatch per job. A separate cooperative cancellation sample took 359.80 µs from observation to terminal. |

The [Diagnostics raw samples](wp01-evidence/diagnostics-baseline.json) use
one producer thread, workstation GC, a 10 MHz stopwatch, 20,000 warmup
operations and five samples of 100,000 operations per scenario. Use the
[Diagnostics reproduction commands](../benchmarks/Groundwork.Diagnostics.Benchmarks/README.md)
and the [Simulation benchmark command](../src/Groundwork.Simulation/README.md#verification).
ECS and Jobs measurements are written to TRX by
`StorageBaselineReportsCreateLookupQueryAndDelete`,
`ReportsNoOpWorkerAndCoordinatorTimingSample` and
`BoundedQueueAndCallerDisposalDoNotLoseSchedulerOwnership` in the standard matrix.

## Public API index

| Package | WP01 entry points and contracts |
| --- | --- |
| `Groundwork.Core` | `IEngineModule`, `EngineModuleContext`, `RuntimeSafetyAuthority`, `ITickPipeline`, world publication and job authority ports. [Core API freeze](architecture/wp01-core-api-freeze.md), [Jobs API](architecture/wp01-core-jobs-api.md), [safety API](architecture/wp01-core-safety-api.md) |
| `Groundwork.Platform.Windows` | `WindowsHostModule`, `WindowsHostOptions`, input capture and window-state observation. [Windows module](../src/Groundwork.Platform.Windows/README.md) |
| `Groundwork.Simulation` | `FixedStepSimulation`, `SimulationOptions`, named barriers and recovery boundary. [Simulation module](../src/Groundwork.Simulation/README.md) |
| `Groundwork.Ecs` | `EcsWorld`, `EcsStorageBudget`, `ComponentRegistryBuilder`, queries and owned or borrowed input. [ECS module](../src/Groundwork.Ecs/README.md) |
| `Groundwork.Jobs` | `JobScheduler`, job admission and dispatch, cancellation and terminal outcomes. [Jobs module](../src/Groundwork.Jobs/README.md) |
| `Groundwork.Diagnostics` | `DiagnosticDispatcher`, `DiagnosticMetrics`, `BarrierSupervisorPolicy`. [Diagnostics module](../src/Groundwork.Diagnostics/README.md) |

## Decisions and WP02 entry conditions

| Lane | Merged implementation |
| --- | --- |
| C00 and Core | [Contracts #11](https://github.com/DaveBeusing/Groundwork/pull/11), [runtime API #15](https://github.com/DaveBeusing/Groundwork/pull/15), [Core API #16](https://github.com/DaveBeusing/Groundwork/pull/16), [Core #17](https://github.com/DaveBeusing/Groundwork/pull/17) |
| B00 | [Groundwork #12](https://github.com/DaveBeusing/Groundwork/pull/12), [WYU #2](https://github.com/DaveBeusing/wyu/pull/2) |
| L1 / L2 | [Windows #21](https://github.com/DaveBeusing/Groundwork/pull/21), [Simulation #18](https://github.com/DaveBeusing/Groundwork/pull/18) |
| L3A / L3B | [ECS #19](https://github.com/DaveBeusing/Groundwork/pull/19), [Jobs #20](https://github.com/DaveBeusing/Groundwork/pull/20) |
| L4 | [Windows CI and packaging #13](https://github.com/DaveBeusing/Groundwork/pull/13), [immutable archive #14](https://github.com/DaveBeusing/Groundwork/pull/14) |
| L5 / L6 | [Diagnostics #26](https://github.com/DaveBeusing/Groundwork/pull/26), [WYU host #4](https://github.com/DaveBeusing/wyu/pull/4) |
| Integration fixes | [Deadline #22](https://github.com/DaveBeusing/Groundwork/pull/22), [borrow drain #23](https://github.com/DaveBeusing/Groundwork/pull/23), [capture stop #25](https://github.com/DaveBeusing/Groundwork/pull/25), [composed recovery #28](https://github.com/DaveBeusing/Groundwork/pull/28) |

- Keep game composition in WYU and rendering behind the engine abstraction
  described in [module boundaries](architecture/module-boundaries.md). WP02
  requires a separately reviewed renderer contract and an exact package pin
  update when its engine packages are available.
- WYU exercises an empty ECS world. Composed recovery with an authorized
  continuation policy is verified in the engine fixture. WYU uses the default
  diagnostics policy and fail-closed shutdown for a controlled critical fault.
- The recorded performance samples cover bounded foundation operations. The
  representative full-scale workload and target hardware run remain separate
  performance work.
- Groundwork trackers #2–#7 are still open as of this handover. The table
  records landed implementation, and those trackers retain their own closeout.

## G00 acceptance checklist

- [x] Separate Windows checkouts passed their clean build and test matrices.
- [x] WYU opened and closed a real window from the immutable package pin.
- [x] Shipping dependency and namespace checks preserved repository boundaries.
- [x] Timing, cancellation, recovery and terminal fencing matched the contracts.
- [x] The exercised cleanup, deadline and catch-up cases found no blocker.
- [x] Package provenance and performance samples were recorded with reproduction steps.
- [x] The required lane PRs are merged and the handover reflects their implemented contracts.
- [ ] Owner acceptance of the reviewed G00 handover commit.
