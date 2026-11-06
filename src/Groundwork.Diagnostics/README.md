# WP01 diagnostics and supervision

Create the Core safety authority before starting producers. The `RuntimeSafetyAuthority.Create` overload with `runtimeDiagnosticRecords` reserves that many normal records from the runtime quota for host events. It deducts their record count and maximum payload charge before admitting worlds. The original overload leaves all normal capacity available to worlds. Each world receives its own normal queue and reserved emergency state from Core.

Give producers `IFaultReporter`, and give the host the matching `IDiagnosticReader`. Report lifecycle events with `FaultClass.DiagnosticOnly`, a stable caller code, and the appropriate subsystem, origin, timestamp, and `FaultContext`. Core records the first required primary and cleanup summaries independently of the normal queue. It can also queue subsequent summaries for optional delivery. Queue overflow or contention increments delivery loss. The required evidence ledger and its publication fence remain independent.

`DiagnosticDispatcher` is a host-owned adapter from one Core diagnostic reader to one `IDiagnosticSink`. Pair a runtime reader with runtime ownership, or a world reader with that world's ownership. Construct the dispatcher before admission closes. Call `DrainAsync(maxEvents)` on the host observation path to schedule a finite batch on a worker. It prioritizes each first primary and cleanup summary, then drains normal events in FIFO order. A normal event remains charged until delivery completes. Use one dispatcher per reader. An overlapping drain returns zero, including a call from the sink itself. A throwing sink is disabled and its numeric error code is recorded directly in the separate emergency sink slot. The host can observe these summaries and safety state independently of sink delivery while Core retains them.

Sink events contain only structured values. They omit caller text, exception messages and stack traces. Preserve `RuntimeFailure.Primary` and `RuntimeFailure.Cleanup` for the operational path. Core's required evidence includes affected resource keys for supervision. Timestamps keep the producer clock domain. Jobs uses system-monotonic ticks, so correlate across domains using the structured identities or an explicit clock mapping.

At shutdown, optionally schedule a last drain and stop the dispatcher with Core's shutdown deadline. The dispatcher closes delivery and cancels sink work. It returns incomplete cleanup if the shared deadline expires. The Core ownership lease stays registered until the worker and cancellation callbacks actually finish. Call Core settlement independently of sink delivery. There is no automatic flush, retry, file I/O or external backend.

## Safe-barrier review

Construct a `BarrierSupervisorPolicy` per world with a scan limit equal to `DiagnosticQuota.RequiredEvidenceRecords`. It reads the admitted ledger one record at a time without a second evidence buffer. The host supplies the policy when opening the world. The logical writer issues a proof only after checking coherent committed state and isolating failed dependencies. The coordinator starts the review through `world.Coordinator.TryBegin`, requests a decision through `world.Supervisor`, and applies it through `world.Coordinator.TryApply`. Core authenticates the request and rechecks stamp, generation, deadline and terminal fences at application.

Cancellation, dependency failure, stale results and write conflicts can continue only with that current proof. Recoverable job faults and invalid access pause. Missing, stale, lost or unrecognized evidence and critical faults stop. Use `CompleteUnverified` when the writer cannot establish coherence. On a verified request that fails to produce a decision within budget, use `CompleteWithoutDecision` to fence recovery at that barrier. Keep caller cancellation and shutdown separate. There are no automatic retries or conflict overrides.

## Metrics and baseline

Use `DiagnosticMetrics.Disabled` or allocate one fixed-slot recorder at composition. A producer records without waiting for a contended slot. The host reads coherent per-metric samples, total, latest, saturation and optional drop count.

| Measurements | Unit |
| --- | --- |
| `TickWorkNanoseconds`, `TickBacklogNanoseconds`, `TickDroppedNanoseconds` | ns |
| `JobQueueNanoseconds`, `JobWorkerNanoseconds`, `JobDispatchNanoseconds` | ns |
| `FrameWorkNanoseconds` | ns, reserved for future frame work |
| `CompletedTicks`, `RejectedBatches`, `DependencyCancellations`, `RecoveryPauses`, `FatalStops` | count |

The composition root measures tick work around the tick driver, reads backlog and drop from `SimulationIteration`, and records their converted nanoseconds. Divide Simulation scaled values by `BaseRateHz * TimeSpan.TicksPerSecond` to recover clock units before conversion, or carry out the complete conversion in `Int128` to avoid intermediate rounding. For Jobs, record deltas from scheduler snapshots using their `TimestampFrequency`, and report rejected batches and policy decisions at their authoritative completion points. Metrics never participate in a publication decision.

The Release baseline on Windows 10.0.26300 x64 and .NET 10.0.14 used an Intel Family 6 Model 198 processor with 24 logical CPUs, workstation GC, and a 10 MHz stopwatch. It used 20,000 warmup operations and five samples of 100,000 operations. Median costs were 3.80 ns for a disabled counter, 52.52 ns for an enabled counter, 245.60 ns for a duration sample, 141.17 ns for an optional record, acquire and release, and 74.68 ns for rejection with 15 queued records and one held lease. The delegate control measured 0.63 ns. Producer-thread allocation was 0.000 bytes per operation across those cases. These are single-producer workstation measurements. See the [benchmark instructions](../../benchmarks/Groundwork.Diagnostics.Benchmarks/README.md) for saving raw samples and comparing later runs.
