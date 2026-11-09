# WP11-C00 meshing probe

This isolated CPU prototype compares two candidate implementations on the
same deterministic signed-distance fixtures. It does not change the
production solution or the frozen contract in PR #77.

## Reproduce on Windows

Use PowerShell 7 and .NET SDK 10.0.403. From the repository root:

```powershell
$tables = Join-Path $env:TEMP 'wp11-transvoxel-51a494f.cpp'
Invoke-WebRequest 'https://raw.githubusercontent.com/EricLengyel/Transvoxel/51a494f03c5b024cd153b596bcc7152eb3cc93a6/Transvoxel.cpp' -OutFile $tables
./benchmarks/Groundwork.Wp11Meshing.Probe/Run.ps1 -Tables $tables -Output ./artifacts/wp11-c00-meshing.json
```

The runner verifies the SHA-256 of the MIT-licensed upstream tables.
The tables are loaded as data and are not vendored. The second candidate
uses a regularized Hermite QEF with an in-cell clamp and a 2:1 leaf-owner
edge traversal. It is a constrained Dual Contouring prototype.
[Transvoxel tables and license](https://github.com/EricLengyel/Transvoxel/tree/51a494f03c5b024cd153b596bcc7152eb3cc93a6) · [Dual Contouring of Hermite Data](https://people.eecs.berkeley.edu/~jrs/meshpapers/JuLosassoSchaeferWarren.pdf)

## Measurement boundary

Each fixture uses a common 33³ sample grid over a 32³ cell region with
eight 16³ bricks. The layouts cover uniform resolution, one plane with
2:1 transitions, and three orthogonal 2:1 boundaries. The junction
fixture crosses the surface close to their intersection. Pressure
extracts eight identical regions and retains their outputs in one
single-threaded invocation. That case exercises retained outputs.

Each algorithm and fixture pair runs in a fresh process, Release with
tiered compilation disabled. Warm-up, input sampling, table decoding,
forced collection, checksum and topology audit are outside the timed
extraction. Measured work includes construction of the triangle output
and its topology-driven winding. Record p50, p95 and p99 with nearest-rank
percentiles. The managed allocation counter measures bytes allocated
on the extracting thread, rather than peak live memory. The heap delta
is a sampled process-wide managed heap estimate. The peak working set
includes the runtime, audit and warm-up. Both implementations use the
same triangle-soup output and topology auditor.

The auditor welds positions on a 1e-4 cell-unit grid. It counts open
edges, nonmanifold edges, inconsistent winding, degenerate triangles and
connected components. Degeneracy uses the angle between triangle edges so a
small triangle with distinct vertices can remain valid. The runner rejects
topology failures and unexpected component counts for these closed fixtures.
The result JSON also records field residual,
observed minimum gradient alignment and a deterministic triangle-stream
checksum. These diagnostics do not establish freedom from geometric
self-intersection, collision parity, material agreement or analytic
feature preservation. This run does not cover cube-sphere mapping,
production streaming queues or GPU/retirement allocation.

For the Windows reference run, pass `-RequireReferenceHardware`. The
runner checks for eight physical cores and 32 GiB of installed RAM before
building or measuring. The switch does not set CPU affinity or cap memory.

[Initial workstation results](results-windows.json) · [Workstation results after the bridge fix](results-windows-bridge-fix.json)

## Workstation reference and collision diagnostics

Run the standard matrix on the workstation without the hardware guard.
The approved workstation profile and all timings are in
[the reference data](results-windows-workstation-reference.json).

Run the additional Marching Cubes/Transvoxel diagnostics separately so
they do not affect the timing or memory figures:

```powershell
./benchmarks/Groundwork.Wp11Meshing.Probe/Run.ps1 -Tables $tables -Output ./artifacts/wp11-c00-mc-geometry.json -GeometryOnly
```

The diagnostic labels regular fine, regular coarse and transition faces
and compares their oriented normals with the local analytic density
gradient at triangle centroids. It checks each overlapping triangle
bounding-box pair for an intersection segment or coplanar area using
double-precision predicates with a 1e-9 cell-unit distance tolerance.
Ordinary shared edges and isolated point contacts are excluded. Built-in
controls exercise known crossings, coplanar overlap and legal adjacency.

Two-sided raycasts against the triangle mesh are compared with sign
crossings of the continuous analytic fixture field, sampled at 1/32
cell and refined by bisection. The matrix includes a three-axis ray
grid and targeted axial and transverse rays through the bridge.
The LOD band includes rays with a mesh or density crossing within one
cell of a relevant transition plane. Bridge core samples are classified
by the parity of mesh ray crossings from outside the region. The
0.75-cell distance cutoff is a diagnostic threshold. It is not a
production collision budget. The diagnostic does not exercise swept
bodies, contact resolution or the production collision publisher.

[Raw diagnostic results](results-windows-mc-geometry.json)

## Thin collision features

```powershell
./benchmarks/Groundwork.Wp11Meshing.Probe/Run.ps1 -Tables $tables -Output ./artifacts/wp11-c00-collision.json -CollisionComparison
```

This mode runs the same six geometry cases with and without the collision
resolution floor. A half-cell scan looks for a solid witness at density
at most -0.25 with air one cell away on both sides along an axis. A
positive witness promotes the entire published collision region to a
uniform 0.5-cell lattice. Otherwise the requested LOD layout is preserved.
The scan is a prototype heuristic over an analytic density source. It
does not guarantee detection of arbitrary sub-grid features. The probe
publishes one region and does not exercise transitions between a promoted
region and adjacent collision regions.

The comparison repeats the existing ray paths and sweeps spheres with
radii 0.15 and 0.35 across and along the bridge and across active LOD
planes in the sphere fixtures. The sweep uses conservative advancement
by the clearance to the triangle mesh or analytic fixture. It records
clear paths, first contact, initial overlap and unresolved queries.
A 0.25-cell first-contact difference is a diagnostic cutoff. This is
a swept-sphere test without a contact response solver. The runner gates
the protected bridge and requires unchanged mesh hashes for the controls.
LOD-band ray counts can change when a mesh crossing enters or leaves
the band, so compare both numerator and denominator.

[Paired workstation diagnostics](results-windows-mc-collision-refinement.json)

## Junction collision and mixed region boundaries

```powershell
./benchmarks/Groundwork.Wp11Meshing.Probe/Run.ps1 -Tables $tables -Output ./artifacts/wp11-c00-boundaries.json -CollisionBoundaries
```

The three-plane junction uses a separate, uniform quarter-cell collision
mesh from the same density field. Its render mesh retains the Transvoxel
transitions. This avoids using displaced render seam vertices for
collision queries. The runner compares the previous collision results
against the separate mesh using the same ray and sphere trajectories.

The mixed-region cases extract meshes independently on either side of
X = 16 from one sampled density field. Spacing is 0.5 in all axes on
the left and 1.0 in X with 0.5 in Y/Z on the right. Both meshes use the
same half-cell face lattice and the same interpolation on shared edges.
The audit checks equal, oppositely directed contour edges by exact
position before combining the triangles for collision queries. It then
checks topology, triangle intersections, ray parity, and swept-sphere
contacts. The regression threshold of 0.1 cell on paired contacts is a
prototype diagnostic. The run covers two static regions across one
face and does not exercise streaming, collision publication or handoff
between independently scheduled owners.

The runner also repeats the missed grazing ray at X = 15.319, Z = 4.673
along +Y and records both crossings. For tangentially mismatched regions
the usual spacing is 0.25 cells on the fine side and 0.5 on the coarse
side in all three axes. For thin solids, the probe pads and aligns the
detected witness bounds around the seam. Within the resulting axis
bands the spacing is 0.125 and 0.25. Each axis returns to its usual
spacing outside its band. Fully refined cells are restricted to the
intersection of the bands. The projected bands still produce
anisotropic cells outside that intersection. Each coarse transition
face continues to pair with two complete fine intervals along each
tangential axis. The fine owner contributes the Transvoxel transition
strip, and its coarse-plane contour is checked against the coarse
region's contour with exact, oppositely oriented edges. Both meshes are
queried together. The runner requires both crossings of the grazing ray
with at most 0.15 cells of paired error, and preserves the 0.1-cell
swept-sphere diagnostic. It requires zero crossing-count discrepancies
in the tangential bridge band, including the previously failing ray at
Y = 16.673, Z = 14.819 along +X. The bridge core and sphere contacts
remain gated separately.

[Workstation grazing-ray and tangential-boundary results](results-windows-mc-tangential-boundaries.json) · [Previous half-cell junction and normal-only boundary results](results-windows-mc-collision-boundaries.json)

## Collision build performance on the workstation

```powershell
./benchmarks/Groundwork.Wp11Meshing.Probe/Run.ps1 -Tables $tables -Output ./artifacts/wp11-c00-collision-performance.json -CollisionPerformance -Repetitions 20
```

Each case runs in its own Release process with tiered compilation
disabled. After one warm-up, each sample builds the lattice and mesh
from scratch, including creation of the optional ray fallback. The sampling stage includes the thin-solid scan when
enabled. The extraction stage includes the transition triangles and
triangle output. Checksums, forced GC and table loading are outside
the timed sections. The benchmark excludes query time, geometry audit
and publication. Peak working set is the maximum for the entire child
process, including runtime and warm-up. Managed allocations are the
median per build. The run compares the local policy with the earlier whole-pair fine
lattice and the original coarse bridge. Each runs in a separate process.

[Local 2:1 boundary diagnostics](results-windows-mc-local-boundaries.json) · [Local and whole-pair performance](results-windows-mc-local-performance.json) · [Previous full-refinement diagnostics](results-windows-mc-bridge-edge.json)

## Off-seam grazing-ray fallback

On the locally refined collision mesh, a ray with no triangle hit can
query the analytic signed-distance source outside the protected seam
band (one unit on either side). The fallback considers only intervals
with two outside endpoints, so it reports complete entry/exit pairs.
It rejects zero-free intervals with the 1-Lipschitz distance bound and
subdivides the others to 1/4096 cell, up to 4,096 density evaluations
per ray. Budget exhaustion returns no partial crossings and is reported
separately. The fixed-step density oracle used by the audit is separate
from this search, but both evaluate the same fixture field. Mesh hits,
swept-sphere queries and mesh extraction retain their previous paths.

The diagnostic records raw mesh misses, rescued rays and final query
misses separately. This prototype requires an analytic density source
with the stated Lipschitz bound. The build benchmark includes fallback
construction and excludes per-ray query cost.

[Off-seam ray results](results-windows-mc-grazing-rays.json) · [Build performance with fallback](results-windows-mc-grazing-performance.json)

## Fallback query latency and edited density

```powershell
./benchmarks/Groundwork.Wp11Meshing.Probe/Run.ps1 -Tables $tables -Output ./artifacts/wp11-c00-ray-latency.json -RayLatency
```

The runner uses three fresh Release processes. Each case warms up with
512 calls and records 10,000 individually timed fallback invocations
by default. The timer includes the fallback result allocations and
excludes mesh traversal, snapshot construction, validation and forced
collection. Timestamps are not overhead-corrected. The output records
per-process latency percentiles and density evaluation counts.

The edited fixture uses immutable spherical CSG brushes with minimum
for addition and maximum of the negated brush for subtraction. The
mesh and the fallback share the same copied edit snapshot. The checks
cover removing an existing grazing hit, recovering an off-seam
sub-grid addition, retaining other grazing hits, keeping the old
snapshot stable, and failing closed on budget exhaustion. The historical
latency run documented a sub-grid addition that the mesh missed inside
the seam. The edited seam run below covers that case with a collision
primitive. The fixture does not exercise persisted edits or concurrent
publication.

[Edited density and query latency results](results-windows-mc-edited-ray-latency.json)

## Small additions inside the protected seam

An additive spherical brush wholly inside the protected band becomes
an analytic collision sphere if the base field proves it is isolated
and its support is disjoint from all other edit supports. The same
immutable snapshot keeps the sphere out of the sampled mesh field and
includes it in the reference field. The query merges analytic sphere
crossings with mesh and off-seam fallback crossings. A moving sphere
tests the sum of the two radii and uses the earliest valid contact.
No seam density fallback or extra grid resolution is required.

The edited run uses the same -RayLatency command. It reruns the broad
ray grid, the 76 existing bridge sweeps and 17 targeted edited sweeps,
including forward and reverse passage, misses, initial overlaps and
removed material. It tests one isolated addition within the band and
one straddling the actual 2:1 plane. An overlapping subtractive edit
prevents separation into a primitive. Brushes attached to terrain or
overlapping other edits remain in the sampled CSG path.

[Edited seam ray and sphere results](results-windows-mc-edited-seam.json)

## Ray intervals affected by off-seam subtraction

A ray with mesh hits can recheck a local interval around a subtractive
sphere that lies wholly outside the protected seam. The ray must
intersect the brush or pass within a margin of 0.125 cells. The probe
pads the resulting interval by half a cell, clips it outside the seam
and merges overlaps. It replaces
crossings only when mesh parity and density agree at both endpoints.
The bounded signed-distance search then finds sign changes in the final
edited snapshot, including a short empty interval inside a solid. A
ray has at most 4,096 density evaluations for this repair. Budget
exhaustion keeps the original mesh hits.

The edited run checks the known cut in both directions. It also reruns
the ray grid and edited sphere sweeps. The interval repair applies to
rays only. The narrow-cut sphere query below is a separate path.
This prototype uses static snapshots and
rays originating outside the solid.

[Subtractive ray and edited sphere results](results-windows-mc-subtractive-rays.json)

## Moving spheres in a narrow subtractive cut

For an isolated spherical cut outside the seam, a moving sphere that
starts wholly in the cut uses the cut eroded by the moving radius.
Convexity proves the path clear when the end also lies inside. For a
path leaving that volume, the probe computes the first exit and
verifies that its radial contact point lies in the original terrain.
If the contact cannot be certified, the narrow-cut solver returns
indeterminate and allows the opening solver below to handle eligible
cases. Intersecting edits and paths starting outside the eroded cut
are outside the narrow-cut shortcut.

The edited suite checks six paths whose mesh-only query reports an
initial overlap. Two short paths stay free. Four paths hit the cut wall
in either direction with radii 0.015 and 0.03 cells. It compares first
contact with both a closed-form value and the independent conservative
density sweep. The existing edited and unedited ray and sphere cases
remain regression checks.

[Narrow-cut ray and sphere results](results-windows-mc-subtractive-sweeps.json)

## Moving spheres through a cut opening

For a local opening between a spherical terrain lobe and a spherical
cut, the probe sweeps against the remaining clipped solid. The
distance considers valid projections onto either spherical face and
the circular edge where they meet. The solver is eligible only when
a convex bound around the entire swept sphere excludes the other
terrain components and unrelated edit supports. The local model
below extends this to a pair of overlapping subtractive spheres.

The edited suite checks four free passages through the opening
at two radii and in both directions, plus an entry and exit that hit
the rim. The clear paths have a geometric certificate. Rim contacts
are checked against an independent planar intersection. A raw CSG
field sweep and the mesh-only sweep are recorded as comparisons.

[Opening and collision regression results](results-windows-mc-cut-opening.json)

## Overlapping cuts at an opening

The local distance query supports one spherical terrain lobe and up
to two overlapping spherical cuts. A candidate on a spherical face
or an intersection circle must satisfy all remaining solid/void
constraints. Three-surface intersection points provide the endpoints
when an otherwise nearest point lies on a clipped portion of a circle.
The swept-volume bound must still exclude other terrain and edit
supports. Larger edit groups use the same bounded local model described
below.

The edited matrix adds a symmetric pair of cuts. Its midplane provides
independent controls for four clear passes at two radii and two rim
contacts. The expanded brush margin in the ray repair also covers a
mesh gap where a ray passes just outside both cuts. The same run checks
the broad ray grid and the 76 existing bridge sweeps in that snapshot.

[Overlapping-cut and collision regression results](results-windows-mc-overlapping-cuts.json)

## Ordered overlapping addition and subtraction

The same local opening solver can evaluate one overlapping additive
and subtractive brush in snapshot order. The retained faces,
intersection circles and endpoints must belong to the final solid.
A later union may restore material removed by an earlier subtraction.
The swept-volume bound still limits the query to a single spherical
terrain lobe and local spherical edits outside the seam.

The edited suite checks both orders with seven targeted sphere paths
each. These include axial entry and exit contacts at two radii, clear
off-axis paths in both directions and a contact at the brush
intersection circle that becomes an initial overlap when the
addition is applied last. The suite compares the contacts with
independent axial and planar calculations. It repeats the broad ray
grid and the existing bridge sweeps for both orders. The targeted
axial ray also verifies the thin positive cap that the mesh misses.

[Mixed edit and collision regression results](results-windows-mc-mixed-edits.json)

## Three or more overlapping brushes

The local opening query accepts a connected group of up to eight
spherical edits within the existing swept-volume bound. It preserves
snapshot order and verifies all candidate faces, clipped circles and
three-surface intersection points against the final solid. Edits
that cannot touch the swept bound are ignored. The solver still
requires a single spherical terrain lobe and an off-seam opening.

The Workstation regression run covers three coaxial brushes and two
four-brush orders. The fourth brush either restores a solid fragment
or is overwritten by the final subtraction. Each group runs seven
targeted sphere paths, the broad ray grid and the 76 bridge sweeps.
The targeted ray checks four crossings with the fragment removed and
six with it restored. The next suite exercises the eight-edit bound.

```powershell
./benchmarks/Groundwork.Wp11Meshing.Probe/Run.ps1 -Tables $tables -Output ./artifacts/wp11-c00-larger-groups.json -RayLatency -RayRuns 1 -RaySamples 1000
```

[Larger edit group results](results-windows-mc-larger-edit-groups.json)

## Five through eight overlapping brushes

The upper-bound suite adds four alternating additive and subtractive
operations to the existing four-brush sequence. It checks an axial
entry and exit at two radii, two free bypass directions and an axial
ray with six, eight or ten crossings depending on the final solids.
Each variant also runs the broad ray grid and the existing 76 bridge
sweeps.

A separate six-brush control cancels an addition with a later
subtraction of exactly the same sphere. The local boundary solver
drops an addition if a later subtraction wholly contains its support.
No solid membership is changed by this simplification. It prevents
a coincident zero-valued shell from being treated as collision
geometry. The ray and sphere controls both verify the cancellation.

```powershell
./benchmarks/Groundwork.Wp11Meshing.Probe/Run.ps1 -Tables $tables -Output ./artifacts/wp11-c00-upper-groups.json -RayLatency -RayRuns 1 -RaySamples 1000
```

[Five-to-eight brush results](results-windows-mc-upper-edit-groups.json)

## Spatially offset groups of five through eight brushes

The spatial variant offsets the brush centers along both directions
orthogonal to the opening axis. A nonzero scalar triple product
guards against accidentally testing coplanar centers. Entry and exit
paths are aligned with different exposed faces and use independently
computed contact distances. The five-brush case also tests the
intersection rim of the restored and trimmed material. The ray
control composes one-dimensional intervals in edit order using
independent sphere roots.

Each variant runs the broad ray grid, targeted paths and the existing
76 bridge sweeps. The previous coaxial and smaller edited matrices
remain regression checks. The fixture still uses a single spherical
terrain lobe and a local opening outside the protected seam.

```powershell
./benchmarks/Groundwork.Wp11Meshing.Probe/Run.ps1 -Tables $tables -Output ./artifacts/wp11-c00-spatial-groups.json -RayLatency -RayRuns 1 -RaySamples 1000
```

[Spatial brush group results](results-windows-mc-spatial-edit-groups.json)

## Spatial brush groups on the active 2:1 seam

This fixture places the opening center at X = 16 on the active
Transvoxel transition. The brush centers are noncoplanar. Targeted
entry and exit paths span both sides of the seam. Two bypasses
actually cross without contact, and a separate inward sweep crosses
before reaching the inner cut wall. The analytic opening solver can
query the seam fixture because its bounded neighborhood contains
one known spherical terrain lobe and a connected local edit group.
Other fixtures retain their existing seam restrictions.

For this spherical fixture the bounded ray repair can splice a
brush-local interval across the seam when mesh parity and field sign
agree at both endpoints. It can also recheck an empty mesh query near
the opening, including a grazing ray that the transition triangles
missed just beyond the brush support. The empty-query radial margin
is one cell. The existing budget and endpoint checks still apply.

```powershell
./benchmarks/Groundwork.Wp11Meshing.Probe/Run.ps1 -Tables $tables -Output ./artifacts/wp11-c00-spatial-seam-groups.json -RayLatency -RayRuns 1 -RaySamples 1000
```

[Spatial seam brush group results](results-windows-mc-spatial-seam-edit-groups.json)

## Non-spherical terrain on the edited 2:1 seam

This fixture uses the front of a finite box near X = 16. The local
solver substitutes the front half-space only when the bound around
the swept ball cannot reach any of the other five faces. It checks
the face projection, spherical edit surfaces, their intersection
circles and valid three-surface endpoints. The edit order and
eight-brush bound are unchanged. Other box faces and arbitrary
terrain shapes are outside this local analytic model.

Each group checks the existing spatial entry and exit paths, a
contact after crossing the seam and a contact at the plane/cut
intersection. The ray reference independently combines a box slab
interval with the brush intervals. The suite also repeats the ray
grid and 32 additional sphere sweeps per variant.

```powershell
./benchmarks/Groundwork.Wp11Meshing.Probe/Run.ps1 -Tables $tables -Output ./artifacts/wp11-c00-flat-seam.json -RayLatency -RayRuns 1 -RaySamples 1000
```

[Flat seam edit group results](results-windows-mc-flat-seam-edit-groups.json)
