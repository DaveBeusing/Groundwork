# Static D3D12 texture residency

Attach a `D3D12TextureHandoff` to a volume renderer before starting it. Publish the world, renderer epoch, view, terrain revision, coverage generation, and complete appearance target set. Each material input has its own sequence. Retain the exact target when changing an unrelated palette slot.

Reserve source, scratch, cooked payload, copy staging, aligned native allocation, and one descriptor before starting a worker. Cook or read and validate the asset with the portable `TextureCooker` on the producer side within that reservation. Transfer the immutable result after the worker releases its source and scratch, or settle the ticket after cancellation and worker exit. Request credit includes the candidate while the prior binding still owns its native credit.

The renderer checks the native level-zero format and footprint before allocation. A bounded queue turn can submit one copy of at most 2 MiB including the footprint. Protected volume staging and presentation receive priority. The owner polls assigned copy retirement independently of scene traffic, settles staging after the copy fence, and installs a current binding at the next eligible render boundary. A successful installation can redraw the already authorized terrain while paused.

The static reference route samples the lowest declared BaseColor target from the current cut, using the level-zero texels and the cooked sRGB interpretation. An unavailable target uses existing terrain color. Data and Normal targets have distinct validation and remain available for later shader consumers. The reference sampling coordinates are local to the existing terrain vertex route. INT-01 owns canonical material selection and projection. The shader-visible heap retains separate slots for the material and shadow bindings.

`ReadTextureDiagnostics()` reports actual resident and pending native bytes, staging bytes, combined native high-water and copy traffic. Camera and scene submissions upload zero texture bytes. A failed signal retains possible native references and their credits until existing queue custody settles or device removal is confirmed.

With resource snapshots enabled, the same Direct Queue ledger reports texture and texture staging separately. A copy updates the submitted fence and its own copy fence. Frame sequences, draw and Present fences, and pass observations still describe scene submissions. The terrain pass counts the texture backing when it records a draw that samples the binding.
