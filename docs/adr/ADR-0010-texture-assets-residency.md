# ADR-0010 — Deterministic texture assets, asynchronous upload and GPU residency

**Status:** Proposed for architecture review; not accepted or implemented by this document
**Date:** 2026-11-15
**Decision owner:** Asset/Rendering contracts, D3D12 GPU lifetime, diagnostics; WYU asset-content integration
**Baseline:** Groundwork master a8d9e94f3890299de24739688b6ff2582c53dbf8
**Trace:** GWE-TR-001 GFX-005 and GFX-012; [implementation map](../architecture/rendering-parity-workplan.md)
**Related:** [ADR-0008](ADR-0008-render-pass-resource-custody.md), [ADR-0009](ADR-0009-pbr-material-model.md), [ADR-0006](ADR-0006-packaging-release.md), [WP11](../architecture/wp11-procedural-planet-contract.md), [WP16](../architecture/wp16-instancing-streaming-contract.md)

## Problem and observed baseline

The renderer already provides bounded terrain and prop upload admission, resource retirement and diagnostic hooks. It does not establish a general-purpose cooked texture asset pipeline, mip streaming, texture-residency service or stable material texture binding in the inspected repository tree. Texture work must coexist with protected collision/edit publication and the current exclusive D3D12 owner.

## Proposed decision

1. **Immutable cooked texture contract.** Build an offline, deterministic conversion pathway from validated source images into versioned cooked assets; keep importer/cooker outside render/simulation hot paths. Each cooked asset records a content SHA-256 over canonical source bytes, importer recipe and version, format and dimensions, mip layout, color-space metadata and output bytes. Repeated cook on the same toolchain/input MUST produce the same output digest; toolchain/compiler identity is part of reproducibility evidence.
2. **Explicit formats and interpretation.** Specify accepted dimensions, mip counts, array layers, pixel-size ceilings and source corruption behavior before import. First D3D12 tranche supports a minimal tested subset (e.g. RGBA8 and device-supported BC1/BC3/BC5, with BC7 only after verified support/tooling). Color/emissive maps are sRGB; normal, height, roughness, metallic and AO are linear data. Mip generation preserves semantic color and normal-map convention. Invalid images fail closed and do not allocate GPU resources.
3. **Separation of identities.** Persistent assets use content/version identity; runtime GPU texture and descriptor handles are device-local, typed and generation checked. No saved game or voxel cell stores a GPU pointer, residency token or mutable descriptor slot. Palette references immutable asset IDs; replacement changes an explicit versioned binding.
4. **Bounded asynchronous upload.** Decode/import off the render thread. Stage mips through accounted, finite CPU/GPU allocations and per-frame transfer quotas. Reuse existing admitted D3D12 copy/submission and fence-retirement policy; do not create untracked queues or release uploaded/staging data before the last copy or draw use retires. A failed signal or device removal follows existing fail-closed custody semantics.
5. **Incremental residency.** Initially guarantee a defined lower-resolution usable mip for a resident visible texture, with higher mip levels requested asynchronously according to view distance, priority and budget. Eviction retains descriptor validity: replace with a stable fallback or a lower mip only when safe for in-flight frames. Headroom, priorities, retry and GPU-memory limits are determined with #88 reference measurements; do not invent an engine-wide VRAM number in this ADR.
6. **Protected-world work first.** Texture and visual-only uploads MUST NOT take CPU job, GPU upload or memory credits reserved for collision, terrain edits/recovery or the committed geometry needed for movement. Respect WP16's existing prop upload limit and count texture/prop/terrain traffic separately. A texture miss degrades visuals safely, never bricks movement or initiates extra terrain brick requests.
7. **Coherence and failure UX.** An asset completion binds only to the requesting renderer/device epoch and matching material/terrain publication. Reject stale, superseded or wrong-world completions. When an image is missing, damaged or unsupported, present a documented visible placeholder and a bounded diagnostic while keeping authoritative state unchanged.
8. **Virtual texturing later.** Sparse/tiled virtual texturing and global unified GPU-memory compaction are deferred until profiling demonstrates need; do not add them merely to match competitor feature names.

## Alternatives

- **Load full-resolution images synchronously each time:** rejected; frame stalls and unbounded allocation risk.
- **Keep all textures fully resident:** useful for tiny fixtures, not a general large-world policy.
- **Implement virtual texturing first:** deferred pending measured demand and DX12 sparse-resource complexity.
- **Immutable cooked assets + bounded mip residency (selected proposal):** reduces hot-path uncertainty and supports incremental quality.

## Implementation slices

- TEX-01: portable descriptor/validation and deterministic offline cook test fixture; no D3D12 requirement.
- TEX-02: one bounded D3D12 upload/descriptor/fallback path with WARP readback and failure tests; initially use static complete texture mip chains where convenient.
- TEX-03: budgeted asynchronous mip promotion/demotion, eviction, residency diagnostics and backpressure under simultaneous WP11/WP16 traffic.
- INT-01: one WYU-independent reference terrain material and image/performance regression; WYU-specific recipes/content integrate in their own PR against an exact verified package set.

## Required evidence / acceptance

- Same source/tool/recipe yields identical cooked SHA-256 across clean runs; malformed/truncated/oversized/mismatched sRGB/linear inputs are rejected with controlled failures. No arbitrary path-dependent identity.
- WARP and NVIDIA Debug Layer/DRED verify sRGB sampling, mip LOD selection, texture replacement, stale descriptor rejection and fallback pixels. Inspect readback images at near, far, rebase, edit and renderer handoff.
- Inject upload quota exhaustion, concurrent terrain edit, paused/cancelled transfers, resize, device removal and shutdown. No use-after-free, unbounded queue/memory growth, unobserved descriptor reuse, blocked movement or protected work.
- A/B reference-class measurements record upload bytes, queue waits, resident/retired CPU/GPU bytes, texture mip counts, draw/pass GPU p95/p99 and high-water per view/scene. Separate WARP functional evidence from hardware performance proof. No hard budget is asserted before #88 baseline.
- Preserve immutable package and WYU locked restore boundaries from ADR-0006; do not introduce undeclared third-party tooling or assets without license/provenance review.

## Consequences / approval

Requires importer and asset-validation work, stable GPU descriptor ownership and observable memory budgets. Enables textured PBR, HDR and larger visual environments without making world state dependent on graphics residency. Four independent ADR-0007 specialist reviews, sequential integration review and explicit owner sign-off are required before accepting the decision or merging implementation.