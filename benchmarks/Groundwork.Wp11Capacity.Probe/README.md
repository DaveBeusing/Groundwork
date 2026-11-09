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
## Concurrent terrain route

Use the same Release binary and table path with the `concurrent-terrain`
mode.

```powershell
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE concurrent-terrain $tablePath 100
```

Two staged streaming jobs fill the speculative GPU and upload budgets
and wait on the gated copy queue. Two additional speculative CPU and
slot reservations admit two competing mesh builds during each protected
movement, edit and recovery build. A start barrier and timestamp
intersection verify three overlapping build intervals in every phase.
The coordinator joins both competitors before submitting the protected
upload. The competing outputs are discarded and their staging
reservations stay held through recovery. Dispatch and join are included
in the protected job wall time and separately from the individual build
intervals.

The first rendered region has a walkable platform spanning a 2:1 LOD
boundary. The route samples ground height on the actual triangle mesh
at no more than 0.35 world units per step and rejects missing ground or
a height change above 0.55. The unedited mesh supports the direct route.
The movement mesh supports the approach but rejects the uncovered edge.
After the matching recovered mesh is presented, the edit blocks the
direct path and a detour crosses the seam. Publication and retirement
use the capacity harness. The route models grounded motion with sampled
ray queries, with full character dynamics and production streaming
outside the scope of this benchmark.

The budget comparison preserves the serial test's limits. It uses the
largest observation for each matching category, including the packed
CPU payload of the competing jobs. The retained collision triangles,
meshing intermediates and allocations outside the byte ledger contribute
to process memory.

[Concurrent workstation measurements](results-concurrent-terrain-windows-nvidia.json)

## Fixed worker pool and latency breakdown

Use `pooled-terrain` to run the same grounded route, saturation checks,
streaming gate and GPU retirement with a bounded FIFO and three persistent
meshing threads.

```powershell
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE pooled-terrain $tablePath 150
```

Each protected phase dispatches one protected build and two competing builds.
Queue time runs from submission to dequeue, barrier time from worker entry
to the beginning of the build, and build wall time covers density sampling,
extraction and packing. The coordinator records wave time through the final
join. Initial staging builds still run on the coordinator, as in the
concurrent baseline. This queue admits one wave at a time, while speculative
GPU copies and streaming quota remain saturated through recovery.

The probe records managed allocation deltas on each worker and process-wide
GC pause duration deltas around each build, wave and route. Wave and route
samples also include GC collection count deltas by generation. Build wall
times include pauses. Process-wide pause deltas across concurrent intervals
can count the same pause, so these values must not be added or attributed to
an individual worker. Queue time also includes worker wake-up and scheduling
delay. No scheduler trace is collected.

[Fixed worker pool measurements](results-pooled-terrain-windows-nvidia.json)

## Sustained CPU backlog

Run the same three-thread pool with a continuously full three-entry
FIFO and with speculative GPU copies held through recovery.

```powershell
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE backlogged-terrain $tablePath 150
```

Each dequeue immediately refills the FIFO under the same lock. One
protected job can wait at ingress. At the next dequeue it replaces the
speculative refill at the tail, behind the existing queued jobs. Its
measured wait includes ingress and FIFO time. Background jobs build
and pack terrain continuously and discard their output after measuring
it. The CPU queue stays full during mesh builds, upload, collision
checks and Present. The harness stops refilling after recovery, drains
active workers, and then opens the GPU streaming gate for retirement.

Packed vertex high-water counts retained and in-flight packed arrays
from the point of allocation through their logical use. The upper
bound assumes all three workers can simultaneously hold the largest
speculative output alongside the retained payload. Queued jobs reserve
capacity but do not yet materialize vertex arrays. Working set and
private bytes include the rest of the process. GC deltas are process
wide and can overlap across jobs.

[Sustained backlog measurements](results-backlogged-terrain-windows-nvidia.json)

## Reserved worker and bounded speculative vertices

Use the same saturated route with one of the three workers reserved
for protected jobs.

```powershell
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE reserved-terrain $tablePath 150
```

Movement, edit and recovery go directly to the reserved worker through
a single dedicated slot. The other two workers keep the three-entry
speculative FIFO full under the same dequeue-and-refill lock. Both
GPU streaming copies remain gated through recovery.

