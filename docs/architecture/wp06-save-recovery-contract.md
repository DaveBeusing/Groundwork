# WP06 save and recovery contract

**Status** Proposed for review before implementation. Builds on the
[roadmap](../roadmap.md), [WP05 handover](../wp05-handover.md),
[walkable region contract](wp05-walkable-region-contract.md) and
[repository boundaries](../adr/ADR-0001-repository-boundaries.md).

## Scope and ownership

A save is one complete game checkpoint for one local slot. It captures the
world recipe, terrain changes, player position and inventory from the same
successful simulation publication. A restart reconstructs the terrain and
restores the checkpoint. An interrupted write leaves a complete committed
generation available if the slot previously had one.

Groundwork owns the versioned envelope, terrain delta codec, bounded
validation and recoverable slot storage. Propose a `Groundwork.Persistence`
package with BCL and `Groundwork.Voxels` dependencies. Voxels remains
independent of persistence and file I/O. WYU owns generator and item catalogs,
checkpoint composition, player payload semantics, the user-facing slot
location and save or resume actions. The host schedules file I/O using Core
work ownership. Groundwork has no game, ECS, renderer or Windows UI dependency.
Cross-machine synchronization, multi-user writes and planetary terrain are
later milestones.

## World recipe and terrain edits

- A world lineage has a stable nonzero 16-byte ID and an immutable recipe
  consisting of generator ID, generator version, seed and finite bounds.
  The application registers supported generators by the exact ID/version
  pair. For the WYU WP05 region, reserve ID 1 and version 1 for the existing
  deterministic recipe, with seed `0x57505535` in the acceptance fixture.
  The same pair must produce identical base occupancy for every supported
  seed and address across releases. A changed generator gets a new version.
- WP06 schema 1 uses the existing flat signed 16 × 16 × 16 chunks. Each
  address has signed chunk coordinates and an X-fast local cell index
  `x + 16*y + 256*z` in `[0, 4095]`. Chunk coordinates remain in
  `[-1024, 1023]`. Schema 1 stores bounds for X/Z and uses chunk layer
  Y = 0. Bounds are inclusive. Derive signed coordinates from world cells
  with floor division, then subtract the chunk origin for the local cell.
- The snapshot is a sparse overlay of final occupancy values relative to
  the exact generator. `0` explicitly removes a solid base cell and `1`
  fills a base air cell. Restoring a cell to its base value removes its
  entry. Serialize one nonempty record per changed chunk in signed X/Y/Z
  order, with strictly increasing local indices. Reject duplicate,
  out-of-bounds or redundant entries and occupancy values outside 0/1.
  The overlay owns edits to unloaded chunks and survives cache eviction.
- After a successful tick, capture immutable overlay and game values as one
  checkpoint before dispatching a write. Later ticks can continue using
  their own state. Load and validate the whole checkpoint before making
  any part of it active. Regenerate each requested chunk and apply its
  overlay before collision admission and meshing, including after unload
  and reload. Rebuild affected seam neighbors through the region pipeline.
  A restored player pose must be valid against reconstructed occupancy
  before WYU publishes a resumed region. Live chunk revisions, load
  identities, render generations and GPU resources are derived again.

### Address compatibility probe

The flat mapping `chunk = floor(cell / 16)` and
`local = cell - 16*chunk` round-tripped all 32,768 signed cell coordinates
on one axis. The same local indexing works on a face-local grid. A
cube-face probe with extent 16 used `+X(u,v) = (16,v,u)` and
`+Z(u,v) = (u,v,16)`. The two face centers have the same local coordinates
and distinct positions. At `u = 16, v = 0` the faces share the same
edge position. A future planetary address therefore needs a face or chart
identity and a seam rule in addition to local chunk coordinates. Schema 1
binds its flat address interpretation to the registered generator and
bounds. A future planetary schema must carry an explicit address domain.
Existing flat edits retain their original interpretation.

## Canonical format

