# WP17-G00 Windows integration handover

Ember Ridges is WYU's explicit V3 world selection. Groundwork supplies a seeded relief evaluator shared by volumetric terrain and uncarved orbital relief. V3 preserves the cave route, ore depletion and terrain edits through Save/Continue. V1 and V2 keep their existing generator identities.

| Input | Verified identity |
| --- | --- |
| Groundwork master | `905c5682f2988bcd0b74ebeafc01f03ee6b6f01c` · [Windows CI](https://github.com/DaveBeusing/Groundwork/actions/runs/43632281999) |
| WYU master | `0463245a6ecfb3c9606b7303b37b5b3286a8cba0` · [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/43635931519) |
| Groundwork packages | `0.1.0-dev.43632281999.1` · [Immutable archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43632281999.1-immutable-complete) |
| Manifest SHA-256 | `E687281D56B7B407D81CA9CA9EB946AE60E74112DE5B6CEAF586359B0DC68594` |

## Windows workstation results

Groundwork built in Release with locked restore. All three focused V3 generator tests and all three V3 portal readback cases passed. The portal cases include WARP and hardware with Debug Layer and DRED. The focused checkpoint/restore run passed 24 of 25 tests, including V3. The V2 process-abort harness hit a sharing violation while reading its ready marker and passed on isolated retry. The initial intermittent failure remains recorded here.

Both repositories were freshly cloned on `master` and started with clean working trees. WYU verified the manifest and all ten packages, restored the locked dependencies in a fresh package cache and built Release. All nine gameplay tests and the normal and controlled-fault headless shutdown checks passed.

The two-process seed 42 route ran on the NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU with driver 32.0.15.9653, Debug Layer and DRED. The first process descended into the cave, mined four ore and saved terrain revision 2. The second reopened the underground pose and edit overlay, confirmed the underground launch rejection, reached supported exterior terrain, and completed the orbit and return. Touchdown retained the edit revision and inventory. Touchdown occurred at simulation tick 20755. Both processes closed the window and released Core and native resources. Final reports showed zero D3D12 errors, corruption and live native child objects, with 10 debug warnings on descent and 196 on return.

The following point heights compare the published near collision mesh with the actual orbit render mesh along matching radial directions on unedited terrain. All three fell within the 0.12 m fixture tolerance. The test selects a resolved vertex for each feature and samples the corresponding near terrain.

| Feature | Near mesh | Orbit mesh | Absolute difference |
| --- | ---: | ---: | ---: |
| Ridge | +1.9950 m | +2.0001 m | 0.0051 m |
| Main valley | -1.4816 m | -1.5001 m | 0.0185 m |
| Tributary | -0.9901 m | -0.9961 m | 0.0060 m |

## Reproduction and remaining qualification

Run `scripts/Verify-Bootstrap.ps1` from the Groundwork checkout. From the WYU checkout, run `eng/Test-VerifiedGroundworkConsumer.ps1` with the archive directory and the manifest digest, source commit and version above. Then run `eng/Test-ConnectedRelief.ps1` on the Windows NVIDIA desktop with Graphics Tools installed. The script writes the two route logs to a fresh fixture directory and prints its path.

The workstation run covers the functional route and numeric mesh points. The [contract](architecture/wp17-landform-contract.md) also calls for fixed control transects with off-feature samples, final landing Save/Continue, and production performance measurements on the WP11 reference class. The present route verifies underground Save/Continue and in-memory state at touchdown. Keep those remaining WP17 gates open in [Groundwork #115](https://github.com/DaveBeusing/Groundwork/issues/115). WP15 default activation and [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89) remain open.

[WYU #45](https://github.com/DaveBeusing/wyu/issues/45) · [WYU PR #58](https://github.com/DaveBeusing/wyu/pull/58)
