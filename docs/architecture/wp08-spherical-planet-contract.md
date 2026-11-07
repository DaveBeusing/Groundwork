# WP08 spherical planet contract

WP08 qualifies a walkable route on one small, fixed spherical planet. It
uses the Windows host and verified package workflow from the
[WP07 handover](../wp07-handover.md). Groundwork owns reusable addressing,
sampling, coverage, collision, mesh publication and diagnostics. WYU owns
the reference terrain recipe, waypoints, controls and route presentation.
Planetary edits and persistence, vehicles and flight have later contracts.
The WP07 prototype and its flat save domain retain their existing behavior.

## Reference planet

Use a stationary, planet-centered Cartesian frame with a 64 m reference
radius. The terrain is a single radial surface for this fixture. Its
recipe has seed `0x57505538` and generator version 1. For a canonical
unit direction `d`, use the following signed height in millimeters:

```text
heightMm = RoundToEven((1200 * d.X * d.Z) + (600 * d.Y))
position = (64 + heightMm / 1000.0) * d
```

Evaluate in double precision with the displayed operation order. Version
1 deliberately uses the same reference shape for every seed. The seed is
part of the recipe identity so a future generator can use it without
changing the addressing model. Use a muted base terrain and distinct route
markers in WYU, following the WP07 convention of combining color with shape.

## Charts and shared samples

Partition the surface into six outward-facing cube charts. The normal
`N` is the signed unit axis named by the face. The chart bases satisfy
`U × V = N`.

| Face | U | V |
| --- | --- | --- |
| +X | −Z | +Y |
| −X | +Z | +Y |
| +Y | +X | −Z |
| −Y | +X | +Z |
| +Z | +X | +Y |
| −Z | −X | +Y |

A patch key is `(face, level, tileU, tileV)`. Level 0 covers the entire
face. Level 5 is the finest. Tile indices are in `[0, 2^level)`. Each
patch has 16 intervals per edge. For a patch-local sample index
`k ∈ [0,16]`, its finest face-grid index is
`(16 * tile + k) * 2^(5 - level)` along each axis.

With `D = 512` and finest face-grid indices `i,j ∈ [0,D]`, define:

```text
B = D*N + (2*i-D)*U + (2*j-D)*V
d = B / length(B)
```

The signed integer triple `B` is the identity of a terrain sample.
All charts and levels that reference it use the same direction, recipe
and height. Derive edge orientation by matching canonical endpoints
and preserve the sequence or reverse it as required. For continuous
Cartesian-to-chart queries, select the dominant absolute component,
breaking ties X, then Y, then Z. Its sign selects the face. Divide the
dot products with `U` and `V` by the dot product with `N`. Reject
zero and non-finite directions and invalid patch keys.

## Detail transitions and publication

Cover the visible planet with a balanced quadtree. Adjacent leaves
differ by at most one level, including across cube faces. At a mixed
edge, the fine patch emits only boundary segments whose endpoints
match the coarse patch. The odd fine boundary samples are omitted from
the stitched edge. A fine patch with two adjacent transition edges
uses a joint corner topology. Use outward triangles and deterministic
ordering. Shared segment pairs must match in opposite order, including
when a seam crosses a chart boundary.

Keep a finest-level support band covering at least 6 m of surface
distance around the player, expanded for the admitted sweep and contact
footprint. Keep mixed-detail edges outside this band. Use the same
finest-level positions and triangulation for visible terrain and
collision inside the band. The outer surface may use coarser leaves.
Use separate refine and coarsen thresholds so stationary input near a
threshold settles. A patch covering the support band remains finest
until it and the required halo can safely leave the band.

Publish complete, coherent region generations. Stage the required
patches, shared-edge topology and collision coverage under Core
ownership. A generation becomes usable for movement only after its
matching mesh and camera frame have presented. Until then, preserve a
compatible previous presentation and stop movement at the last
supported position if the required coverage is missing. A failed or
stale candidate cannot replace active coverage. A renderer fault follows
the existing Core stop path. Cancellation closes admission, and GPU
retirement retains custody until the provider can safely release it.

