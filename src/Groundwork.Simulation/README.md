# WP01-L2 fixed-step Simulation

Compose `FixedStepSimulation` with the shared engine context, an input source,
the world coordinator's `ITickPipeline`, its safety state and the retained
simulation wake. Call `AdvanceAsync` from one coordinator. Wait for the returned
timestamp or the retained wake between calls. A timestamp already due means
there is enough debt to advance immediately. Paused and stopped iterations have
no time-based wake.

Use the runtime reporter with the default null fault scope. When the context
uses a world reporter, pass that reporter's `WorldId` as `faultWorld`. Fault
scope is fixed before the first input capture, including failures that happen
before a barrier returns its stamp.

The defaults run at 60 base steps and 20 gameplay steps per simulated second,
with at most five steps per iteration and a 250 ms backlog. Input is captured
once per tick. `BeginTick` establishes the state for gameplay and base work,
then `CommitResults` precedes observer publication. Time is debited only after
the observer swap succeeds. The returned debt fields are exact scaled values
in the units defined by the [Core API freeze](../../docs/architecture/wp01-core-api-freeze.md#tick-handoff-and-public-simulation-operations).

`RequestPause` and `RequestResume` control user pause independently of recovery
pause. The first paused iteration accounts for its final running sample. On
resume, retained debt is serviced from a fresh clock origin. An interrupted
active tick stops without replay. Cancellation between ticks returns the
iteration's settled metrics, including time already dropped at the cap. The coordinator may inspect
`LastConfirmedBarrier` after an iteration returns or throws. A throwing pipeline
callback can leave a later writer revision, so the pipeline owns authoritative
failure reporting for its internal state.

### Host recovery boundary

For resumable recovery, the coordinator applies a recovery pause after a tick's
observer frame has been published and `AdvanceAsync` has returned, before
starting another tick. Configure `MaxStepsPerIteration = 1` when the host needs
to review evidence between every tick. Use the current writer-owned
`CommitResults` stamp and pending fault generation to open the Core review.
During the pause, the committed writer state and last published observer frame
remain visible. Pending evidence and the pause fence subsequent publication.

Drive a paused iteration before attempting recovery so the driver can account
for the final running sample and exclude time spent paused. `RequestResume`
only clears user pause. To continue, the host must establish isolation and
coherence, then obtain and apply a fresh Core-authorized continuation for the
current stamp and generation. The default diagnostics policy classifies a
recoverable job fault as pause on each review. A host that supports recovery
must configure a policy when opening the world that can verify the conditions
for continuation.

An interrupted active tick is terminal. The host must fence and settle that
world while preserving the prior observer frame and the last confirmed writer
barrier for diagnosis. Parking an active callback through recovery would hide
the pause from the driver's time accounting.

## Verification

Run the solution gate for the test suites. Run the scheduler-only benchmark
from the repository root with the repository's .NET SDK.

```powershell
$project = 'benchmarks/Groundwork.Simulation.Benchmarks/Groundwork.Simulation.Benchmarks.csproj'
dotnet restore $project --configfile NuGet.Config --locked-mode
dotnet run --project $project -c Release --no-restore
```

The benchmark supplies one exact step per clock sample and synchronous empty
phase callbacks. It warms up 20,000 ticks and measures 100,000 ticks, reporting
elapsed time and current-thread allocated bytes. It measures the scheduler and
Core state reads. World simulation, asynchronous work and Windows event pump
costs depend on their respective lanes.