The packed-vertex budget is 4 MiB. After reserving the known retained
route payloads, including the staged streaming buffers, the remainder
is the cap for active speculative builds. Each speculative build acquires
its known packed size before meshing and returns the credit after its
output is discarded. If its preferred fixture cannot fit, it builds
the smaller fixture instead. Queued descriptors do not allocate packed
arrays. The test checks the byte reservation before every packed array
allocation in the competing and protected builds and verifies that
speculative credits return to zero. The cap covers packed vertices.
Density grids, triangle intermediates and runtime allocations contribute
to the separately measured process memory.

[Reserved-worker workstation measurements](results-reserved-terrain-windows-nvidia.json)

## Optimized edit and recovery

Run `optimized-terrain` with the same reserved worker, speculative
vertex cap and full streaming queues.

```powershell
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE optimized-terrain $tablePath 150
```

The edited fixture has three distinct local terrain inputs for eight
placements. The edit extracts each distinct mesh once, then packs all
eight placements in the original order. Startup compares the packed
vertices, transition count and collision mesh against the original
eight independent extractions. Recovery reuses the live, committed
generation's CPU payload. It performs a separate GPU copy, verifies the
markers and sampled vertex words and draws from the recovery buffer.

Edit and recovery use separate preallocated default-heap buffers and a
shared persistently mapped upload buffer. The coordinator writes staging
only when the previous copy fence and readback have completed. It returns
default-buffer leases after use, and the next route starts after drain.
Memory samples include the entire pool even when its buffers are idle.
The duplicate-input optimization is specific to the identical local
fields, edits and LOD layouts in this fixture.

[Optimized workstation measurements](results-optimized-terrain-windows-nvidia.json)

## Shared extraction and paced Present

Use `paced-terrain` to extend the optimized route to movement, initial
staging and speculative CPU jobs.

```powershell
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE paced-terrain $tablePath 150
```

Jobs extract each unique local fixture and LOD layout once and still pack
all eight placements. The startup check compares the output of every job
with independent extraction for each placement. CPU streaming still
continuously builds terrain. Movement uses a separate preallocated default
buffer and the same fenced upload staging as edit and recovery.

The flip chain waits for the DXGI frame-latency handle before recording
and calls Present with sync interval 1 and DO_NOT_WAIT. If DXGI reports
WAS_STILL_DRAWING, the harness retries the same frame within a bounded
interval. Present wall time includes every retry and retry wait. The time
from frame start through queue fence includes the readiness wait. The
readiness time and retry count are also recorded separately.

[Shared extraction and paced Present measurements](results-paced-terrain-windows-nvidia.json)

## Distinct density fields and LOD layouts

Run `varied-terrain` with the same reserved worker, speculative packed
vertex cap, full speculative CPU FIFO, gated GPU copies and paced
Present as the shared-extraction test.

```powershell
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE varied-terrain $tablePath 150
```

Each job extracts all eight 32-cell regions independently. The sampled
density field has a different local translation per region and the
eight layouts use four differently oriented half-fine face layouts and four distinct fine octants. The preflight records density sample SHA-256, the fine-brick
mask and mesh counts for every region. It checks pairwise distinct
fields and masks within each job and requires transition triangles
in every region. The first region preserves the walkable platform
and 2:1 seam for the same collision and detour checks. Recovery still
reuses the live CPU payload of the confirmed generation before a new
verified GPU upload.

[Distinct-region workstation measurements](results-varied-terrain-windows-nvidia.json)

## Tuned extraction for distinct regions

Run `tuned-terrain` with exactly the same density fields, LOD masks,
CPU backlog and gated streaming copies as `varied-terrain`.

```powershell
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE tuned-terrain $tablePath 150
```

Each worker retains one density array and seven temporary triangle
lists. Density samples are rebuilt for every region. The first
region's collision mesh and the final packed payload have independent
ownership. The extractor reads regular-cell corners by linear grid
strides and computes transition positions only for nonuniform cases.
The same interpolation and triangulation functions emit the output in
the original order. Startup compares packed vertices, transition counts
and collision triangles against the reference extractor for every job.
No mesh inputs are deduplicated in this mode.

[Tuned distinct-region measurements](results-tuned-terrain-windows-nvidia.json)

## Draw completion before Present

Use `ready-present-terrain` to keep the tuned extraction, distinct
region inputs, reserved worker and saturated queues while adding an
explicit draw-completion fence before calling DXGI.

```powershell
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE ready-present-terrain $tablePath 150
```

