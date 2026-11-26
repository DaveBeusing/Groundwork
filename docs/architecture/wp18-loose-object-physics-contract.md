# WP18 · Loose-object physics contract

Groundwork owns the bounded sphere simulation and the integration with [WP11-C02 terrain collision](../../src/Groundwork.Voxels/VolumeCollision.md). WYU owns the repeatable loose-rock scene, its inputs, and presentation. WP16 instancing is optional. The same planet terrain interfaces apply to supported generators. Body-to-body collision, player interaction, vehicles, fracture and durable body persistence are follow-up work.

## Step and state

- Advance once per finalized 60 Hz base step with a fixed duration of 1/60 s. A pause, render frame, maintenance barrier or rejected publication advances no physics time. Never accumulate missed displacement for a held body. Inputs enter at tick boundaries in stable body-ID order. Index and queue traversal must give the same observable ordering regardless of insertion order or worker completion.
- Each body has a stable ID, positive radius, canonical planet-centered position, orientation, linear and angular velocity, friction, restitution, sleep counter and state. Validate finite inputs, normalized orientation, coefficients in [0,1], and radius within the supported query limits. Use the existing planetary gravity model. Integrate gravity before sweeping this tick's displacement. Freeze fixture initial states, speed limits, sleep thresholds, quantization and numeric rounding rules before accepting the implementation. Replay compares the quantized committed state sequence, including flags, hold reasons and wake timing.
- Sweep a sphere as a capsule with coincident endpoints. Test that representation against walls, ceilings and LOD transitions. Extend C02 if its degenerate-axis path cannot satisfy those fixtures. A contact uses the outward normal and time of impact from the published mesh. Apply restitution to inward normal velocity and friction to tangential slip, transferring tangential impulse to rotation so rocks can roll. The frozen response parameters must give stable resting contact without bounce at low speed.
- Allow at most two bounded sweeps per serviced body per tick, including support, overlap or wake checks. A second sweep may resolve the remaining displacement after a first contact. A second contact ends translation for that tick at the last verified pose. No unchecked displacement or depenetration. Initial overlap may hold the body until a later publication permits a complete valid step.

## Publication and coverage

- Add a synchronous C02 tick-read scope backed by preallocated reader slots. After the matched-frame and later BeginTick motion-admission gate, acquire one slot for the current committed collision publication under the coordinator's ownership guard. Return an explicit hold on quota exhaustion, stale barrier, missing coverage or revoked admission. Charge the existing reader quota and retain the generation exactly as for a regular reader. The scope must allocate no lease, closure, delegate or boxed state per tick after warm-up.
- Use a stack-scoped query view inside the synchronous read scope. It cannot escape, cross an await or survive scope exit. Release the slot in `finally`, including on exceptions and cancellation. Stop rejects new scopes and waits for outstanding readers before reclaiming geometry. Retain only value identities and the proposed body states after release. Revalidate the originating barrier, generation and terrain revision during CommitResults. Publish accepted body results with the same root as ordinary gameplay and player movement. When terrain supersedes that publication, discard its physics proposals.
- Verify acquisition, quota rejection, stale or stopped access, exception cleanup, and stop with a held scope. Include acquisition and release, along with accepted and held paths, in the warmed allocation and step measurements.
- On missing coverage, invalid geometry or input, query budget exhaustion or unresolved overlap, retain the body's last committed pose, velocities and sleep counter. Record a bounded diagnostic reason. Retry against the next eligible publication without replaying missed ticks. A failed second sweep holds the entire proposal. Terrain streaming and edits retain their existing priorities and quotas. Physics issues no terrain requests and never waits for loading or a GPU fence.
- Store simulation and contact-index coordinates in planet space. A pure render-origin rebase changes only local presentation transforms. It does not re-index or wake a body. Present bodies and terrain with the same frame origin. Never interpolate between transforms expressed in different origins. A publication mask or uncovered region cannot be bypassed by carrying forward an old collision lease.

## Sleep and admission

