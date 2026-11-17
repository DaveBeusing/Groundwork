# WP11 volume streaming

VolumeStreamingCoordinator composes immutable terrain with Core's world root. Groundwork.Voxels depends on Core for this authority. The existing standalone address, field and brick APIs continue to work.

## Host sequence

1. Configure VolumeStreamingLimits with disjoint baseline, movement, first-edit, recovery and speculative envelopes. Every dimension, including retained and retiring data, must fit the total. Candidate storage covers every envelope's retirement slots.
2. Select a bounded canonical window with VolumeStreamingSelection.Select. Pass the last selection for distance hysteresis. The support halo remains fine regardless of visibility.
3. Start each replacement with ChangeView, even when reusing the same origin, to revoke the prior view and close coverage. Call TryStage with the complete resource vector and mesh payload ceiling before dispatch. Execute the returned work outside authority locks, normally in Groundwork.Jobs. Dispose undispatched work. Use JobDispatchReservation to keep worker and queue headroom for protected handler IDs. Speculation cannot borrow that dispatch capacity or protected resource envelopes.
4. Confirm matching provider mask readiness, seal Core's CommitResults candidates, and call TryPreparePublication. Supply the host's composed gameplay root and dispositions when ordinary ECS work is present. Core.TryFinalize performs the sole authoritative swap. The permit orders cancellation and Stop against the swap and records its finalized stamp without a callback.
5. Register a frame for the committed generation and current view. Acknowledge only a successful provider Present and Signal. Call BeginTick after Core finalizes the next BeginTick barrier to revalidate evidence and open coverage. Use the [C02 collision and movement API](VolumeCollision.md) to validate and publish the player against that same generation.
6. Keep the frame registration alive through GPU settlement. Reader leases pin old generations. Stop closes admission and cancels work, while runtime custody remains until workers and retained readers settle.

The coordinator builds the portable 3D window at B01 strides 1 and 2. Renderer submission and broader planetary presentation belong to C03. Terrain brushes and their coupled ECS edits belong to D01. The publication root already binds plan, density, material, terrain revision, view, origin and frame evidence.

## Accounting and evidence

CPU credits include brick arrays, plan/request metadata, one validated triangle count per mesh region, mesh references and the reserved mesh payload. Plan validation records counts for the layout that passes the winding check, including recalculation on the all-fine fallback. Native/upload/retirement amounts are admission credits for later provider ownership. They are not measured physical GPU usage. Q01/Q02 establish production limits and reference-hardware qualification.

A bounded event FIFO stores view identities, generation, terrain revision and frame identity. Its reserved commit cell resolves from Core's atomic permit outcome. A full FIFO closes the journal epoch and retains terminal evidence. Consume events to return journal capacity. This is independent of diagnostic logging.

Late or canceled workers cannot publish and return credits only after disposing prepared artifacts. A committed root remains the authoritative save cut even while its view is held. Retained mesh and density readers stay valid until disposal.

## Reuse from a private build

When the host already needs a private build, it can admit it with `TryStagePrivate` and explicitly pass the completed work to the publishing job's `ExecuteReusing`. Dispose private work after its last use, including successful execution. The donor stays under its own reservation and cannot enter publication or native preparation. The recipient keeps its complete reservation and falls back to sampling and meshing if no compatible donor can be borrowed.

Reuse requires the same coordinator, view, source instance and base overlay. A staged edit invalidates any brick whose density vertices or material cells intersect its writes. Mesh reuse also checks the final LODs of the region and its face neighbors, the plan window used for clamping, and the gradient halo. Each recipient prepares its own plan and binds reused triangles to that plan and revision. The detailed execution profile reports adoption separately from output equality.

A private build can call `ExecuteDetailedProfiledReusingPublishedBricks` when rebuilding coverage from the committed terrain. The committed root can supply immutable bricks after a view or renderer switch if the sampler instance, overlay and terrain revision match. The donor is borrowed through a bounded reader lease. An unavailable donor or a missing brick falls back to sampling. The private build keeps its full reservation and builds its own mesh plan and meshes. This does not grant the private build publication authority.
