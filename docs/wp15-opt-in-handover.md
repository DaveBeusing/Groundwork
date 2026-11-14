# WP15 Windows opt-in handover

Set WYU_WP15=on to enable directional sunlight and two near-terrain shadow cascades on Far Horizon. The default uses Package E lighting. The [WP15 contract](architecture/wp15-sunlight-shadow-contract.md) records the requirements for default enablement.

| Input | Verified identity |
| --- | --- |
| Groundwork master | 20871f43a809aa81c6347e4ab93b163fca9df1df. [Windows CI](https://github.com/DaveBeusing/Groundwork/actions/runs/43527092534) |
| WYU master | df787057cecd3443442a90aca80c130a079eec81. [Windows CI](https://github.com/DaveBeusing/wyu/actions/runs/43580543540) |
| Groundwork packages | 0.1.0-dev.43527092534.1. [Immutable archive](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43527092534.1-immutable) |
| Manifest SHA-256 | 683743030047CFA68CD944E8C2C466EA5EE958D2EDE4079FD9F547B1AAE5D232 |

## Windows integration

Both master checkouts were clean. Groundwork rebuilt its D3D12 Release tests from source and passed all 11 cases selected by 'FullyQualifiedName~Shadow|FullyQualifiedName~Sunlight'. These cover sunlight, shadow direction, overlap, rebase, cache reuse and shadow pixels in the first successful Present after a terrain edit. The desktop cases exercised WARP and NVIDIA with Debug Layer and DRED. WYU passed a fresh locked restore of the ten archived packages, nine gameplay tests, headless startup and controlled fault shutdown.

The pinned WYU build passed the opt-in ground route for seeds 42 and 20261111 on WARP and NVIDIA RTX PRO 5000 Blackwell Laptop, driver 32.0.15.9653. The route presented midday, low sun, camera turns, night, rebase and a terrain edit. Both adapters reached terrain revision 2 and the same final sequence per seed (80 and 82). An NVIDIA run with WYU_WP15 unset verified the default camera path. Desktop route logs recorded debug warnings, zero errors, zero corruption and zero live child objects at shutdown.

The NVIDIA flight route exercised both seeds with WP15 enabled. Twilight, both orbit handoffs and the Package G horizon, sun and star pixel checks passed. Landing and Continue logged identical sunlight direction, direct and ambient gains and shadow state at saved environment tick 56164 for each seed.

## Reproduction and remaining qualification

Run the Groundwork Release D3D12 tests with the filter above. Run WYU's eng/Test-VerifiedGroundworkConsumer.ps1 with the archive, commit and manifest listed above. Set WYU_WP15=on and WYU_WP15_PROBE=route and run the Release client with --volume-smoke <seed> <warp|hardware>. Clear the probe variable and run eng/Test-ReferenceSky.ps1 -Adapter hardware with WYU_WP15=on for flight and Continue.

The existing five-pair 1080p moving-sun replay stayed within the absolute GPU budget. Pooled relative p95/p99 deltas were +29.62%/+25.98% at midday, +15.59%/+15.67% at low sun and +15.55%/+15.75% during repeated turns. All phases exceeded the relative +10%/+15% limits. This handover adds functional verification and uses the existing performance evidence. Default enablement and reference-system production qualification remain open.

[WYU #56](https://github.com/DaveBeusing/wyu/pull/56) · [Groundwork #113](https://github.com/DaveBeusing/Groundwork/issues/113) · [Groundwork #89](https://github.com/DaveBeusing/Groundwork/issues/89)
