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
