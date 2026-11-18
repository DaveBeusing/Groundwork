# Terrain PBR on a shared D3D12 session

The surface and static orbit renderers can reuse one `D3D12DeviceSession`.
They use the existing INT-01 material shader, canonical triplanar mapping and
TEX-03 cooked texture handoff. A consumer may keep the previous renderer
factories for an untextured mode.

## Surface

1. Create the near renderer with `CreateWithTexturedSessionVolumeTerrain`.
   Pass the existing terrain coordinator, its renderer epoch, and the same
   optional biome color evaluator as in the legacy session path.
2. Attach a fresh `D3D12TextureHandoff` before starting the renderer. Build
   each `D3D12TextureView` from the coordinator's world ID and the published
   terrain's view, revision and generation.
3. Publish the immutable palette and its per-slot targets for the accepted
   cut. Reuse a target and its sequence when its cooked reference is unchanged.
   A request or worker completion for an older cut remains stale. The exact
   resident version can move to the next cut on the same renderer. Its backing
   stays charged until native retirement. When a cut exceeds the existing
   appearance packing or native geometry budget, it uses the existing
   biome-colored geometry fallback.

## Orbit

1. Retain the canonical planet-centred sample position and ushort material ID
   for each vertex of the orbital proxy. Convert each source mesh with
   `TryBuildOrbitalTerrainMesh`. Use the returned radial normals and triplanar
   data in a zero-translation region. If vertex expansion exceeds the mesh
   limit, split the source into smaller parts or use the legacy orbit renderer.
   The region must also fit the aggregate native geometry limit.
2. Create `CreateWithSessionTexturedMeshes` with the same device session,
   a fresh texture handoff, and a fixed view for this region, using a fresh
   renderer epoch. Submit ordinary identity-world scenes. The consumer can
   pass directional sunlight together with the existing sky. The fixed orbit
   view is eligible for optional texture uploads after the first scene presents.
3. Publish and fulfill the same material definitions for that fixed orbit
   view. Stop the old renderer before starting the next renderer on the
   session. Each renderer owns its texture resources and uses a fresh handoff,
   including on the return to the surface.

Use the same canonical material IDs on both paths. Very wide orbital
triangles can use the existing numeric material fallback when their canonical
coordinates exceed the per-triangle mapping window. Keep the collider,
terrain publication and save identity independent of material selection.
