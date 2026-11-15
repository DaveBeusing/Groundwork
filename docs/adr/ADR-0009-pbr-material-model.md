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


1. **Preserve canonical material identity.** The existing nonzero ushort identifies a voxel surface material. Zero stays reserved. The persisted `MaterialCatalogVersion` describes generator and catalogue semantics. Altering which canonical IDs mean what requires explicit recipe/catalogue versioning and the existing incompatible-save protection. An immutable presentation palette maps the same IDs to material descriptors. Give it a separate visual revision and pay no persistence or generator cost for appearance-only changes. Checkpoints and overlays remain byte-compatible for visual-only changes. Keep the existing WP13 canonical color evaluation as the legacy path.
2. **Use portable linear-light parameters.** Groundwork.Rendering owns immutable material and palette descriptors. Validate finite linear base RGB, metallic, roughness and AO in [0,1], finite nonnegative HDR emissive RGB and finite nonnegative normal strength. Define an opaque initial BRDF with GGX, Smith and Schlick, an explicit dielectric F0, roughness floor, sidedness and invalid-input rules before MAT-02 shader integration. MAT-01 texture references carry the canonical cooked ID syntax from ADR-0010 and the expected map semantic. They need neither the cooker nor native GPU state. Reject mismatched color spaces at binding. Use sRGB decode for base-color and emissive maps and linear data for roughness, metallic, AO and normals. Document tangent handedness and normal Y convention.
3. **Install one appearance set per frame.** The renderer stages an immutable palette and binding set. At the render-owned cut before recording and final submission authorization, validate the palette, requested asset version and target slot/group revision against the current renderer/device epoch and authorized world/view, terrain publication and origin. Preserve the producer-side first-content registration and its retained reader as specified in ADR-0008. Latch one immutable full-set revision per render submission. A request targets the revision of its affected binding slot or atomic group. Other slots may publish a new full set while that target stays current. Capture that set for every applicable pass and hold its exact descriptors and resources through last use. A completion for an obsolete set cannot bind even when its terrain generation is unchanged. A completion during recording waits for a later authorized frame. Reuse validated immutable bytes separately where safe. Until replacement is ready use the current palette's deterministic fallback, including its untextured parameters, and keep the disabled legacy color path. Coalesce appearance-only invalidations to one pending render-owned redraw, including while paused if the view remains authorized. A renderer-local submission identity pins the new bindings and fences. An appearance-only redraw cannot consume the WP11 first-content sequence, replace its pending content or generate another authority receipt for an acknowledged frame. No tick advancement, terrain/collision publication, extra brick requests or geometry rebuild for material-only changes.
4. **Derive canonical mapping in portable code.** MAT-03 defines the fixed physical texel scale and stable canonical-to-local decomposition before float conversion, triplanar basis, normal blend and bounded sampling contract. Test coordinates at matching surface points across face edges, corners, mixed LOD, edit neighborhoods and rebase against a declared tolerance in texels. MAT-03 defines a bounded deterministic orbit selector from canonical surface samples to existing palette inputs, including deterministic tie-breaking at mixed-material boundaries and a defined proxy fallback. INT-01 owns its first near and orbit runtime bindings. Use the same immutable palette and compatible canonical surface inputs on both sides. Compare material selection and texel coordinates at shared points with at least two distinguishable materials across an edge or LOD transition. Verify handoff and appearance separately where orbit uses WP17's uncarved proxy and near geometry represents caves or edits. The selector requests no bricks and changes no canonical samples. Props may use UV/tangent mapping with the same BRDF and existing WP16 limits.

## Rejected/deferred alternatives

- **Vertex colors only:** safe and fast but insufficient for detailed plausible surfaces; retain as fallback.
- **Store runtime GPU handles or texture names in each voxel:** rejected; violates renderer ownership, portability and sparse storage budgets.
- **Automatically replace ushort material IDs with wide handles:** rejected without capacity evidence; resolve through a palette, not larger per-cell storage.
- **Full material node editor, translucency, parallax tessellation and dynamic GI now:** deferred; they are separate rendering/workflow decisions.

## Implementation slices

- MAT-01: portable descriptor, palette validation, material ID mapping and explicit color-space tests; avoid native changes.
- MAT-02: fixed-reference analytic BRDF tests and opt-in untextured PBR shader with WARP image readback.
- MAT-03: portable canonical triplanar mapping and mixed-LOD/seam/rebase fixtures. INT-01 owns the first textured near/orbit fixture and its minimal runtime binding.

## Required evidence / acceptance


- MAT-01 verifies validation, missing-ID fallback, immutable and distinct palette revisions, and unchanged generator IDs, checkpoint compatibility and material catalogue version through visual-only Save/Continue. Keep existing incompatible recipe protection.
- MAT-02 measures numeric BRDF/reference tolerances and WARP pixels for parameter sweeps, dielectric/metallic fixtures and legacy fallback. MAT-03 verifies all 12 face edges and 8 corners, mixed LOD, caves, unaffected edit samples, rebase and shared near/orbit control points.
- With the validated pass adapter, TEX-02/INT-01 exercise same-terrain palette supersession, completion during recording, late first Present after an edit/rebase, near/orbit handoff, and paused redraw for both an acknowledged view and a view with first content pending. Reverse completion order for independent slots and a superseded same-slot request. Check distinguishable fallback/replacement and combined shadow/material outputs, including an earlier in-flight frame with its original binding and a rejected stale binding.
- Pair Windows reference-class runs at 1920×1080 with fixed camera, light, geometry and material set. Record GPU terrain-pass p95/p99, CPU submission, texture bytes, descriptors and memory. Keep WP15/WP16 budgets and #88 qualification. WYU chooses game content through immutable exact-pinned engine packages.

## Consequences / approval

Material identity and portable contracts become reusable foundations for caves, props, HDR and future lighting. Shading and texture bandwidth costs require explicit per-quality-tier limits. No complete PBR pipeline or benchmark is claimed. Four ADR-0007 specialist reviews, a sequential integration review and explicit owner sign-off are mandatory before this proposed decision becomes approved.
