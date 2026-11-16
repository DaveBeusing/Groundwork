# TEX-03 paired mip residency measurement

Run from a clean checkout on the Windows x64 reference machine reserved for #88. Keep the adapter, driver, SDK and workload fixed across comparisons. The harness records the machine configuration and source commit so that reference eligibility can be checked independently.

```powershell
./benchmarks/Measure-Tex03MipResidency.ps1 -Adapter Hardware -Pairs 5 -MachineLabel reference-candidate -OutputDirectory ./artifacts/tex03-reference
```

Use `-Adapter Warp` with a different output directory for a separate software run. A workstation run can use `-Pairs 1 -MachineLabel local-workstation` to check the harness. The default is five pairs, each in a fresh test host. Within each pair the two routes run on sequential devices, with fixed-first and tiered-first alternating between pairs. Locked restore and the Release build each run once. The output includes their logs, one TRX per pair, host metadata, raw frame observations and aggregate and per-pair summaries. Before pooling, the script checks each pair against the first pair's fixture, actual adapter and driver, runtime, process architecture, dimensions, frame counts, Stopwatch frequency, actual Debug Layer and DRED states, embedded shader hashes, texture identity and mip policy. A mismatch reports the changed field and pair. The accepted configuration appears in the summary. The test requests Debug Layer and DRED for the paired run.

Both routes render the same two-triangle terrain fixture at 640×360 with the same camera turns and one deterministic sRGB texture. There are eight warm-up draws per route, followed by 64 measured draws. Camera distance moves through 36, 16, 6, 16 and 36 meters. Fixed residency keeps an initialized 512×512 mip 0. Adaptive residency uses the production handoff policy to select initialized mip 2 at first use, promotes to mip 0 at 10 meters or closer, and demotes at 30 meters or farther. Each pair checks equal center color and depth and the expected copy count. `observations.json` includes the cooked asset ID, requested and sampleable mip, adapter and driver, timing, and bytes for each matched frame.

CPU record time spans render-plan recording and validation between the engine's prepared and recorded plan hooks. GPU terrain time uses the direct queue timestamp queries around the terrain pass. Turn time spans the handoff, any native submission and copy-fence wait, the draw, and synchronous offscreen readback. Copy wait includes polling and is sampled on copy turns only. CPU and wait samples retain Stopwatch ticks and the frequency. Summaries use the nearest-rank p50, p95 and p99.

Resident bytes are the aligned backing of the binding used for each frame. Candidate-plus-resident and staging high-water use the actual allocation sizes at copy submission. Retiring bytes are the versioned predecessor's aligned backing at installation, before actual release. Combined high-water is the simultaneous sum of candidate, resident and staging at a measured turn. Uploaded bytes count the initial measured copy and all subsequent measured copies, excluding warm-up and teardown. Descriptor high-water is the maximum number of charged version slots in the handoff ledger, including overlap during replacement. These measurements describe committed texture backing and upload payload on this fixture.

The harness drives the handoff and native copy/retirement APIs synchronously and exercises the production offscreen terrain path. Its timing scope excludes window Present, concurrent terrain or WP16 scheduling, protected-arrival latency and operating-system video-memory budgets.

## Protected edit latency under mixed visual load

Run in an interactive Windows desktop session. The command starts five fresh Release test hosts per adapter by default. Each sample establishes a drawable terrain view, a resident prop cell and a coarse texture before starting measurement.

```powershell
./benchmarks/Measure-Tex03MixedLatency.ps1 -Adapter Both -Samples 5 -MachineLabel local-workstation -OutputDirectory ./artifacts/tex03-mixed
```

A full 2 MiB texture copy triggers a prepared producer as it reaches the queue's signal call. A first-use texture, a texture refinement and a second WP16 prop cell are already staged. The producer submits a protected edit through the existing WP11 path. The measured run lets the native signal and completion proceed normally. The observer collects the edit's first Present and retirement timestamps from the resource ledger, joins them to the frame and fence, and checks the exact edited generation's authority receipt. The separate ordering regression still exercises a deliberately held signal.

The output includes host and source metadata, restore/build and per-sample logs, TRX files, raw QPC observations, resource accounting, and nearest-rank p50/p95/p99 by adapter. Use a fresh output directory and keep the host, driver and power settings fixed. Native Debug Layer and DRED are disabled by default for these timing samples. Use a separate output directory with `-NativeDiagnostics Enabled` for a diagnostic run. Resource snapshots remain enabled in both modes.

The raw timeline starts at native copy preparation and records the trigger just after Execute returns, before the queue's fence signal. The interval includes producer scheduling and fixture work. Copy completion means the first fence completion observed by the renderer. The summary groups actual mask admission observed before that mark separately from admission at or after it. An empty group has no latency estimates. Raw timestamps distinguish producer wake, protected mask and terrain-work admission, native staging, publication, Present retirement, and movement reopening at BeginTick. The workload covers one protected edit under concurrent visual demand. The broader #88 Windows reference route and production budgets remain open.
