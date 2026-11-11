# WP11-Q01 workstation capacity smoke

This report records the first Q01 fixture run from Groundwork master 730f62099e0adbe1aec8cc9425a707fdccf5bcb3 plus the measurement instrumentation at 43ce8ae6ed8c90d724a4e51ec2c8a299c4734503. The checkout was clean at capture. The measurements qualify the individual fixture assertions. Production limits remain open in [#88](https://github.com/DaveBeusing/Groundwork/issues/88).

## Reproduction

From a clean checkout on an interactive Windows x64 desktop, run:

```powershell
pwsh -NoProfile -File benchmarks/Measure-Wp11TerrainCapacity.ps1 -Samples 5 -Desktop -OutputDirectory (Join-Path $env:TEMP 'wp11-q01-workstation')
```

Supply a new output directory per run. The script records the build and machine metadata, per-run logs, observations and a nearest-rank summary. Omit `-Desktop` for the CPU-only cases. The Windows desktop fixtures require the D3D12 debug layer and DRED. The measurement switch adds output at explicit fixture boundaries. It does not add I/O to production simulation or rendering.

The workstation has an Intel Core Ultra 9 285HX (24 physical and 24 logical cores), 68,137,205,760 bytes of RAM, Windows build 26300.9711 and .NET SDK 10.0.403 with the 10.0.14 .NET 10 runtime installed. The D3D12 test selected NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU, driver 32.0.15.9653, and WARP 10.0.26100.9711. These are Release source builds with locked dependencies. No external package feed or production package binary was sampled.

Each suite starts a fresh test host for each sample and discards the first execution. JIT and fixture startup can still contribute to each observation. CPU pressure uses the existing fixed terrain fixtures and their explicit admission quotas. D3D12 uses the seed 42, radius 128 fixture and its masked replacement and rebase. Phase times are elapsed time between test boundaries. They include fixture work and any host or polling waits within those boundaries. There are five observations per row, so p95, p99 and maximum coincide.

## Observed capacity

| Fixture and boundary | Terrain CPU ledger bytes | Readers | Observed native mesh bytes |
| --- | ---: | ---: | ---: |
| Coordinator baseline | 24,597,184 | 2 | n/a |
| Coordinator speculation full | 49,194,368 | 2 | n/a |
| Coordinator first-edit class, old geometry retained | 73,791,552 | 3 | n/a |
| Coordinator recovery with retained generations | 98,388,736 | 5 | n/a |
| Coordinator after releasing old work and leases | 24,597,184 | 2 | n/a |
| D3D12 first and replacement fence observations, each adapter | 30,985,792 | 1 | 1,769,472 |
| D3D12 shutdown, each adapter | 0 | 0 | 0 |

Coordinator high-water was two density slots, two mesh slots, two terrain queue slots, 261 ledger lease slots and four retirement slots. Its native dimensions are synthetic credits. The D3D12 fixture recorded a 61,971,584-byte CPU ledger high-water during replacement and one live native mesh buffer at each sampled fence boundary. These native samples do not establish a device residency high-water. The largest sampled process private byte values were 94,937,088 for the coordinator, 113,217,536 for WARP and 262,504,448 for NVIDIA. Process bytes and the terrain ledger have different scopes.

The scheduler case established one blocked speculative worker and one queued speculative job. Another speculative admission was denied and the reserved worker completed its terrain generation. The coordinator case separately denied speculation at the configured boundary, exercised the first-edit and recovery work classes while retaining old geometry, then returned to one live generation. The ±1 fixture covered each resource dimension. The D3D12 case used the native renderer with fence-completion observation held by the test hook. It verified movement hold and resource retirement after release. The scheduler and D3D12 observations came from separate test runs. The D3D12 hook delays observed completion while the queue can execute, so it provides custody evidence for a delayed fence.

| Phase | p50 ms | p95/p99/max ms |
| --- | ---: | ---: |
| Coordinator initial publication | 1,285.88 | 1,391.74 |
| Coordinator first-edit work class | 895.73 | 1,051.43 |
| Coordinator recovery and movement reopen | 665.45 | 788.72 |
| Protected scheduler dispatch through completion | 799.75 | 906.36 |
| NVIDIA first fence observation | 1,337.14 | 1,475.73 |
| NVIDIA replacement fence observation | 1,008.73 | 1,192.38 |
| NVIDIA shutdown | 74.26 | 104.73 |
| WARP first fence observation | 1,354.10 | 1,848.09 |
| WARP replacement fence observation | 1,073.34 | 1,136.45 |
| WARP shutdown | 47.12 | 153.55 |

## Budget evidence and remaining gate

One staged coordinator unit charges 24,597,184 CPU bytes. The fixture observed four concurrent units, so its demonstrated occupancy needs at least 98,388,736 charged CPU bytes and the observed concurrent slot vector. This is a lower bound for this fixture. Reserving three retained units and four independent staging units gives its configured CPU ceiling of 172,180,288 bytes. Those capacities use the fixture's generated meshes and admission topology. For the sampled D3D12 geometry the native allocation was 1,769,472 bytes and the existing fixture allowed 4 MiB for each native geometry reservation.

The earlier C00 direct NVIDIA prototype tested full CPU queue and blocked GPU streaming together. Its 11 ms p99 and 16 ms maximum apply to the recorded frame-fence population, including recovery. The values above are fixture phase timings. See the [C00 evidence](https://github.com/DaveBeusing/Groundwork/issues/78#issuecomment-6778821188) and the WP11 contract's C00-4/R09.

The available workstation falls outside the specified 8-core/32-GiB reference class. The outstanding Q01 run needs the integrated walk/turn, edit, recovery and coverage-transition route with a full CPU queue and a GPU streaming block in the same run. It must record per-state allocation and frame/worker distributions, attempt and completion counts, and compare diagnostics-on/off and source baselines. Only those observations can set production latency, retry and resource ceilings. The current evidence leaves the Q01 acceptance gate open.
