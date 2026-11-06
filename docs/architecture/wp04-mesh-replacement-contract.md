# WP04 terrain mesh replacement contract

**Status** Proposed for review before implementation. Extends the
[WP03 two-chunk terrain contract](wp03-two-chunk-terrain-contract.md)
and the [WP02 renderer contract](wp02-d3d12-renderer-contract.md).
The [roadmap](../roadmap.md) defines the milestone.

## Scope and compatibility

WYU edits one cell on the shared boundary of its existing two-chunk scene.
Groundwork builds two complete meshes from the resulting snapshots and D3D12
replaces them as a unit while the renderer is running. The old pair can
remain visible while the new pair is prepared. WP04 keeps the same two
chunk coordinates for the scene lifetime. Streaming, collision, persistence,
LOD, partial buffer updates and multiple scenes are later milestones.

Runtime replacement is opt-in. Preserve existing constructors,
`CreateWithMeshes`, fixed mesh mode, the built-in cube, the renderer-free
host, `IRenderer`, `RenderFrame` and the existing frame admission results
and sequence semantics. Reuse the current immutable `RenderMeshPair` and
its size and translation constraints. Keep the frame value free of mesh
references. Neither rendering package takes a production dependency on
Voxels. WYU owns the edit input and scene composition.

## Scene edits and stale results

- One scene coordinator owns the authoritative immutable chunk snapshots.
  After publication it captures both together for meshing. Each changed
  chunk gets a strictly increasing nonzero revision. An unchanged neighbor
  retains its snapshot and revision. Every committed pair change also gets
  one strictly increasing nonzero **geometry generation**, scoped to this
  renderer instance. Chunk revisions, geometry generations and frame
  sequences are independent. Exhaustion requires stopping the scene before
  wrap. A no-op edit does not create a new generation.
- Declare the desired generation at the edit's post-publication handoff
  before dispatching its mesh build. A failed world publication produces
  neither a declaration nor a mesh build. The same coordinator serializes
  changes to its current snapshot pair, generation declaration and mesh
  submission. After a successful declaration, a newer generation may
  supersede an earlier one even if the newer build has not finished.
- At most one build is in flight and one latest request can wait per scene.
  The waiting request holds the newest immutable input pair. Superseded
  waiting requests are dropped. Cancellation is cooperative and does not
  free the worker's slot until that work settles. Keep at most one completed
  result awaiting the coordinator, within the single build slot. The
  worker receives only owned snapshots and never calls the renderer.
- The build uses `ChunkMesher.BuildPair` on the captured pair, then converts
  both canonical meshes to immutable render inputs with their chunk-origin
  translations. Build and copy run outside writer barriers and observer
  leases. At handoff check the scene is still current, the generation still
  matches the latest declared generation, and `ChunkMeshPair.Matches`
  confirms both original snapshots. Discard the whole result if any check
  fails. Retry the current build only while it is still current and within
  the same slot bound. Stop closes the coordinator's submission path and
  settles worker ownership through the existing runtime protocol. The
  simulation coordinator never waits for the worker or GPU.

## Portable renderer admission

Provide an optional mesh-replacement capability on the portable rendering
boundary. The contract below defines outcomes. Exact public type and
method names belong to the implementation review.

| Operation | Rule |
| --- | --- |
| Opt in at construction | Supply an initial immutable pair as generation 1. The owner installs it before reporting Running. Existing fixed mode remains available. |
| Declare desired generation | While Running, accept only a positive generation strictly greater than the last declared one. A new declaration advances the watermark and drops an older pending candidate. It does not evict the active native pair. |
| Submit mesh pair | While Running, accept a complete immutable pair only for the current desired generation, once per generation. Reject zero, missing input, older or undeclared generations and duplicates without changing the watermark or pending work. Empty meshes follow WP03. |
| Read status | Return a coherent snapshot of desired, installed and last presented mesh generation. Zero means none for installed or presented. Last presented identifies the generation used by the last successful normal mesh-scene present. A clear-only present does not advance it. |
| Stop | Close geometry admission with frame admission. Reject further declarations and submissions, drop queued CPU references and wake the owner. Repeated stop shares the existing deadline and settlement. |

