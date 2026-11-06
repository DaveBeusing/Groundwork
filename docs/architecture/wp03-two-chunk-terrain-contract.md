# WP03 two-chunk terrain contract

**Status** Proposed for owner review before implementation. This document
defines the first terrain slice against the [WP02 handover](../wp02-handover.md).

## Milestone and dependencies

WYU supplies a deterministic occupancy fixture for two face-adjacent chunks
and presents them as one static scene through the Windows D3D12 provider.
The demonstration uses chunk coordinates `(0,0,0)` and `(1,0,0)` and a camera
that shows the top surfaces and the shared boundary. In the low chunk, fill
local `y < 4`. In the high chunk, fill `y < 4` for `z < 8` and `y < 8`
otherwise. This gives a continuous top across part of the seam and an
exposed step along the rest. Editing, streaming, LOD, procedural planets, collisions,
persistence, transparency and material textures are outside this slice.

| Package or owner | Proposed responsibility |
| --- | --- |
| `Groundwork.Voxels` | New BCL-only package for immutable chunk snapshots and CPU surface meshing. No ECS entity per cell. |
| `Groundwork.Rendering` | Adds immutable, bounded portable mesh data and a two-instance scene description. Retains its existing Core dependency. |
| `Groundwork.Rendering.D3D12` | Consumes portable mesh data and owns native resources. Keeps the WP02 dependency graph and toolchain. |
| WYU composition root | Chooses and populates the terrain, builds the mesh pair and maps it to portable rendering inputs. Consumes exact immutable Groundwork packages. |

Neither renderer package references `Groundwork.Voxels`. Groundwork has no
WYU reference. Approval covers the proposed package and graph updates under
[ADR-0001](../adr/ADR-0001-repository-boundaries.md).

## Chunk snapshots

- `ChunkCoordinate` is a value with signed integer `X`, `Y` and `Z`. The
  first slice admits `[-1024, 1023]` on each axis. A chunk has 16 cells on
  each axis, one world unit per cell, and origin `(16X, 16Y, 16Z)`. Validate
  the coordinate before computing the origin. Origins and boundary vertices
  are exactly representable as single-precision values in this range.
- `ChunkSnapshot.Create(coordinate, revision, ReadOnlySpan<byte> cells)`
  owns a copy of exactly 4,096 bytes. Only `0` (air) and `1` (opaque solid)
  are valid. Local voxel coordinates are in `[0,15]`. Storage order is
  `x + 16 * (y + 16 * z)`. Invalid coordinates, lengths, revision zero
  and unknown cell values throw `ArgumentException` before publication.
- The scene owner assigns a positive, strictly increasing `ulong` revision
  per coordinate in one scene lifetime. Revision reuse and wrap are invalid.
  The snapshot never exposes mutable backing storage. The caller may reuse
  its input after creation completes and must keep it stable during the copy.
- `BuildPair(first, second)` accepts two complete snapshots whose coordinates
  differ by exactly one on one axis and match on the other two. Reject equal,
  diagonal or non-adjacent keys with `ArgumentException`. Canonicalize input order to low then high
  along the differing axis. The result carries both coordinates and revisions
  and contains two complete meshes in that order. Mesh buffers are immutable
  and exposed through read-only spans for conversion to rendering inputs.
- Capture both snapshots together at the scene owner. If either input changes
  before handoff, discard the entire candidate and rebuild both meshes. Check
  both captured snapshots against the current pair at handoff. A missing
  expected member prevents building the pair. The first implementation is
  synchronous during scene preparation. No asynchronous job protocol is needed.

## Boundary meshing

For each solid cell, sample the axial neighbor. For local neighbors, sample
the current chunk. Across the shared face, sample the other chunk's matching
boundary cell. Beyond the outer boundary of the complete pair, sample air.
Emit a quad only for a solid cell whose neighbor is air. The solid cell owns
the emitted face. A solid/solid seam has no faces. A solid/air seam has one
face per adjacent cell pair, emitted by the solid side. Air/air emits none.

Visit cells in increasing `z`, then `y`, then `x` with `x` innermost. Visit
faces in `-X, +X, -Y, +Y, -Z, +Z` order. Each emitted quad has four local
positions and six zero-based `uint` indices. Use the lowest grid corner `a`
on the face plane, then emit `a`, `a+T`, `a+T+B`, `a+B` and triangles
`(0,1,2)`, `(0,2,3)` relative to that quad's first vertex. The positive face
plane is at the voxel coordinate plus one along its axis. `T` and `B` are
positive unit offsets whose cross product points outward.

| Face | `T` | `B` |
| --- | --- | --- |
| `-X` | `+Z` | `+Y` |
| `+X` | `+Y` | `+Z` |
| `-Y` | `+X` | `+Z` |
| `+Y` | `+Z` | `+X` |
| `-Z` | `+Y` | `+X` |
| `+Z` | `+X` | `+Y` |

