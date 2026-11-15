# ADR-0008 — Rendering pass contracts and GPU resource custody

**Status:** Proposed for architecture review; not accepted or implemented by this document
**Date:** 2026-11-15
**Decision owner:** Rendering architecture / D3D12 integration; affected Core safety, diagnostics, WYU consumer
**Baseline:** Groundwork master a8d9e94f3890299de24739688b6ff2582c53dbf8
**Trace:** GWE-2026-001 finding F07; GWE-TR-001 GFX-004; [implementation map](../architecture/rendering-parity-workplan.md)
**Existing constraints:** [ADR-0001](ADR-0001-repository-boundaries.md), [ADR-0005](ADR-0005-diagnostics-recovery.md), [ADR-0007](ADR-0007-solo-review-governance.md), [WP02](../architecture/wp02-d3d12-renderer-contract.md), [WP11](../architecture/wp11-procedural-planet-contract.md), [WP15](../architecture/wp15-sunlight-shadow-contract.md), [WP16](../architecture/wp16-instancing-streaming-contract.md)

## Problem and evidence

The existing D3D12 backend records an explicit finite rendering sequence with a single render owner, established resource-state transitions, bounded admission and fence-based retirement. GpuPipeline.cs, GpuShadows.cs, GpuRetirement.cs, D3D12RenderOwner.cs and WindowsSwapChain.cs show these mechanisms. Groundwork already renders terrain, shadows, sky and props; it is not a blank renderer.

Adding material textures, HDR, multiple lights and postprocess targets increases the number of cross-pass resource dependencies. An unstructured extension would make ownership and hazard review expensive. No measurement currently proves that a full render graph is faster or necessary.

## Proposed decision

1. **Preserve the existing renderer and its safety authority.** Add an explicit, validated rendering pass and resource description before any replacement of execution scheduling. The near/far rendering owner, committed terrain generation, renderer epoch, presentation view and existing GPU-fence retirement authority remain authoritative. The pass description MUST NOT initiate new simulation, voxel or collision publication.
2. **Pass contracts.** Each pass definition MUST declare a stable pass identifier, ordered inputs and outputs, read/write intentions, required resource/subresource states, clear/load/store behavior, viewport extent class, and whether the output survives to a later frame. No pass may read an output absent from its declared dependencies. Invalid or cyclic plans fail before native command submission and preserve the last coherent presentation state.
3. **Resource contracts.** Every GPU resource/descriptor is represented by a backend-owned typed, generation-checked identity. Descriptions carry format, dimensions, usage, residency/admission class, device/renderer ownership and last-use fence. Portable Groundwork.Rendering contracts MUST NOT embed D3D12 pointers or depend on Windows. A missing/invalid identity is rejected, not silently aliased.
4. **First execution model: validated ordered passes.** Keep explicit pass submission on the current D3D12 direct queue. Build a minimal pass dependency validator and integrate existing sky, terrain and shadow passes without introducing a new scheduler or transient-alias allocator. Consider a full DAG scheduler/render graph in a **separate measured ADR** only if added passes demonstrate a clear, reproducible maintainability and/or CPU/GPU improvement.
5. **Custody and barriers.** Validate per-subresource transition requirements and read/write hazards against the declared pass order. Descriptor replacement, staging, texture mip mutation, resize, device loss and teardown MUST reuse the existing explicit GPU-fence ownership model. Submission with an unassigned/failed signal MUST NOT make resources reusable. Only proven queue retirement or confirmed device removal allows release under existing policies.
6. **World publication coherence.** Passes that sample terrain, shadows, material textures or related caches MUST refer to one committed world/view/generation and the correct render origin. A stale candidate or a rejected material/texture upload MUST NOT appear in the first Present of a newer generation.
7. **Finite work and diagnostics.** Validation and staging MUST be bounded and allocation-aware; no synchronous file I/O, shader compilation or forced CPU/GPU wait on ordinary frame paths. Expose opt-in per-pass names, GPU timestamp markers and resource occupancy to the existing diagnostics/benchmark facilities in #88, without inventing benchmark results.

## Alternatives

- **Continue only ad hoc call ordering:** minimal immediate code; rejected as the default expansion strategy because pass state and ownership would be under-specified.
- **Install a full Render Graph immediately:** could simplify transient allocation and automatic barriers; deferred because it would modify working ownership and render scheduling before a workload or benchmark justifies it.
- **A minimal declaration/validation layer with ordered execution (selected proposal):** incremental, testable, and compatible with the existing retirement model; costs some metadata and validation code.

## First implementation slices

- RPA-01: portable pass/resource descriptors and a pure graph/hazard validator. No native execution changes.
- RPA-02: adapter from the existing fixed terrain/sky/shadow execution order to the validated plan. Preserve exact ordering, resource transitions, fallback and output.
- RPA-03: optional resource-lifetime diagnostics and a narrowly scoped transient-target prototype if HDR later needs it. Do not merge speculative aliasing without measurements.

## Required evidence / acceptance

- Portable tests reject missing producer, duplicate write without dependency, cycle, illegal read/write state, missing clear/load source and stale resource generation; accept valid ordered and branched plans.
- WARP readback compares baseline and validated execution for clear, sky, terrain, shadow, edit-first-Present and near/far handoff. Preserve identical pixels where inputs/pipelines are unchanged; document intentional differences.
- WARP and NVIDIA with Debug Layer and DRED exercise resize, occlusion, held upload, stale mesh/texture generation, device removal and stop; zero live GPU child objects at successful shutdown. No regression in fence retention tests.
- Paired Windows Release benchmark on the approved reference class records CPU pass-plan/record time, GPU pass times, frame p50/p95/p99, allocations, draw counts, GPU memory and additional barriers. Hard new budgets must be adopted using #88 evidence; do not assert performance parity from functional tests.
- Existing WP11, WP15 and WP16 performance, upload and safety limits remain unchanged, including zero extra terrain brick requests from camera/material-only changes.

## Consequences / rollback

Positive: explicit contracts for postprocessing and future material passes; easier correctness and profiling. Negative: additional metadata and potential validation cost; an initial ordered approach does not optimize pass overlap. Rollback is confined to new pass description/adapter code; no forced conversion of existing worlds, saves or Game APIs.

## Approval gate

This ADR is a proposal. Obtain the ADR-0007 four independent specialist reviews, sequential integration review and explicit owner acceptance of the exact final head SHA. No implementation issue is authorized for merge by this document alone.