- Enter sleep only after confirmed support and consecutive accepted steps below the frozen linear and angular thresholds. Index each sleeping sphere by its canonical support and volume footprint. Advance no sleep timer on held steps. Sleeping bodies run no periodic contact probes.
- Consume committed terrain changes, including the affected mesh and collision halo, rather than pending brush inputs. Schedule only sleepers whose indexed footprint intersects that affected region. If streaming replaces support or removes its coverage, mark affected sleepers held and recheck against valid coverage before allowing motion. A coarse bounds query can find candidates, but unrelated sleepers stay asleep. During an edit storm deduplicate by body ID and retain the newest required publication.
- Use fixed-capacity body slots, index and wake queue. Keep pending or coverage-blocked bodies held, and keep wake debt until it can be serviced. Schedule eligible entries in stable order without allowing a coverage-blocked body to starve covered bodies. Reserve sufficient work for 32 of the 128 eligible rechecks per tick in the wake fixture. A recheck uses the same sweep budget and counts toward serviced active work. Reject new bodies explicitly at capacity. Overflow cannot silently drop a required recheck or displace protected movement or edit work.

## Work and acceptance

| Work | Gate |
| --- | --- |
| Portable simulation | Freeze response and replay fixtures. Verify fall, bounce, rolling, rest, input-order determinism, fast wall impact, seam and ceiling contacts, initial overlap, second-sweep failure and held-state recovery. |
| Terrain and lifecycle | Verify edit-triggered selective wakes, more affected bodies than fit in one tick, repeated edits, support replacement and eviction, missing coverage, exhausted budgets, stale publication, stop, and rebase with active, sleeping and pending bodies. Existing terrain collision, movement and edit regressions must pass. |
| WYU and handover | Archive green Groundwork packages immutably and pin their exact version. Run the loose-rock scene on Windows from clean master checkouts through sleep, terrain edit, wake, unavailable coverage and rebase. Compare matched live and recorded playback runs. Record workstation functionality and reference qualification separately. |

### Reference collision mesh

Use one complete 4 × 4 × 4-brick collision publication per covered performance scenario. Each brick spans 32³ lattice cells at a 1 m cell size. In local brick order use LOD 0 for the first two X columns and LOD 1 for the remaining two (32 bricks at each LOD), with the actual 2:1 transitions present. Measure across all eight 2 × 2 × 2-brick mesh groups. The fixture must contain at least 4,096 collision triangles, including transition triangles, and include a traversable slope and a wall. Place active paths on both sides of the transition and include crossings. Use the same plan and collision mesh for live and playback within each scenario. For coverage-loss and wake scenarios, freeze the publication changes and their tick schedule too.

Before collecting gate data, record and freeze the generator and recipe or density fixture, brick keys, LOD map, per-group regular and transition triangle counts, collision geometry digest, body inputs and query limits. A changed fixture requires new paired results. A bounded acceleration structure may prune candidate triangles without simplifying the collision mesh. Verify matching statuses and quantized contact results against C02 on the mixed-LOD fixture, including seam ties and budget exhaustion as a separate failure case. Include acceleration storage and scratch in the memory gate.

For release or production activation, measure in Windows x64 Release on the WP11 reference class. Use the same route, camera, visible models and rendering settings for live physics and recorded playback. Run at least five repetitions of at least 10,000 measured ticks per scenario after warm-up. Report sustained active load, sleeping bodies, edit wakes and unavailable coverage separately. Include accepted and held tick counts, sweep counts and reasons alongside timing and memory. Measure the full physics step including queries and wake processing. Merge and unqualified preview follow [ADR-0011](../adr/ADR-0011-reference-measurement-policy.md).

| Metric | Gate |
| --- | --- |
| Step latency | p95 ≤ 1.0 ms and p99 ≤ 2.0 ms with 128 active and 1,024 sleeping bodies. Wake processing also meets p99 ≤ 2.0 ms. |
| Contact work | At most two terrain sweeps per serviced body per tick, within existing per-query limits. Zero contact queries for unaffected sleepers. |
| Wake admission | All 128 affected sleepers admitted to recheck within four ticks when coverage and active capacity are available. |
| Memory and streaming | At most 8 MiB of incremental peak CPU memory, including body state, index, queues and scratch. Zero physics heap allocations per tick after warm-up and zero additional terrain brick requests. |
| Integrated CPU frame time | At most +5% p95 and +10% p99 versus playback. Saturation preserves the WP11 protected movement and edit limits. |

Apply [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88) and [#89](https://github.com/DaveBeusing/Groundwork/issues/89) for reference limits and pressure cases. WP15 standard activation and #89 remain open.

[Groundwork #116](https://github.com/DaveBeusing/Groundwork/issues/116) · [WYU #46](https://github.com/DaveBeusing/wyu/issues/46)
