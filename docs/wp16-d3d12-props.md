# WP16 D3D12 prop cells

Attach a dedicated D3D12PropCells stream to a volume renderer before starting it. Supply immutable near and far meshes and separate placement and residency quotas. Publish the installed volume's renderer epoch, terrain revision and coverage generation with the eligible cells and the conservative invalidation set. Complete or stage each worker request after the worker exits and releases its terrain lease. The render owner installs a ready cell as a unit.

The terrain frame and prop visibility use the same installed volume generation. A mismatched publication suppresses props in that frame. For matching publications, the portable admission layer carries forward unaffected cells where the producer can prove coverage and support samples remain valid.

The renderer shares geometry across cells and issues one indexed instanced draw per model, material and LOD group. Camera changes and rebases rewrite the visible-reference table and reuse the geometry and instance payload. Props depth-test against terrain and use the same distance fog. Terrain shadows keep their existing opt-in behavior.

Each frame reserves space for all possible visible references within the 2 MiB upload limit. Asset and cell payloads that fit the configured slice are deferred as units when the remaining space runs out. This implementation rejects individual assets and cell configurations larger than that slice at admission. Its fixed native arenas are capped at 64 MiB, with a separate conservative admission estimate for CPU storage. The arenas stay in GPU custody until the existing fence retirement or confirmed device-loss path releases them.

The renderer tests cover grouped color and depth, rebase without new mesh or instance payload, residency saturation, asset upload deferral, and pixel readback from the first Present after a live terrain edit. The Windows workflow requires the WARP instancing readback. The full WP16 performance gate, prop-specific resize and device-loss qualification, and the WYU vegetation route remain open.
