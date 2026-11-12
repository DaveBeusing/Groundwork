# WP13 Windows integration handover

The workstation integration passed for Far Horizon with seeds 42 and 20261111. Both reference worlds rendered from walking altitude through orbit and returned to a supported landing. The visual recipe is wyu.reference-biomes.v1. [WP13 contract](architecture/wp13-biome-rendering-contract.md)

## Tested inputs

| Input | Identity |
| --- | --- |
| Groundwork master and package source | 8d6b239f3bc78a61158d122be427b202342ae47a. [Windows CI](https://github.com/DaveBeusing/Groundwork/actions/runs/43301223359) |
| WYU master | a085328c425b69034f69f5d0113d4a4dbaefd7f5. [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/43319246200) |
| Exact ten-package set | 0.1.0-dev.43301223359.1. [Immutable archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43301223359.1-immutable) |
| Package manifest SHA-256 | BC7931C80C74F910E2C715650062232B1A59F57EE956394E399B96DB7A46B51D |
| Workstation | Windows 11 Enterprise build 26300, .NET SDK 10.0.403 |
| Hardware adapter | NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU, 10DE:2C38, driver 32.0.15.9653 |
| Software adapter | Microsoft Basic Render Driver (WARP) |

Both tracked worktrees were clean at their remote master revisions. Groundwork restored and rebuilt the D3D12 test project from source. WYU cleaned the Release build, verified the ten archived packages, and rebuilt with a fresh locked NuGet restore. The package source commit matches the tested Groundwork master.

## Two-seed desktop route

Each seed opened in a new isolated world through the normal world selector. The windowed NVIDIA Release client displayed the ground terrain, took off with F6, entered orbit, braked and landed. F10 committed generation 1 at terrain revision 1 and closed the client cleanly. Both routes reached orbit at tick 2640 and touchdown at tick 20664. The diagnostic logs identified the exact WYU master build and requested seed.

| Seed | Visual checkpoints | Route result |
| --- | --- | --- |
| 42 | Ochre ground at spawn, green and violet-grey orbital terrain, ochre during braking | Ground, orbit, landing and save passed twice |
| 20261111 | Green/olive ground at spawn, ochre-to-violet blend and green orbital checkpoints | Ground, orbit, landing and save passed twice |

The desktop observations cover representative viewpoints. The canonical control test provides the deterministic seam comparison. For each seed it verified 486 samples, including 92 shared chart points, exact IDs and weights across serial and reverse parallel evaluation, all three dominant zones, normalized blend weights and zero managed allocation in the sampled evaluator loop. Separate walking replays for both seeds completed on WARP and NVIDIA, including mixed LOD and a rebase.

## Readback, save and GPU settlement

- Nine filtered Groundwork tests passed. They covered default and optional color readback, mixed LOD, rebase, rejection of private color candidates, editing and queue custody. The WARP and NVIDIA color readback and edit variants used Debug Layer and DRED.
- All eight WYU gameplay tests passed. The verified consumer also passed headless and controlled-fault shutdown, WP11 edited-terrain recovery after an interrupted write, and the WP07 checkpoint regressions.
- The scripted flight regression passed on WARP and NVIDIA with matching flight traces, touchdown continuation, aborted-flight recovery, Debug Layer and DRED. Intermediate handoffs reported the deliberately retained queue. Final reports had zero errors, corruption, discarded messages and live children.
- The NVIDIA Ember Caves regression passed mining, underground continuation, return to supported surface and native shutdown with Debug Layer and DRED. Both cave processes reported zero errors, corruption, discarded messages and live children. Ember Caves still uses the geology palette.

To reproduce the focused producer tests, run the D3D12 test project in Release with the filter FullyQualifiedName~VolumeColorPolicy|FullyQualifiedName~WarpVolumeReadbackCoversTransition|FullyQualifiedName~WarpDeviceSessionTests. For WYU, use eng/Test-VerifiedGroundworkConsumer.ps1 with the archive above and the SaveSmoke and PrototypeSaveSmoke switches. Run eng/Test-ProceduralFlight.ps1 and eng/Test-CaveExploration.ps1 from the resulting Release build. For the visual route, start a reference world for each seed, capture ground and multiple orbital viewpoints, then save after touchdown.

## Timing evidence and remaining gates

The [WYU integration PR](https://github.com/DaveBeusing/wyu/pull/51) records the final five-pair Windows NVIDIA A/B on seed 42, using the same package set. Its tested head e986d9a has the same source tree as the WYU merge commit. Ground and orbit Present budgets passed. The p95 changes were −2.43% for near packing, +3.26% for globe build, −5.19% for ground to orbit and −6.50% for orbit to ground. The workstation route rerun did not collect new per-frame GPU timings or concurrent native peak memory. The PR records the earlier sampled GPU counters and managed memory measurements with their scope.

The workstation verifies the two-seed integration. Exact visual continuity at every contractual waypoint, the aggregate transient CPU peak and reference-system pressure and production budgets remain separate qualification evidence. [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88) and [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89) remain open.
