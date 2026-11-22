# Volume relocation and checkpoint verification

## Relocation

A host captures the committed terrain publication and walking pose before
asking for destination coverage. The destination is expressed in planet-centered
metres. The requested and settled foot positions must both be within 512 m of
the source. Settlement checks at most 1 m below the requested foot.

Use the normal fresh-view, generation and mask protocol. After the destination
terrain is ready, a D3D12 host calls `TryPrepareVolumeRelocation`. An unbound
coordinator uses `TryPrepareRelocation`. Both require the same authoritative
source pose, a matching terrain revision and adequate fine coverage for the
destination. An optional static-obstacle sweep should use the immutable
gameplay state associated with that source.

A prepared destination is provisional. Commit the returned publication at the
matching Core barrier to publish the terrain and player pose together. Follow
the normal presentation and movement-gate protocol before resuming walking.
A rejected or revoked preparation leaves the previous committed root intact.
If a destination mask was submitted, restore and present source coverage
before opening movement again.

## Checkpoint verification

`VolumeCheckpointSlot.VerifyCommitted` compares the selected committed member
to the caller's expected checkpoint using the canonical codec bytes. A
`Verified` result confirms an exact match. `Mismatch` indicates a readable
committed member with different content. A busy lease, missing lease, or
unresolved publication keeps its corresponding status.

Verification uses an existing lock and keeps the slot files intact. It does
not settle a recovery intent or write a replacement checkpoint.
