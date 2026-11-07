# WP09 surface-to-orbit contract

WP09 extends the [WP08 planet](wp08-spherical-planet-contract.md) with one
flight from an edited landing site to orbit and back in the same world
session. Groundwork owns the portable flight state, collision and terrain
admission, local frame, planetary checkpoint codec and renderer lifetime.
WYU owns the craft controls, guidance, landing-site recipe, display and
scripted input trace. The mission uses one stationary planet and one craft.
Atmosphere, orbital transfers between bodies, general vehicle physics and
midflight resume are deferred. The WP07 flat save slot retains its format.

## Reference mission

Keep the WP08 radius of 64 m, generator version 1 and seed `0x57505538`.
Use the WP08 equatorial 35° direction for the landing-site center. WYU
defines a small, visibly distinct landing pad by applying a bounded set of
height edits to canonical terrain samples around that center. The fixture
starts on the edited surface and saves the grounded checkpoint before
takeoff. The route uses the same fixed-step flight and landing admission
for scripted and interactive controls.

For the reference pad, raise samples by 120 mm within a 1 m surface arc
of the center, taper linearly to zero at 2 m, and round to even in
millimeters. Measure the arc on the reference sphere. WYU distinguishes
the pad by color and silhouette and exposes the same target to the
guidance and landing fixture.

| Phase | Admission and checkpoint |
| --- | --- |
| Grounded | Present the edited terrain and matching camera, prove landing footprint support and commit the departure checkpoint. |
| Ascent and insertion | Apply bounded thrust from the pad. Cross the near/far view thresholds and establish prograde, approximately circular flight at radius 96 m. |
| Coast | Coast for one unwrapped revolution about the planet, from a recorded insertion reference ray. Verify orbit bounds across the full coast. |
| Return | Use a recorded braking and descent sequence toward the same pad. Admit the low approach only with matching, presented collision coverage for the predicted landing footprint. |
| Touchdown and reload | Land within 0.25 m of the original site center. Commit the landing checkpoint, restart in a fresh process, rebuild the edited pad and verify the persisted landing pose and identical edit overlay. |

The release fixture records the input trace with a schema version and
checks its digest. Freeze the trace after the portable flight and landing
tests pass. Replay it twice on Windows and compare mission ticks and
semantic checkpoints. Report loading holds, frame timings and hardware
separately. Advance the trace only on an admitted mission tick. Renderer
frame cadence does not drive the simulation.

## State, forces and transitions

Keep position, velocity and orientation in planet-centered double
precision. Use the existing 60 Hz fixed step. Gravity at position `p` is
`-mu * p / |p|^3`, where `mu = 9.81 * 64 * 64 m^3/s^2`. This matches WP08
gravity at the reference surface. Integrate velocity before position
with acceleration sampled from the old position. Apply at most 30 m/s²
of commanded thrust per tick. Reject non-finite input, non-unit orientation
beyond a squared norm error of `1e-9` and invalid radii before
publication. View mode never changes gravity or the authoritative state.

For insertion, require radius `96 ± 0.25 m`, radial speed at most
`0.1 m/s` and prograde tangent speed within `0.1 m/s` of
`sqrt(mu / 96)`. During the unpowered coast, require radial distance in
`[95.5, 96.5] m`, stay within 0.1 m of the insertion plane and complete
one revolution measured by the signed unwrapped angle in that plane.
Report specific orbital energy and angular momentum at entry and exit.
The fixture must satisfy these gates through thrust and gravity, without
setting the position or velocity at a phase change.
The analytic circular reference at 96 m is about 20.459 m/s with a
29.483 s period. Use the analytic reference for the portable coast fixture.

Near ground, sweep the fixed landing footprint across the proposed
movement interval before accepting the next pose. Use the 0.25 m WP08
body radius for this craft fixture. Bound sweep work in the portable
implementation and reject steps that exceed its supported sweep length.
Require an outward contact normal within 30° of local up, radial descent
speed at most 1 m/s and tangential speed at most 0.5 m/s for touchdown.
Faster or steeper contact produces a blocked landing result and preserves
the last safe state. Missing support produces a distinct loading hold,
which preserves pose, velocity, orientation and the mission tick. On
touchdown, record contact and zero the craft velocity in the same commit.

## Terrain and camera publication

Use one render scene throughout the mission. Reuse the WP08 8 m origin
grid and 12 m rebase rule, with authoritative state independent of the
origin. Enter the far view at 20 m of altitude above the reference radius
and request near view at or below 16 m during descent. Keep the current
compatible generation while preparing the next. Use bounded, balanced
patches and a coarser outer view for orbit, and preserve a finest-level
support band on approach. Request the near candidate from the predicted
landing direction before descending through 12 m. Hold the last
safe approach pose if the candidate is late or the landing footprint
lacks confirmed coverage. Keep stop and cancellation responsive during
the hold. If the approach readiness deadline expires, fail the mission
through Core without advancing the descent.

