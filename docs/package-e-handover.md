# Package E Windows integration handover

The Windows workstation replay verified Far Horizon daylight, regional fog, and return from orbit on the merged Groundwork and WYU integration. The [Package E contract](architecture/package-e-distance-fog-contract.md) defines the renderer and gameplay boundaries.

| Input | Tested identity |
| --- | --- |
| Groundwork master | `2d86e96db95e855c190b2cebbf0b1f4ab2c70a53`. [Windows CI](https://github.com/DaveBeusing/Groundwork/actions/runs/43332217203) |
| WYU master | `2175016cc3820e8c009d8bc757282aa3f9af59b2`. [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/43444636926) |
| Groundwork packages | `0.1.0-dev.43332217203.1`. [Immutable archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43332217203.1-immutable-complete) |
| Manifest SHA-256 | `078C3925FCE45E0B5710246A3BC54440A0702B7BEAD7CD7462FBEEB2B9C92E6C` |
| Workstation | Windows 11 Enterprise build 26300, .NET SDK 10.0.403 |
| Hardware | NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU, driver 32.0.15.9653 |
| Software | Microsoft Basic Render Driver (WARP) |

Both clean checkouts were pinned to the fetched remote master commits. Groundwork rebuilt its D3D12 tests from source. WYU verified the ten archived packages against the expected manifest, exercised tampered and missing package rejection, and rebuilt with a fresh locked restore.

## Clean Windows replay

- **Day/night and weather rules.** All nine WYU gameplay tests passed. Seeds 42 and 20261111 covered dawn, noon, dusk and night, positive and negative region boundaries, tick 59→60, cache eviction and altitude fade. Weather update p99 was 0.0498 ms and 0.0097 ms respectively, against 0.25 ms. Each seed measured 10,000 updates with zero managed allocations after warm-up and 880 bytes of resident weather state.
- **Regional fog in the Windows renderer.** Both seeds crossed camera x=128 m in three committed 60 Hz steps on WARP and NVIDIA. The adapters matched for each seed. The exact-boundary submission and samples on its two sides used the same terrain publication. The original exploration/rebase routes also matched.
- **Orbit and Continue.** The seed-42 flight replay verified daylight parity across near/orbit handoffs, zero fog in orbit, and local fog on descent. WARP and NVIDIA matched the flight trace and restored the landed pose, environment tick 29664 and fog opacity 0.32236606. Aborted flight restored the preflight checkpoint. With Debug Layer and DRED, intermediate handoffs reported one deliberately retained queue. Final shutdown reported zero errors, corruption, discarded messages and live children. The NVIDIA landed writer killed before NTFS installation preserved the prior checkpoint and reconstructed fog.
- **Groundwork renderer.** Eleven focused D3D12 tests passed. WARP and NVIDIA exercised Debug Layer and DRED, RGB fog readback before/inside/after the band, unchanged alpha and depth, rebase parity, invalid frame admission, edited terrain and release. The WARP distance-fog readback is required in Groundwork CI.

Run `eng/Test-VerifiedGroundworkConsumer.ps1` against the archive and manifest above, then `eng/Test-VolumeExploration.ps1` and `eng/Test-ProceduralFlight.ps1` against the Release client. The Groundwork Release filter is `FullyQualifiedName~DistanceFog|FullyQualifiedName~VolumeColorPolicy|FullyQualifiedName~WarpVolumeReadbackCoversTransition`.

## Previously measured qualification

[WYU #52](https://github.com/DaveBeusing/wyu/pull/52) records the instrumented workstation evidence on the integration source. Five fresh NVIDIA processes, with 2,200 frames per variant per phase after warm-up, met the GPU terrain-draw budgets in all three phases. The clean handover replay used the released packages and did not rerun those diagnostic measurements.

| Terrain draw phase | p95 delta | p99 delta |
| --- | ---: | ---: |
| Clear | −0.0320% | −0.0248% |
| Dense | +0.0534% | +0.0851% |
| Region transition | +0.1032% | +0.1135% |

The native weather-only comparison recorded 1,260 draws for 1,260 frames in each variant and zero mesh uploads. Weather heap plus estimated stack scratch was about 8.9 KiB. The diagnostic NTFS probe interrupted generation 3 after installation and before acknowledgment. Two restarts with the pinned client restored the new environment tick, unchanged landed pose, and identical fog.

Earlier PresentMon GPU Busy for the clear phase exceeded its thresholds. A separate GPU timestamp series that overlapped desktop tests had two p99 outliers. Both remain recorded in WYU #52. The isolated timestamps qualify the terrain draw on the workstation. Reference-system pressure and production qualification remain open in [Groundwork #88](https://github.com/DaveBeusing/Groundwork/issues/88) and [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89).