All integers are little-endian. Counts and lengths are unsigned unless
specified. The 16-byte identifiers are opaque byte strings compared
byte-for-byte. The permanent framing prefix occupies bytes 0–55. Future
payload schemas must preserve this prefix and checksum rule so older readers
can identify an intact newer commit without decoding its payload.

| Offset | Field | Encoding |
| ---: | --- | --- |
| 0 | Magic | Eight ASCII bytes `GWSAVE01` |
| 8 | Envelope framing version | u16, 1 |
| 10 | Payload schema version | u16, nonzero, 1 for WP06 |
| 12 | Total frame length, including checksum | u32 |
| 16 | Committed generation | u64, nonzero |
| 24 | World lineage ID | 16 bytes, nonzero |
| 40 | Save request ID | 16 bytes, nonzero |
| 56 | Generator ID, generator version, seed | Three u32 fields in order. ID and version are nonzero |
| 68 | Bounds | Four i32 fields in order min X, max X, min Z, max Z |
| 84 | Changed chunk count, total edited cell count | Two u32 fields |
| 92 | Game payload schema version, game payload length | Two u32 fields |
| 100 | Terrain records, followed by game payload bytes | Schema 1 layout below |
| End − 32 | Integrity footer | Raw SHA-256 over every preceding byte, including the prefix |

Each terrain record starts with three i32 coordinates X, Y, Z and one u32
cell count. Each cell record is a u16 local index, one u8 occupancy byte and
one reserved u8 set to zero. Counts and lengths must match the records and
actual file length exactly. A zero total edit count requires zero chunk
records. The checksum detects accidental damage and binds the generation
and lineage to their payload. Reject trailing bytes and noncanonical input.
Resolve compatibility before decoding the game payload or allocating from
any untrusted count. Use checked arithmetic for all offsets and sizes.

Groundwork validates the game payload boundary and passes the bounded bytes
to the application codec. For WYU game schema 1, use the proposed checkpoint
payload of three i32 position coordinates in units of 1/1,024 voxel, a u32
inventory entry count, then sorted pairs of nonzero u32 item ID and quantity.
Limit the list to 256 unique item IDs. An empty inventory is valid. WYU
validates the exact payload length, item IDs and quantities against the
active game catalog and validates the pose against reconstructed terrain.

The reference fixture has generation 1, lineage bytes `01` through
`10` in hex and request bytes `11` through `20` in hex. It uses generator
1/version 1, seed `0x57505535` and X/Z bounds `[-4, 4]` / `[-2, 2]`.
It removes local cell `(15,3,4)` (index 1087) in chunk `(-1,0,0)`.
Game schema 1 stores position `(-20992,4096,7680)` in fixed-point units
and zero inventory entries. The 168-byte frame has footer
`31c91f7703b1e481dc1ab05878e33098a6c60e8b525d6244f4624d626e5f6f9e`.

| Hard limit per slot | Maximum |
| --- | ---: |
| Committed files and private candidate | Two committed files and one candidate |
| Frame bytes | 2 MiB each, including checksum |
| Terrain overlay | 1,024 changed chunks and 262,144 edited cells |
| Game payload | 256 KiB |
| Active I/O work | One load or save per slot, with no internal waiting queue |

Check file length before allocation. Readers use bounded buffers and
validate counts against the declared remaining bytes and caps. A writer
checks the canonical checkpoint and budgets before touching either
committed file. Report capacity rejection without changing the slot.

## Commit and recovery

Use an application-owned slot directory on a local Windows NTFS volume for
WP06 qualification. The host selects the path independently of save data.
Use fixed file names `save.a`, `save.b` and `save.pending` in that
directory. A cross-process exclusive slot lease serializes scans and
publication. A second caller receives `Busy`. A process exit releases
its lease, regardless of whether the lock file is still present.

1. Under the slot lease, inspect both committed files. An empty slot starts
   at generation 1 with an expected generation of 0. A write to an existing
   slot supplies the generation and lineage it read. Reject an obsolete
   base or different recipe without mutating disk. Generations increase by
   one and stop before overflow.
   A retry carries the same request ID. If it matches the newest commit and
   the canonical checkpoint bytes match, return that commit. Compare the
   lineage, recipe, overlay and game payload independently of the generation
   and checksum. Reusing a retained ID with a different checkpoint fails.
   A retry after an intervening commit receives a conflict. The slot writer
   generates a new ID for each new logical request.
