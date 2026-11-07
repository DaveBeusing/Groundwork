# WP07 playable WYU prototype contract

WP07 takes the [WP06 handover](../wp06-handover.md) as its starting point.
The player lands in a bounded first-person test region, finds three stone
deposits, and builds and activates a rescue beacon. The same game can be
saved and resumed before or after activation.

## Player loop

1. The Windows client opens a screen with **New Game**, **Continue** and
   **Quit**. **New Game** opens the fixed landing site when the prototype
   slot is empty. An existing slot offers **Continue** and preserves the
   saved game. Replacing an occupied slot is outside this prototype.
2. At the landing site, the objective and controls appear in the game.
   A marked pad shows where the beacon will go. A compass marker points
   toward the nearest remaining deposit, with a fixed tie-break by deposit
   order. Once all deposits are mined, the marker leads back to the pad.
3. Walk to each deposit, aim at its visible face and use **Interact**.
   The focused prompt names the resource. Mining removes the deposit,
   updates collision and adds one stone. The HUD shows carried stone and
   total deposits collected.
4. Return to the pad and use **Interact** to install the foundation, mast
   and signal head in that order. The pad prompt names the next module and
   its cost. Each visible module occupies its own socket and costs one stone.
   A final **Interact** transmits the rescue signal. The game confirms
   **Rescue beacon online** and lets the player keep walking, save or quit.
5. **Save** is available from the pause screen. **Continue** restores the
   last committed position, carried stone, mined deposits, built modules
   and signal state. Loading a completed game shows the beacon online.

The initial controls are **WASD** to move, **Left/Right** to turn,
**Up/Down** to look vertically, **E** to interact and **Esc** to pause.
Menus support arrow navigation and **Enter**. Ask for confirmation before
quitting with progress newer than the last confirmed save.
List the controls in-game. Show the focused
action or reason it is unavailable, the objective, and a distinct pending,
confirmed or failed save status. Prompts must remain legible at the existing
960 × 540 client size. The loop starts from the normal Windows launch path
and can be completed using the on-screen guidance.

## Reference world and actions

Use the WP05 flat signed 16 × 16 × 16 chunk domain with X bounds
`[-4, 4]` and Z bounds `[-2, 2]` in chunk coordinates, terrain in chunk
layer Y 0, and the WP05 seeded surface rules. The prototype adds the deposits
below to a new WYU generator, ID 2, version 1, seed `0x57505537`.
The player starts at `(-20.5, 4, 7.5)` in voxel units. Deposits are solid
protrusions on the flat floor at Y 4.

| Site | World voxel or anchor (X, Y, Z) |
| --- | --- |
| West deposit | `(-10, 4, 7)` |
| Seam deposit | `(0, 4, 7)` |
| East deposit | `(18, 4, 7)` |
| Beacon foundation, mast, signal head | `(-19, 4, 9)`, `(-19, 5, 9)`, `(-19, 6, 9)` |
| Pad interaction anchor | `(-17.5, 5, 9.5)` |

The reference route crosses X chunk planes `-16`, `0` and `16`
outbound and on return. Every deposit and pad socket is reachable from the
walking surface. Mining a deposit leaves its floor intact. The pad anchor
is outside all three sockets and stays accessible after construction.

For deposits, take the first solid cell hit by the view ray within four
voxels of the eye. A closer solid voxel occludes the target. The pad uses
its visible interaction anchor within the same reach, with line of sight
and a focus prompt. If both targets qualify, choose the nearer hit, with a
solid hit winning a tie. Perform each action on a press edge and recheck
target, reach, coverage, inventory and prerequisites on admission. Holding
the key cannot spend or award repeatedly. The next module's solid volume
must be clear of the player before placement.

There are three deposits and three one-stone purchases. Stone has no
other use in WP07. Modules are permanent once placed and their mask is an
ordered prefix. Actions outside reach, through a wall, with insufficient
stone or while terrain is changing leave the world and inventory unchanged.
No other crafting, mining, building or multiplayer rules are part of WP07.

## Ownership and publication

WYU owns the recipe, input mapping, objective UI, interaction rules,
inventory, checkpoint payload and composition. Groundwork owns reusable
voxel snapshots, streaming, meshing, collision, renderer lifecycle,
supervision and save storage. Use independently built, exactly pinned
Groundwork packages. A reusable API gap needs a small Groundwork PR with a
specific failing case and an independent package build before WYU adopts it.

