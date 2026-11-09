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

## Route

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

## Measurement boundaries

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