2. Use `save.a` for the first commit and the other committed file on later
   writes, retaining the currently selected generation untouched. Write
   the complete candidate to `save.pending`, flush its
   contents to storage, close it and verify its length, checksum and
   envelope. Use a same-directory atomic publication primitive to replace
   the inactive committed file. Atomic publication is the commit point.
   The implementation must establish the replacement and flush semantics
   on its qualified backend. A sequence that deletes the active file before
   publishing is forbidden.
3. Verify the published file before acknowledging success. A failure with
   an uncertain publication result is resolved by a fresh scan under the
   slot lease using the request ID. If the result remains unavailable,
   report an indeterminate outcome. Cancellation can abandon a candidate
   before publication begins. Once admitted, publication and verification
   settle under the same lease, including during host stop. A deadline can
   report incomplete settlement while the worker retains custody until the
   operation returns. File I/O and flushes run outside simulation barriers
   and observer leases.
4. On load, ignore any private candidate. Validate committed framing, file
   length and checksum before interpreting the payload. Examine surviving
   generations in descending order. A generation with a supported schema
   and valid payload can be selected. A malformed generation can be skipped
   with recovery evidence, including when its known-schema payload fails
   validation despite a valid checksum. Report the selected generation,
   damaged file and validation outcome. Equal generations with different
   intact bytes, different intact lineages or inconsistent decoded recipes
   are conflicts. Byte-identical duplicates can select `save.a` as a
   deterministic tie-break.
5. An intact newest generation with an unsupported payload schema,
   generator or game catalog blocks resume and subsequent writes with an
   explicit compatibility result. Preserve both files. An unknown framing
   version or a read/access error also blocks automatic selection when the
   reader cannot rule out a newer intact commit. If both files are absent,
   return `Empty`. If committed files exist and neither can be recovered,
   report failure and preserve the evidence. Recovery never creates a
   replacement world implicitly.

A process interrupted before publication leaves the previous committed
generation available. Interruption during atomic publication permits
either adjacent complete generation. Interruption after publication can
leave the new generation committed before the caller receives success.
These guarantees cover process interruption on the qualified local
filesystem. Machine power-loss durability requires separate storage and
failure qualification.

## Review and acceptance

| Gate | Required evidence |
| --- | --- |
| Format | Golden schema-1 bytes and digest, canonical ordering and stable output for the same checkpoint. Round-trip seed, generator version, signed addresses, explicit removal/addition, position and inventory. Test limits, checked overflow, truncation, malformed framing and counts, trailing data, duplicates and unsupported versions. |
| Restore | Save an edit at a shared chunk boundary, leave the region, restart and reload it. Assert the same occupancy and seam geometry, collision at the edited surface, and the same player position and inventory. Validate that failed resume publishes no partial game or world state. |
| Recovery model | Inject interruption before write, mid-header/body/footer, before and after file flush, around atomic publication, and after publication before acknowledgement. Assert exact old/new selection at each checkpoint, private candidate exclusion, corrupt-newest recovery and preservation on both-corrupt or unsupported-newest cases. |
| Windows backend | Use a child process and controlled barriers to terminate real writes on local NTFS before and after publication. Verify the actual files from a fresh process, including first-save interruption, alternating committed slots, busy writer, lost acknowledgement retry, stale base and blocked access. |
| Runtime and integration | Verify save capture on one published tick, later edits staying outside that checkpoint, bounded work and native shutdown during a save. Restore the exact verified Groundwork package in a clean WYU checkout, demonstrate edit, save, interrupted follow-up and successful resume, and record the tested commits and results. |

Implementation order after contract approval is portable format and terrain
overlay validation, Windows storage and crash qualification, then verified
package integration and the WYU save/resume handover.