The coordinator waits for the graphics queue to finish the submitted
draw and transition the buffer back to PRESENT before calling Present.
The post-Present fence still governs reuse and retirement. The benchmark
records the pre-Present draw fence wait separately and includes both
fences, swapchain readiness and all Present attempts in frame wall time.
The Present call and frame budgets stay the same.

[Draw-ready workstation measurements](results-ready-present-terrain-windows-nvidia.json)

## Present sync-interval comparison

Use `immediate-present-terrain` for the same tuned route and DXGI
readiness wait with sync interval 0. It uses the original post-Present
fence without the experimental pre-Present draw wait.

```powershell
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE immediate-present-terrain $tablePath 150
```

Interval 0 changes flip-queue behavior. S_OK confirms submission and
does not establish display timing for individual frames. This test
continues to charge readiness, retries and the retirement fence to
the full frame wall time. The original `tuned-terrain` mode retains
interval 1 for comparison.

[Sync interval 0 measurements](results-immediate-present-terrain-windows-nvidia.json)

## Row masks for distinct-region extraction

Use `row-masked-terrain` to retain the distinct inputs, tuned scratch,
reserved worker, packed vertex cap and interval-0 Present path.

```powershell
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE row-masked-terrain $tablePath 150
```

Sampling records the negative-density bits for each row in worker-local
scratch. The regular-cell pass combines the four neighboring rows and
checks only cells with both negative and nonnegative corners at the
current LOD stride. It visits crossing cells in the original order.
Transition triangles still use the same extractor. All eight regions
are sampled and meshed separately in each build, including the initial
and speculative jobs. Preflight compares each job's packed vertices,
transition count and collision triangles against the reference path.

[Row-mask workstation measurements](results-row-masked-terrain-windows-nvidia.json)

## ETW correlation of Present and fence timing

In an elevated terminal, start the checked-in WPR profile. Run the
benchmark itself from a regular terminal and stop WPR once it exits.

```powershell
wpr -start "./benchmarks/Groundwork.Wp11Capacity.Probe/C00.wprp!C00" -filemode
# In the regular terminal, after building the Release probe
$env:GROUNDWORK_C00_TRACE_CLOCK = '1'
dotnet artifacts/bin/Groundwork.Wp11Capacity.Probe/release/Groundwork.Wp11Capacity.Probe.dll 10DE row-masked-terrain $tablePath 600 > run1.json
# In the elevated terminal
wpr -stop c00.etl
```

The optional JSON markers include the process and native render-thread
IDs, QPC frequency and exact frame, Present and fence boundaries.
Match DXGI events 42 and 43 on the same process and thread inside the
Present interval. Intersect context switches and ReadyThread events
with each QPC interval to separate running, runnable, waiting and
unknown time. DXGI time is nested inside the measured Present call.
The interval from Present return to the frame fence includes signaling
and waiting on the CPU, while the GPU draw duration is a separate GPU
timestamp measurement. This profile omits stacks and the run yielded
no DxgKrnl events. The raw ETL is kept local because scheduler events
cover the workstation.

[ETW workstation measurements](results-row-masked-etw-windows-nvidia.json)

## CPU stacks and DxgKrnl follow-up

Use `C00Stacks.wprp!C00Stacks` in place of the lighter profile above.
The profile adds sampled CPU stacks, image-load events and DxgKrnl
Present, scheduling and driver events. Record with the same QPC markers.
Filter samples to the benchmark PID and native render TID inside each
Present window, then resolve matching OS symbols. Record unresolved
driver frames as module-relative offsets. Sampled stacks establish
which code was executing at the sample times. They do not give an exact
duration for an individual routine. Keep the system-wide ETL local.

[Stack and DxgKrnl workstation measurements](results-row-masked-etw-stacks-windows-nvidia.json)

## Pinning a benchmark to a scan-out adapter

Run the probe with `outputs` to list active Windows DisplayConfig paths
and the separate DXGI output enumeration. The DisplayConfig target
adapter identifies the scan-out leg used by this test. On a hybrid
machine, the DXGI enumeration may associate both outputs with the
rendering adapter even when DisplayConfig reports a different target.

Set `GROUNDWORK_C00_OUTPUT_VENDOR` to `8086` for Intel or `10DE` for
NVIDIA. The probe prefers the single external target on that vendor,
otherwise it requires a single target. The rendering adapter remains
the explicit first argument. The probe verifies the window is fully
inside the chosen monitor before every frame and checks the selected
DisplayConfig path again at process completion.

[External Intel scan-out ETW measurements](results-row-masked-etw-hybrid-displayport-windows-nvidia.json)
