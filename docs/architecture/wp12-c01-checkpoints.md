# WP12-C01 · V2 volume checkpoints

A volume checkpoint stores a finalized terrain revision, canonical edit overlay, player pose and the game bytes encoded from that same immutable world root. The V2 game payload includes the inventory effect of natural ore depletion when the host encodes that state. Restore opens a fresh Core world at the saved completed tick and rebuilds terrain, meshes and collision from the exact saved descriptor and overlay. The host restores its game state from the same decoded cut. Movement waits for the existing terrain, pose and presentation checks.

## Format and compatibility

| Planet generator | File magic | Checkpoint schema |
| --- | --- | --- |
| V1 landform | `GWVOLCP1` | 1 |
| V2 geology | `GWVOLCP2` | 2 |

The existing `v2` directory is the volume-save namespace for both formats. The file schema identifies the byte format within that namespace. V1 encoding is byte-identical. V2 uses the same fixed header, body layout, SHA-256 coverage, and overlay and game size bounds. The header binds slot and lineage, operation, planet and world IDs, seed, generator, density and material versions, recipe digest, radius and cell size. V2 overlay density rows keep the original signed values. Material rows carry a nonzero UInt16 material and at least one flag, depletion in bit 16 or placement provenance in bit 17. Higher bits are reserved. The codec validates order, bounds, flags and identity before the host receives data. Terrain restore also validates the material catalog and whether a depleted cell was a natural ore deposit in that recipe.

A slot resolves only its configured generator and exact recipe digest. Unsupported recipes, schema/generator mismatches and incompatible data remain intact. Game bytes remain opaque to Groundwork and pass through the slot's host validation callback.

## NTFS recovery

The two-member NTFS protocol and intent record protect the entire cut. A terminated writer before member replacement leaves the previous generation readable. After replacement, the matching intent permits recovery of the installed generation. Density edits, placement/depletion state, inventory bytes and pose recover together. On reload, refilling and removing a depleted cell cannot yield the original natural deposit again.

Tests cover V1 golden bytes and compatibility, V2 structural validation, a V2 cut captured through the real streaming authority and writer, and child-process interruption during the write, intent, replacement and acknowledgment stages. The integration fixture compares collision and pose after rebuilding the cut and tries to mine the depleted cell again. WP11 Q02 [#89](https://github.com/DaveBeusing/Groundwork/issues/89) and production acceptance remain open pending the reference run.
