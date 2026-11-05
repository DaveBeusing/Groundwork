# WP01 L3A ECS handover

Register copy-out values in `ComponentRegistryBuilder`, freeze the catalog with `Build`, and open an `EcsWorld` using the runtime authority, supervisor policy, explicit `EcsLimits`, a shared `EcsStorageBudget`, and optional partition IDs. Keep the same budget across worlds and runtime restarts. Registered types are unmanaged and cannot contain pointers or native handles. Encoded values use the running process's native layout and are suitable for local jobs. They have no persisted or cross-platform wire format.

The composition root retains `EcsWorld` and its Core safety ports. Give Jobs `EcsWorld.Inputs` and pass only the dispatched input reader and result writer to workers. `Read<T>` returns a copied value or explicit status. `Query<T>` copies matching values in ascending slot order from a single immutable root. `PublishObserver` acquires a tracked owner before reading the current root and transfers the frame to Core on a successful swap. Consumers read `EcsObserverState` through Core's bounded previous/current observer leases and drop frame references before releasing the lease. Queries return owned copies without exposing backing storage. Host reads, staging and observer publication need Core operation capacity in addition to job activity.

The logical writer closes the phase and drains Core borrow pins before sealing candidates. `EcsWorld.Stage` admits one tracked plan per sealed barrier, validates against the barrier-entry root, detects conflicts across all eligible batches, and stages valid batches without publishing. Each rejected batch has an `EcsBatchIssue` with its source, reason, available target and conflicting source. The coordinator reports these issues and obtains any required supervisor decision before calling `Prepare` with the authorized safety generation. Call `FinalizePublication` on a prepared plan or `Abort` when the attempt cannot proceed. Dispose an unsettled plan to abort and acknowledge its ownership. Retain the plan until settlement. Core owns receipts, cancellation, sealing and the final root swap. A structural or mixed batch is deferred intact at `CommitResults` and revalidated at `BeginTick`. Existing entity destruction conflicts with every write to that entity. A partition membership write conflicts with another batch's write to any existing member of that partition at the barrier entry. Ordinary writes to disjoint members remain independent. Unpartitioned creations get distinct IDs in the authority's canonical batch order.

Storage uses a persistent sorted slot map, immutable per-entity cell maps and immutable partition membership sets. Per-component tombstones preserve absence versions until the entity is destroyed. A destroyed slot can be reused only at the next generation. Its maximal generation retires it permanently. The smallest available slot is reused first. Point lookup is logarithmic in slot count plus component lookup, and a sequential query visits all slots, including retired or empty ones. A borrow pins its exact root until the copy completes and the owner disposes the input. Snapshot jobs copy only authorized declared targets and retain their captured versions after releasing the root pin.

The logical world byte charge includes root, slot, partition, membership, and cell metadata estimates plus component payloads. Opening a world verifies the retained-world limit against the per-root budget, bounded admitted job histories, two frames per observer lease, and current/staging roots. It reserves the configured retained-world limit in the shared storage budget before creating a root. Keep the `EcsWorld` until Core reports the world or runtime released, then call `TryReleaseStorage` to acknowledge reclamation. It verifies that Core has cleared the committed source and releases the charge once. Leave the charge in place through pending shutdown and custody. Budget admission and usage do not consult Core gates, so an unreleased world can consume capacity without blocking unrelated admissions. These charges are estimates of lane-owned live data under the capability ownership rules rather than measurements of the managed heap or application-owned query copies. Staging uses persistent roots and temporarily allocates replacement paths. The first implementation favors isolation and deterministic ordering over dense component iteration.

## Local verification and baseline

Run the project on Windows with the repository SDK and NuGet configuration.

```powershell
dotnet restore tests/Groundwork.Ecs.Tests/Groundwork.Ecs.Tests.csproj --configfile NuGet.Config --locked-mode
dotnet test tests/Groundwork.Ecs.Tests/Groundwork.Ecs.Tests.csproj -c Release --no-restore --filter 'TestCategory=EcsBenchmark' --logger 'console;verbosity=detailed'
```

The `EcsBenchmark` case measures 1,024 registered positions, 64 repeated point lookup and query passes, and deletion of all 1,024 entities. It reports thread-local allocated bytes, logical live bytes and an approximate post-collection live heap delta. The local L3A working snapshot on .NET runtime 10.0.14, Windows NT 10.0.26300.0 and 24 logical processors produced the following single run.

| Operation | Operations | Approximate throughput | Allocated bytes |
| --- | ---: | ---: | ---: |
| Create and add | 1,024 | 292,630/s | 1,475,184 |
| Point lookup | 65,536 | 1,573,977/s | 0 |
| Sequential query row | 65,536 | 3,732,288/s | 6,896 |
| Delete | 1,024 | 480,030/s | 1,327,728 |

The populated root reported 463,104 logical charged bytes and an approximate 245,904-byte live heap increase. This in-process storage baseline excludes the scheduler, barriers, and rendering. It has no pass/fail throughput threshold.
