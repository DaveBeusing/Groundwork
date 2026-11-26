# WP17 · Connected ridges and valleys contract

Groundwork owns the optional landform evaluator, its recipe and persisted identity, and compatibility with WP11 and WP12. WYU owns the Far Horizon recipe, world selection and reference route. Continue from the WP11 canonical lattice and the [WP12 geology and checkpoint contracts](wp12-underground-geology-contract.md). WP13 biome colors and WP16 props can consume the resulting terrain through their existing paths. WP15 sunlight and shadows remain opt-in. Water, sediment transport and runtime erosion are outside WP17.

## Sampling and shared relief

- Evaluate at a signed Int64 lattice coordinate using the world seed and a validated immutable recipe. Return uncarved elevation in the existing 1/256 lattice-cell units, with independently testable contributions for ridges, the main valley, tributaries and local cuts. Chart, LOD, brick ownership, traversal order, render origin and thread assignment do not enter evaluation. Keep the existing deterministic radius conversion and density sign convention.
- Use quantized arithmetic with explicit rounding and signed division rules. Specify checked intermediate ranges, octave or stencil limits, and the maximum primitive evaluations per query before integrating a candidate. Reject recipes outside those bounds at construction. Sampling uses fixed local work, no mutable global generation state, no drainage precompute, and no allocation after warm-up. Freeze the algorithm, seed streams, constants, canonical recipe bytes and reference hashes together.
- The same uncarved evaluator supplies surface elevation for orbital relief and baseline density for volume generation. WP12 derives depth from that baseline before applying its existing carving and material rules. WP17 must conservatively extend portal depth margins for the chosen profile. Carved density and edited overlays continue through the existing meshing, collision and coherent publication path. Orbital relief represents uncarved large-scale terrain.
- Freeze control transects per seed before qualification. Include a connected ridge crest, a main valley floor, a tributary that joins it, and a bounded cut. Define the canonical coordinates, expected signs and height bounds, sampling spacing and near/orbit tolerances for each. At orbital patch resolution compare the resolved profiles and ordering of the large features. Include neighboring off-feature controls to catch a field that only passes by flattening the scene.

## Identity, persistence and failure

- Reserve generator version 3 for the combined profile after prototype selection. Its parameter digest covers a versioned canonical encoding of the relief algorithm, relief parameters and geology recipe identity. Explicitly select it for new WP17 worlds. V1 and V2 continue through their existing evaluator and checkpoint paths with byte-identical results. Loading never upgrades an existing world's generator as a side effect.
- Reuse the geology checkpoint layout only where its existing envelope and overlay/payload validation represent V3. Add explicit V3 dispatch for encoding, decoding and overlay validation. A layout change needs its own schema version. Commit recipe identity, terrain edits, ore depletion and player state through the existing NTFS publication/recovery protocol. Unknown identities, mismatches and failed recovery preserve the saved bytes and surface a recoverable error. Verify walkable support and clearance on spawn, Continue and landing before opening movement.
- Preserve existing terrain resource admission and generation checks. A failed, late or over-budget build cannot replace the last coherent mesh and collision publication. WP17 adds no persistent GPU landform storage. Terrain geometry remains subject to existing quotas.

## Work and acceptance

| Order | Deliverable and gate |
| --- | --- |
| P00 | Prototype bounded local candidates against the same WP12 base relief. Record the feature transects, exact recipe inputs, maximum sampling work, allocation and timing data. Select and freeze a candidate before assigning its persisted recipe identity. |
| A01 | Implement canonical sampling, the V3 generator and shared uncarved evaluator. For three frozen seeds record sample and brick goldens in fresh processes, serial execution and deliberately reordered parallel jobs. Cover signed coordinates, extreme supported radii, all twelve cube-face edges and eight corners, mixed LOD and rebases. Keep V1/V2 goldens byte-identical. |
| B01 | Exercise V3 through published meshing, collision and edits. Apply the WP12 B01 gates for entrance and portal traversal, sealed chambers, material queries and coherent mesh/collision across edits and rebases. Validate spawn and landing footprints. |
| C01 | Exercise V3 Save/Continue and WP12 C01 NTFS interruption/recovery. Compare restored geometry, material and ore depletion. Verify V1/V2 reopen with original identity and reject unknown V3 recipes without modifying the slot. |
| W01 / G00 | Archive green Groundwork master packages immutably, pin the exact archive in WYU and qualify clean Windows checkouts. Run the ridge, valley, tributary, cave entrance, ascent, orbit and return route. Compare fixed control profiles from ground to orbit and verify final landing plus Save/Continue. Record integration and reference qualification separately. |

Use Windows x64 Release at 1920 × 1080 on the WP11 reference class. Compare WP17 with WP12 base relief in the same build. Match seeds, radii, brick keys, LODs, worker counts and optional biome, prop and lighting settings. Match route waypoints and motion profiles and report brick and triangle counts. After warm-up use at least five independent paired runs, 2,000 measured bricks per variant per generator scenario and 2,000 measured frames per variant per route phase. Report open surface, steep relief, cave entrance and return from orbit separately. Record recipe, package, source and trace identities, adapter, driver and applicable caps with each result.

| Metric | Gate |
| --- | --- |
| Brick generation CPU | At most +10% p95 and +15% p99 per brick. Report queue wait separately. |
| Walkable coverage | At most +15% p95 for first walkable publication and at most +15% p95 for landing recovery, assessed separately. |
| Orbital mesh build | At most +10% p95 at equal patch resolution. |
| Streaming frames | At most +5% p95 and +10% p99. Report CPU and GPU frame times separately and apply the WP11 protected movement and edit limits. |
| Memory and sampling | At most 8 MiB incremental peak CPU memory across concurrent landform jobs, zero heap allocations per warmed landform query and zero additional persistent GPU bytes for landform data. |

Apply the full performance protocol, [WP11 reference limits](https://github.com/DaveBeusing/Groundwork/issues/88) and [pressure cases](https://github.com/DaveBeusing/Groundwork/issues/89) for release or production activation. Workstation results can qualify functional integration and inform the prototype decision. Merge and unqualified preview follow [ADR-0011](../adr/ADR-0011-reference-measurement-policy.md).

[Groundwork #115](https://github.com/DaveBeusing/Groundwork/issues/115) · [WYU #45](https://github.com/DaveBeusing/wyu/issues/45)