WYU admits at most one mining or building change at a time. Stage the
candidate from a published tick. Keep the prior live state and its
visible region while loading and meshing the candidate. Pause movement,
further mutations and new save capture during this transition and show a
pending status. The pause screen can open during a transition. Completion
of work already admitted continues while gameplay input is paused.
At a published tick after the matching renderer generation
has presented, adopt the collision view, resource count and objective
state together before resuming input. A request superseded by stop cannot
publish game progress. If the candidate fails, retain the prior checkpoint
and coherent scene for a recoverable failure. If the renderer has faulted
or coherent presentation cannot be recovered, stop through Core and report
the error. Stale results cannot advance inventory, collision or the HUD.
Activation changes only the objective state and is published on a tick
after the final module has presented.

Rebuild affected seam geometry from both chunks in one generation. Restore
and stream the same effective voxel state for both collision and meshes.
Represent modules as three solid cells derived from the ordered module
mask. Save only deposit removals in the terrain overlay. This gives each
edit one authoritative representation and restores the same effective
region after unload or restart.

Reuse the WP05 limits of nine inner chunks, 50 snapshot slots, two load
reservations, one mesh reservation and 96 MiB of CPU mesh data. Count active
and staged work together against those bounds. Maintain at most one pending
world mutation and one active save, with no waiting queue. A save already
in flight keeps its immutable capture while a later action changes the
live game. Reuse WP06 storage limits and stop custody rules. On stop, cancel
unpublished staging and let an admitted atomic save publication settle
under its lease. Release native window, GPU and Core resources through the
existing bounded lifecycle.

## Save and compatibility

Use the WP06 envelope and Windows NTFS slot with a separate application-owned
WP07 directory. The WP06 test mode keeps its own slot and schema 1.
Prototype **Continue** reads only the WP07 slot. Unsupported intact newest
data blocks resume and further saves, preserves the files and shows a clear
message. If recovery selects an older valid generation, show its generation
and the recovery outcome. Failed loads leave the start screen in control
without publishing a partial game. Only an empty slot permits New Game.
Start its lineage on the first save under the existing slot lease.

WYU game payload schema 2 uses exactly 20 bytes. Integers are little-endian.
The WP06 terrain overlay permits only the canonical removal of the three
designated deposits.

| Offset | Field | Validation |
| ---: | --- | --- |
| 0 | Three signed 32-bit position coordinates | 1/1,024 voxel units, within the recipe and valid on reconstructed collision terrain |
| 12 | Unsigned 32-bit carried stone | 0–3, equal to removed deposits minus installed modules |
| 16 | Unsigned 8-bit module mask | One of `0b000`, `0b001`, `0b011`, `0b111` |
| 17 | Unsigned 8-bit signal state | 0 or 1, with 1 requiring all modules |
| 18 | Two reserved bytes | Both zero |

Save a supported, nonintersecting player position from one published tick
together with the game state and canonical terrain overlay. A rejected
capture keeps the last good checkpoint and shows the reason. Use a stable
request ID for an indeterminate retry of the same capture. A fresh request
has a new ID. The selected game state is valid only after recipe,
compatibility, resource accounting, module ordering and collision
validation all pass.

## Acceptance before WP07 handover

| Gate | Required evidence |
| --- | --- |
| Deterministic route | Start from the documented seed and spawn. Cross all three chunk planes, mine all three deposits, return with three stone, build three ordered modules and activate the beacon. Record the seed, generator, input trace, resulting state and queue high-water marks. Run the trace twice and compare committed state and canonical checkpoint contents apart from generated identities. |
| Interaction and coherence | Exercise held input, occlusion, out-of-reach targets, occupied build socket, insufficient resources, stale worker results and stop during a pending edit. Verify the seam deposit disappears from geometry and collision in the same admitted world state. Unload and reload the edited chunk and verify its state persists. |
| Save and resume | From separate processes, resume after the first mine, after partial construction and after activation. Verify position, inventory, occupancy, mesh, module mask and goal state. Cover malformed payloads, unsupported newest data, occupied-slot protection, and a later edit while an earlier save is pending. Kill a real Windows writer before and after publication and verify the expected complete checkpoint on local NTFS. |
| Windows integration | Build and run from clean checkouts using the verified archive and locked restore. Keep Windows CI green. Exercise the playable loop and native shutdown on NVIDIA with Debug Layer and DRED. Run targeted WARP readback and retirement tests if the native render path changes. Record adapter, diagnostic and resource settlement results. |
| New-player playtest | Observe at least two first-time players using separate empty slots, the normal launch screen and in-game controls without coaching. Both complete the objective and save and resume. Record where they hesitate, get lost or misunderstand feedback, then use the findings to choose the next gameplay priority. |

Review and accept this contract before implementation. Implement the WYU
rules and deterministic fixtures first, then playable input, UI and region
integration, then checkpoint and Windows qualification. Close with a
Groundwork handover linking the tested commits, package provenance and
playtest results.