Local positions are integer grid points in `[0,16]` on all axes and are
represented by three floats. No vertex merging or greedy meshing is required.
The conservative bound is six quads per cell. Each chunk is limited to
24,576 quads, 98,304 vertices and 147,456 indices. Check counts and byte
arithmetic before allocation. A valid empty chunk mesh has no vertices or
indices. The order and values of both outputs are deterministic for a given
pair. Reversing the input argument order produces the same canonical output.

## Renderer handoff and retirement

- `RenderMesh.Create(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)`
  validates and owns its CPU buffers. Enforce the vertex and index limits
  above, finite positions, indices strictly below the vertex count, and a
  triangle-list index count divisible by three. Empty means both spans are
  empty. Invalid input throws `ArgumentException`. Callers keep both spans
  stable during the copy and may reuse them after it returns. The provider
  reads the immutable buffers without exposing writable storage to the caller.
- A `RenderMeshPair` contains exactly two `RenderMeshInstance` values, each
  with a mesh and a finite translation. WYU maps the canonical chunk meshes
  to local positions with their respective chunk origins as translations.
  The pair is the unit of handoff. Complete both meshes and perform their
  copies outside simulation barriers and observer leases. Reject an absent
  mesh or invalid instance before creating the renderer. A valid pair can
  contain one or two empty meshes.
- Proposed Windows entry point `D3D12Renderer.CreateWithMeshes(window,
  ownership, pair, diagnostics, useWarp, startupTimeout, operationTimeout)`.
  Use the same optional diagnostics, adapter and timeout defaults as the
  existing construction path. Geometry is fixed for that renderer instance.
  The existing constructors, `IRenderer`, `RenderFrame`, submission results,
  latest-only slot and sequence rules stay compatible. A `RenderScene` uses
  its current world transform, tint, view and projection for the pair in
  mesh mode. Mesh vertices use row-vector `Translation * World * View *
  Projection` order. Null scene still clears only. Existing construction
  retains the built-in cube, and no-renderer WYU startup remains available.
- The D3D12 render owner uploads the pair once before reporting `Running`.
  Use one bounded upload-heap geometry buffer in `GENERIC_READ`, with
  12-byte positions and 4-byte indices. The maximum logical payload is
  3,538,944 bytes for two meshes before native allocation padding. Skip empty
  draws and allocate no geometry buffer for a fully empty pair. The prototype
  binds the upload buffer directly as vertex/index views and does no mesh
  copying, allocation or upload per frame. Both draws use the same depth
  target and frame camera. The owner drops its CPU mesh references once
  upload completes or startup settles.
- The render owner and its runtime lease retain GPU geometry through queue
  draw retirement and the post-`Present` fence. Resize and minimize preserve
  the geometry. Preserve the WP02 timeout, admission and custody rules during
  startup, resize and shutdown. A failed signal keeps retirement unproven.
  Release only after safe settlement or confirmed device removal. The
  simulation coordinator submits copied frame values after publication and
  never waits on the GPU. Runtime geometry replacement requires its own
  reviewed retirement protocol.

## Implementation gates after approval

| Slice | Owns | Depends on | Acceptance evidence |
| --- | --- | --- | --- |
| WP03-A, storage and meshing | `Groundwork.Voxels`, its tests, solution and required graph/package allowlist entries | Contract | Copy isolation, invalid input, deterministic ordering, seams and bounds. |
| WP03-B, portable handoff | `Groundwork.Rendering` and its tests | Contract | Copies, empty meshes, limits, invalid indices/positions/translations and unchanged WP02 frame behavior. Can run alongside A. |
| WP03-C, D3D12 terrain path | `Groundwork.Rendering.D3D12` and its tests | B | Both mesh instances draw with shared depth. Active Debug Layer, WARP readback, native lifecycle and zero live child objects. |
| WP03-D, package and WYU proof | Groundwork verification and release evidence, then WYU composition and exact pins | A, B, C | Fresh isolated package consumers and a visible two-chunk scene on NVIDIA or another qualified hardware adapter. Clean native close. |

Deterministic mesher acceptance vectors use an empty pair (0/0 quads), two
isolated touching solid cells (5/5), one of those cells removed (6/0), a
fully solid pair (1,280/1,280 with no internal plane), and a globally alternating
checkerboard (12,288 per chunk). In the full `+X` pair, clear local cell
`(0,8,8)` in the high chunk and rebuild with its next revision. Expect
1,281/1,285 quads, including a new face on the unchanged chunk. Verify emitted
indices and outward winding, invalid topology, a signed-coordinate seam,
and rejection of the stale pair. WARP readback uses fixed-camera samples on
both chunks and across the visible seam, and checks depth ordering. The CPU
face-count tests verify absence of hidden internal faces. Run the existing
cube, renderer-free and resource-custody regressions as part of integration.

The contract PR is documentation only. The package and implementation slices
start after acceptance under [ADR-0007](../adr/ADR-0007-solo-review-governance.md).
