# WP16 Windows integration handover

Far Horizon streams biome-dependent rocks, scree, grass and trees using the bounded D3D12 prop renderer. WYU derives placements from the seed and published terrain. Its optional placement worker runs alongside movement, cancels superseded work and releases its terrain lease before staging. Edit reuse requires matching collision coverage and brick LOD plus support outside the edit halo.

| Input | Verified identity |
| --- | --- |
| Groundwork master | 8382269eecd3957a60afe1ec5bea2eda020044cb. [Windows CI](https://github.com/DaveBeusing/Groundwork/actions/runs/43590571564) |
| WYU master | d1918a791afd20ed15497ead2404120eda91f777. [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/43607324255) |
| Groundwork packages | 0.1.0-dev.43590571564.1. [Immutable archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43590571564.1-immutable-complete) |
| Manifest SHA-256 | FD8683BDE1749AA65324FC847F2A61D7C20CAEE22CBB6ADFEBCA8F87C3F9F4C5 |

## Windows integration

Both checkouts were freshly cloned on master. Groundwork passed seven portable prop-cell tests and twelve focused D3D12 cases, including WARP and NVIDIA with Debug Layer and DRED. Those cases cover rebase, the first Present after a terrain edit, upload saturation, atomic replacement and cancellation, and optional prop initialization failure. WYU passed verification of all ten archived packages, a fresh locked restore, the Release build, nine gameplay tests, and normal and controlled-fault headless shutdown.

Both 480-tick routes passed on the NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU with driver 32.0.15.9653, Debug Layer, and DRED. The window presented at 944 × 501. Each run committed twelve movement ticks while prop placement was deliberately held. The checks rejected reuse when the collision window shifted or only the LOD changed, and the camera sweep uploaded zero mesh or instance payload bytes. Mixed prop LOD groups drew across the route, which also crossed a voxel terrain LOD boundary and rebased.

| Seed | Terrain views / windows | Peak prop draws / visible instances | Staged / retired cells | Debug warnings |
| --- | ---: | ---: | ---: | ---: |
| 42 | 3 / 2 | 8 / 179 | 355 / 231 | 492 |
| 20261111 | 5 / 3 | 6 / 183 | 490 / 362 | 498 |

Both runs closed the window and released native children with zero D3D12 errors or corruption. The staged and retired counts are cumulative placement-work counts, including refreshes.

## Reproduction and remaining qualification

In WYU, run eng/Test-VerifiedGroundworkConsumer.ps1 with the archived package directory, manifest SHA-256, Groundwork commit, and package version above. Then run eng/Test-ReferenceProps.ps1 on a Windows desktop with NVIDIA and Graphics Tools. The script runs both seeds in separate processes.

The functional integration is ready for handover. The dedicated hysteresis, edited-tree pixel check, save/continue placement equivalence, dense stress fixture and reference-class performance budgets remain open under the [WP16 contract](architecture/wp16-instancing-streaming-contract.md). WP15 remains opt-in and the WP11 production qualification remains open.

[Groundwork #114](https://github.com/DaveBeusing/Groundwork/issues/114) · [WYU #44](https://github.com/DaveBeusing/wyu/issues/44) · [WYU PR #57](https://github.com/DaveBeusing/wyu/pull/57) · [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89)