Every presentation binds mesh data, camera, origin revision, view mode and
edit revision. Publish collision coverage only after the matching
presentation acknowledgement. A stale completion, failed build or
unacknowledged frame cannot advance the terrain or edit revision. Use
the WP08 limits of 192 leaves per generation, 384 retained patch records,
two in-flight mesh jobs, one staged generation, 96 MiB CPU mesh storage
and 64 MiB native mesh storage. A candidate beyond budget is rejected
before allocation. Reuse Core admission and D3D12 fence custody through
stop, including cancellation while preparing terrain or holding a GPU
submission.

## Planetary checkpoint

Use a separate planetary address domain and slot. Retain the framing,
integrity and atomic publication rules from the
[WP06 contract](wp06-save-recovery-contract.md). Define planetary payload schema 2
under the same framing prefix. Encode recipe identity, landed pose and a
sparse sorted overlay of signed millimeter height deltas. Key each edit by
the WP08 canonical finest-grid integer triple, ordered lexicographically.
A cube edge or corner has one identity. Reject duplicates, invalid
surface triples, zero deltas, deltas outside ±500 mm and counts above 4,096
before installing any state. Samples shared by multiple levels use the
same delta in each level. Apply the overlay before collision and mesh
generation. Persist neither local origins nor renderer or GPU handles.

Save only after a committed grounded tick. Validate the complete
checkpoint, including the saved pose against reconstructed terrain, before
publishing a resumed session. An interruption before installation keeps
the previous committed generation. An interruption after installation
leaves a complete readable generation. Validate that an older flat reader
recognizes a newer intact frame as unsupported and preserves it. The
portable layout and reference frame are defined below. The Windows slot
writer and interruption tests follow in the publication stage.

The 56-byte WP06 prefix is identical except for payload schema 2 at
offset 10. Integers and IEEE 754 binary64 values are little-endian.
The common 32-byte SHA-256 footer covers the prefix and entire payload.

| Offset | Contents | Encoding |
| ---: | --- | --- |
| 56 | Generator ID, generator version, seed | Three u32 values. The ID and version must be nonzero. |
| 68 | Edit count | u32, 0 through 4,096 |
| 72 | Landed position in planet axes | Three binary64 values, X/Y/Z in meters |
| 96 | Landed orientation in planet axes | Four binary64 values, quaternion X/Y/Z/W |
| 128 | Height edits | Eight bytes each. Four i16 values for X/Y/Z and signed delta in millimeters. |
| 128 + 8 × edit count | Integrity footer | 32 raw SHA-256 bytes |

Coordinates must be even integers in `[-512,512]`, with at least one
component on the cube boundary. The decoder validates length, edit order
and values before allocating the overlay or resolving generator support.
The position must be finite and strictly between the craft body radius
and 76 m from the center. The quaternion must satisfy the flight unit
tolerance. These are structural checks. The caller must rebuild terrain
and verify the landed footprint before publishing the restored pose.

The 176-byte reference frame uses generation 1, lineage bytes `01`
through `10` and request bytes `11` through `20` in hexadecimal.
It stores generator 1/version 1, seed `0x57505538`, pose `(64, 0, 0)`
with identity orientation, and edits `(-512,-512,-512) → -25` mm and
`(512,0,512) → +120` mm. Its SHA-256 footer is
`F4BD8984B29C60075791FCF43DBC1AB385A14D8FEC795B4D9C5D62670BB0BD75`.

## Implementation and qualification

| Stage | Required evidence |
| --- | --- |
| Portable flight | Fixed-step thrust and gravity, analytic circular-orbit comparison, bounded coast drift, swept touchdown and negative cases for non-finite input, excessive descent, steep contact and missing coverage. |
| Edits and checkpoints | Canonical edge and corner edits, coarse/fine sample equality, reload from regenerated terrain, roundtrip and malformed cases, and Windows process termination before and after publication. |
| Renderer | Paired camera, mesh, view and origin through both transitions. Check WARP color and depth across representative seam and approach samples. Hold a replacement and a retirement fence in separate stop tests and prove safe release. |
| WYU integration | Run the entire scripted flight twice with exact Groundwork packages on NVIDIA, Debug Layer and DRED. Verify landing and reload, stable semantic checkpoints, bounded resource high-water marks, and zero live GPU child objects after native shutdown. |
| Handover | Record clean Windows checkouts, exact commits and package digests, post-merge CI, the trace digest and observed qualification results. |
