# WP13 · Biomes from surface to orbit

WP13 defines the implementation and acceptance contract for Package B in the [roadmap](../roadmap.md). Groundwork supplies optional near-terrain vertex color evaluation. WYU ships three recognizable landscape zones on its reference planet, with continuous transitions and consistent placement from walking altitude through ascent, orbit and landing. [Groundwork #110](https://github.com/DaveBeusing/Groundwork/issues/110), [WYU #40](https://github.com/DaveBeusing/wyu/issues/40)

Use WP11 terrain, streaming and flight as the implementation baseline. The [WP12 handover](../wp12-handover.md) adds a regression route for V2 rock and ore, underground checkpoint restore and supported return to the surface. WP12 production qualification and the WP11 reference pressure work are separate gates. Groundwork #89 remains open. Vegetation and instancing are WP16. Weather, fauna, ecosystem simulation, biome-dependent resources and fog are outside this contract.

## Groundwork · Optional color evaluation

- Evaluate a supplied color policy when preparing the existing volumetric terrain upload batch. The inputs are canonical planet-centred position in metres and the existing material ID. Obtain the position from mesh origin, mesh-local vertex and cell size before converting to a render-relative position. A change in camera, render origin, cube chart or job order must have no effect on the inputs for a shared geometric sample. The existing material palette is the default.
- The policy and its settings are immutable for a batch and safe for concurrent builds. Identical canonical input and policy revision must yield identical color. Validate any policy output against the current vertex-color format before publishing. If evaluation throws, yields an invalid color or exceeds its admitted resources, reject the candidate through the existing failure path. Keep the prior complete terrain and collision publication available. The ordinary cancellation, stop and fence-retirement rules apply to all partially prepared work.
- Use the existing color buffer and publication path for mesh replacements and edits. Account for temporary evaluation memory in the existing CPU reservation. A pure camera update may neither evaluate colors nor allocate biome-related data. For unchanged geometry, enablement must add zero persistent GPU bytes, draw calls and terrain uploads. Evaluating colors leaves canonical density, material IDs, collision, terrain revisions, checkpoint payloads and their formats intact.

## WYU · One recipe and palette for both views

- Own the versioned recipe, palette and any bounded per-build cache in WYU. Evaluate two frozen seeds using world seed, canonical planet-centred position and existing landform data. Use low-frequency fields for coherent regions, continuous blend weights for transitions and a deterministic tie rule for biome ID. Freeze the quantization, weight normalization and control points with the recipe. Both consumers use the same evaluator and palette. Use the current near terrain mesh and orbital patches and retain the landing marker.
- Compare canonical control points that are identical in both consumers. Compare exact biome IDs and quantized weights there. Assess visual continuity across differing mesh sample positions by the large-scale zone layout and transition alignment. A fine or coarse representation of the same canonical point must agree. A local origin change must preserve biome identity and blend.
- The reference-planet route is the playable acceptance fixture. Keep WP12 Ember Caves on its established material palette for this milestone. If V2 terrain color evaluation is later enabled, demonstrate that strata, ore and the rope/HUD route remain legible before shipping that change. Existing WP11 and WP12 saves continue through their existing slot and format dispatch. The recipe for this release is derived visual configuration and adds no checkpoint field.

## Verification and budgets

Freeze the exact commits, recipe revision, two seeds, canonical sample vectors, route, expected checkpoints and comparison settings before collecting acceptance evidence. Record adapter, driver, resolution, presentation mode, build and phase. Record each gate as passed, failed or pending.

| Gate | Required evidence |
| --- | --- |
| Groundwork portable and D3D12 | Default colors pass existing regressions. A diagnostic policy checks canonical position and material on a mesh rebuild, a mixed-LOD seam and a rebase. Reject a failing policy without replacing the prior committed view. WARP readback checks color and depth with the policy on and off. A Windows NVIDIA run with Debug Layer and DRED exercises upload, edit, stop and fence retirement and reports zero graphics errors, corruption and live GPU child objects. |
| WYU continuity | For both fixed seeds compare exact biome ID and quantized weights across near/orbital evaluators at the frozen shared points and across serial, reordered and parallel jobs. The route crosses a biome transition, a cube face edge, a LOD transition and a rebase, then takes off, orbits and lands. Capture views at the frozen checkpoints and confirm zone placement and the landing marker. |
| Save and cave regression | Open an existing WP11 checkpoint and compare pose, edit overlay and collision results with the baseline. Run the established exploration, flight and save regressions. On the WP12 cave fixture verify strata and ore cues, mining, restart under the roof, the climb HUD and supported surface return. |
| Time | Ground and orbit frame time p95 at most +3% and p99 at most +5%. Near-upload preparation, globe build and time to first visible frame after each ground/orbit renderer transition each have a p95 limit of +10%. |
| Resources | At most 4 MiB additional temporary CPU peak in aggregate across concurrent builds. At equal geometry, zero additional persistent GPU bytes, draw calls or terrain uploads. A pure camera update incurs zero biome queries and zero biome allocations. |
| Pressure and production | With a full speculative CPU queue and speculative GPU uploads blocked, required movement and edits still satisfy WP11. Record protected progress, rejected/stale work and holds. Workstation evidence supports integration. Production acceptance requires the reference budgets and pressure qualification. [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88), [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89) |

Measure biome rendering off and on in the same Windows x64 Release build. Use the same seeds, input replay, resolution, presentation mode, driver and geometry in each pair. Collect at least five measured runs after warm-up and at least 2,000 frame time samples per variant and phase. Report ground, orbit and renderer-transition phases separately, record GPU time separately, and retain waits and failed attempts in the evidence.

## Work sequence

| Work | Output |
| --- | --- |
| Groundwork #110 | Optional evaluator and bounded batch integration. Portable and D3D12 coverage, followed by an exact verified immutable package archive from green Windows CI on master. |
| WYU #40 | Exact archive pin. Shared recipe and palette for near and orbital rendering. Deterministic controls, route and regression evidence, including paired performance measurements. |
| Cross-repository handover | Rebuild and exercise both repositories from clean Windows master checkouts. Record exact package provenance, route and measurements, and mark remaining reference gates explicitly. |

The contract review precedes implementation. Package E and WP16 consume the approved biome integration as specified in the roadmap.