Distinguish invalid input, stale or undeclared generation, duplicate, not
running and closed outcomes from successful admission. Admission is a short
CPU operation. It copies a reference to immutable buffers, performs no
native operation and waits for neither GPU nor presentation. Successful
admission means queued, with installation and presentation reported
separately. Wake the render owner on a successful submission even if no
new frame arrives. Providers without the optional capability expose their
existing interface.

The geometry mailbox has one pending pair and the owner has at most one
CPU candidate in preparation. A newer declaration can supersede either.
The owner rechecks lifecycle and the watermark when taking a candidate and
again at installation. The final check and installed-generation publication
share one short admission critical section with declarations and closure.
The installation must also be ordered against terminal Core stop, target
revocation and the watchdog deadline. The provider must define a commit
point at which the operation is still permitted and within its deadline.
If stop, revocation or expiration wins, installation does not occur. If
installation wins, later stop still revokes any new draw or present
admission. Do not rely on asynchronous stop notification alone to decide
whether the operation may commit.

Move ownership and publish status in the short commit section. Keep
native allocation, mapping, fence queries and COM release outside it.
Use the WP02 ownership order without nested authority locks. If a candidate
loses the commit race, release its private, unsubmitted resources without
changing installed state. A generation installed before newer intent arrived
remains a valid fallback until the next eligible installation.

## D3D12 installation and retirement

1. The existing render owner is the only thread that allocates, maps,
   installs and releases native mesh resources. A replacement is one
   watchdog-bounded operation using the existing Core and target admission
   rules. On transient denial, leave the candidate pending or return it
   to the empty pending slot only while it is still the desired generation
   and admission is open. This does not count as a second submission.
   Retry on the owner's existing progress wake or bounded poll without
   busy-spinning. Terminal revocation prevents further replacement.
2. Attempt installation at a frame boundary with a drawable target and a
   current non-null scene, after the previous frame's CPU operations have
   returned and the queue is idle. Reuse the existing retirement tracker.
   Queue idleness must cover the last draw and any admitted normal
   `Present` and its post-Present fence. If draw was submitted but
   `Present` was denied, the draw fence still has to retire. A failed
   signal leaves retirement unproven. The fence's removal sentinel alone
   does not prove safe normal reuse.
3. Prepare the candidate in a private bounded WP03 upload buffer. Check
   packing and translated coordinates, unmap on every mapped exit, and
   make no GPU submission referencing it before installation. A fully
   empty pair allocates no geometry buffer. The old native pair stays
   active throughout preparation.
4. Revalidate drawability and the target revision before installation. If
   suspension or a target change is observed during preparation, release
   the unsubmitted native buffer and retain the CPU candidate for a later
   turn only if it is still eligible. An already admitted preparation
   may return after suspension. Start no preparation while the owner
   observes a suspended surface. After the final admission check, install
   both mesh views together and mark the current scene for redraw. Release the old geometry on the
   render owner after the retirement proof in step 2. Complete that release
   before preparing another replacement. All draws in a frame use one
   installed generation and the same depth target. A redraw may reuse the
   last frame sequence. It does not advance the simulation or manufacture a
   new frame sequence. Frames queued during preparation keep their normal
   latest-only ordering. Before drawing, take the latest eligible frame.
   A clear-only frame can follow installation and leaves the mesh generation
   unpresented until a mesh-scene draw is successfully presented. A failed or
   visibility-test present does not advance the presented geometry generation.
5. While minimized, occluded or without a scene, retain the active pair
   and only the latest eligible pending pair. On restore or the next
   drawable scene, evaluate the pending candidate again. Resize changes
   swap-chain resources under WP02 rules. It does not recreate geometry
   by itself.

At most two native pair buffers may be live per renderer, including partial
candidates or resources retained in custody. The maximum logical geometry
payload is 7,077,888 bytes before native allocation padding. There is at
most one outstanding frame retirement and no queue of retiring mesh
generations. Preserve the current position and index layout, translation
order and shared depth buffer. Steady-state frames perform no mesh upload.

### Failure and shutdown