The reference limits are 192 leaves per generation, 384 patch records
across active, staged and reader-retained CPU coverage, two in-flight terrain jobs, one
staged generation and no waiting job queue. Bound aggregate CPU mesh
storage at 96 MiB and native mesh storage at 64 MiB, including resources
retained by readers or GPU retirement. Batch leaves deterministically
within the renderer's existing nine instance and per-mesh limits.
Reject excess candidates before allocation and keep valid coverage.
If the existing renderer ports cannot guarantee a matching camera frame
and mesh origin, qualify a scoped Groundwork port change before WYU
integration.

## Precision, gravity and walking

Keep authoritative pose, contact queries, tangent velocity and heading
in planet-centered doubles. The local render frame translates the
planet axes by an origin snapped to an 8 m Cartesian grid with
round-to-even ties. Stage a rebase when the player is more than 12 m from
the published origin. Subtract the origin in double precision before
converting to floats. Each mesh and frame carries the same origin
revision, and the view and projection consume the matching camera pose.
The publication rule also applies to origin changes. Rebasing preserves
the authoritative pose, velocity and heading.

Up is the normalized planet-centered position. Gravity points radially
inward at a nominal 9.81 m/s². Transport the tangent heading and velocity
using the shortest rotation from old up to new up, then project the
heading back onto the new tangent plane and normalize it. Use that frame
for camera yaw and walking controls. Chart coordinates select terrain
data. They do not drive camera or movement orientation.

Run walking on the existing fixed simulation step. Sweep a player body
with a 0.25 m radius and 1.7 m total height against the guarded terrain.
The reference walking speed is 4 m/s. The acceptance route stays on
walkable slopes and grounded. Separate fixtures exercise a blocked
step, an unsupported slope and unavailable terrain. On a missing
coverage result, admit no unsupported motion, retain the last supported
pose, and expose a loading reason. Publish the new orientation and
contact only with the admitted movement.

## Qualification

Use the equatorial direction `(cos θ, 0, sin θ)`. Start on the surface
at `θ = 35°` facing increasing θ. Walk to `145°`, turn deliberately,
and return to the start. Use a scripted input fixture with waypoint
tolerances of 0.25 m in surface position. Record the input trace, fixed
tick, face, tile, LOD, origin revision, pose, contact and terrain
generation. The route crosses the +X/+Z and +Z/−X face edges in both
directions and crosses same-face patch boundaries. Require at least one
refine and one coarsen outside the support band and a rebase on each
leg. Use focused fixtures for forced rebase, corner traversal and
delayed generation. Repeat the route and compare semantic checkpoints
apart from readiness waits and measured timings.

| Gate | Required evidence |
| --- | --- |
| Addresses and seams | All 12 cube edges and eight corners, both edge orientations, deterministic ties, patch bounds, invalid input, and identical canonical samples across levels. |
| Meshing and visibility | Matching boundary segments for equal and mixed levels, adjacent transition edges, and no degenerate or inward-facing triangles. Shared endpoints agree within 1 mm in the common rendered frame. Use WARP color/depth readback at shallow and steep view angles to reject clear-color gaps in the interior of the seam, away from the silhouette. |
| Precision and controls | Verify inverse charts and local-coordinate reconstruction, including a precision stress fixture with a 6,400 km radius. A forced rebase contributes at most 1 mm position residual, 1 mm/s velocity residual and 0.1° heading residual in derived coordinates. Verify continuity at face edges and corners separately from intended radial rotation and player turns. |
| Collision and coverage | The settled route stays grounded with at most 1 cm penetration and 2 cm support separation. Cover delayed, failed and stale candidates, support across seams, and stop during staging or GPU retirement. Record holds and resource high-water marks. |
| Windows integration | Clean checkouts, exact verified Groundwork packages, green Windows CI, targeted WARP readback and the full WYU route on NVIDIA with Debug Layer and DRED. Record adapter, route traces and live-object report, including zero live GPU child objects after shutdown. |

Implement portable charts and topology first, then support queries,
movement and generation publication, then the Windows renderer checks
and WYU route. Close with a Groundwork handover that records exact
commits, package provenance and the qualification evidence.
