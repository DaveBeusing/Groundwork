# WP11-D02 volume checkpoints

The application supplies a local NTFS directory named `v2` under its save root. Reserve a previously absent lowercase GUID slot directory. The reservation file binds that slot to a world, lineage and exact planet descriptor. Existing slots are opened with the same identifiers and descriptor. The old heightmap and flat-save formats remain separate.

## Schema 1

All integers and binary64 values use little-endian byte order. GUIDs use the .NET byte representation returned by `Guid.TryWriteBytes`. The 170-byte identity record contains these fields in order.

| Bytes | Field |
| --- | --- |
| 8 | ASCII magic `GWVOLCP1` |
| 2 | Schema |
| 16 each | Slot ID, lineage ID |
| 8 | Expected base generation |
| 16 each | Operation nonce, world ID, planet ID |
| 8 | Seed |
| 4 each | Generator, terrain schema, material catalogue version |
| 32 | Generator parameter SHA-256 |
| 8 each | Radius and cell size in metres |
| 4 | Overlay byte count |

The next 32 bytes are the SHA-256 of that identity record. The body starts with three unsigned 64-bit integers for finalized tick, terrain revision and capture ordinal. All three are positive. A one-byte player presence flag follows, then nine binary64 values for position, heading and velocity. A zero flag requires all 72 value bytes to be zero. The body continues with the overlay and up to 65,536 opaque game bytes. The last 32 bytes are the SHA-256 of all preceding bytes.

The overlay starts with signed 32-bit density and material counts whose sum is at most 65,536. Each density row followed by each material row contains signed 64-bit X, Y, Z and value. Within each table the keys increase lexicographically and fall within the generator lattice domain. Density is in units of 1/256 cell. A material row identifies a nonzero unsigned 16-bit member of the descriptor's material catalogue. The application validates catalogue membership and game payload compatibility and supplies the exact baseline on restore. Overlay entries identical to baseline are rejected by the terrain restore entry point.

## Capture and restore

Reserve a checkpoint reader during BeginTick before CommitResults. Core binds the reservation to the immutable root when it finalizes CommitResults. The caller takes the retained cut for the host-tracked save worker. The lease and immutable world root refer to the same terrain generation. Encode the application state from that root and copy the overlay on a host-tracked worker. An exclusive NTFS writer handle holds admission authority for the life of the capacity-one slot writer. It binds the expected generation and retry nonce at admission, and keeps encoded bytes until publication settles. The application owns the tracked worker lifetime and game-payload encoding. Terrain restoration seeds the coordinator with the checkpoint's overlay and revision before building its first coverage. Initialize Core and the fixed-step driver with the saved completed tick. The coordinator publishes the saved pose and velocity with rebuilt terrain and admits movement after fresh coverage.

## NTFS publication and recovery

The writer holds an exclusive slot lease and pins the directory chain. It classifies both committed names under the lease. Foreign identity, unreadable identity or unknown format protects the slot. A payload-damaged member with a verified compatible identity can expose a valid sibling as read-only recovery. To resume writes from a recovered cut, reserve a separate slot and explicitly copy the checkpoint with new slot and lineage IDs. Reuse the recovery-copy nonce to retry the same copy.

Generation is the operation's expected base plus one. Odd generations use `volume.a`; even generations use `volume.b`. The writer uses a uniquely created private file, flushes and verifies it, rechecks both members including their NTFS identities, atomically installs a separately staged and flushed intent record, and publishes by directory-relative write-through rename. The intent binds both prior member digests and the candidate digest. After process interruption, a fresh reader checks the intent against the observed committed members. An unresolved intent blocks writes. Unpublished staging files are never eligible for load. The next admitted write cleans private candidates from the reserved staging directory under the same lease.

The two committed members retain the retry receipts. An identical retained operation returns AlreadyCommitted, changed bytes with the same operation return Conflict, and a rotated operation with an old base returns Stale. Fresh saves must advance the (tick, capture ordinal) pair and cannot reduce terrain revision. Cancellation before rename preserves the committed pair. After rename the slot verifies the committed result before reporting success.
