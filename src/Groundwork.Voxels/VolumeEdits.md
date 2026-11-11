# WP11-D01 terrain edits

Construct an editable VolumeStreamingCoordinator with an immutable brick source
and a maximum number of sparse overlay rows. The source object identifies the
generator lineage for every subsequent stream operation. Budget each generation
with RequiredEditableCpuBytes. This includes the full overlay arrays and the
refined request array. Renderer packing credits are in addition to this amount.

A brush uses inclusive integer lattice vertices, one input tick, a nonzero ID
and an expected terrain revision. Add and remove stamp the selected vertices
to at least half a cell on the solid or empty side of the density field.
Incident material cells receive the specified nonzero material on addition
or their generator material on removal. Density remains the solidity source.
Rows equal to the generator are removed from the sparse overlay.

At a finalized BeginTick with admitted terrain, call SealEditTick with the
whole input set. It sorts by brush ID, validates lineage, revision and coverage,
and reserves bounded journal capacity. The current sealed tick retains receipts
and accepts an identical retry. A newer accepted tick replaces the retained
receipts. Only the first unresolved ID can stage. After a successful edit, the
remaining IDs in that tick are stale and must be resubmitted with new IDs and
the new revision. The journal's queued intents and Core-backed commit record
determine those stale outcomes.

Call ChangeView to hold movement before TryStageEdit. Execute the returned
work outside the authority lock. RejectEdit settles an abandoned head before
Core preparation. Failure leaves the previous root in place. Compute the dirty
dependency box with TryGetEditDependencies. Editable builds conservatively
regenerate the whole selected window and refine bricks around edited samples,
including material, normal and transition dependencies. This accounts for LOD
plan changes that can extend beyond the brush. Rebuilding a different window
uses the same committed global overlay.

Use the existing C03 native staging and mask protocol before preparing
CommitResults. Core publishes the overlay, complete mesh and collision generation
in one root. Movement remains held until the provider retires the corresponding
frame and a later BeginTick admits it. An edit is terrain-only. Gameplay effects
and checkpoint storage are separate work packages.

TryWriteEditOverlay on a retained committed lease writes a little-endian Int32
density count and material count, followed by sorted density rows then material
rows. Each row has signed Int64 X, Y, Z and value fields in lexicographic XYZ
order. The payload digest contributes to mesh-plan identity. A checkpoint
consumer supplies the lineage and revision from the same publication cut.
