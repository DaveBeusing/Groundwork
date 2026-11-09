# WP11 C00 capacity probe

This standalone Windows x64 probe exercises resource admission and
publication ordering using real D3D12 buffer allocations and copies.
It uses fixed payloads and a deterministic route state, separate from
the engine, renderer and voxel generation.

```powershell
./benchmarks/Groundwork.Wp11Capacity.Probe/Run.ps1 -Output ./artifacts/wp11-c00-capacity.json
```

Requires PowerShell 7, .NET 10 and an NVIDIA D3D12 hardware adapter by
default. The runner starts three fresh Release processes. The pinned
TerraFX Windows binding matches the Groundwork renderer dependency.

## Fixed payload route

1. Allocate and retain root 40. Admit two speculative jobs and queue
   their GPU copies on a second queue behind a closed fence.
2. Reject extra speculative CPU, default-buffer, upload and slot
   requests before allocating. Cancel one job while retaining its
   charges. Neither job may consume protected capacity.
3. Prepare and verify movement coverage on the other GPU queue.
   Advance the covered route while both streaming copies are pending.
4. Commit the first queued edit (41). Reject the later edit (42),
   stale presentation acknowledgement and movement on mismatched roots.
5. Allocate and verify a separate recovery buffer with root 41 while
   old geometry is retained. Accept the matching acknowledgement and
   advance covered movement again.
6. Queue final GPU reads of the retained old and cancelled buffers.
   Keep their charges while retirement is blocked. Open the gate,
   await the fences, verify both readback markers and then release.
   Confirm that the ledger returns to zero after settlement.

The ledger tests one below, exactly at, and one above each byte or slot
limit. Worker completion releases its slot independently of bytes.
Allocation and settlement samples distinguish reservation, real payload
and ownership. The test intentionally holds GPU work through an
explicit fence, rather than depending on machine scheduling.

## Fixed payload measurement boundaries

- CPU payload is the sum of live, touched managed payloads. Process
  peak working set and sampled private bytes also include runtime
  and driver allocations.
- GPU default buffers and upload buffers use D3D12 allocation sizes.
  The sum includes a separately identified 64 KiB readback buffer.
- DXGI current usage is sampled at route transitions for local and
  non-local memory. These include driver usage and may miss transient
  peaks between samples. They are not heap accounting or residency
  guarantees.
- The fixture uses synthetic caps, a deterministic publication model,
  one hardware adapter and serial protected copies. It does not
  qualify production concurrency, meshes, the present path, GPU
  memory budgets, latency percentiles, fault handling or hardware
  scaling.

[Workstation NVIDIA measurements](results-windows-nvidia.json)

## Terrain and Present route

Build the same Release project, then run the executable on the Windows desktop.
Pass the pinned Transvoxel table source used by the meshing probe. The executable
verifies its SHA-256 before measuring.

```powershell
dotnet build benchmarks/Groundwork.Wp11Capacity.Probe -c Release
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE terrain $tablePath 100
```

Each job builds and packs eight 32-cell C00 terrain regions using Marching
Cubes and Transvoxel, with bridge and cavity fields and 2:1 face or corner
layouts. The route checks that the first edit changes the packed mesh and
that recovery rebuilds the same edited vertices. The two speculative jobs
fill the retained vertex, default-buffer, upload and job-slot quotas, with
their actual copies pending on the gated queue. Movement draws the old
mesh, a stale frame leaves movement and the next edit blocked, and a frame
with the recovered mesh resumes the route. Every frame submits actual
triangles to a visible 640×480 D3D12 flip chain and requires S_OK from
Present(1, 0). The old and cancelled resources retain their charges
until the retirement fence and readback settle.

The results retain every measured route. Percentiles use nearest rank
after five warm-up routes. Mesh wall time includes density sampling,
extraction and vertex packing. Verified upload wall time includes buffer
creation, mapping, copying, fence and sampled vertex readback. GPU draw
timestamps bracket the recorded graphics work. Present wall time covers
the DXGI call and frame fence wall time covers recording through queue
completion. Monitor scanout timing is outside the measurement.

CPU capacity accounts for retained packed vertices. Density grids,
intermediate meshing allocations, runtime and driver overhead are visible
in the process metrics. The resource peak sums concurrent default,
upload, readback and swap-chain allocation sizes. DXGI usage is sampled
at route checkpoints and includes other process-side driver allocations.

The terrain mode uses a small fixed fixture, one device and one process,
serial protected work, and a deterministic model for movement and edit
publication. Streaming saturation here means exhausted quotas and queued
copies held on the fence. The draw uses a flat color, a fixed projection
and no depth target. Use the data to size this fixture and to plan
production measurements with real world traversal and concurrent workers.

[Workstation terrain and Present measurements](results-terrain-windows-nvidia.json)