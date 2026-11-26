# Package G lookup diagnostic follow-up

Prepared 21 November 2026 against Groundwork base `985eaa25c3be0b682fd4c04df664146f46d4c62a`.
This is an experimental diagnostic candidate for [Groundwork #112](https://github.com/DaveBeusing/Groundwork/issues/112)
and [WYU #42](https://github.com/DaveBeusing/wyu/issues/42), not a qualified replacement for the direct atmosphere path.
The reference runner's manifest must identify the final source commits, actual assemblies, hashes and consumer configuration.

## X11D comparison on 23 November 2026

The measured renderer at `8943e96` and WYU `bd59baa` completed the synthetic
Direct fixture and all 240 native WYU flights across Direct and Lookup.
The synthetic Direct fixture passed. Integrated GPU timing remains
**CHANGES REQUIRED**. Direct failed 9 Far Horizon and 17 Orbit Combined
comparison rows. Experimental Lookup failed 3 and 8, respectively.

In Lookup Orbit Combined Surface pairs 1, 2 and 4, subtracting each enabled
frame's measured sky time still leaves p95 above the permitted whole-frame
ceiling. The two Direct Orbit Combined frame overruns merit a separate
pass-timing comparison. The companion adds opt-in markers for previous-owner
prop shutdown and renderer disposal. Preserve the original budgets and
collect both pass breakdown and scheduling coverage before attributing the
remaining costs.

The hashes, pooled and pair-level findings, and focused commands are in the
[WYU reference series report](https://github.com/DaveBeusing/wyu/blob/498ad40b3113b7ebe0ecbd56f8ab1304ad0ffbec/docs/dawn-twilight/reference-series-2026-11-23.md).


## Scope

- The default remains the existing Direct sky, pass order and terrain and prop rendering. Start the process with
  `GROUNDWORK_TWILIGHT_PREVIEW=1` to select the unqualified Twilight Preview. Add `GROUNDWORK_SKY_LOOKUP_PROTOTYPE=1`
  for the diagnostic lookup path and `GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES=1` to test storage reuse. The device captures
  these flags at creation. Direct Preview qualification requires the Preview flag and rejects the lookup flag.
- The prototype retains the fixed 256 x 128 radiance and transmission fields, the existing sky draw and its per-frame
  compute dispatch. Both fields are regenerated from the current camera, planet and celestial parameters every sky frame.
- Ground and outer-atmosphere boundary classification selects the original eight-view/four-solar-sample integration
  where interpolation would cross a geometric boundary. Sun and stars retain their full-resolution analytic paths.
- The accompanying lookup-only classifier avoids unnecessary root calculations strictly inside the outer sphere.
  Direct integration and hybrid fallback still use the original ground/intersection calculations. This changes neither
  sample counts nor the image acceptance limits.

## Session pipeline lifetime

`D3D12DeviceSession` now retains a separate, complete lookup triplet: root signature, graphics PSO and compute PSO.
Publication and acquisition occur atomically under the existing exclusive attached-session lock. Each owner acquires
its own COM references; disposing the session releases only the session's references, so an attached owner can finish safely.
The existing direct-sky cache has separate fields and cannot supply the lookup ABI.

LUT textures, descriptor heaps, frame constants, command allocators and fences remain owner-local. A pipeline cache hit
therefore does not mean resource preparation is complete. An explicit owner-ready state guards preparation, and failed
partial allocation releases that owner's resources without invalidating an already complete session cache.
There is no extra warm-up dispatch and no reuse of another owner's mutable LUT contents.

## First-use diagnostics

The optional internal `AfterSkyLookupPreparationStage` hook has signature `Action<string,long,long,bool>`.
It emits QPC start/end timestamps once per owner for `textures`, `descriptors`, `compute-pipeline` and `ready`.
The first two stages always report `reused=false`; reuse on the latter stages describes the compute PSO/complete immutable
pipeline triplet, not textures or descriptors. `ready` spans the whole preparation interval and is not a fourth additive component.
Graphics-root/PSO creation or acquisition remains observable through `AfterSkyPipelineReady`.

These observations remain inside the original sky-record and handoff boundaries. Warm frames return before observer
or timestamp work. Existing GPU/CPU split diagnostics retain the original total interval and do not subtract preparation,
barriers, per-frame updates or waits from an acceptance measurement.

## Local evidence

Local functional runs cover three sequential owners with one creation and two actual native-triplet reuses on WARP
and NVIDIA, direct/lookup mode isolation, session disposal while an owner remains live, partial allocation failure before
and after cache population, owner-local texture preparation, zero heap allocations in warm sky records and strict final
live-object cleanup. The cache/lifetime suite contains nine successful test cases; the capture/default suites contain four
successful cases on each backend.

The combined candidate also produced 212 SHA-identical payload comparisons: all 27 original V5 cases on both backends
with direct/lookup/fallback-mask data, ten additional sun-radius/FOV cases on both backends, and ten default renderer BMPs.
This is evidence for the tested inputs, not a new image tolerance or a performance result.

Retained local evidence paths, relative to the checkout:

- `artifacts/lookup-cache-combined-build.log`
- `artifacts/lookup-cache-combined-results/lookup-cache-combined.trx`
- `artifacts/lookup-cache-combined-warp-results/lookup-cache-combined-warp.trx`
- `artifacts/lookup-cache-combined-nvidia-results/lookup-cache-combined-nvidia.trx`
- `artifacts/lookup-cache-combined-evidence/`
- `artifacts/lookup-cache-combined-comparison.json`

The earlier rejected Sun/Stars culling experiment is not part of this change. Its nonidentical NVIDIA payloads remain
recorded separately in `artifacts/rejected-stars-sun-comparison.json`; they were not accepted by increasing a tolerance.

## Acceptance still open

The RTX 5070 diagnostic preceding this cache change left ascent/orbit GPU p99 and outward-handoff acceptance open.
It contained only one handoff observation per direction and variant. Repeated pipeline creation was directly observed,
but retaining it does not establish that the complete handoff or steady-frame GPU tails meet their limits.

The exact final candidate still requires the original 1920 x 1080 reference comparison with B and E enabled in both
variants, unchanged route/seed/presentation settings, at least five runs and 2,000 retained frames per variant and phase,
and at least 30 handoffs per direction and variant. Limits remain sky GPU p95/p99 <= 0.8/1.2 ms, total GPU <= +5/+8%,
handoff p95 <= +10%, and combined atmosphere preparation/recording CPU p99 <= 0.10 ms with no warm per-frame allocations.
Failed/cancelled attempts, warm-ups and denominators must remain visible.

The known LUT texture allocation is 1 MiB per owner on the tested adapters. This does not close the full 4 MiB incremental
GPU-memory requirement: descriptor/PSO/driver backing, overlapping or retiring owners, and the applicable WP11 pressure
cases still require their own evidence. Installed-package qualification, independent final reviews and required CI must
cover the final pinned engine/client state. No merge, release or performance qualification is established by this handover.
