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

The three-plane junction uses a separate, uniform half-cell collision
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

[Workstation junction and mixed-region results](results-windows-mc-collision-boundaries.json)