A superseded result or orderly stop is an expected discard. Invalid portable
submissions are rejected before ownership transfer. A native preparation
failure, including translation overflow found during packing, follows the
provider's existing supervised fault path with a bounded geometry-stage
diagnostic. Clean up the private candidate if quiescence is proven.
Never publish part of a candidate or resume admission after terminal
fault. Keep the old active pair in normal stop custody.

Stop revokes new operation admission and prevents installation even if an
already admitted preparation later returns. Pending CPU references can
be dropped immediately. The owner retains any resources that an admitted
native operation or GPU work may still reference. A blocked operation or
unproven fence at the earliest Core deadline reports incomplete cleanup.
Timeout and cancellation do not prove quiescence. Keep the render owner,
runtime lease and any affected target or HWND in existing process custody.
A late return with retirement proof, or confirmed device removal after CPU
native calls return, permits cleanup. Continue pumping the STA until the
target release acknowledgment. Acknowledge only after all operations that
can reference the target have returned and swap-chain references are
released. The runtime lease outlives final device cleanup.

## Acceptance evidence

| Gate | Required evidence |
| --- | --- |
| Meshing and stale data | Apply and revert the WYU edit. Verify that the unchanged neighbor gains the expected exposed boundary face on removal and loses it on restore. Delay one generation's mesh result until the next declaration, and verify whole-pair rejection without an installed or presented regression. |
| Portable admission and owner lifecycle | Use deterministic test seams to cover invalid, duplicate and undeclared generations, queue bounds, declaration versus installation races, direct stop, Core stop and target revocation at the commit boundary, plus suspension or a newer declaration during preparation. Assert no split pair, no new allocation while suspended, and no change to legacy frame sequencing. |
| WARP readback and CI | Present or render a controlled pair before and after replacement on the same device. Sample pixels unique to each old and new instance and pixels on both sides of the join, including the first mesh-scene draw after installation. Assert actual RGBA and numeric depth, with the near draw surviving a later far draw. Verify both draw bindings use the old generation before commit and the new generation on that first mesh-scene draw. Verify clear-only and empty-pair behavior. Require the replacement readback to execute and pass on Windows CI. |
| WARP and NVIDIA desktop | With active Debug Layer and DRED, present successive generations, queue replacements while minimized, and verify that only the latest eligible generation installs after restore. Verify resize, native close, and active GPU-buffer high-water mark at or below two. Run the controlled color/depth readback on NVIDIA as well. Record adapter, driver and passed counts. |
| Interrupted replacement | On both adapters hold a test callback after private allocation and before final installation. Request stop and prove no candidate publication, with both active and private resources held until the callback returns and safe cleanup follows. In deterministic provider tests delay draw and post-Present retirement separately and inject a failed signal. Verify that the old buffer cannot be released early and that a stale candidate never installs. Any intentional in-process hold must have a bounded test-controlled release. |
| Native cleanup | After successful or late settlement, verify no device removal on the healthy path, zero debug errors, corruption, discarded messages and live GPU child objects, and a successful live-object report. Record warning counts. Verify renderer, HWND and Core lease release. Tests of device removal and failed signals verify their distinct custody outcomes. |
| WYU packaged integration | From clean Windows checkouts, archive verified Groundwork packages and pin the exact versions in WYU. Run the two-chunk scene on NVIDIA with active Debug Layer and DRED. Observe the edit, restore, then native close and clean settlement. Retain the existing cube and renderer-free regressions. |

The WYU demo toggles the low chunk's local cell `(15,3,4)` at the shared
`+X` face. The high chunk's neighbor at `(0,3,4)` stays solid. Removing
the low cell exposes the high cell's `-X` face and restoring it removes
that face again. Choose a camera or test sample that makes the resulting
notch visible. Compare installed and presented generations when confirming
the edit, rather than relying only on successful queue admission.

## Implementation order after approval

1. Define the optional portable admission and status contract with fake-owner
   tests. Validate the seam edit against the existing Voxels mesher.
2. Implement D3D12 staging, installation, retirement and diagnostics. Add
   required WARP CI and qualify desktop WARP and NVIDIA.
3. Archive the verified packages, pin them in WYU, add the edit demonstration
   and run the clean cross-repository handover.
