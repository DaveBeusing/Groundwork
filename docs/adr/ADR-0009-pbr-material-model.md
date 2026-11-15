# ADR-0009 — Portable PBR material model and voxel surface identity

**Status:** Proposed for architecture review; not accepted or implemented by this document
**Date:** 2026-11-15
**Decision owner:** Portable rendering and voxel architecture; D3D12 shader integration; WYU content owner for palettes
**Baseline:** Groundwork master a8d9e94f3890299de24739688b6ff2582c53dbf8
**Trace:** GWE-TR-001 GFX-006; [implementation map](../architecture/rendering-parity-workplan.md)
**Related:** [ADR-0008](ADR-0008-render-pass-resource-custody.md), [ADR-0010](ADR-0010-texture-assets-residency.md), [WP11](../architecture/wp11-procedural-planet-contract.md), [WP13](../architecture/wp13-biome-rendering-contract.md), [WP17](../architecture/wp17-landform-contract.md)

## Problem and observed baseline

Today PlanetBrickStore stores nonzero ushort cell material identities and PlanetVolumeMesher emits VolumeVertex with a ushort Material. D3D12VolumeTerrain optionally resolves that identity to a packed vertex RGBA color via a color evaluator. Cube.hlsl uses vertex colors/tint, ambient/direct sunlight, shadow visibility and fog. A textured metallic-roughness material/BRDF pipeline is not established in that inspected path. Material IDs MUST NOT be described as wholly absent.

We need reusable, visually plausible materials for terrain, caves and props without converting every voxel to an ECS entity, introducing platform dependencies into portable code or breaking world/renderer publication coherence.

## Proposed decision

1. **Material identity is not a color.** Retain the existing canonical nonzero ushort voxel material ID for terrain geometry and persistence; zero remains reserved/invalid under existing contracts. Resolve IDs through a versioned immutable material-palette mapping. Do not renumber existing persisted generator IDs implicitly; a recipe change requires an explicit version and incompatible saves remain detected and preserved.
2. **Portable description.** Introduce a data-only material descriptor in Groundwork.Rendering (or an architecture-approved portable dependency location) with base color, metallic, roughness, normal strength, ambient-occlusion factor, emission and optional texture references. Validate all numeric ranges and color-space semantics before renderer admission. No native GPU pointers, D3D12 descriptor handles, WYU asset paths or game-only material names enter portable API.
3. **Physically based shading.** Initial opaque path uses linear-light metallic/roughness PBR: energy-conserving diffuse/specular partition, GGX/Trowbridge-Reitz distribution, Smith masking-shadowing and Schlick Fresnel with stable grazing-angle behavior. Fix and test the roughness floor, dielectric F0 convention, normal orientation, double-sided policy and invalid-input fallback before shader integration. Do not confuse microfacet roughness with legacy ambient intensity.
4. **Texture and color contract.** Base-color/emissive maps are sampled with the appropriate sRGB-to-linear conversion; roughness, metalness, occlusion and normal maps remain data/linear. Tangent-space conventions (including handedness/normal Y sign) are explicit for meshes. Texture identity and sampler ownership are supplied by ADR-0010, not embedded in authoritative terrain samples.
5. **Procedural surface coordinates.** Voxel terrain uses stable planet-canonical lattice/material identity to derive world-aligned triplanar coordinates at a defined physical texel scale. Avoid large single-precision world positions: perform canonical-to-local decomposition before GPU evaluation. Coordinates MUST remain continuous across all cube faces, mixed LODs, edits, rebase and near/orbit transitions. Weighted triplanar blending/normal mapping MUST use a documented basis and bounded sampling.
6. **Props and terrain share the BRDF, not necessarily the mapping.** Prop meshes may use conventional UV/tangent attributes while terrain uses triplanar mapping. Material palette selection and instance grouping respect existing WP16 budgets and immutable mesh publication.
7. **Presentation fallbacks.** Untextured and unavailable material assets produce a deterministic visible fallback; invalid material binding never mutates collision, generator state or save data. First implementation remains opt-in so current Package B/E/G/WP15 presentation tests can be compared unchanged.

## Rejected/deferred alternatives

- **Vertex colors only:** safe and fast but insufficient for detailed plausible surfaces; retain as fallback.
- **Store runtime GPU handles or texture names in each voxel:** rejected; violates renderer ownership, portability and sparse storage budgets.
- **Automatically replace ushort material IDs with wide handles:** rejected without capacity evidence; resolve through a palette, not larger per-cell storage.
- **Full material node editor, translucency, parallax tessellation and dynamic GI now:** deferred; they are separate rendering/workflow decisions.

## Implementation slices

- MAT-01: portable descriptor, palette validation, material ID mapping and explicit color-space tests; avoid native changes.
- MAT-02: fixed-reference analytic BRDF tests and opt-in untextured PBR shader with WARP image readback.
- MAT-03: canonical triplanar mapping and mixed-LOD/seam/rebase tests, then one textured terrain/cave reference fixture after ADR-0010's validated texture upload.

## Required evidence / acceptance

- Material identity is stable through deterministic terrain generation, 12 cube-face edges/8 corners, edits, save/Continue, and near/orbit handoff. Existing ushort samples and generator versions are not silently remapped.
- Parameter test matrix includes roughness and metallic sweeps, dielectric/metallic fixtures, normal map polarity, base-color sRGB vs linear, emissive and invalid NaN/out-of-range inputs. Keep golden images plus objective pixel/energy checks on WARP; repeat NVIDIA Debug Layer/DRED.
- No surface discontinuity, stretched local-float coordinates or incorrect triplanar normal at LOD seams/rebase. Voxel edits invalidate only the necessary render material/geometry caches for the committed publication; rejected work is invisible.
- Compare equal-geometry baseline/PBR at a frozen 1920×1080 reference camera, light and material set. Record GPU terrain-pass incremental p95/p99, CPU submission, texture fetch/bandwidth, descriptors and resident bytes. Publish limits only after #88 reference measurements. Existing WP15 and WP16 budgets cannot be weakened.
- Groundwork remains game-neutral; WYU supplies recipe/palette choices via immutable exact-pinned engine packages, not cross-repository source linkage.

## Consequences / approval

Material identity and portable contracts become reusable foundations for caves, props, HDR and future lighting. Shading and texture bandwidth costs require explicit per-quality-tier limits. No complete PBR pipeline or benchmark is claimed. Four ADR-0007 specialist reviews, a sequential integration review and explicit owner sign-off are mandatory before this proposed decision becomes approved.